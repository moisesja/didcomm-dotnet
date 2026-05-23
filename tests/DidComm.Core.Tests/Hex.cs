namespace DidComm.Tests;

/// <summary>
/// Hex decode helper for embedding test vectors copied verbatim out of RFCs. Whitespace,
/// newlines, and ASCII colons are stripped; case is irrelevant.
/// </summary>
internal static class Hex
{
    public static byte[] Decode(string hex)
    {
        var stripped = new char[hex.Length];
        var len = 0;
        foreach (var c in hex)
        {
            if (char.IsWhiteSpace(c) || c == ':') continue;
            stripped[len++] = c;
        }
        if ((len & 1) != 0)
            throw new ArgumentException($"Hex input has odd nibble count after stripping whitespace ({len}).", nameof(hex));

        var bytes = new byte[len / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var hi = ParseNibble(stripped[i * 2]);
            var lo = ParseNibble(stripped[i * 2 + 1]);
            bytes[i] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    private static int ParseNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => throw new ArgumentException($"Invalid hex character '{c}'."),
    };
}
