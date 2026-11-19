using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Consistency;
using DidComm.Facade;
using DidComm.InteropTests.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using NetDid.Core;
using NetDid.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Adv98.D1;

/// <summary>
/// #98 adversarial pass, dimension D1: FR-API-09 at the mediator's endpoint for every wire position
/// that names a DID or an id the mediator keys on: Coordinate Mediation 3.0 and 2.0
/// <c>recipient_did</c>, Pickup <c>recipient_did</c> (status and delivery requests), a
/// <c>message_id_list</c> entry, and a forward's <c>next</c>. A value of 2049 characters and one of
/// 1 MiB are refused before any store or resolver call: the mediator's resolver sees no call naming
/// the value and none for any DID other than the sender's and the mediator's own; nothing is stored,
/// registered or removed; nothing is answered; and the value reaches no response body and no log line.
/// </summary>
public sealed class D1NoResolverBeforeCapTests(ITestOutputHelper output)
{
    private const string Sentinel = "D1CAPSENTINEL";

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var position in new[] { "cm30-recipient_did", "cm20-recipient_did", "pickup-status-recipient_did", "pickup-delivery-recipient_did", "pickup-message_id_list", "forward-next" })
        {
            data.Add(position, 2049);
            data.Add(position, 1024 * 1024);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task An_over_long_identifier_is_refused_before_any_resolver_store_or_reply(string position, int length)
    {
        var resolverCalls = new ConcurrentQueue<string>();
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            o.MaxReceiveBytes = 8 * 1024 * 1024; // let the 1 MiB value reach the parser
            o.PostConfigureServices = services => D1CountingResolver.Decorate(services, resolverCalls);
        });
        var bob = await mediator.NewPeerAsync();
        var held = await mediator.NewRecipientAsync();
        (await mediator.Mediation.GrantAsync(new MediationGrant(bob.Did, new[] { mediator.Did }), default)).Should().NotBeNull();
        (await mediator.Mediation.UpdateRecipientsAsync(bob.Did, new[] { new RecipientUpdate(held.Did, RecipientUpdateAction.Add) }, default))
            .Should().Equal(RecipientStoreOutcome.Added);
        var queued = await mediator.Queue.DeliverAsync(held.Did, Encoding.UTF8.GetBytes("{\"protected\":\"x\"}"));
        queued.MessageId.Should().NotBeNull();

        // A DID-shaped value with the sentinel up front, so a quoted prefix is caught too.
        var value = "did:example:" + Sentinel + new string('q', length - "did:example:".Length - Sentinel.Length);
        value.Length.Should().Be(length);
        long now = mediator.Clock.Now.ToUnixTimeSeconds();

        string packed;
        string? senderDid = bob.Did;
        switch (position)
        {
            case "cm30-recipient_did":
                packed = await AuthcryptAsync(bob, Raw(CoordinateMediation.RecipientUpdateType, bob, mediator.Did, Updates(value), now), mediator.Did);
                break;
            case "cm20-recipient_did":
                packed = await AuthcryptAsync(bob, Raw(CoordinateMediation20.KeylistUpdateType, bob, mediator.Did, Updates(value), now), mediator.Did);
                break;
            case "pickup-status-recipient_did":
                packed = await AuthcryptAsync(bob, Raw(MessagePickup.StatusRequestType, bob, mediator.Did, new JsonObject { ["recipient_did"] = value }, now), mediator.Did);
                break;
            case "pickup-delivery-recipient_did":
                packed = await AuthcryptAsync(bob, Raw(MessagePickup.DeliveryRequestType, bob, mediator.Did, new JsonObject { ["limit"] = 10, ["recipient_did"] = value }, now), mediator.Did);
                break;
            case "pickup-message_id_list":
                packed = await AuthcryptAsync(bob, Raw(MessagePickup.MessagesReceivedType, bob, mediator.Did, new JsonObject { ["message_id_list"] = new JsonArray(value, queued.MessageId) }, now), mediator.Did);
                break;
            case "forward-next":
                var sender = await mediator.NewSenderAsync();
                senderDid = sender.Did;
                var inner = await sender.AuthcryptAsync(sender.Note(held.Did), held.Did);
                (packed, _) = await sender.ForwardAsync(mediator.Did, held.Did, inner, f => f.Body!["next"] = value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(position));
        }
        var warmCalls = resolverCalls.Count;

        var response = await PostAsync(mediator, packed);
        if (position == "forward-next")
            await WaitForForwardOutcomeAsync(mediator);

        var calls = resolverCalls.ToArray();
        var subjects = calls.Select(c => DidSubject.DidSubjectOf(c) ?? "(not a DID: " + c.Length + " chars)").Distinct().ToArray();
        output.WriteLine($"{position} x {length}: HTTP {(int)response.Status}, body {response.Body.Length} chars; resolver calls {calls.Length} (before the post {warmCalls}), longest {calls.DefaultIfEmpty("").Max(c => c.Length)} chars");
        foreach (var subject in subjects)
            output.WriteLine($"  resolved: {(subject == bob.Did ? "bob" : subject == mediator.Did ? "mediator" : subject == senderDid ? "sender" : subject[..Math.Min(40, subject.Length)])}");

        calls.Should().NotContain(c => c.Contains(Sentinel, StringComparison.Ordinal), "the over-long value is never resolved");
        calls.Should().OnlyContain(c => c.Length <= 4096);
        subjects.Should().BeSubsetOf(new[] { bob.Did, mediator.Did, senderDid! }.Distinct(), "only the sender's and the mediator's own DIDs are resolved");
        response.Body.Should().NotContain(Sentinel);
        if (position == "forward-next")
        {
            var outcomes = mediator.Measurements.TagValues("didcomm.forward.outcomes", "didcomm.forward.outcome");
            output.WriteLine($"  forward outcome: {string.Join(", ", outcomes)}");
            response.Status.Should().Be(HttpStatusCode.Accepted);
            response.Body.Should().BeEmpty();
            outcomes.Should().Equal(new[] { "Dropped-Malformed" }, "the bound is applied when the forward is parsed, before the registration lookup");
        }
        else
        {
            response.Status.Should().Be(HttpStatusCode.BadRequest, "a malformed request from an authenticated peer is refused, not answered");
        }
        (await mediator.Mediation.ListRecipientsAsync(bob.Did, 0, 10, default)).Dids.Should().Equal(held.Did);
        (await mediator.Mediation.FindGrantByRecipientDidAsync(value[..2048], default)).Should().BeNull();
        (await mediator.Store.GetStatusAsync(bob.Did, null, mediator.Clock.Now, default)).MessageCount.Should().Be(1, "nothing was queued, delivered or acknowledged");
        mediator.Logs.Entries.Should().NotContain(e => e.Contains(Sentinel, StringComparison.Ordinal) || e.Contains("qqqqqqqqqqqqqqqq", StringComparison.Ordinal));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static JsonObject Updates(string did)
        => new() { ["updates"] = new JsonArray(new JsonObject { ["recipient_did"] = did, ["action"] = "add" }) };

    private static Message Raw(string type, TestPeer from, string to, JsonObject body, long createdTime)
        => new MessageBuilder().WithType(type).WithFrom(from.Did).WithTo(to).WithBody(body)
            .WithCreatedTime(createdTime).WithReturnRoute(ReturnRouteMode.All).Build();

    private static async Task<string> AuthcryptAsync(TestPeer from, Message message, string to)
        => (await from.Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { to }, From: from.Did, Forward: false))).Message;

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(MediatorTestServer mediator, string packed)
    {
        using var client = mediator.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, mediator.Options.Pattern)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(packed)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(MediaTypes.Encrypted);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // The forward is answered 202 before the handler decides; wait for the one outcome it records.
    private static Task WaitForForwardOutcomeAsync(MediatorTestServer mediator)
        => MediatorTestServer.WaitUntilAsync(() => mediator.Measurements.Sum("didcomm.forward.outcomes") >= 1, "the forward's outcome is recorded");

    private sealed class D1CountingResolver(IDidResolver inner, ConcurrentQueue<string> calls) : IDidResolver
    {
        public static void Decorate(IServiceCollection services, ConcurrentQueue<string> calls)
        {
            var descriptor = services.Last(d => d.ServiceType == typeof(IDidResolver));
            services.Remove(descriptor);
            services.AddSingleton<IDidResolver>(sp =>
            {
                var original = descriptor.ImplementationInstance as IDidResolver
                    ?? (descriptor.ImplementationFactory is { } factory
                        ? (IDidResolver)factory(sp)
                        : (IDidResolver)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
                return new D1CountingResolver(original, calls);
            });
        }

        public Task<DidResolutionResult> ResolveAsync(string did, DidResolutionOptions? options = null, CancellationToken ct = default)
        {
            calls.Enqueue(did);
            return inner.ResolveAsync(did, options, ct);
        }

        public bool CanResolve(string did)
        {
            calls.Enqueue(did);
            return inner.CanResolve(did);
        }
    }
}
