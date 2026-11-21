using System.Text;

namespace DidComm.TestSupport;

/// <summary>
/// Stand-in messages in encrypted form, for tests of a mediator's mailbox. The mailbox stores only a
/// DIDComm message whose outermost layer is a JWE (Message Pickup 3.0 L172, 4.0 L261; #100 OD-1),
/// judged structurally, the way the library's unpack classifies a JWE: a JSON object with a
/// <c>ciphertext</c> member. A stand-in is <c>{"ciphertext":"…"}</c> with a value of base64url
/// characters, so a test can give it an exact size. Nothing ever decrypts it; a test that needs a
/// message the recipient can open packs a real one.
/// </summary>
public static class StandInJwe
{
    /// <summary>The bytes around the value, <c>{"ciphertext":"</c> and <c>"}</c>: the size of the smallest stand-in.</summary>
    public const int MinSize = 17;

    private const string Prefix = "{\"ciphertext\":\"";
    private const string Suffix = "\"}";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>
    /// A stand-in of exactly <paramref name="size"/> bytes. Two seeds give two different stand-ins
    /// whenever the value has at least two characters (the first two encode the seed).
    /// </summary>
    /// <param name="size">The size in bytes; at least <see cref="MinSize"/>.</param>
    /// <param name="seed">Varies the value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is below <see cref="MinSize"/>.</exception>
    public static byte[] OfSize(int size, byte seed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, MinSize);
        var value = new char[size - MinSize];
        for (var i = 0; i < value.Length; i++)
        {
            value[i] = i switch
            {
                0 => Alphabet[seed & 63],
                1 => Alphabet[seed >> 6],
                _ => Alphabet[(seed + i * 31) & 63],
            };
        }
        return Encoding.ASCII.GetBytes(Prefix + new string(value) + Suffix);
    }

    /// <summary>The seed <see cref="OfSize"/> made <paramref name="standIn"/> with, for a test that tells messages apart by it.</summary>
    /// <param name="standIn">A stand-in of at least <see cref="MinSize"/> + 2 bytes, as made by <see cref="OfSize"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="standIn"/> is not such a stand-in.</exception>
    public static byte SeedOf(ReadOnlySpan<byte> standIn)
    {
        var low = standIn.Length >= MinSize + 2 ? Alphabet.IndexOf((char)standIn[Prefix.Length], StringComparison.Ordinal) : -1;
        var high = low >= 0 ? Alphabet.IndexOf((char)standIn[Prefix.Length + 1], StringComparison.Ordinal) : -1;
        if (low < 0 || high is < 0 or > 3)
            throw new ArgumentException("Not a stand-in made by OfSize with a value of at least two characters.", nameof(standIn));
        return (byte)(low + (high << 6));
    }

    /// <summary>A stand-in whose value is <paramref name="text"/>, for a test that recognises the message it reads back.</summary>
    /// <param name="text">Base64url characters (nothing that needs a JSON escape).</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> holds a character outside base64url.</exception>
    public static byte[] Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var c in text)
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
                throw new ArgumentException("A stand-in's value is made of base64url characters.", nameof(text));
        }
        return Encoding.ASCII.GetBytes(Prefix + text + Suffix);
    }
}
