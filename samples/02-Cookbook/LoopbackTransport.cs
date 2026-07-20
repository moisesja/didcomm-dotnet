using DidComm.Facade;
using DidComm.Protocols;
using DidComm.Transports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DidComm.Samples.Cookbook;

/// <summary>
/// An in-process transport so cookbook sections can call <c>SendAsync</c> / <c>QueryFeaturesAsync</c>
/// without a real network. It plays "the other agent": it unpacks the message you send, runs it
/// through the same dispatcher (so the built-in handler answers), and delivers any reply back into
/// the dispatcher — which is what completes an initiator awaiting a response. In a real app this is
/// your HTTP transport and the reply arrives at your own receive endpoint.
/// </summary>
internal sealed class LoopbackTransport(IServiceProvider services) : IDidCommTransport
{
    public string Scheme => "loopback";

    public bool CanHandle(Uri endpoint) => string.Equals(endpoint.Scheme, Scheme, StringComparison.OrdinalIgnoreCase);

    public async Task<TransportResult> SendAsync(TransportRequest request, CancellationToken ct)
    {
        // Resolve lazily: the client depends on this transport, so we can't take it in the ctor.
        var client = services.GetRequiredService<DidCommClient>();
        var dispatcher = services.GetRequiredService<ProtocolDispatcher>();
        var options = services.GetRequiredService<IOptions<DidCommOptions>>().Value;

        var packed = System.Text.Encoding.UTF8.GetString(request.Payload.Span);
        var inbound = await client.UnpackAsync(packed, ct).ConfigureAwait(false);
        var outcome = await dispatcher.DispatchAsync(inbound, client, options, ct).ConfigureAwait(false);

        if (outcome is { Reply: { To.Count: > 0, From: not null } reply })
        {
            // Deliver the reply back the same way, so an awaiting initiator's observer sees it.
            var packedReply = (await client.PackEncryptedAsync(
                reply,
                new PackEncryptedOptions(Recipients: reply.To.ToArray(), From: reply.From),
                ct).ConfigureAwait(false)).Message;
            var replyInbound = await client.UnpackAsync(packedReply, ct).ConfigureAwait(false);
            await dispatcher.DispatchAsync(replyInbound, client, options, ct).ConfigureAwait(false);
        }

        return new TransportResult(Accepted: true, HttpStatusCode: 202);
    }
}
