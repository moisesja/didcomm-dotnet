using DidComm.Exceptions;
using DidComm.Transports.Stomp;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Transports.Stomp;

/// <summary>
/// FR-TRN-12 — strict STOMP 1.2 frame codec: round-trips (including the header-escaping edge
/// cases) and the malformed-frame negatives (missing NUL, lying content-length, header
/// injection via raw CR/LF, undefined escapes, trailing junk).
/// </summary>
public sealed class StompFrameCodecTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"protected":"eyJ..."}""");

    [Fact]
    public void Encode_Send_ProducesSpecWireShape()
    {
        var frame = new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", "/didcomm"),
            KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
        }, Body);

        var wire = StompFrameCodec.Encode(frame);
        var text = Encoding.UTF8.GetString(wire);

        text.Should().StartWith("SEND\ndestination:/didcomm\ncontent-type:application/didcomm-encrypted+json\n");
        text.Should().Contain($"content-length:{Body.Length}\n\n");
        wire[^1].Should().Be(0x00, "frames terminate with NUL");
    }

    [Fact]
    public void RoundTrip_Send_PreservesCommandHeadersAndBody()
    {
        var frame = new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", "/didcomm"),
            KeyValuePair.Create("content-type", "application/didcomm-encrypted+json"),
        }, Body);

        var decoded = StompFrameCodec.Decode(StompFrameCodec.Encode(frame));

        decoded.Command.Should().Be("SEND");
        decoded.TryGetHeader("destination", out var dest).Should().BeTrue();
        dest.Should().Be("/didcomm");
        decoded.TryGetHeader("content-type", out var mediaType).Should().BeTrue();
        mediaType.Should().Be("application/didcomm-encrypted+json");
        decoded.TryGetHeader("content-length", out var len).Should().BeTrue();
        len.Should().Be(Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        decoded.Body.ToArray().Should().Equal(Body);
    }

    [Theory]
    [InlineData("colon:in:value")]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("back\\slash")]
    [InlineData("all\\of:it\r\n:")]
    public void RoundTrip_HeaderEscaping_EdgeCases(string hostileValue)
    {
        // STOMP 1.2 escapes \r \n \c \\ in headers of non-CONNECT frames; the value must
        // survive byte-exact, and the escaping must prevent any header/frame injection.
        var frame = new StompFrame("SEND", new[]
        {
            KeyValuePair.Create("destination", "/d"),
            KeyValuePair.Create("x-hostile", hostileValue),
        }, ReadOnlyMemory<byte>.Empty);

        var decoded = StompFrameCodec.Decode(StompFrameCodec.Encode(frame));

        decoded.TryGetHeader("x-hostile", out var value).Should().BeTrue();
        value.Should().Be(hostileValue);
        // The injection didn't smuggle an extra header.
        decoded.Headers.Should().HaveCount(3); // destination, x-hostile, content-length
    }

    [Fact]
    public void RoundTrip_Connected_ControlFrame()
    {
        var frame = new StompFrame("CONNECTED", new[]
        {
            KeyValuePair.Create("version", "1.2"),
            KeyValuePair.Create("heart-beat", "0,0"),
        }, ReadOnlyMemory<byte>.Empty);

        var decoded = StompFrameCodec.Decode(StompFrameCodec.Encode(frame));

        decoded.Command.Should().Be("CONNECTED");
        decoded.TryGetHeader("version", out var v).Should().BeTrue();
        v.Should().Be("1.2");
        decoded.Body.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Connect_HeaderValues_AreNotEscaped_AndColonInValueSurvives()
    {
        // STOMP 1.2: CONNECT/CONNECTED do not use escaping; a colon in the VALUE is legal
        // because the parser splits on the first colon only (IPv6 hosts need this).
        var frame = new StompFrame("CONNECT", new[]
        {
            KeyValuePair.Create("accept-version", "1.2"),
            KeyValuePair.Create("host", "[::1]"),
        }, ReadOnlyMemory<byte>.Empty);

        var decoded = StompFrameCodec.Decode(StompFrameCodec.Encode(frame));

        decoded.TryGetHeader("host", out var host).Should().BeTrue();
        host.Should().Be("[::1]");
    }

    [Fact]
    public void Decode_ToleratesCrLfLineEndings_AndTrailingEols()
    {
        var wire = Encoding.UTF8.GetBytes("SEND\r\ndestination:/d\r\ncontent-length:2\r\n\r\nhi\0\r\n");

        var decoded = StompFrameCodec.Decode(wire);

        decoded.Command.Should().Be("SEND");
        Encoding.UTF8.GetString(decoded.Body.Span).Should().Be("hi");
    }

    [Fact]
    public void Decode_BodyWithNulByte_IsExact_WhenContentLengthGoverns()
    {
        var body = new byte[] { 0x01, 0x00, 0x02 }; // embedded NUL — only content-length can carry this
        var decoded = StompFrameCodec.Decode(StompFrameCodec.Encode(
            new StompFrame("SEND", new[] { KeyValuePair.Create("destination", "/d") }, body)));

        decoded.Body.ToArray().Should().Equal(body);
    }

    [Fact]
    public void Decode_WithoutContentLength_BodyRunsToFirstNul()
    {
        var wire = Encoding.UTF8.GetBytes("SEND\ndestination:/d\n\nhello\0");

        var decoded = StompFrameCodec.Decode(wire);

        Encoding.UTF8.GetString(decoded.Body.Span).Should().Be("hello");
    }

    // ---- Malformed-frame negatives ----

    [Fact]
    public void Decode_MissingNulTerminator_Rejected()
    {
        var wire = Encoding.UTF8.GetBytes("SEND\ndestination:/d\n\nbody-with-no-terminator");

        var act = () => StompFrameCodec.Decode(wire);

        act.Should().Throw<MalformedMessageException>().WithMessage("*NUL*");
    }

    [Theory]
    [InlineData("abc")]      // non-numeric
    [InlineData("-1")]       // sign not allowed (NumberStyles.None)
    [InlineData(" 2")]       // whitespace not allowed
    [InlineData("999")]      // exceeds the actual body
    public void Decode_BadContentLength_Rejected(string declared)
    {
        var wire = Encoding.UTF8.GetBytes($"SEND\ndestination:/d\ncontent-length:{declared}\n\nhi\0");

        var act = () => StompFrameCodec.Decode(wire);

        act.Should().Throw<MalformedMessageException>().WithMessage("*content-length*");
    }

    [Fact]
    public void Decode_ContentLengthNotLandingOnNul_Rejected()
    {
        // Declared 1 but the terminator sits after 2 bytes — a truncation/smuggling attempt.
        var wire = Encoding.UTF8.GetBytes("SEND\ndestination:/d\ncontent-length:1\n\nhi\0");

        var act = () => StompFrameCodec.Decode(wire);

        act.Should().Throw<MalformedMessageException>().WithMessage("*NUL-terminated*");
    }

    [Fact]
    public void Decode_TrailingBytesAfterNul_Rejected()
    {
        // A second smuggled frame in the same WebSocket message must not parse.
        var wire = Encoding.UTF8.GetBytes("SEND\ndestination:/d\ncontent-length:2\n\nhi\0SEND\n\n\0");

        var act = () => StompFrameCodec.Decode(wire);

        act.Should().Throw<MalformedMessageException>().WithMessage("*trailing*");
    }

    [Fact]
    public void Decode_RawCrInsideHeaderLine_Rejected()
    {
        // Header injection via a bare CR (not part of CRLF). Built by hand — Encode escapes it.
        var wire = Encoding.UTF8.GetBytes("SEND\ndest\rination:/d\ncontent-length:0\n\n\0");

        var act = () => StompFrameCodec.Decode(wire);

        act.Should().Throw<MalformedMessageException>().WithMessage("*raw CR*");
    }

    [Fact]
    public void Decode_UndefinedEscapeSequence_Rejected()
    {
        // \t is not one of \r \n \c \\ — fatal per STOMP 1.2.
        var wire = Encoding.UTF8.GetBytes("SEND\nx:\\t\ncontent-length:0\n\n\0");

        var act = () => StompFrameCodec.Decode(wire);

        act.Should().Throw<MalformedMessageException>().WithMessage("*escape*");
    }

    [Fact]
    public void Decode_DanglingEscape_Rejected()
    {
        var wire = Encoding.UTF8.GetBytes("SEND\nx:value\\\ncontent-length:0\n\n\0");

        var act = () => StompFrameCodec.Decode(wire);

        act.Should().Throw<MalformedMessageException>().WithMessage("*escape*");
    }

    [Fact]
    public void Decode_HeaderLineWithoutColon_Rejected()
    {
        var wire = Encoding.UTF8.GetBytes("SEND\nnocolonhere\n\n\0");

        var act = () => StompFrameCodec.Decode(wire);

        act.Should().Throw<MalformedMessageException>().WithMessage("*separator*");
    }

    [Fact]
    public void Decode_EmptyInput_Rejected()
    {
        var act = () => StompFrameCodec.Decode(Encoding.UTF8.GetBytes("\r\n\n"));

        act.Should().Throw<MalformedMessageException>().WithMessage("*empty*");
    }

    [Fact]
    public void Decode_TooManyHeaders_Rejected()
    {
        var sb = new StringBuilder("SEND\n");
        for (var i = 0; i < 40; i++)
            sb.Append("h").Append(i).Append(":v\n");
        sb.Append("\n\0");

        var act = () => StompFrameCodec.Decode(Encoding.UTF8.GetBytes(sb.ToString()));

        act.Should().Throw<MalformedMessageException>().WithMessage("*headers*");
    }

    [Fact]
    public void Decode_RepeatedHeader_FirstOccurrenceWins()
    {
        var wire = Encoding.UTF8.GetBytes("SEND\ndestination:/first\ndestination:/second\ncontent-length:0\n\n\0");

        var decoded = StompFrameCodec.Decode(wire);

        decoded.TryGetHeader("destination", out var dest).Should().BeTrue();
        dest.Should().Be("/first", "STOMP 1.2: only the first occurrence of a repeated header is meaningful");
    }

    // ---- Encode-side guards ----

    [Fact]
    public void Encode_RejectsCallerSuppliedContentLength()
    {
        var frame = new StompFrame("SEND", new[] { KeyValuePair.Create("content-length", "5") }, Body);

        var act = () => StompFrameCodec.Encode(frame);

        act.Should().Throw<ArgumentException>().WithMessage("*content-length*");
    }

    [Fact]
    public void Encode_RejectsCommandWithLineBreak()
    {
        var frame = new StompFrame("SE\nND", Array.Empty<KeyValuePair<string, string>>(), ReadOnlyMemory<byte>.Empty);

        var act = () => StompFrameCodec.Encode(frame);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encode_RejectsUnescapableConnectHeader()
    {
        // CONNECT has no escaping, so CR/LF are unrepresentable there.
        var frame = new StompFrame("CONNECT", new[] { KeyValuePair.Create("host", "evil\nhost") }, ReadOnlyMemory<byte>.Empty);

        var act = () => StompFrameCodec.Encode(frame);

        act.Should().Throw<ArgumentException>();
    }
}
