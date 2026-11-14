using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Pipeline;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA-1 (#96): <see cref="MediationClient"/>'s arguments over the real channel — every refusal
/// happens synchronously, before the endpoint is resolved or anything is sent, and quotes no value
/// (whitespace, control characters, empty method-specific ids, DID URLs of each form, paging edges,
/// update lists with a DID URL or a null entry, overrides).
/// </summary>
public sealed class MediationClientQa1Tests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();
    private int _resolved;
    private MediatorClientChannel _channel = null!;
    private MediationClient _client = null!;

    public async Task InitializeAsync()
    {
        _w = await ClientWorld.CreateAsync();
        _channel = Create(_w, _send, time: _time, resolveEndpoint: (_, _) =>
        {
            Interlocked.Increment(ref _resolved);
            return Task.FromResult(Wss);
        });
        _client = new MediationClient(_channel, logger: null);
    }

    public async Task DisposeAsync()
    {
        await _channel.DisposeAsync();
        await _w.DisposeAsync();
    }

    public static TheoryData<string> BadDids => new()
    {
        "",
        " ",
        " did:example:qa1",
        "did:example:qa1 ",
        "did:example:qa1\r\nInjected: line",
        "did:example:qa1\u0000",
        "did:",
        "did:example",
        "did:example:",
        "did:example:qa1#",
        "did:example:qa1#key-1",
        "did:example:qa1?",
        "did:example:qa1?service=a",
        "did:example:qa1/",
        "did:example:qa1/path",
        "did:example:" + new string('z', 2049),
        "urn:uuid:7d7c0ab0-62c5-4a8b-9a4a-2c5b0d1f2f11",
        "https://mediator.example/",
    };

    [Theory]
    [MemberData(nameof(BadDids))]
    public void Every_operation_refuses_a_bad_mediator_or_from_DID_synchronously_quoting_nothing(string bad)
    {
        foreach (var (name, act) in Operations(bad, _w.Local.Did).Concat(Operations(_w.Mediator.Did, bad)))
        {
            var thrown = FluentActions.Invoking(act).Should().Throw<ArgumentException>(name).Which;
            if (bad.Trim().Length > 4)
                thrown.Message.Should().NotContain(bad.Trim()[..Math.Min(bad.Trim().Length, 30)], name);
        }
        _resolved.Should().Be(0);
        _send.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(1001, 0)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(0, -1)]
    [InlineData(10, int.MinValue)]
    public void A_page_out_of_range_is_refused_synchronously(int limit, int offset)
    {
        Sync(() => _ = _client.QueryRecipientsAsync(_w.Mediator.Did, _w.Local.Did, new RecipientQueryPaging(limit, offset)))
            .Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("paginate");
        _resolved.Should().Be(0);
        _send.Count.Should().Be(0);
    }

    public static TheoryData<string> BadUpdateDids => new()
    {
        "did:example:qa1#key-1",
        "did:example:qa1?x=1",
        "did:example:qa1/p",
        " did:example:qa1",
        "did:example:" + new string('z', 2049),
    };

    [Theory]
    [MemberData(nameof(BadUpdateDids))]
    public void An_update_naming_anything_but_a_bare_DID_is_refused_by_index_synchronously(string bad)
    {
        var updates = new[]
        {
            new RecipientUpdate("did:example:fine", RecipientUpdateAction.Add),
            new RecipientUpdate(bad, RecipientUpdateAction.Remove),
        };

        var thrown = Sync(() => _ = _client.UpdateRecipientsAsync(_w.Mediator.Did, _w.Local.Did, updates)).Should().Throw<ArgumentException>().Which;

        thrown.Message.Should().Contain("Update 1").And.NotContain(bad.Trim()[..Math.Min(bad.Trim().Length, 30)]);
        _resolved.Should().Be(0);
        _send.Count.Should().Be(0);
    }

    [Fact]
    public void An_update_list_with_a_null_entry_or_an_undefined_action_is_refused_synchronously()
    {
        Sync(() => _ = _client.UpdateRecipientsAsync(_w.Mediator.Did, _w.Local.Did, new RecipientUpdate[] { null! }))
            .Should().Throw<ArgumentException>().Which.Message.Should().Contain("Update 0");
        Sync(() => _ = _client.UpdateRecipientsAsync(_w.Mediator.Did, _w.Local.Did, new[] { new RecipientUpdate("did:example:a", (RecipientUpdateAction)99) }))
            .Should().Throw<ArgumentException>().Which.Message.Should().Contain("Update 0");
        _send.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("https://user:secret@mediator.example/")]
    [InlineData("wss://token@mediator.example/ws")]
    public void A_relative_or_credentialed_override_is_refused_synchronously_without_quoting_it(string uri)
    {
        var endpoint = new Uri(uri, UriKind.RelativeOrAbsolute);
        foreach (var (name, act) in Operations(_w.Mediator.Did, _w.Local.Did, endpoint))
        {
            var thrown = FluentActions.Invoking(act).Should().Throw<ArgumentException>(name).Which;
            thrown.ParamName.Should().Be("serviceEndpointOverride");
            thrown.Message.Should().NotContain("secret").And.NotContain("token");
        }
        _resolved.Should().Be(0);
        _send.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_valid_call_resolves_once_and_sends_the_query_first()
    {
        var call = _client.QueryRecipientsAsync(_w.Mediator.Did, _w.Local.Did);
        await _send.FirstCall;

        _resolved.Should().Be(1);
        _send.Calls.Single().Message.Type.Should().Be(Protocols.DiscoverFeatures.DiscoverFeatures.QueriesType);
        _time.Advance(_channel.Options.RequestTimeout);
        await FluentActions.Awaiting(() => call).Should().ThrowAsync<TimeoutException>();
    }

    private static Action Sync(Action act) => act;

    // Every public operation, as a synchronous call (argument checks run before the returned task).
    private IEnumerable<(string Name, Action Act)> Operations(string mediatorDid, string from, Uri? endpoint = null)
    {
        var updates = new[] { new RecipientUpdate("did:example:fine", RecipientUpdateAction.Add) };
        yield return ("RequestMediationAsync", () => _ = _client.RequestMediationAsync(mediatorDid, from, endpoint));
        yield return ("UpdateRecipientsAsync", () => _ = _client.UpdateRecipientsAsync(mediatorDid, from, updates, endpoint));
        yield return ("QueryRecipientsAsync", () => _ = _client.QueryRecipientsAsync(mediatorDid, from, null, endpoint));
        yield return ("QueryAllRecipientsAsync", () => _ = _client.QueryAllRecipientsAsync(mediatorDid, from, endpoint));
    }
}
