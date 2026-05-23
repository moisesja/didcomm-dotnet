namespace DidComm.Protocols;

/// <summary>
/// A DIDComm protocol semantic version (<c>major.minor</c>). DIDComm message-type URIs do
/// not carry a patch component (FR-PROTO-02): two implementations interoperate when their
/// majors match; differing minors interoperate at the older minor.
/// </summary>
/// <param name="Major">Major version number (incompatible changes between majors).</param>
/// <param name="Minor">Minor version number (older minor is the interop floor; FR-PROTO-02).</param>
internal readonly record struct ProtocolVersion(int Major, int Minor) : IComparable<ProtocolVersion>
{
    /// <summary>Render as <c>"major.minor"</c>.</summary>
    public override string ToString() => $"{Major}.{Minor}";

    /// <summary>Parse a <c>"major.minor"</c> string; returns <c>false</c> on any deviation from that shape.</summary>
    /// <param name="value">Input like <c>"2.1"</c>.</param>
    /// <param name="version">Resulting parsed version on success.</param>
    public static bool TryParse(string? value, out ProtocolVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(value)) return false;
        var dot = value.IndexOf('.');
        if (dot <= 0 || dot == value.Length - 1) return false;
        if (value.IndexOf('.', dot + 1) >= 0) return false;
        if (!int.TryParse(value.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major))
            return false;
        if (!int.TryParse(value.AsSpan(dot + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor))
            return false;
        version = new ProtocolVersion(major, minor);
        return true;
    }

    /// <summary>
    /// Spec-semver compatibility check (FR-PROTO-02): two versions are compatible when their
    /// majors are equal. The shared operating minor is the lower of the two.
    /// </summary>
    /// <param name="other">The peer's protocol version.</param>
    public bool IsCompatibleWith(ProtocolVersion other) => Major == other.Major;

    /// <summary>
    /// Return the minor at which two compatible versions should interoperate (the lower of the
    /// two). Returns <c>null</c> when <see cref="IsCompatibleWith"/> is <c>false</c>.
    /// </summary>
    /// <param name="other">The peer's protocol version.</param>
    public ProtocolVersion? NegotiateWith(ProtocolVersion other)
        => IsCompatibleWith(other)
            ? new ProtocolVersion(Major, Math.Min(Minor, other.Minor))
            : null;

    /// <inheritdoc />
    public int CompareTo(ProtocolVersion other)
    {
        var majorCmp = Major.CompareTo(other.Major);
        return majorCmp != 0 ? majorCmp : Minor.CompareTo(other.Minor);
    }
}
