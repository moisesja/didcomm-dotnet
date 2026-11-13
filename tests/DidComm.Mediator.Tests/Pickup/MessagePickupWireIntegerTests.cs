using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupWireSupport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The 64-bit integer reader that #94 added to the shared JSON readers
/// (<c>WireJson.TryReadIntegerText(text, limit, out long)</c> and <c>WireJson.ReadLong</c>), which
/// the <c>status</c> members <c>longest_waited_seconds</c>, <c>newest_received_time</c>,
/// <c>oldest_received_time</c> and <c>total_bytes</c> are read with. The rules are those of the
/// 32-bit reader (#93's tests cover that one and are not repeated here): a JSON number is an
/// integer when its fractional part is zero, however it is spelt; an integer beyond the range is
/// clamped, however large; nothing is multiplied out.
/// </summary>
public sealed class MessagePickupWireIntegerTests
{
    // ---- the text reader, 64 bits wide ----------------------------------------------------

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-0", 0L)]
    [InlineData("7", 7L)]
    [InlineData("-7", -7L)]
    [InlineData("2147483647", 2147483647L)]
    [InlineData("2147483648", 2147483648L)]                         // past 32 bits: not clamped
    [InlineData("-2147483649", -2147483649L)]
    [InlineData("1658085169123", 1658085169123L)]
    [InlineData("9223372036854775806", 9223372036854775806L)]
    [InlineData("9223372036854775807", long.MaxValue)]              // the last value that is itself
    [InlineData("9223372036854775808", long.MaxValue)]              // one more: clamped
    [InlineData("9223372036854775809", long.MaxValue)]
    [InlineData("18446744073709551615", long.MaxValue)]             // UInt64.MaxValue
    [InlineData("-9223372036854775807", -9223372036854775807L)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("-9223372036854775809", long.MinValue)]
    [InlineData("123456789012345678901234567890", long.MaxValue)]   // beyond System.Decimal
    [InlineData("1.0", 1L)]
    [InlineData("1.000000000000000000000000000000", 1L)]
    [InlineData("0.0", 0L)]
    [InlineData("-0.0", 0L)]
    [InlineData("12345678901234567890123.0", long.MaxValue)]
    [InlineData("1E3", 1000L)]
    [InlineData("1e+3", 1000L)]
    [InlineData("1e03", 1000L)]
    [InlineData("1e0", 1L)]
    [InlineData("1e-0", 1L)]
    [InlineData("1000e-3", 1L)]
    [InlineData("1.5e1", 15L)]
    [InlineData("2500e-2", 25L)]
    [InlineData("123.456e3", 123456L)]
    [InlineData("0.000001e6", 1L)]
    [InlineData("0.0050e3", 5L)]
    [InlineData("1e18", 1000000000000000000L)]
    [InlineData("9e18", 9000000000000000000L)]
    [InlineData("1e19", long.MaxValue)]
    [InlineData("-1e19", long.MinValue)]
    [InlineData("9.223372036854775806e18", 9223372036854775806L)]
    [InlineData("9.223372036854775807e18", long.MaxValue)]
    [InlineData("9.223372036854775808e18", long.MaxValue)]
    [InlineData("-9.223372036854775808e18", long.MinValue)]
    [InlineData("-9.223372036854775809e18", long.MinValue)]
    [InlineData("92233720368547758070e-1", long.MaxValue)]          // twenty digits, nineteen of them integer
    [InlineData("922337203685477580700e-2", long.MaxValue)]
    [InlineData("1e400", long.MaxValue)]                            // beyond System.Double
    [InlineData("1.5e400", long.MaxValue)]                          // 15 followed by 399 zeros: an integer
    [InlineData("-1e400", long.MinValue)]
    [InlineData("0e400", 0L)]
    [InlineData("0.000e5", 0L)]
    [InlineData("0e999999999999999999999", 0L)]                     // an exponent no integer type holds
    [InlineData("0e-999999999999999999999", 0L)]
    [InlineData("1e99999999999999999999", long.MaxValue)]
    [InlineData("-1e99999999999999999999", long.MinValue)]
    public void A_number_literal_with_a_zero_fraction_is_read_and_clamped_to_64_bits(string text, long expected)
    {
        WireJson.TryReadIntegerText(text, long.MaxValue, out var value).Should().BeTrue(text);
        value.Should().Be(expected, text);
    }

    [Theory]
    // a non-zero fractional part
    [InlineData("1.5")]
    [InlineData("0.5")]
    [InlineData("-0.5")]
    [InlineData("1e-1")]
    [InlineData("15e-1")]
    [InlineData("1000e-4")]
    [InlineData("0.000001e5")]
    [InlineData("1e-400")]
    [InlineData("1e-99999999999999999999")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("9223372036854775807.5")]
    [InlineData("92233720368547758075e-1")]
    [InlineData("123456789012345678901234567890.5")]               // too large and fractional: fractional wins
    // not a JSON number at all (the reader is only ever handed number text; it must still not misread)
    [InlineData("")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData(".5")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("1e-")]
    [InlineData("e5")]
    [InlineData("+1")]
    [InlineData("1x")]
    [InlineData("1.x")]
    [InlineData("1ex")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1,000")]
    [InlineData("--1")]
    public void A_fraction_or_text_that_is_not_a_number_is_not_an_integer(string text)
    {
        WireJson.TryReadIntegerText(text, long.MaxValue, out var value).Should().BeFalse(text);
        value.Should().Be(0, "nothing is handed back for a refusal");
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("99.0", 99L)]
    [InlineData("100", 100L)]
    [InlineData("101", 100L)]
    [InlineData("1e2", 100L)]
    [InlineData("1e3", 100L)]
    [InlineData("-100", -100L)]
    [InlineData("-101", -101L)]
    [InlineData("-102", -101L)]
    [InlineData("-1e400", -101L)]
    public void The_result_is_clamped_to_the_symmetric_range_of_whatever_limit_is_given(string text, long expected)
    {
        // [-limit - 1, limit]: the shape of a two's-complement range.
        WireJson.TryReadIntegerText(text, 100, out var value).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Fact]
    public void A_thousand_digit_integer_is_clamped_and_a_thousand_digit_fraction_is_refused()
    {
        var digits = new string('9', 1000);

        WireJson.TryReadIntegerText(digits, long.MaxValue, out var value).Should().BeTrue();
        value.Should().Be(long.MaxValue);
        WireJson.TryReadIntegerText("-" + digits, long.MaxValue, out value).Should().BeTrue();
        value.Should().Be(long.MinValue);
        WireJson.TryReadIntegerText(digits + "." + new string('0', 1000), long.MaxValue, out value).Should().BeTrue();
        value.Should().Be(long.MaxValue);
        WireJson.TryReadIntegerText("0." + new string('0', 999) + "1e1000", long.MaxValue, out value).Should().BeTrue("the exponent moves the one digit left of the point");
        value.Should().Be(1);

        WireJson.TryReadIntegerText(digits + "." + new string('0', 999) + "1", long.MaxValue, out _).Should().BeFalse();
        WireJson.TryReadIntegerText("0." + digits, long.MaxValue, out _).Should().BeFalse();
        WireJson.TryReadIntegerText("0." + new string('0', 999) + "1e999", long.MaxValue, out _).Should().BeFalse("one place short");
    }

    // ---- against arbitrary-precision arithmetic ---------------------------------------------

    [Fact]
    public void The_reader_agrees_with_arbitrary_precision_arithmetic_on_generated_literals()
    {
        // The product never multiplies the digits out; the oracle does nothing else. 5000 literals
        // from a fixed seed: signs, leading zeros in the fraction, trailing zeros, exponents of
        // both signs, values around every clamp.
        var checkedLiterals = 0;
        var integers = 0;
        foreach (var literal in Literals(5000, seed: 94).Concat(Boundaries()))
        {
            foreach (var limit in new[] { long.MaxValue, int.MaxValue, 1000L })
            {
                var expected = Expected(literal, limit);

                var read = WireJson.TryReadIntegerText(literal, limit, out var value);

                read.Should().Be(expected is not null, "literal {0}, limit {1}", literal, limit);
                if (expected is { } integer)
                {
                    value.Should().Be(integer, "literal {0}, limit {1}", literal, limit);
                    integers++;
                }
            }

            // The 32-bit entry point is the 64-bit one with the 32-bit limit, and so is the 64-bit
            // answer clamped.
            var narrowRead = WireJson.TryReadIntegerText(literal, out int narrow);
            var wideRead = WireJson.TryReadIntegerText(literal, long.MaxValue, out var wide);
            narrowRead.Should().Be(wideRead, "literal {0}", literal);
            if (wideRead)
                narrow.Should().Be((int)Math.Clamp(wide, int.MinValue, int.MaxValue), "literal {0}", literal);
            checkedLiterals++;
        }

        checkedLiterals.Should().BeGreaterThan(5000);
        integers.Should().BeGreaterThan(3000, "the generator must not produce fractions only");
    }

    [Fact]
    public void The_parsers_agree_with_arbitrary_precision_arithmetic_on_generated_literals()
    {
        // The same oracle one level up: `limit` (clamped to 32 bits, never negative) and
        // `total_bytes` (clamped to 64 bits, never negative), through the public parsers. Whatever
        // the literal, the only exception is MalformedMessageException.
        foreach (var literal in Literals(600, seed: 9400).Concat(Boundaries()))
        {
            var deliveryRequest = Raw(P + "/delivery-request", $$"""{"limit":{{literal}}}""");
            if (Expected(literal, int.MaxValue) is { } limit and >= 0)
            {
                MessagePickup.TryParseDeliveryRequest(deliveryRequest, out var parsedLimit, out _).Should().BeTrue(literal);
                parsedLimit.Should().Be((int)limit, literal);
            }
            else
            {
                Refused(() => MessagePickup.TryParseDeliveryRequest(deliveryRequest, out _, out _)).Message.Should().Contain("'limit'");
            }

            var status = Raw(P + "/status", $$"""{"message_count":1,"total_bytes":{{literal}}}""");
            if (Expected(literal, long.MaxValue) is { } bytes and >= 0)
            {
                MessagePickup.TryParseStatus(status, out var parsedStatus).Should().BeTrue(literal);
                parsedStatus!.TotalBytes.Should().Be(bytes, literal);
            }
            else
            {
                Refused(() => MessagePickup.TryParseStatus(status, out _)).Message.Should().Contain("'total_bytes'");
            }
        }
    }

    [Fact]
    public void The_oracle_itself_is_right_about_the_values_everyone_knows()
    {
        Expected("0", long.MaxValue).Should().Be(0);
        Expected("-0.0", long.MaxValue).Should().Be(0);
        Expected("10.0", long.MaxValue).Should().Be(10);
        Expected("1e1", long.MaxValue).Should().Be(10);
        Expected("2500e-2", long.MaxValue).Should().Be(25);
        Expected("0.0050e3", long.MaxValue).Should().Be(5);
        Expected("1.5", long.MaxValue).Should().BeNull();
        Expected("1e-3", long.MaxValue).Should().BeNull();
        Expected("9223372036854775807", long.MaxValue).Should().Be(long.MaxValue);
        Expected("9223372036854775808", long.MaxValue).Should().Be(long.MaxValue);
        Expected("-9223372036854775808", long.MaxValue).Should().Be(long.MinValue);
        Expected("-9223372036854775809", long.MaxValue).Should().Be(long.MinValue);
        Expected("2147483648", int.MaxValue).Should().Be(int.MaxValue);
        Expected("-2147483649", int.MaxValue).Should().Be(int.MinValue);
        Expected("1e400", long.MaxValue).Should().Be(long.MaxValue);
        Expected("-1e400", 1000).Should().Be(-1001);
    }

    // ---- ReadLong, over a JsonElement -------------------------------------------------------

    [Theory]
    [InlineData("5", 5L)]
    [InlineData("-5", -5L)]
    [InlineData("5.0", 5L)]
    [InlineData("5e0", 5L)]
    [InlineData("50e-1", 5L)]
    [InlineData("2147483648", 2147483648L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("9223372036854775807.0", long.MaxValue)]
    [InlineData("9223372036854775808", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("-9223372036854775809", long.MinValue)]
    [InlineData("1e400", long.MaxValue)]
    [InlineData("-1e400", long.MinValue)]
    public void ReadLong_reads_a_json_number_as_a_clamped_64_bit_integer(string json, long expected)
    {
        using var document = JsonDocument.Parse(json);

        WireJson.ReadLong(document.RootElement, "some.member", "some-message").Should().Be(expected);

        // The 32-bit reader gives the same answer, clamped further.
        WireJson.ReadInteger(document.RootElement, "some.member", "some-message").Should().Be((int)Math.Clamp(expected, int.MinValue, int.MaxValue));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1e-3")]
    [InlineData("-0.5")]
    [InlineData("9223372036854775807.5")]
    [InlineData("\"5\"")]
    [InlineData("\"SENTINEL\"")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("[5]")]
    [InlineData("{\"value\":5}")]
    public void ReadLong_refuses_what_is_not_an_integer_naming_the_member_and_nothing_else(string json)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement;

        Refused(() => WireJson.ReadLong(element, "some.member", "some-message"))
            .Message.Should().Be("some-message 'some.member' must be an integer.");
        Refused(() => WireJson.ReadInteger(element, "some.member", "some-message"))
            .Message.Should().Be("some-message 'some.member' must be an integer.");
    }

    // ---- the generator and the oracle -------------------------------------------------------

    /// <summary>
    /// The integer a JSON number literal denotes, clamped to [-limit - 1, limit]; <c>null</c> when
    /// the number has a non-zero fractional part. Arbitrary-precision arithmetic, nothing shared
    /// with the product.
    /// </summary>
    private static long? Expected(string literal, long limit)
    {
        var text = literal.AsSpan();
        var negative = text[0] == '-';
        if (negative)
            text = text[1..];

        var exponentAt = text.IndexOfAny('e', 'E');
        var exponent = exponentAt < 0 ? BigInteger.Zero : BigInteger.Parse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var pointAt = mantissa.IndexOf('.');
        var whole = pointAt < 0 ? mantissa : mantissa[..pointAt];
        var fraction = pointAt < 0 ? ReadOnlySpan<char>.Empty : mantissa[(pointAt + 1)..];

        // The value is digits × 10^shift.
        var digits = string.Concat(whole, fraction).TrimStart('0');
        if (digits.Length == 0)
            return 0;
        var shift = exponent - fraction.Length;

        BigInteger magnitude;
        if (shift >= 0)
        {
            // Forty places past the last digit is beyond any limit a long can express.
            magnitude = shift > 40 ? BigInteger.Pow(10, 60) : BigInteger.Parse(digits, CultureInfo.InvariantCulture) * BigInteger.Pow(10, (int)shift);
        }
        else
        {
            var drop = -shift;
            if (drop >= digits.Length)
                return null; // strictly between 0 and 1
            var keep = digits.Length - (int)drop;
            if (digits.AsSpan(keep).IndexOfAnyExcept('0') >= 0)
                return null;
            magnitude = BigInteger.Parse(digits.AsSpan(0, keep), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (negative)
            return magnitude > (BigInteger)limit + 1 ? -limit - 1 : (long)-magnitude;
        return magnitude > limit ? limit : (long)magnitude;
    }

    /// <summary>Valid JSON number literals from a fixed seed.</summary>
    private static IEnumerable<string> Literals(int count, int seed)
    {
        var random = new Random(seed);
        char Digit() => random.Next(3) == 0 ? '0' : (char)('0' + random.Next(10));

        for (var i = 0; i < count; i++)
        {
            var text = new StringBuilder();
            if (random.Next(4) == 0)
                text.Append('-');

            var wholeLength = random.Next(5) == 0 ? 0 : random.Next(1, 24);
            if (wholeLength == 0)
            {
                text.Append('0');
            }
            else
            {
                text.Append((char)('1' + random.Next(9)));
                for (var j = 1; j < wholeLength; j++)
                    text.Append(Digit());
            }

            if (random.Next(2) == 0)
            {
                text.Append('.');
                var fractionLength = random.Next(1, 24);
                var shape = random.Next(3); // all zeros | anything | digits, then zeros
                for (var j = 0; j < fractionLength; j++)
                    text.Append(shape == 0 || (shape == 2 && j >= fractionLength / 2) ? '0' : Digit());
            }

            if (random.Next(2) == 0)
            {
                text.Append(random.Next(2) == 0 ? 'e' : 'E');
                switch (random.Next(3))
                {
                    case 0:
                        text.Append('-');
                        break;
                    case 1:
                        text.Append('+');
                        break;
                }

                text.Append(random.Next(20) == 0 ? "400" : random.Next(0, 31).ToString(CultureInfo.InvariantCulture));
            }

            yield return text.ToString();
        }
    }

    /// <summary>The values on either side of every clamp, in several spellings.</summary>
    private static IEnumerable<string> Boundaries()
    {
        foreach (var edge in new[] { "999", "1000", "1001", "2147483647", "2147483648", "2147483649", "9223372036854775807", "9223372036854775808", "9223372036854775809" })
        {
            foreach (var sign in new[] { string.Empty, "-" })
            {
                yield return sign + edge;
                yield return sign + edge + ".0";
                yield return sign + edge + ".5";
                yield return sign + edge + "0e-1";
                yield return sign + edge + "5e-1";
                yield return sign + edge[..1] + "." + edge[1..] + "e" + (edge.Length - 1).ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
