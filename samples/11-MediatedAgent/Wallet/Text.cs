namespace DidComm.Samples.MediatedAgent.Wallet;

/// <summary>Display helpers: the sample never prints a whole DID, key or packed message.</summary>
public static class Text
{
    /// <summary>The first <paramref name="max"/> characters of <paramref name="value"/>, with an ellipsis when cut.</summary>
    /// <param name="value">The value to show.</param>
    /// <param name="max">The most characters to show.</param>
    public static string Trunc(string? value, int max)
        => value is null ? "<null>" : value.Length <= max ? value : value[..(max - 1)] + "…";
}
