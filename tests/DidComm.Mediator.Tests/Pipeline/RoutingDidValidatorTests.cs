using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Pipeline;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Resolution;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary>
/// <see cref="RoutingDidValidator"/> (FR-MED-07): the first DIDComm entry must name an absolute
/// http/https/ws/wss URI, never a DID or the queue URI; at least one keyAgreement key whose secret is
/// held; resolution failures, an unsupported method and a hang fail; routing keys and DID-valued
/// later entries are Warnings; refusals name the index and the exception type, never the DID.
/// </summary>
public sealed class RoutingDidValidatorTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(HostedFixture fixture) => Fixture = fixture;

        public HostedFixture Fixture { get; }
        public string Did => Fixture.Mediator!.Did;
        public IDidKeyService Keys => Fixture.Services.GetRequiredService<IDidKeyService>();
        public ISecretsResolver Secrets => Fixture.Services.GetRequiredService<ISecretsResolver>();

        public static async Task<Harness> CreateAsync(DidCommServiceSpec? service = null, bool holdSecrets = true)
            => new(await HostedFixture.CreateAsync(service, holdSecrets));

        public RoutingDidValidator Validator(IServiceEndpointResolver? resolver = null, ISecretsResolver? secrets = null, TimeSpan? timeout = null)
            => new(
                resolver ?? Fixture.Services.GetRequiredService<IServiceEndpointResolver>(),
                Keys,
                secrets ?? Secrets,
                Fixture.Time,
                timeout ?? RoutingDidValidator.DefaultTimeout,
                Fixture.Services.GetRequiredService<ILogger<MediatorHostedService>>());

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }

    private static ScriptedServiceResolver Returning(params DidCommServiceInfo[] services)
        => new((_, _) => Task.FromResult<IReadOnlyList<DidCommServiceInfo>>(services));

    private static DidCommServiceInfo Entry(string uri, params string[] routingKeys) => new(uri, routingKeys, new[] { "didcomm/v2" });

    private static async Task<InvalidOperationException> RefusedAsync(Harness harness, RoutingDidValidator validator, string? did = null)
    {
        var target = did ?? harness.Did;
        var refusal = (await validator.Invoking(v => v.ValidateAsync(new[] { target }, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()).Which;
        refusal.Message.Should().Contain("index 0");
        refusal.Message.Should().NotContain(target);
        refusal.InnerException.Should().BeNull("an inner exception's text could name the DID");
        return refusal;
    }

    [Theory]
    [InlineData("https://mediator-host.example/didcomm")]
    [InlineData("http://mediator-host.example/didcomm")]
    [InlineData("ws://mediator-host.example/didcomm")]
    [InlineData("wss://mediator-host.example/didcomm")]
    [InlineData("HTTPS://mediator-host.example/didcomm")]
    public async Task A_did_peer_whose_first_service_is_a_transport_uri_and_whose_key_is_held_passes(string uri)
    {
        await using var harness = await Harness.CreateAsync(new DidCommServiceSpec(uri, Accept: new[] { "didcomm/v2" }));

        await harness.Validator().Invoking(v => v.ValidateAsync(new[] { harness.Did }, CancellationToken.None)).Should().NotThrowAsync();
        harness.Fixture.Logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_service_that_names_a_did_as_its_uri_is_refused()
    {
        await using var harness = await Harness.CreateAsync(new DidCommServiceSpec("did:example:another-mediator"));

        var refusal = await RefusedAsync(harness, harness.Validator());

        refusal.Message.Should().Contain("names a DID");
        refusal.Message.Should().NotContain("another-mediator");
    }

    [Fact]
    public async Task No_service_is_refused()
    {
        await using var harness = await Harness.CreateAsync();

        var refusal = await RefusedAsync(harness, harness.Validator(Returning()));

        refusal.Message.Should().Contain("no DIDCommMessaging service");
    }

    [Theory]
    [InlineData("ftp://mediator-host.example/didcomm")]
    [InlineData("mailto:mediator@mediator-host.example")]
    [InlineData("didcomm:transport/queue")]
    [InlineData("/didcomm")]
    [InlineData("mediator-host.example/didcomm")]
    public async Task A_first_service_uri_that_is_not_an_absolute_transport_uri_is_refused(string uri)
    {
        await using var harness = await Harness.CreateAsync();

        var refusal = await RefusedAsync(harness, harness.Validator(Returning(Entry(uri))));

        refusal.Message.Should().Contain("not an absolute http, https, ws or wss URI");
        refusal.Message.Should().NotContain("mediator-host");
    }

    [Fact]
    public async Task Only_the_first_entry_counts_for_the_endpoint()
    {
        await using var harness = await Harness.CreateAsync();

        await RefusedAsync(harness, harness.Validator(Returning(Entry("ftp://mediator-host.example/x"), Entry("https://mediator-host.example/didcomm"))));
    }

    [Fact]
    public async Task An_empty_first_uri_is_refused()
    {
        await using var harness = await Harness.CreateAsync();

        await RefusedAsync(harness, harness.Validator(Returning(Entry(""))));
    }

    [Fact]
    public async Task Routing_keys_on_the_mediators_own_service_are_a_warning_not_a_refusal()
    {
        await using var harness = await Harness.CreateAsync(new DidCommServiceSpec("https://mediator-host.example/didcomm", RoutingKeys: new[] { "did:example:some-other-mediator#key-1" }));

        await harness.Validator().Invoking(v => v.ValidateAsync(new[] { harness.Did }, CancellationToken.None)).Should().NotThrowAsync();

        var warning = harness.Fixture.Logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which;
        warning.Everything.Should().Contain("routing keys");
        PipelineLogs.Leaking(harness.Fixture.Logs, harness.Did, "some-other-mediator", "mediator-host").Should().BeEmpty();
    }

    [Fact]
    public async Task A_later_entry_that_names_a_did_is_a_warning()
    {
        await using var harness = await Harness.CreateAsync();

        await harness.Validator(Returning(Entry("https://mediator-host.example/didcomm"), Entry("did:example:fallback-mediator")))
            .Invoking(v => v.ValidateAsync(new[] { harness.Did }, CancellationToken.None)).Should().NotThrowAsync();

        harness.Fixture.Logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Everything.Should().Contain("entry 1").And.NotContain("fallback-mediator");
    }

    [Fact]
    public async Task No_held_key_agreement_secret_is_refused()
    {
        await using var harness = await Harness.CreateAsync(holdSecrets: false);

        var refusal = await RefusedAsync(harness, harness.Validator());

        refusal.Message.Should().Contain("holds the private key of none");
    }

    [Fact]
    public async Task A_held_authentication_key_alone_is_not_enough()
    {
        await using var harness = await Harness.CreateAsync(holdSecrets: false);
        var secrets = new InMemorySecretsResolver();
        secrets.Add(harness.Fixture.Mediator!.Privates[1]); // the Ed25519 authentication key; [0] is the X25519 keyAgreement key

        await RefusedAsync(harness, harness.Validator(secrets: secrets));
    }

    [Fact]
    public async Task A_failing_secrets_resolver_is_refused_by_type()
    {
        await using var harness = await Harness.CreateAsync();

        var refusal = await RefusedAsync(harness, harness.Validator(secrets: new ThrowingSecrets()));

        refusal.Message.Should().Contain(nameof(HandlerBoomException)).And.NotContain(HandlerBoomException.Marker);
    }

    [Fact]
    public async Task An_unresolvable_did_is_refused_by_exception_type()
    {
        await using var harness = await Harness.CreateAsync();

        var refusal = await RefusedAsync(harness, harness.Validator(), did: "did:example:no-such-mediator");

        refusal.Message.Should().Contain("could not be resolved").And.Contain("Exception");
    }

    [Fact]
    public async Task An_unsupported_method_is_refused()
    {
        await using var harness = await Harness.CreateAsync();

        var refusal = await RefusedAsync(harness, harness.Validator(), did: "did:web:mediator-host.example");

        refusal.Message.Should().Contain("not supported");
    }

    [Fact]
    public async Task A_resolver_fault_is_refused_by_type_without_its_text()
    {
        await using var harness = await Harness.CreateAsync();
        var resolver = new ScriptedServiceResolver((did, _) => throw new DidResolutionException(did, HandlerBoomException.Marker));

        var refusal = await RefusedAsync(harness, harness.Validator(resolver));

        refusal.Message.Should().Contain(nameof(DidResolutionException)).And.NotContain(HandlerBoomException.Marker);
    }

    [Fact]
    public async Task A_resolver_that_hangs_is_refused_after_the_bound_on_the_injected_clock()
    {
        await using var harness = await Harness.CreateAsync();
        var hang = new TaskCompletionSource<IReadOnlyList<DidCommServiceInfo>>();
        var validation = harness.Validator(new ScriptedServiceResolver((_, _) => hang.Task)).ValidateAsync(new[] { harness.Did }, CancellationToken.None);

        await harness.Fixture.Time.WaitForScheduledTimersAsync(1, Wait);
        harness.Fixture.Time.Advance(TimeSpan.FromSeconds(29));
        validation.IsCompleted.Should().BeFalse();
        harness.Fixture.Time.Advance(TimeSpan.FromSeconds(1));

        var refusal = (await validation.Awaiting(v => v).Should().ThrowAsync<InvalidOperationException>()).Which;
        refusal.Message.Should().Contain("index 0").And.Contain("30 seconds");
        hang.SetException(new HandlerBoomException(HandlerBoomException.Marker)); // the abandoned check faults later: observed, nothing escapes
    }

    [Fact]
    public async Task A_cancelled_start_leaves_as_cancellation()
    {
        await using var harness = await Harness.CreateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await harness.Validator().Invoking(v => v.ValidateAsync(new[] { harness.Did }, cts.Token)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_index_names_which_routing_did_failed()
    {
        await using var harness = await Harness.CreateAsync();

        var refusal = (await harness.Validator().Invoking(v => v.ValidateAsync(new[] { harness.Did, "did:example:second-mediator" }, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>()).Which;

        refusal.Message.Should().Contain("index 1").And.NotContain("second-mediator");
    }

    private sealed class ThrowingSecrets : ISecretsResolver
    {
        public Task<DataProofsDotnet.Jose.Jwk?> FindAsync(string kid, CancellationToken ct = default) => throw new HandlerBoomException(HandlerBoomException.Marker);
        public Task<IReadOnlyList<string>> FindPresentAsync(IEnumerable<string> kids, CancellationToken ct = default) => throw new HandlerBoomException(HandlerBoomException.Marker);
    }
}
