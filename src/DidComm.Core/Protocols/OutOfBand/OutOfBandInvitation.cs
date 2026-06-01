using System.Text.Json.Nodes;
using DidComm.Messages;

namespace DidComm.Protocols.OutOfBand;

/// <summary>
/// A parsed Out-of-Band 2.0 invitation (FR-OOB-01). A thin, read-only projection over the
/// underlying plaintext <see cref="Messages.Message"/>: the top-level <c>id</c> / <c>from</c>
/// headers and the <c>goal_code</c> / <c>goal</c> / <c>accept</c> members that live inside the
/// message <c>body</c>, plus any <c>attachments</c> carrying the alternative protocol messages
/// a recipient may act on.
/// </summary>
/// <remarks>
/// Build one with <see cref="OutOfBand.CreateInvitation"/>; recover one from a URL with
/// <see cref="OutOfBand.FromUrl"/> or from a short-form GET body with
/// <see cref="OutOfBand.FromPlaintext"/>. The recipient's response correlates back to this
/// invitation by setting its <c>pthid</c> to <see cref="Id"/> (FR-OOB-03).
/// </remarks>
public sealed class OutOfBandInvitation
{
    /// <summary>Wrap an already-built invitation message. Callers normally use the <see cref="OutOfBand"/> factories.</summary>
    /// <param name="message">A message whose <c>type</c> is the OOB invitation type.</param>
    internal OutOfBandInvitation(Message message)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
    }

    /// <summary>The underlying plaintext message — pack it, attach it, or inspect extension headers via this.</summary>
    public Message Message { get; }

    /// <summary>The invitation id. Recipients use this as the <c>pthid</c> of their response (FR-OOB-03).</summary>
    public string Id => Message.Id;

    /// <summary>The sender DID recipients use for future interactions (REQUIRED for OOB).</summary>
    public string? From => Message.From;

    /// <summary>The self-attested <c>goal_code</c> from the body, or <c>null</c> when absent.</summary>
    public string? GoalCode => ReadBodyString("goal_code");

    /// <summary>The self-attested human-readable <c>goal</c> from the body, or <c>null</c> when absent.</summary>
    public string? Goal => ReadBodyString("goal");

    /// <summary>The ordered <c>accept</c> media-type profiles from the body, or an empty list when absent.</summary>
    public IReadOnlyList<string> Accept => ReadBodyStringArray("accept");

    /// <summary>The invitation attachments (alternative protocol messages), or an empty list when absent.</summary>
    public IReadOnlyList<Attachment> Attachments => Message.Attachments is { } a ? a.ToList() : Array.Empty<Attachment>();

    private string? ReadBodyString(string field)
    {
        if (Message.Body is null) return null;
        if (!Message.Body.TryGetPropertyValue(field, out var node) || node is null) return null;
        // Pattern-match JsonValue rather than AsValue() so a malformed peer body (object/array
        // where a string was expected) yields null instead of throwing.
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }

    private IReadOnlyList<string> ReadBodyStringArray(string field)
    {
        if (Message.Body is null) return Array.Empty<string>();
        if (!Message.Body.TryGetPropertyValue(field, out var node) || node is not JsonArray array)
            return Array.Empty<string>();

        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s))
                list.Add(s);
        }
        return list;
    }
}
