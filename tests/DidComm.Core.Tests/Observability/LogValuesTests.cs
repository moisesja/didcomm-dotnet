using DidComm.Diagnostics;
using AwesomeAssertions;
using Xunit;

namespace DidComm.Tests.Observability;

/// <summary>
/// #95 AD-4: the one rule every package uses to write a stranger's message id into a log line; and
/// the one rule for writing a stranger's message type onto a span tag.
/// </summary>
public sealed class LogValuesTests
{
    [Fact]
    public void An_id_up_to_128_characters_is_written_as_it_is_and_a_longer_one_by_its_length()
    {
        LogValues.Id(null).Should().Be("(none)");
        LogValues.Id("").Should().Be("");
        LogValues.Id(new string('a', 128)).Should().Be(new string('a', 128));
        LogValues.Id(new string('a', 129)).Should().Be("(id of 129 characters)");
        LogValues.Id(new string('a', 200_000)).Should().Be("(id of 200000 characters)");
    }

    [Fact]
    public void A_type_up_to_256_URI_characters_is_written_as_it_is_and_a_longer_one_by_its_length()
    {
        var at = TypeOfLength(256);
        var over = TypeOfLength(257);
        var huge = TypeOfLength(200_000);

        LogValues.MessageType(null).Should().Be("(no type)");
        LogValues.MessageType("").Should().Be("");
        LogValues.MessageType("https://didcomm.org/basicmessage/2.0/message").Should().Be("https://didcomm.org/basicmessage/2.0/message");
        LogValues.MessageType(at).Should().Be(at);
        LogValues.MessageType(over).Should().Be("(type of 257 characters)");
        LogValues.MessageType(huge).Should().Be("(type of 200000 characters)");
    }

    [Fact]
    public void Every_URI_character_is_kept_percent_escapes_included()
    {
        const string everyUriCharacter = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~:/?#[]@!$&'()*+,;=%";
        var type = "https://example.com/" + everyUriCharacter + "/1.0/m";
        const string escaped = "https://example.com/caf%C3%A9%0A%1B/1.0/m";

        LogValues.MessageType(type).Should().Be(type);
        LogValues.MessageType(escaped).Should().Be(escaped, "a percent-escape is URI text; only the character it stands for is not");
    }

    /// <summary>
    /// Each class of character a URI cannot hold: C0 controls (line feed, carriage return, tab, NUL,
    /// ESC), DEL, C1 controls (NEL, CSI), the space, the ASCII characters RFC 3986 leaves out, and
    /// text outside ASCII (a letter, the Unicode line and paragraph separators, a bidirectional
    /// override, a byte-order mark, a surrogate pair).
    /// </summary>
    [Theory]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    [InlineData(0x09)]
    [InlineData(0x00)]
    [InlineData(0x1B)]
    [InlineData(0x7F)]
    [InlineData(0x85)]
    [InlineData(0x9B)]
    [InlineData(0x20)]
    [InlineData((int)'"')]
    [InlineData((int)'<')]
    [InlineData((int)'>')]
    [InlineData((int)'\\')]
    [InlineData((int)'^')]
    [InlineData((int)'`')]
    [InlineData((int)'{')]
    [InlineData((int)'|')]
    [InlineData((int)'}')]
    [InlineData(0xE9)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x202E)]
    [InlineData(0xFEFF)]
    [InlineData(0x1F600)]
    public void A_type_holding_a_character_outside_a_URI_is_replaced_without_echoing_it(int codePoint)
    {
        var type = "https://didcomm.org/x" + char.ConvertFromUtf32(codePoint) + "FORGED/1.0/ping";

        var written = LogValues.MessageType(type);

        written.Should().Be("(type with characters outside a URI)");
    }

    [Fact]
    public void An_over_long_type_with_characters_outside_a_URI_is_replaced_by_its_length()
    {
        var type = "https://didcomm.org/x\n\u001b[2J" + new string('a', 300) + "/1.0/ping";

        LogValues.MessageType(type).Should().Be($"(type of {type.Length} characters)");
    }

    [Fact]
    public void The_innermost_type_name_walks_the_whole_chain()
    {
        var chain = new InvalidOperationException("outer", new AggregateException(new FormatException("inner")));

        LogValues.InnermostTypeName(chain).Should().Be(nameof(FormatException));
        LogValues.InnermostTypeName(new TimeoutException()).Should().Be(nameof(TimeoutException));
    }

    /// <summary>A valid Message Type URI of exactly <paramref name="length"/> URI characters.</summary>
    private static string TypeOfLength(int length)
    {
        const string prefix = "https://didcomm.org/";
        const string suffix = "/1.0/ping";
        var type = prefix + new string('a', length - prefix.Length - suffix.Length) + suffix;
        type.Length.Should().Be(length);
        return type;
    }
}
