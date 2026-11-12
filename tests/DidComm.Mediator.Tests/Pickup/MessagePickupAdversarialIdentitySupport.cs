using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DataProofsDotnet.Jose.Encryption;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.Transports.ReturnRoute;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>What happened to one hand-written envelope: where it stopped and with what.</summary>
internal sealed record MessagePickupAdversarialIdentityDelivery(Exception? UnpackError, Exception? DispatchError, DispatchOutcome? Outcome)
{
    /// <summary>
    /// Where the envelope ended: <c>unpack</c> (refused by the receive path with a
    /// <see cref="DidCommException"/>), <c>malformed</c> (a <see cref="MalformedMessageException"/>
    /// out of dispatch), <c>reply</c> / <c>no-reply</c> / <c>no-handler</c> (dispatched), or the
    /// full name of anything else that was thrown — which is what a test must never see.
    /// </summary>
    public string Verdict
    {
        get
        {
            if (UnpackError is not null)
                return UnpackError is DidCommException ? "unpack" : "unpack threw " + UnpackError.GetType().FullName;
            if (DispatchError is not null)
                return DispatchError is MalformedMessageException ? "malformed" : "dispatch threw " + DispatchError.GetType().FullName;
            return Outcome!.Result switch
            {
                DispatchResult.ReplyProduced => "reply",
                DispatchResult.NoReply => "no-reply",
                DispatchResult.NoHandler => "no-handler",
                var other => other.ToString(),
            };
        }
    }

    /// <summary>The text of whatever was thrown, as an endpoint's log would print it (the endpoints log the exception object).</summary>
    public string ThrownText => (UnpackError ?? DispatchError)?.ToString() ?? string.Empty;
}

/// <summary>
/// Authcrypt envelopes over plaintext bytes written by hand, so a test can send JSON the library's
/// own packer never emits: duplicate members, escapes, odd number literals, extra headers.
/// </summary>
internal static class MessagePickupAdversarialIdentityWire
{
    /// <summary>
    /// The text of a JSON <c>\uXXXX</c> escape for <paramref name="hex"/>. Built from pieces so the
    /// six characters reach the plaintext as an escape, whatever wrote this file (lesson L-049).
    /// </summary>
    public static string Escape(string hex) => "\\" + "u" + hex;

    /// <summary>A plaintext message as JSON text. <paramref name="type"/> is written as given (it must not need escaping).</summary>
    public static string Plaintext(string type, string from, string to, string? bodyJson, string? id = null, string? extraMembers = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"").Append(id ?? Guid.NewGuid().ToString()).Append('"');
        sb.Append(",\"typ\":\"application/didcomm-plain+json\"");
        sb.Append(",\"type\":").Append(JsonSerializer.Serialize(type));
        sb.Append(",\"from\":\"").Append(from).Append('"');
        sb.Append(",\"to\":[\"").Append(to).Append("\"]");
        if (extraMembers is not null)
            sb.Append(',').Append(extraMembers);
        if (bodyJson is not null)
            sb.Append(",\"body\":").Append(bodyJson);
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>ECDH-1PU authcrypt of <paramref name="plaintextJson"/> from <paramref name="sender"/> to <paramref name="recipient"/>: a real envelope around bytes nobody validated.</summary>
    public static async Task<string> AuthcryptAsync(MediatorTestHost host, string plaintextJson, PeerIdentity sender, PeerIdentity recipient)
    {
        var crypto = new JoseCryptoProvider();
        var keyOps = new KeyOperationResolver(host.Secrets, null, crypto);
        var senderJwk = sender.Privates.First(j => j.Crv == "X25519");
        var recipientJwk = recipient.Privates.First(j => j.Crv == "X25519");
        var senderKey = await keyOps.ResolveKeyAgreementAsync(senderJwk.Kid!, CancellationToken.None)
            ?? throw new InvalidOperationException("The test container holds no key for the sender.");
        var recipientPublic = new Jwk { Kty = recipientJwk.Kty, Crv = recipientJwk.Crv, X = recipientJwk.X, Kid = recipientJwk.Kid };
        return await JweBuilder.BuildEcdh1PuA256KwAsync(
            Encoding.UTF8.GetBytes(plaintextJson),
            new[] { recipientPublic },
            senderKey,
            senderJwk.Kid!,
            JoseAlgorithms.A256CbcHs512,
            crypto,
            MediaTypes.Encrypted,
            CancellationToken.None);
    }

    /// <summary>Authcrypt, unpack as the mediator and dispatch on <paramref name="session"/>, recording where anything was thrown.</summary>
    public static async Task<MessagePickupAdversarialIdentityDelivery> DeliverAsync(
        PickupFixture fx, string plaintextJson, PeerIdentity sender, PeerIdentity mediator, IReturnRouteSession? session)
    {
        var packed = await AuthcryptAsync(fx.Host, plaintextJson, sender, mediator);
        UnpackResult received;
        try
        {
            received = await fx.Host.Client.UnpackAsync(packed);
        }
        catch (Exception ex)
        {
            return new MessagePickupAdversarialIdentityDelivery(ex, null, null);
        }

        try
        {
            var outcome = await fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, Task.CompletedTask, session, CancellationToken.None);
            return new MessagePickupAdversarialIdentityDelivery(null, null, outcome);
        }
        catch (Exception ex)
        {
            return new MessagePickupAdversarialIdentityDelivery(null, ex, null);
        }
    }

    /// <summary><paramref name="depth"/> nested objects around a number.</summary>
    public static string Nested(int depth)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < depth; i++)
            sb.Append("{\"a\":");
        sb.Append('1');
        sb.Append('}', depth);
        return sb.ToString();
    }
}

/// <summary>
/// The real <see cref="InMemoryMediationStore"/> with every read recorded and one seam: a callback
/// that runs after <see cref="FindGrantByRecipientDidAsync"/> has read its answer and before the
/// caller gets it — the place where another request's change lands in a real, concurrent mediator.
/// </summary>
internal sealed class MessagePickupAdversarialIdentityMediationStore : IMediationStore
{
    public InMemoryMediationStore Inner { get; } = new(new CoordinateMediationOptions());

    /// <summary>The reads the pickup role made, in order, with the DID each was about.</summary>
    public ConcurrentQueue<(string Method, string Did)> Reads { get; } = new();

    /// <summary>Runs after a lookup has read its (now possibly stale) answer; gets the DID asked about.</summary>
    public Func<string, ValueTask>? AfterFind { get; set; }

    public ValueTask<MediationGrant?> GetGrantAsync(string peerDid, CancellationToken ct)
    {
        Reads.Enqueue((nameof(GetGrantAsync), peerDid));
        return Inner.GetGrantAsync(peerDid, ct);
    }

    public async ValueTask<MediationGrant?> FindGrantByRecipientDidAsync(string recipientDid, CancellationToken ct)
    {
        Reads.Enqueue((nameof(FindGrantByRecipientDidAsync), recipientDid));
        var answer = await Inner.FindGrantByRecipientDidAsync(recipientDid, ct);
        if (AfterFind is { } hook)
            await hook(recipientDid);
        return answer;
    }

    public ValueTask<MediationGrant?> GrantAsync(MediationGrant grant, CancellationToken ct) => Inner.GrantAsync(grant, ct);

    public ValueTask<bool> RevokeAsync(string peerDid, CancellationToken ct) => Inner.RevokeAsync(peerDid, ct);

    public ValueTask<IReadOnlyList<RecipientStoreOutcome>> UpdateRecipientsAsync(string peerDid, IReadOnlyList<RecipientUpdate> updates, CancellationToken ct)
        => Inner.UpdateRecipientsAsync(peerDid, updates, ct);

    public ValueTask<RecipientPage> ListRecipientsAsync(string peerDid, int offset, int limit, CancellationToken ct)
        => Inner.ListRecipientsAsync(peerDid, offset, limit, ct);
}

/// <summary>
/// The integer reader of Coordinate Mediation exactly as <c>main</c> has it
/// (<c>MediationBodies.ReadInteger</c> / <c>TryReadIntegerText</c> / <c>Accumulate</c> at merge
/// <c>0d64ec9</c>), copied here as the oracle for the reader #94 generalised and moved to
/// <c>DidComm.Mediator.Internal.WireJson</c>. Nothing in it may be "improved": it is the old
/// behaviour, bug for bug.
/// </summary>
internal static class MessagePickupAdversarialIdentityMainIntegers
{
    /// <summary><c>main</c>'s <c>ReadInteger</c> for a member that is present: the value, or <c>null</c> where it threw "must be an integer".</summary>
    public static int? ReadInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            return null;
        if (value.TryGetInt64(out var wide))
            return (int)Math.Clamp(wide, int.MinValue, int.MaxValue);
        if (TryReadIntegerText(value.GetRawText(), out var clamped))
            return clamped;
        return null;
    }

    public static bool TryReadIntegerText(ReadOnlySpan<char> text, out int value)
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

        var pointPosition = (long)whole.Length + exponent;
        long magnitude = 0;
        var index = 0L;
        var overflow = false;
        if (!Accumulate(whole, pointPosition, ref index, ref magnitude, ref overflow)
            || !Accumulate(fraction, pointPosition, ref index, ref magnitude, ref overflow))
        {
            return false;
        }

        if (!overflow && magnitude != 0)
        {
            for (var pad = index; pad < pointPosition && !overflow; pad++)
            {
                magnitude *= 10;
                overflow = magnitude > int.MaxValue;
            }
        }

        value = overflow
            ? (negative ? int.MinValue : int.MaxValue)
            : (int)(negative ? -magnitude : magnitude);
        return true;
    }

    private static bool Accumulate(ReadOnlySpan<char> digits, long pointPosition, ref long index, ref long magnitude, ref bool overflow)
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
                magnitude = magnitude * 10 + digit;
                overflow = magnitude > int.MaxValue;
            }
            index++;
        }
        return true;
    }
}
