using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Protocols.ProblemReport;
using DidComm.Protocols.Trace;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetDid.Core;
using NetDid.Core.Crypto;
using Xunit;

// L-014.
using ProblemReportApi = DidComm.Protocols.ProblemReport.ProblemReport;

namespace DidComm.InteropTests.Protocols;

public sealed class ProblemReportAndTraceDiTests
{
    [Fact]
    public void AddBuiltInProtocols_registers_ProblemReport_with_default_options()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
        });
        using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<ProtocolHandlerRegistry>();
        registry.TryResolve(ProblemReportApi.MessageType, out var handler).Should().BeTrue();
        handler.Should().BeOfType<ProblemReportHandler>();

        sp.GetRequiredService<IOptions<ProblemReportOptions>>().Value.CascadeThreshold.Should().Be(5);
    }

    [Fact]
    public void Trace_is_NOT_registered_by_AddBuiltInProtocols()
    {
        // FR-PROTO-11a: Trace is off by default; AddBuiltInProtocols must NOT register it.
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.AddBuiltInProtocols();
        });
        using var sp = services.BuildServiceProvider();
        sp.GetService<TraceOptions>().Should().BeNull();
    }

    [Fact]
    public void EnableTracing_with_empty_allowlist_throws()
    {
        var services = new ServiceCollection();
        Action act = () => services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.EnableTracing(o => o.Enabled = true);
        });
        act.Should().Throw<InvalidOperationException>().WithMessage("*FR-PROTO-11a*");
    }

    [Fact]
    public void EnableTracing_with_allowlist_registers_TraceOptions()
    {
        var services = new ServiceCollection();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.EnableTracing(o =>
            {
                o.Enabled = true;
                o.AllowedReportingUris.Add("https://trace.example.com/report");
            });
        });
        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<TraceOptions>();
        options.Enabled.Should().BeTrue();
        options.AllowedReportingUris.Should().Contain("https://trace.example.com/report");

        // Symmetric with ProblemReportOptions: IOptions<TraceOptions> resolves to the same
        // instance for consumers that prefer the Options pattern.
        var wrapped = sp.GetRequiredService<IOptions<TraceOptions>>();
        wrapped.Value.Should().BeSameAs(options);
    }

    [Fact]
    public async Task End_to_end_cascade_guard_emits_max_errors_exceeded_after_threshold()
    {
        var services = new ServiceCollection();
        var secrets = new InMemorySecretsResolver();
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddBuiltInProtocols();
            b.Configure(o => { /* default cascade threshold = 5 */ });
        });
        services.Configure<ProblemReportOptions>(o => o.CascadeThreshold = 2);
        using var sp = services.BuildServiceProvider();

        var manager = sp.GetRequiredService<IDidManager>();
        var keyGen = sp.GetRequiredService<IKeyGenerator>();
        var crypto = sp.GetRequiredService<ICryptoProvider>();
        var alice = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        var bob = await PeerIdentityFactory.CreateAsync(manager, keyGen, crypto);
        foreach (var jwk in alice.Privates) secrets.Add(jwk);
        foreach (var jwk in bob.Privates) secrets.Add(jwk);

        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var options = sp.GetRequiredService<IOptions<DidCommOptions>>().Value;

        // Three inbound error reports on the same pthid → third one trips the cascade guard.
        DispatchOutcome? trip = null;
        for (var i = 0; i < 3; i++)
        {
            var report = ProblemReportApi.Create(
                from: alice.Did, to: bob.Did,
                code: "e.p.xfer.cant-use-endpoint",
                pthid: "thread-fail",
                comment: "Attempt {1} failed.",
                args: new[] { (i + 1).ToString() });
            var packed = await client.PackEncryptedAsync(report, new PackEncryptedOptions(
                Recipients: new[] { bob.Did }, From: alice.Did));
            var unpacked = await client.UnpackAsync(packed.Message);
            trip = await dispatcher.DispatchAsync(unpacked, client, options);
        }

        // The third dispatch should have produced the cascade-stop reply.
        trip.Should().NotBeNull();
        trip!.Result.Should().Be(DispatchResult.ReplyProduced);
        ProblemReportApi.ReadCode(trip.Reply!).Should().Be(ProblemReportApi.MaxErrorsExceededCode);
        trip.Reply!.Pthid.Should().Be("thread-fail");
        trip.Reply.From.Should().Be(bob.Did);
        trip.Reply.To.Should().Equal(alice.Did);
    }
}
