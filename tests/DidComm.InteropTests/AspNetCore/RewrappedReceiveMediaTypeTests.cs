using System.Net;
using AwesomeAssertions;
using DidComm.AspNetCore;
using DidComm.InteropTests.Mediator;
using DidComm.Messages;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlainPath = DidComm.InteropTests.AspNetCore.RewrappedReceiveTests.PlainPath;

namespace DidComm.InteropTests.AspNetCore;

/// <summary>
/// #110, O-3: a message opened out of forward layers re-wrapped for this agent must be in an
/// envelope the endpoint accepts (<see cref="DidCommReceiveOptions.AcceptedMediaTypes"/>), as it
/// would have to be had it arrived on its own. The wrapper is anoncrypted, so its media type alone
/// let a plaintext or signed-only message past an encrypted-only host. The media type of the opened
/// message's own outermost envelope is checked; one that is not accepted is refused like a layer
/// that cannot be opened (the uniform 400 over HTTP, a discard on a WebSocket). On every plain receive
/// path, and on the mediator's endpoints for a message opened as the mediator's own.
/// </summary>
public sealed class RewrappedReceiveMediaTypeTests
{
    public enum Inner
    {
        Encrypted,
        Signed,
        Plain,
    }

    public enum Accepting
    {
        EncryptedOnly,
        Default,
    }

    public static TheoryData<PlainPath, Inner, Accepting> PathsInnersAndHosts()
    {
        var data = new TheoryData<PlainPath, Inner, Accepting>();
        foreach (var path in Enum.GetValues<PlainPath>())
        {
            foreach (var inner in Enum.GetValues<Inner>())
            {
                foreach (var accepting in Enum.GetValues<Accepting>())
                    data.Add(path, inner, accepting);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(PathsInnersAndHosts))]
    public async Task On_every_plain_path_the_message_inside_must_be_in_an_envelope_the_endpoint_accepts(PlainPath path, Inner inner, Accepting accepting)
    {
        await using var r = await PlainReceivers.StartAsync(o =>
        {
            if (accepting == Accepting.EncryptedOnly)
                o.PostConfigureServices = s => s.Configure<DidCommReceiveOptions>(x => x.AcceptedMediaTypes = new[] { DidCommMediaTypes.Encrypted });
        });
        var host = r.Host;
        var message = host.RecordingMessage(host.Alice).Build();
        var packed = inner switch
        {
            Inner.Encrypted => await host.AuthcryptAsync(host.Alice, message),
            Inner.Signed => (await host.PackAsync(host.Alice, message, EnvelopeShape.SignedOnly)).Packed,
            _ => (await host.PackAsync(host.Alice, message, EnvelopeShape.Plaintext)).Packed,
        };

        var delivery = await r.DeliverAsync(path, await r.RewrapAsync(packed, layers: 1));

        if (accepting == Accepting.Default || inner == Inner.Encrypted)
        {
            delivery.AssertAccepted(path);
            delivery.Seen.Should().ContainSingle().Which.Unpacked.Message.Id.Should().Be(message.Id);
        }
        else
        {
            delivery.AssertRefused(path, host, "MalformedMessageException");
        }
        host.LeakingLogLines().Should().BeEmpty();
    }

    public enum Transport
    {
        Http,
        WebSocket,
    }

    public static TheoryData<Transport, Inner, Accepting> TransportsInnersAndHosts()
    {
        var data = new TheoryData<Transport, Inner, Accepting>();
        foreach (var transport in Enum.GetValues<Transport>())
        {
            foreach (var inner in Enum.GetValues<Inner>())
            {
                foreach (var accepting in Enum.GetValues<Accepting>())
                    data.Add(transport, inner, accepting);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(TransportsInnersAndHosts))]
    public async Task On_the_mediators_endpoints_a_message_opened_as_the_mediators_own_must_be_in_an_envelope_it_accepts(Transport transport, Inner inner, Accepting accepting)
    {
        await using var mediator = await MediatorTestServer.StartAsync(o =>
        {
            if (accepting == Accepting.EncryptedOnly)
                o.PostConfigureServices = s => s.Configure<DidCommReceiveOptions>(x => x.AcceptedMediaTypes = new[] { DidCommMediaTypes.Encrypted });
        });
        var alice = await mediator.NewSenderAsync();
        var note = alice.Note(mediator.Did);
        var packed = inner switch
        {
            Inner.Encrypted => await alice.AuthcryptAsync(note, mediator.Did),
            Inner.Signed => await alice.Client.PackSignedAsync(note, alice.Did),
            _ => await alice.Client.PackPlaintextAsync(note),
        };
        var (wrapped, _) = await alice.ForwardAsync(mediator.Did, mediator.Did, packed);
        var accepted = accepting == Accepting.Default || inner == Inner.Encrypted;
        bool Dispatched() => mediator.Logs.Entries.Any(e => e.Contains("No protocol handler registered", StringComparison.Ordinal) && e.Contains(note.Id, StringComparison.Ordinal));

        if (transport == Transport.Http)
        {
            var response = await mediator.PostAsync(wrapped);
            response.Status.Should().Be(accepted ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest);
            response.Body.Should().BeEmpty();
        }
        else
        {
            using var socket = await mediator.ConnectAsync();
            await ReturnRouteTestHost.SendAsync(socket, wrapped);
            // Either event ends the wait: the note reached the dispatcher, or the endpoint discarded it.
            await MediatorTestServer.WaitUntilAsync(
                () => Dispatched() || mediator.Logs.Entries.Any(e => e.StartsWith("MapDidCommWebSocket: discarding", StringComparison.Ordinal)),
                "the mediator handled the delivery");
        }

        Dispatched().Should().Be(accepted, "the note inside reaches the mediator's dispatcher only in an envelope the endpoint accepts");
        if (!accepted)
        {
            var refusal = transport == Transport.Http ? "DidComm receive: rejecting inbound envelope (MalformedMessageException" : "MapDidCommWebSocket: discarding malformed envelope (MalformedMessageException)";
            mediator.Logs.Entries.Should().ContainSingle(e => e.StartsWith(refusal, StringComparison.Ordinal));
        }
        mediator.Outcomes.Should().BeEmpty("the wrapper was opened, not relayed or dropped as a forward");
        mediator.LeakingLogLines().Should().BeEmpty();
    }
}
