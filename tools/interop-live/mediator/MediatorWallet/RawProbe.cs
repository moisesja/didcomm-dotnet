using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using DidComm.Transports.ReturnRoute;

namespace MediatorWallet;

/// <summary>One HTTP exchange as seen on the wire.</summary>
internal sealed record WireAnswer(int Status, long ElapsedMs, string? ContentType, string? Date, string Body, long? DateMinusOurClockSeconds)
{
    public JsonObject Describe() => new()
    {
        ["http_status"] = Status,
        ["elapsed_ms"] = ElapsedMs,
        ["content_type"] = ContentType,
        ["date"] = Date,
        ["date_minus_our_clock_s"] = DateMinusOurClockSeconds,
        ["body_bytes"] = Encoding.UTF8.GetByteCount(Body),
    };
}

/// <summary>
/// The raw half of <c>observe</c>: messages built by hand, packed and unpacked with
/// <see cref="DidCommClient"/> only, sent with a BCL <see cref="HttpClient"/> (no proxy) or a
/// <see cref="ClientWebSocket"/> in Text frames. Nothing here goes through the mediator client or
/// the transports, so what is recorded is what the mediator does, not what our client makes of it.
/// </summary>
internal sealed class RawProbe : IDisposable
{
    public const string Encrypted = "application/didcomm-encrypted+json";
    public const string Cm2 = "https://didcomm.org/coordinate-mediation/2.0";
    public const string Pickup3 = "https://didcomm.org/messagepickup/3.0";

    private readonly HttpClient _http;

    public RawProbe(DidCommClient client, string mediatorDid, string walletDid, Uri endpoint)
    {
        Client = client;
        MediatorDid = mediatorDid;
        WalletDid = walletDid;
        Endpoint = endpoint;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public DidCommClient Client { get; }
    public string MediatorDid { get; }
    public string WalletDid { get; }
    public Uri Endpoint { get; }

    /// <summary>A request from <paramref name="from"/> (default the wallet) to the mediator: fresh id, <c>created_time</c> now, <c>return_route: all</c>.</summary>
    public Message Request(string type, JsonObject body, string? from = null, bool returnRoute = true)
    {
        var builder = new MessageBuilder()
            .WithId(Guid.NewGuid().ToString("D"))
            .WithType(type)
            .WithFrom(from ?? WalletDid)
            .WithTo(MediatorDid)
            .WithCreatedTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .WithBody(body);
        if (returnRoute)
            builder = builder.WithReturnRoute(ReturnRouteMode.All);
        return builder.Build();
    }

    public async Task<string> AuthcryptAsync(Message message, string? from = null, CancellationToken ct = default)
        => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { MediatorDid }, From: from ?? WalletDid), ct).ConfigureAwait(false)).Message;

    public async Task<string> AnoncryptAsync(Message message, CancellationToken ct = default)
        => (await Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { MediatorDid }), ct).ConfigureAwait(false)).Message;

    /// <summary>How long one HTTP exchange may take before it is recorded as unanswered (the mediator answers a no-reply request after 3 s).</summary>
    public static readonly TimeSpan RequestGuard = TimeSpan.FromSeconds(15);

    /// <summary>One POST with its own hang guard: no response within <see cref="RequestGuard"/> is a <see cref="TimeoutException"/>.</summary>
    public async Task<WireAnswer> PostAsync(string body, CancellationToken ct, string contentType = Encrypted)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        guard.CancelAfter(RequestGuard);
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await _http.PostAsync(Endpoint, content, guard.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(guard.Token).ConfigureAwait(false);
            var date = response.Headers.Date;
            return new WireAnswer((int)response.StatusCode, watch.ElapsedMilliseconds, response.Content.Headers.ContentType?.ToString(),
                date?.ToString("R"), text, date is { } d ? d.ToUnixTimeSeconds() - DateTimeOffset.UtcNow.ToUnixTimeSeconds() : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No HTTP response within {RequestGuard.TotalSeconds} s.");
        }
    }

    /// <summary>Pack (authcrypt by default), post, and open the answer when there is one.</summary>
    public async Task<(WireAnswer Wire, UnpackResult? Answer, JsonObject Record)> AskAsync(Message request, CancellationToken ct, bool anoncrypt = false, string? from = null)
    {
        var packed = anoncrypt ? await AnoncryptAsync(request, ct).ConfigureAwait(false) : await AuthcryptAsync(request, from, ct).ConfigureAwait(false);
        var wire = await PostAsync(packed, ct).ConfigureAwait(false);
        var record = new JsonObject { ["request_type"] = request.Type, ["request_packing"] = anoncrypt ? "anoncrypt" : "authcrypt", ["wire"] = wire.Describe() };
        UnpackResult? answer = null;
        if (wire.Body.Length > 0)
        {
            try
            {
                answer = await Client.UnpackAsync(wire.Body, ct).ConfigureAwait(false);
                record["answer"] = DescribeAnswer(answer, request, wire.Date);
            }
            catch (Exception ex)
            {
                record["answer_unpack_error"] = CommandResult.Describe(ex);
                record["answer_body_head"] = wire.Body.Length > 300 ? wire.Body[..300] : wire.Body;
            }
        }
        return (wire, answer, record);
    }

    /// <summary>
    /// The header facts the observations compare (O-4 <c>thid</c>, O-5 <c>created_time</c>, O-6
    /// <c>to</c>, I-11 <c>from_prior</c>, I-13 top-level members, I-15 <c>from</c>) and how it was
    /// protected.
    /// </summary>
    public JsonObject DescribeAnswer(UnpackResult answer, Message? request, string? date)
    {
        var message = answer.Message;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var serialized = JsonSerializer.SerializeToNode(message, PlainJson)!.AsObject();
        // typ is left out: our model fills a default when the member is absent, so it says nothing.
        var keys = serialized.Select(p => p.Key).Where(k => k != "typ").OrderBy(k => k, StringComparer.Ordinal).ToArray();
        return new JsonObject
        {
            ["type"] = message.Type,
            ["id"] = message.Id,
            ["thid"] = message.Thid,
            ["thid_is_request_id"] = request is not null && message.Thid == request.Id,
            ["pthid"] = message.Pthid,
            ["pthid_is_request_id"] = request is not null && message.Pthid == request.Id,
            ["created_time_present"] = message.CreatedTime is not null,
            ["created_time_minus_our_clock_s"] = message.CreatedTime is { } t ? t - now : null,
            ["expires_time_present"] = message.ExpiresTime is not null,
            ["from_is_mediator"] = message.From == MediatorDid,
            ["to"] = message.To is { } to ? new JsonArray(to.Select(x => (JsonNode?)Role(x)).ToArray()) : null,
            ["from_prior_present"] = message.FromPrior is not null,
            ["please_ack"] = message.PleaseAck is { } pa ? new JsonArray(pa.Select(x => (JsonNode?)x).ToArray()) : null,
            ["return_route"] = message.AdditionalHeaders is { } h && h.TryGetValue("return_route", out var rr) ? rr.ToString() : null,
            ["top_level_members"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()),
            ["date_header"] = date,
            ["protection"] = new JsonObject
            {
                ["encrypted"] = answer.Encrypted,
                ["authenticated"] = answer.Authenticated,
                ["non_repudiation"] = answer.NonRepudiation,
                ["anonymous_sender"] = answer.AnonymousSender,
                ["enc"] = answer.ContentEncryption,
                ["kw"] = answer.KeyWrap,
                ["sig_alg"] = answer.SignatureAlgorithm,
                ["sender_is_mediator_key"] = answer.SenderKid?.StartsWith(MediatorDid + "#", StringComparison.Ordinal),
                ["signer_is_mediator_key"] = answer.SignerKid?.StartsWith(MediatorDid + "#", StringComparison.Ordinal),
            },
            ["body"] = message.Body?.DeepClone(),
        };
    }

    /// <summary>Names the DIDs the probe knows instead of printing them.</summary>
    public Func<string, string> Role { get; set; } = did => did;

    /// <summary>Plaintext serialization with null members left out.</summary>
    public static readonly JsonSerializerOptions PlainJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// A forward for the mediator to <paramref name="next"/>, built by hand: the inner envelope
    /// (authcrypt from <paramref name="sender"/> to <paramref name="innerRecipient"/>) attached to a
    /// routing 2.0 <c>forward</c> anoncrypted to the mediator. Returns the forward's id, the inner
    /// envelope and the outer one.
    /// </summary>
    public async Task<(string ForwardId, string Inner, string Outer)> BuildForwardAsync(string sender, string next, string innerRecipient, string text, CancellationToken ct)
    {
        var inner = new MessageBuilder()
            .WithId(Guid.NewGuid().ToString("D"))
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(sender)
            .WithTo(innerRecipient)
            .WithCreatedTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .WithBody(new JsonObject { ["content"] = text })
            .Build();
        var innerPacked = (await Client.PackEncryptedAsync(inner, new PackEncryptedOptions(Recipients: new[] { innerRecipient }, From: sender), ct).ConfigureAwait(false)).Message;
        var forward = ForwardMessage.Create(MediatorDid, next, new[] { innerPacked });
        forward.CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outer = await AnoncryptAsync(forward, ct).ConfigureAwait(false);
        return (forward.Id, innerPacked, outer);
    }

    /// <summary>The attachments of a delivery: id, encoding facts, and the decoded bytes.</summary>
    public static List<(string? Id, byte[]? Bytes, JsonObject Facts)> ReadAttachments(Message delivery)
    {
        var list = new List<(string?, byte[]?, JsonObject)>();
        foreach (var attachment in delivery.Attachments ?? [])
        {
            var facts = new JsonObject { ["id_present"] = attachment.Id is not null, ["media_type"] = attachment.MediaType };
            byte[]? bytes = null;
            if (attachment.Data.Base64 is { } b64)
            {
                facts["data"] = "base64";
                int Count(char c) => b64.Count(x => x == c);
                var url = Count('-') + Count('_');
                var standard = Count('+') + Count('/');
                facts["alphabet"] = url > 0 && standard == 0 ? "url" : standard > 0 && url == 0 ? "standard" : url > 0 ? "mixed" : "ambiguous (no alphabet-specific character: base64 of ASCII JSON with base64url members has none)";
                facts["chars"] = new JsonObject { ["minus"] = Count('-'), ["underscore"] = Count('_'), ["plus"] = Count('+'), ["slash"] = Count('/'), ["equals"] = Count('=') };
                facts["length"] = b64.Length;
                facts["padded"] = b64.EndsWith('=');
                facts["length_mod_4"] = b64.Length % 4;
                bytes = DecodeBase64Any(b64);
            }
            else if (attachment.Data.Json is { } json)
            {
                facts["data"] = "json";
                bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
            }
            else
            {
                facts["data"] = "other";
            }
            list.Add((attachment.Id, bytes, facts));
        }
        return list;
    }

    /// <summary>Compare a delivered envelope with the one the sender attached: byte-equal, JSON-equal, or different.</summary>
    public static string Compare(byte[] delivered, string attached)
    {
        if (delivered.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(attached)) || delivered.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(attached.TrimEnd())))
            return "byte-equal";
        try
        {
            var a = JsonNode.Parse(delivered);
            var b = JsonNode.Parse(attached);
            return JsonNode.DeepEquals(a, b) ? "json-equal" : "different";
        }
        catch (JsonException)
        {
            return "different";
        }
    }

    private static byte[]? DecodeBase64Any(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---- WebSocket (Text frames) ------------------------------------------------------------

    public static async Task<ClientWebSocket> ConnectAsync(Uri ws, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try
        {
            await socket.ConnectAsync(ws, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct)
        => socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    /// <summary>One whole WebSocket message: its frame type, how many frames it took, and its text.</summary>
    public static async Task<(WebSocketMessageType Type, int Frames, string Text)> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var collected = new MemoryStream();
        var frames = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            frames++;
            if (result.MessageType == WebSocketMessageType.Close)
                return (result.MessageType, frames, string.Empty);
            collected.Write(buffer, 0, result.Count);
            if (collected.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("A WebSocket message over 4 MiB.");
            if (result.EndOfMessage)
                return (result.MessageType, frames, Encoding.UTF8.GetString(collected.ToArray()));
        }
    }

    public void Dispose() => _http.Dispose();
}
