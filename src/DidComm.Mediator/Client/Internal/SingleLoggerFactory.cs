using Microsoft.Extensions.Logging;

namespace DidComm.Mediator.Client.Internal;

/// <summary>
/// The logger factory of a client built without dependency injection: it hands the one logger the
/// caller passed to the facade's constructor to every category, so the private channel's lines
/// (dropped answers, timeouts, problem reports) reach the same place as the facade's own instead of
/// being lost. It owns nothing: disposing it leaves the logger alone.
/// </summary>
internal sealed class SingleLoggerFactory : ILoggerFactory
{
    private readonly ILogger _logger;

    private SingleLoggerFactory(ILogger logger) => _logger = logger;

    /// <summary>A factory over <paramref name="logger"/>, or <c>null</c> (no logging) for none.</summary>
    /// <param name="logger">The caller's logger, or <c>null</c>.</param>
    internal static ILoggerFactory? For(ILogger? logger) => logger is null ? null : new SingleLoggerFactory(logger);

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => _logger;

    /// <summary>Not supported: the factory hands out the caller's logger only.</summary>
    /// <param name="provider">Ignored.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void AddProvider(ILoggerProvider provider)
        => throw new NotSupportedException("This factory hands out the logger passed to the client's constructor; it takes no providers.");

    /// <summary>Nothing to release: the logger belongs to the caller.</summary>
    public void Dispose()
    {
    }
}
