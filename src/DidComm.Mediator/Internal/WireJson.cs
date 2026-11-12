using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Exceptions;
using DidComm.Messages;
using DidComm.Protocols;

namespace DidComm.Mediator.Internal;

/// <summary>
/// JSON reading shared by the mediator-role body codecs (Coordinate Mediation, Message Pickup): the
/// body as one strict <see cref="JsonElement"/>, an integer decided from the number's own text, and
/// the message-type test. Every failure is a <see cref="MalformedMessageException"/> that names the
/// member and the rule and never quotes a wire value.
/// </summary>
internal static class WireJson
{
    /// <summary>
    /// The body as a <see cref="JsonElement"/> so every reader uses one strict API regardless of
    /// how the <see cref="JsonObject"/> was built (parsed off the wire or assembled in code).
    /// <c>default</c> (<see cref="JsonValueKind.Undefined"/>) when the message has no body.
    /// </summary>
    public static JsonElement ToElement(JsonObject? body)
    {
        if (body is null)
            return default;
        try
        {
            return JsonSerializer.SerializeToElement(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // A body a host assembled or parsed lazily can hold what cannot be written back out (a
            // cycle, or an escape such as an unpaired surrogate that only fails when it is read).
            // The receive path refuses such text before it gets here; this is the same answer for a
            // message a host built by hand. The cause is not attached: its text may quote the value.
            throw new MalformedMessageException("The message 'body' is not representable as JSON.");
        }
    }

    /// <summary>
    /// Whether <paramref name="message"/> is of type <paramref name="expected"/>, matched as the
    /// handler registry matches (FR-PROTO-01/02): ignoring case and <c>-</c>/<c>_</c>, any minor
    /// version of the same major.
    /// </summary>
    public static bool IsType(Message message, MessageTypeUri expected)
    {
        ArgumentNullException.ThrowIfNull(message);
        return MessageTypeUri.TryParse(message.Type, out var actual) && actual is not null && expected.Matches(actual);
    }

    /// <summary>
    /// Read the REQUIRED integer member <paramref name="name"/> of <paramref name="parent"/>,
    /// clamped to <see cref="int"/>. A JSON integer is decided from the number's own text so that
    /// neither size nor precision can change the answer: a string ("10") or a number with a
    /// non-zero fractional part (1.5, 1e-3) is malformed; JSON does not distinguish 10 from 10.0 or
    /// 1e1, so those read as 10; an integer beyond the 32-bit range, however large, is clamped.
    /// </summary>
    /// <param name="parent">The object holding the member.</param>
    /// <param name="name">The member name.</param>
    /// <param name="path">How the error text names the member (for example <c>'paginate.limit'</c> without the quotes).</param>
    /// <param name="container">How the error text names what lacks the member (for example <c>'paginate'</c> or <c>body</c>).</param>
    /// <param name="message">The message name for the error text.</param>
    public static int ReadRequiredInteger(JsonElement parent, string name, string path, string container, string message)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            throw new MalformedMessageException($"{message} {container} is missing the REQUIRED '{name}' member.");
        return ReadInteger(value, path, message);
    }

    /// <summary>Read <paramref name="value"/> as an integer clamped to <see cref="int"/>; the rules of <see cref="ReadRequiredInteger"/>.</summary>
    public static int ReadInteger(JsonElement value, string path, string message)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw new MalformedMessageException($"{message} '{path}' must be an integer.");
        if (value.TryGetInt64(out var wide))
            return (int)Math.Clamp(wide, int.MinValue, int.MaxValue);
        if (TryReadIntegerText(value.GetRawText(), out var clamped))
            return clamped;
        throw new MalformedMessageException($"{message} '{path}' must be an integer.");
    }

    /// <summary>Read <paramref name="value"/> as an integer clamped to <see cref="long"/>; the rules of <see cref="ReadRequiredInteger"/> with the wider range.</summary>
    public static long ReadLong(JsonElement value, string path, string message)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw new MalformedMessageException($"{message} '{path}' must be an integer.");
        if (value.TryGetInt64(out var wide))
            return wide;
        if (TryReadIntegerText(value.GetRawText(), long.MaxValue, out var clamped))
            return clamped;
        throw new MalformedMessageException($"{message} '{path}' must be an integer.");
    }

    /// <summary>
    /// Read a JSON number literal (<c>-?digits[.digits][(e|E)[+-]digits]</c>) as an integer clamped
    /// to <see cref="int"/>. <c>false</c> when the number has a non-zero fractional part. One pass
    /// over the text, no big-number arithmetic: the digits are never multiplied out.
    /// </summary>
    internal static bool TryReadIntegerText(ReadOnlySpan<char> text, out int value)
    {
        var read = TryReadIntegerText(text, int.MaxValue, out var wide);
        value = (int)wide;
        return read;
    }

    /// <summary>
    /// <see cref="TryReadIntegerText(ReadOnlySpan{char}, out int)"/> for any symmetric range: the
    /// result is clamped to [-<paramref name="limit"/> - 1, <paramref name="limit"/>].
    /// </summary>
    internal static bool TryReadIntegerText(ReadOnlySpan<char> text, long limit, out long value)
    {
        value = 0;
        var negative = text.Length > 0 && text[0] == '-';
        if (negative)
            text = text[1..];

        var exponentAt = text.IndexOfAny('e', 'E');
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var pointAt = mantissa.IndexOf('.');
        var whole = pointAt < 0 ? mantissa : mantissa[..pointAt];
        var fraction = pointAt < 0 ? default : mantissa[(pointAt + 1)..];
        if (whole.IsEmpty)
            return false;

        // The exponent, saturated: beyond this many places every digit of a real message has moved.
        const int saturation = 100_000_000;
        var exponent = 0;
        if (exponentAt >= 0)
        {
            var digits = text[(exponentAt + 1)..];
            var exponentNegative = digits.Length > 0 && digits[0] == '-';
            if (digits.Length > 0 && digits[0] is '-' or '+')
                digits = digits[1..];
            if (digits.IsEmpty)
                return false;
            foreach (var c in digits)
            {
                if (c is < '0' or > '9')
                    return false;
                exponent = Math.Min(saturation, exponent * 10 + (c - '0'));
            }
            if (exponentNegative)
                exponent = -exponent;
        }

        // Digit i of whole+fraction sits left of the decimal point when i < pointPosition (an
        // integer digit) and right of it otherwise (a fractional digit, which must be zero).
        var pointPosition = (long)whole.Length + exponent;
        long magnitude = 0;
        var index = 0L;
        var overflow = false;
        if (!Accumulate(whole, pointPosition, limit, ref index, ref magnitude, ref overflow)
            || !Accumulate(fraction, pointPosition, limit, ref index, ref magnitude, ref overflow))
        {
            return false;
        }

        // Integer places the text does not spell out (1e40): zeros appended to a non-zero value.
        if (!overflow && magnitude != 0)
        {
            for (var pad = index; pad < pointPosition && !overflow; pad++)
            {
                if (magnitude > limit / 10)
                    overflow = true;
                else
                    magnitude *= 10;
            }
        }

        value = overflow
            ? (negative ? -limit - 1 : limit)
            : (negative ? -magnitude : magnitude);
        return true;
    }

    // Feeds one run of digits into the running integer. False for a non-digit or for a non-zero
    // digit right of the decimal point. The magnitude never passes the limit: the step that would
    // take it there sets the overflow flag instead, so nothing here can wrap.
    private static bool Accumulate(ReadOnlySpan<char> digits, long pointPosition, long limit, ref long index, ref long magnitude, ref bool overflow)
    {
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
                return false;
            var digit = c - '0';
            if (index >= pointPosition)
            {
                if (digit != 0)
                    return false;
            }
            else if (!overflow)
            {
                if (magnitude > (limit - digit) / 10)
                    overflow = true;
                else
                    magnitude = magnitude * 10 + digit;
            }
            index++;
        }
        return true;
    }
}
