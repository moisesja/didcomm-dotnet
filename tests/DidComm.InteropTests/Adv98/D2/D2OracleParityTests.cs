using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DidComm.InteropTests.Adv98.D2;

/// <summary>
/// #98 D2 attack 3: an enrolled peer (any authcrypt sender under open enrolment) naming another
/// tenant's <c>recipient_did</c>, nobody's, a DID URL of the other tenant's DID, or a non-DID, in a
/// <c>status-request</c> or a <c>delivery-request</c>, over HTTP and over WebSocket, gets the same
/// answer (same type, same members once the echoed filter is masked, same encrypted length for
/// equal-length filters) and causes no message-store call at all.
/// </summary>
public sealed class D2OracleParityTests
{
    [Fact]
    public async Task Over_http_and_websocket_a_peer_learns_nothing_and_reads_no_store_for_a_scope_it_does_not_own()
    {
        var calls = new ConcurrentQueue<string>();
        await using var mediator = await MediatorTestServer.StartAsync(o => o.PostConfigureServices = services => D2CountingStore.Decorate(services, calls));

        var bob = await mediator.NewPeerAsync();
        var bobsRecipient = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob, bob.Did, bobsRecipient.Did);
        var alice = await mediator.NewSenderAsync();
        var inner = await alice.AuthcryptAsync(alice.Note(bobsRecipient.Did), bobsRecipient.Did);
        await Qa.PostForwardAsync(mediator, (await alice.ForwardAsync(mediator.Did, bobsRecipient.Did, inner)).Packed);
        await mediator.FlushAsync();

        var mallory = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(mallory);
        var nobody = await mediator.NewPeerAsync();
        nobody.Did.Length.Should().Be(bobsRecipient.Did.Length, "equal-length filters make the encrypted lengths comparable");

        // Positive control: the owner's own status reads the store and sees the queued message.
        var before = calls.Count;
        (await mediator.StatusAsync(bob, bobsRecipient.Did)).MessageCount.Should().Be(1);
        (calls.Count - before).Should().BeGreaterThan(0, "the counting store sees the owner's read");

        var scopes = new (string Name, string Value)[]
        {
            ("another tenant's DID", bobsRecipient.Did),
            ("nobody's DID", nobody.Did),
            ("non-DID of equal length", new string('x', bobsRecipient.Did.Length)),
            ("DID URL of another tenant's DID", bobsRecipient.Did + "#key-1"),
            ("DID URL with a query", bobsRecipient.Did + "?service=x"),
        };
        var kinds = new (string Name, Func<string, Message> Build)[]
        {
            ("status-request", s => MessagePickup.CreateStatusRequest(mallory.Did, mediator.Did, s)),
            ("delivery-request limit 10", s => MessagePickup.CreateDeliveryRequest(mallory.Did, mediator.Did, 10, s)),
            ("delivery-request limit 1", s => MessagePickup.CreateDeliveryRequest(mallory.Did, mediator.Did, 1, s)),
        };

        var observations = new List<(string Transport, string Kind, string Scope, string Type, string Body, int Packed, int StoreCalls)>();
        foreach (var kind in kinds)
        {
            foreach (var scope in scopes)
            {
                var request = kind.Build(scope.Value);
                var start = calls.Count;
                var answer = await mediator.AskAsync(mallory, request);
                observations.Add(("http", kind.Name, scope.Name, answer.Message.Type, Normalized(answer.Message, scope.Value), answer.Packed.Length, calls.Count - start));
            }
        }

        using (var socket = await mediator.ConnectAsync())
        {
            foreach (var kind in kinds)
            {
                foreach (var scope in scopes)
                {
                    var request = kind.Build(scope.Value);
                    var start = calls.Count;
                    var answer = await mediator.AskOverSocketAsync(socket, mallory, request);
                    observations.Add(("websocket", kind.Name, scope.Name, answer.Message.Type, Normalized(answer.Message, scope.Value), answer.Packed.Length, calls.Count - start));
                }
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", default);
        }

        observations.Should().OnlyContain(o => o.StoreCalls == 0, "a scope the peer does not own is never read from the message store");
        foreach (var group in observations.GroupBy(o => (o.Transport, o.Kind)))
        {
            group.Select(o => o.Type).Distinct().Should().ContainSingle("{0}: one answer type whoever holds the filter", group.Key);
            group.Select(o => o.Body).Distinct().Should().ContainSingle("{0}: one answer body (filter masked) whoever holds the filter", group.Key);
            group.Where(o => !o.Scope.StartsWith("DID URL", StringComparison.Ordinal)).Select(o => o.Packed).Distinct()
                .Should().ContainSingle("{0}: equal encrypted lengths for equal-length filters", group.Key);
        }
    }

    /// <summary>
    /// Documented limit reproduced ("A registration oracle toward enrolled peers",
    /// docs/codebase-architecture.md): under the default open enrolment, a stranger with a DID it
    /// minted a moment ago is an "enrolled peer" after one message, and the second message tells it
    /// whether a DID is held by another tenant (<c>client_error</c>) or free (<c>success</c>, and it
    /// now holds it). The doc's "Unauthenticated callers learn nothing" is true only in the sense
    /// that a self-made did:peer counts as authenticated.
    /// </summary>
    [Fact]
    public async Task Documented_limit_a_freshly_minted_stranger_reads_the_registration_oracle_in_two_messages()
    {
        await using var mediator = await MediatorTestServer.StartAsync();
        var bob = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(bob);
        var free = await mediator.NewPeerAsync();

        var stranger = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(stranger)).Should().ContainSingle().Which.Should().Be(mediator.Did, "open enrolment: one message makes a stranger an enrolled peer");
        var answer = await mediator.AskAsync(stranger, CoordinateMediation.CreateRecipientUpdate(stranger.Did, mediator.Did, new[]
        {
            new RecipientUpdate(bob.Did, RecipientUpdateAction.Add),
            new RecipientUpdate(free.Did, RecipientUpdateAction.Add),
        }));
        CoordinateMediation.TryParseRecipientUpdateResponse(answer.Message, out var results).Should().BeTrue();

        results!.Select(r => r.Result).Should().Equal(
            new[] { RecipientUpdateOutcome.ClientError, RecipientUpdateOutcome.Success },
            "held by another tenant vs free are told apart (documented)");
    }

    /// <summary>
    /// Attack 4 (capacity oracles), Info: an enrolled stranger tells "the mediator's whole
    /// registry is full" (<c>server_error</c>) from "my own grant is full" (<c>client_error</c>),
    /// and a full grant store answers a new peer with <c>mediate-deny</c>. Reproduced as
    /// designed (MediationCoordinator ToWire); recorded so the threat model can name it.
    /// </summary>
    [Fact]
    public async Task Capacity_answers_tell_an_enrolled_stranger_a_full_registry_from_a_full_grant_and_a_full_grant_store()
    {
        await using var mediator = await MediatorTestServer.StartAsync(o => o.Mediator = m =>
        {
            m.CoordinateMediation.MaxRecipientsPerGrant = 1;
            m.CoordinateMediation.MaxRecipients = 2;
            m.CoordinateMediation.MaxGrants = 3;
        });
        var a = await mediator.NewPeerAsync();
        await mediator.EnrolAsync(a);
        var b = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(b)).Should().ContainSingle();
        var stranger = await mediator.NewPeerAsync();
        (await mediator.RequestMediationAsync(stranger)).Should().ContainSingle();

        // The stranger's grant holds one DID: its second add is client_error (its own limit).
        var one = await mediator.NewPeerAsync();
        var two = await mediator.NewPeerAsync();
        var first = await mediator.AskAsync(stranger, CoordinateMediation.CreateRecipientUpdate(stranger.Did, mediator.Did, new[]
        {
            new RecipientUpdate(one.Did, RecipientUpdateAction.Add),
            new RecipientUpdate(two.Did, RecipientUpdateAction.Add),
        }));
        CoordinateMediation.TryParseRecipientUpdateResponse(first.Message, out var own).Should().BeTrue();

        // B's grant is empty, but the registry (2) is now full: server_error.
        var three = await mediator.NewPeerAsync();
        var second = await mediator.AskAsync(b, CoordinateMediation.CreateRecipientUpdate(b.Did, mediator.Did, new[] { new RecipientUpdate(three.Did, RecipientUpdateAction.Add) }));
        CoordinateMediation.TryParseRecipientUpdateResponse(second.Message, out var global).Should().BeTrue();

        // A fourth peer: the grant store (3) is full.
        var late = await mediator.NewPeerAsync();
        var denied = await mediator.AskAsync(late, CoordinateMediation.CreateMediateRequest(late.Did, mediator.Did));

        own!.Select(r => r.Result).Should().Equal(RecipientUpdateOutcome.Success, RecipientUpdateOutcome.ClientError);
        global!.Single().Result.Should().Be(RecipientUpdateOutcome.ServerError, "a full registry is the mediator's side");
        CoordinateMediation.TryParseDeny(denied.Message).Should().BeTrue("a full grant store is a mediate-deny");
    }

    // The answer body with the echoed filter masked, and the per-request members (id, thid, created_time) dropped.
    private static string Normalized(Message message, string filter)
    {
        var body = (JsonObject)(message.Body?.DeepClone() ?? new JsonObject());
        if (body["recipient_did"] is JsonValue v && v.GetValue<string>() == filter)
            body["recipient_did"] = "<filter>";
        return body.ToJsonString();
    }
}

/// <summary>Replaces the registered <see cref="IMessageStore"/> with a proxy that records every call.</summary>
internal class D2CountingStore : DispatchProxy
{
    private IMessageStore? _inner;
    private ConcurrentQueue<string>? _calls;

    public static void Decorate(IServiceCollection services, ConcurrentQueue<string> calls)
    {
        var descriptor = services.Last(s => s.ServiceType == typeof(IMessageStore));
        services.Remove(descriptor);
        services.AddSingleton<IMessageStore>(sp =>
        {
            var inner = descriptor.ImplementationInstance as IMessageStore
                ?? (descriptor.ImplementationFactory is { } factory
                    ? (IMessageStore)factory(sp)
                    : (IMessageStore)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
            var proxy = Create<IMessageStore, D2CountingStore>();
            var self = (D2CountingStore)(object)proxy;
            self._inner = inner;
            self._calls = calls;
            return proxy;
        });
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        _calls!.Enqueue(targetMethod!.Name);
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
}
