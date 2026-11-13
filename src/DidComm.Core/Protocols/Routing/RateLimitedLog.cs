using Microsoft.Extensions.Logging;

namespace DidComm.Protocols.Routing;

/// <summary>
/// The "first and every 1000th" rule for a log line any stranger can cause once per message: the
/// first occurrence and every <see cref="Every"/>-th are written at the line's own level, the rest at
/// <see cref="LogLevel.Debug"/>, with the same text. Core's copy of the mediator's rule
/// (<c>MediatorLog.RateLimited</c> in <c>DidComm.Mediator</c>, which Core cannot reference). Thread-safe.
/// </summary>
internal static class RateLimitedLog
{
    /// <summary>A rate-limited line is written at its own level the first time and every this-many-th time.</summary>
    internal const long Every = 1000;

    /// <summary>
    /// Count one more occurrence on <paramref name="counter"/> and say at which level to write it:
    /// <paramref name="loud"/> for the first and every <see cref="Every"/>-th, Debug otherwise.
    /// </summary>
    /// <param name="counter">The line's own occurrence counter.</param>
    /// <param name="loud">The level the line is written at when it is not quietened.</param>
    /// <param name="count">The occurrence number, counting this one.</param>
    internal static LogLevel Level(ref long counter, LogLevel loud, out long count)
    {
        count = Interlocked.Increment(ref counter);
        return count == 1 || count % Every == 0 ? loud : LogLevel.Debug;
    }
}
