using System.Collections.Concurrent;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Rotation;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetCrypto;
using NetDid.Core;
using Xunit;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #105 QA pass, kept as regressions: FR-ROT-05 <c>from_prior</c> exp / nbf and FR-ROUTE-09 forward relay-time expiry, in an
/// <c>AddDidComm</c> container whose <c>ReplayProtectionOptions.ClockSkew</c> is set (and proven live
/// on that container's guard), follow <c>DidCommOptions.ExpiresClockSkew</c> only.
/// </summary>
public sealed class ReplayClockSkewQaExpiryTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("exp", 0, 0, false)]
    [InlineData("exp", 0, 3600, false)]
    [InlineData("exp", 1, 0, true)]
    [InlineData("exp", 1, 3600, true)]
    [InlineData("nbf", 0, 0, false)]
    [InlineData("nbf", 0, 3600, false)]
    [InlineData("nbf", 1, 0, true)]
    [InlineData("nbf", 1, 3600, true)]
    public async Task From_prior_exp_and_nbf_follow_ExpiresClockSkew_whatever_ClockSkew(string claim, int expiresSkewSeconds, int replaySkewSeconds, bool accepted)
    {
        var secrets = new InMemorySecretsResolver();
        using var sp = Build(secrets, expiresSkewSeconds, replaySkewSeconds, routing: false, sink: null, clock: () => T0);
        ProveReplaySkewIsLive(sp, replaySkewSeconds, expiresSkewSeconds);
        var prior = await NewPeerAsync(sp, secrets);
        var next = await NewPeerAsync(sp, secrets);
        var bob = await NewPeerAsync(sp, secrets);
        var t0 = T0.ToUnixTimeSeconds();
        var claims = claim == "exp"
            ? new FromPriorClaims(next.Did, prior.Did, t0 - 10, Exp: t0 - 1)
            : new FromPriorClaims(next.Did, prior.Did, t0 - 10, Nbf: t0 + 1);
        var signer = prior.Privates.Single(j => j.Crv == "Ed25519");
        var jwt = await FromPriorBuilder.BuildAsync(claims, signer);
        var message = new MessageBuilder()
            .WithType("https://example.org/qa/1.0/rotated")
            .WithFrom(next.Did)
            .WithTo(bob.Did)
            .WithFromPrior(jwt)
            .Build();
        var client = sp.GetRequiredService<DidCommClient>();
        var packed = await client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: next.Did, Forward: false));

        var unpack = async () => await client.UnpackAsync(packed.Message);

        if (accepted)
            (await unpack()).FromPrior.Should().NotBeNull();
        else
            await unpack.Should().ThrowAsync<ConsistencyException>().Where(e => e.Message.Contains(claim == "exp" ? "FR-ROT-05" : "not yet valid", StringComparison.Ordinal));
    }

    /// <summary>
    /// A forward expiring at T0+60, unpacked at T0 (FR-API-05 accepts it) and dispatched through the
    /// container's own options at T0+61: the handler's relay-time check drops it unless
    /// ExpiresClockSkew ≥ 1 s; an hour of replay skew changes nothing.
    /// </summary>
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 3600, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 3600, true)]
    public async Task Forward_relay_time_expiry_follows_ExpiresClockSkew_whatever_ClockSkew(int expiresSkewSeconds, int replaySkewSeconds, bool relayed)
    {
        var secrets = new InMemorySecretsResolver();
        var sink = new RecordingSink();
        var now = T0;
        using var sp = Build(secrets, expiresSkewSeconds, replaySkewSeconds, routing: true, sink, clock: () => now);
        ProveReplaySkewIsLive(sp, replaySkewSeconds, expiresSkewSeconds);
        var alice = await NewPeerAsync(sp, secrets);
        var bob = await NewPeerAsync(sp, secrets);
        var mediator = await NewPeerAsync(sp, secrets);
        var client = sp.GetRequiredService<DidCommClient>();
        var dispatcher = sp.GetRequiredService<ProtocolDispatcher>();
        var handler = sp.GetRequiredService<ForwardHandler>();
        sp.GetRequiredService<InMemoryForwardRecipientRegistry>().Register(bob.Did);

        var inner = (await client.PackEncryptedAsync(
            new MessageBuilder().WithType("https://example.org/qa/1.0/hello").WithFrom(alice.Did).WithTo(bob.Did).Build(),
            new PackEncryptedOptions(Recipients: new[] { bob.Did }, From: alice.Did))).Message;
        var forward = ForwardMessage.Create(mediator.Did, bob.Did, new[] { inner }, expiresTimeEpochSeconds: T0.AddSeconds(60).ToUnixTimeSeconds());
        var packed = await client.PackEncryptedAsync(forward, new PackEncryptedOptions(Recipients: new[] { mediator.Did }));
        var unpacked = await client.UnpackAsync(packed.Message);

        now = T0.AddSeconds(61);
        var outcome = await dispatcher.DispatchAsync(unpacked, client, sp.GetRequiredService<IOptions<DidCommOptions>>().Value);
        outcome.Result.Should().Be(DispatchResult.NoReply);
        await handler.FlushAsync(Timeout);

        sink.Results.Count.Should().Be(relayed ? 1 : 0);
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static ServiceProvider Build(InMemorySecretsResolver secrets, int expiresSkewSeconds, int replaySkewSeconds, bool routing, IForwardSink? sink, Func<DateTimeOffset> clock)
    {
        var services = new ServiceCollection();
        if (sink is not null)
            services.AddSingleton(sink);
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.AddBuiltInProtocols();
            if (routing)
                b.AddRoutingMediator();
            b.Configure(o =>
            {
                o.Clock = clock;
                o.ExpiresClockSkew = TimeSpan.FromSeconds(expiresSkewSeconds);
            });
        });
        services.AddOptions<ReplayProtectionOptions>().Configure(o => o.ClockSkew = TimeSpan.FromSeconds(replaySkewSeconds));
        return services.BuildServiceProvider();
    }

    private static void ProveReplaySkewIsLive(IServiceProvider sp, int replaySkewSeconds, int expiresSkewSeconds)
    {
        var guard = sp.GetRequiredService<IInboundReplayGuard>();
        var t0 = T0.ToUnixTimeSeconds();
        guard.TryAdmit("did:example:qa", "at-skew", t0 + replaySkewSeconds, T0).Should().Be(ReplayAdmission.Admitted);
        guard.TryAdmit("did:example:qa", "past-skew", t0 + replaySkewSeconds + 1, T0).Should().Be(ReplayAdmission.FutureDated);
        sp.GetRequiredService<IOptions<DidCommOptions>>().Value.ExpiresClockSkew.Should().Be(TimeSpan.FromSeconds(expiresSkewSeconds),
            "the replay skew is not copied into the expiry tolerance");
    }

    private static async Task<PeerIdentity> NewPeerAsync(IServiceProvider sp, InMemorySecretsResolver secrets)
    {
        var peer = await PeerIdentityFactory.CreateAsync(
            sp.GetRequiredService<IDidManager>(),
            sp.GetRequiredService<IKeyGenerator>(),
            sp.GetRequiredService<ICryptoProvider>());
        foreach (var jwk in peer.Privates)
            secrets.Add(jwk);
        return peer;
    }

    private sealed class RecordingSink : IForwardSink
    {
        public ConcurrentQueue<ForwardProcessingResult> Results { get; } = new();

        public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
        {
            Results.Enqueue(result);
            return Task.CompletedTask;
        }
    }
}
