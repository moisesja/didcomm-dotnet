using Microsoft.Extensions.Logging;

namespace DidComm.Protocols.Routing;

/// <summary>
/// The default <see cref="IForwardSink"/>: accepts every forward and drops it, logging the message
/// id at Information. Dropping is its documented contract, not a fault — a complete mediator uses
/// <c>AddMediator()</c> (<c>DidComm.Mediator</c>), which registers its own sink, and a host with a
/// sink of its own registers it before <c>AddRoutingMediator</c>.
/// </summary>
public sealed class NullForwardSink : IForwardSink
{
    private readonly ILogger<NullForwardSink>? _logger;

    /// <summary>Create the sink.</summary>
    /// <param name="logger">Optional logger for the per-forward drop line.</param>
    public NullForwardSink(ILogger<NullForwardSink>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public Task AcceptAsync(ForwardProcessingResult result, ProtocolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        _logger?.LogInformation(
            "Forward {MessageId} accepted by NullForwardSink and dropped; use AddMediator() (DidComm.Mediator), or register an IForwardSink, to relay.",
            context.Received.Message.Id);
        return Task.CompletedTask;
    }
}
