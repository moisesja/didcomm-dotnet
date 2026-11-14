using System.Text.Json;
using System.Text.Json.Nodes;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Mediator.Internal;

namespace DidComm.Mediator.Coordination;

/// <summary>
/// Reads and writes the message bodies of Coordinate Mediation, independent of the protocol
/// version: 3.0 and 2.0 share the entry shapes (<c>recipient_did</c>, <c>action</c>,
/// <c>result</c>, <c>paginate</c>, <c>pagination</c>) and differ only in message names, in the
/// name of the list a query returns (<c>dids</c> / <c>keys</c>) and in whether <c>routing_did</c>
/// is an array or a string.
/// </summary>
/// <remarks>
/// Every reader throws <see cref="MalformedMessageException"/> for a wrong shape and never quotes
/// a wire value: the text names the member and the rule. A DID member is checked against the
/// FR-API-09 bound (<see cref="IdentifierLimits.MaxDidLength"/> UTF-16 code units, one length
/// comparison) before anything else looks at it. Unknown members are ignored.
/// </remarks>
internal static class MediationBodies
{
    internal const string RoutingDid = "routing_did";
    internal const string Updates = "updates";
    internal const string Updated = "updated";
    internal const string RecipientDid = "recipient_did";
    internal const string Action = "action";
    internal const string Result = "result";
    internal const string Paginate = "paginate";
    internal const string Pagination = "pagination";
    internal const string Limit = "limit";
    internal const string Offset = "offset";
    internal const string Count = "count";
    internal const string Remaining = "remaining";

    private const string ActionAdd = "add";
    private const string ActionRemove = "remove";
    private const string ResultClientError = "client_error";
    private const string ResultServerError = "server_error";
    private const string ResultNoChange = "no_change";
    private const string ResultSuccess = "success";

    // ---- writers --------------------------------------------------------------------------

    public static JsonObject WriteRoutingDids(IReadOnlyList<string> routingDids)
    {
        var array = new JsonArray();
        foreach (var did in routingDids)
            array.Add(did);
        return new JsonObject { [RoutingDid] = array };
    }

    public static JsonObject WriteUpdates(IReadOnlyList<RecipientUpdate> updates)
    {
        var array = new JsonArray();
        foreach (var update in updates)
        {
            array.Add(new JsonObject
            {
                [RecipientDid] = update.RecipientDid,
                [Action] = ActionName(update.Action),
            });
        }
        return new JsonObject { [Updates] = array };
    }

    public static JsonObject WriteUpdated(IReadOnlyList<RecipientUpdateResult> updated)
    {
        var array = new JsonArray();
        foreach (var entry in updated)
        {
            array.Add(new JsonObject
            {
                [RecipientDid] = entry.RecipientDid,
                [Action] = ActionName(entry.Action),
                [Result] = ResultName(entry.Result),
            });
        }
        return new JsonObject { [Updated] = array };
    }

    public static JsonObject WritePaginate(RecipientQueryPaging? paginate)
    {
        var body = new JsonObject();
        if (paginate is not null)
        {
            body[Paginate] = new JsonObject
            {
                [Limit] = paginate.Limit,
                [Offset] = paginate.Offset,
            };
        }
        return body;
    }

    public static JsonObject WriteRecipients(string listName, IReadOnlyList<string> dids, RecipientPagination? pagination)
    {
        var array = new JsonArray();
        foreach (var did in dids)
            array.Add(new JsonObject { [RecipientDid] = did });
        var body = new JsonObject { [listName] = array };
        if (pagination is not null)
        {
            body[Pagination] = new JsonObject
            {
                [Count] = pagination.Count,
                [Offset] = pagination.Offset,
                [Remaining] = pagination.Remaining,
            };
        }
        return body;
    }

    // ---- readers --------------------------------------------------------------------------

    /// <summary>
    /// The body as a <see cref="JsonElement"/> so every reader uses one strict API regardless of
    /// how the <see cref="JsonObject"/> was built (parsed off the wire or assembled in code).
    /// <c>default</c> (<see cref="JsonValueKind.Undefined"/>) when the message has no body.
    /// </summary>
    public static JsonElement ToElement(JsonObject? body) => WireJson.ToElement(body);

    /// <summary>
    /// Read <c>routing_did</c>: a single string (the 2.0 shape) or an array (3.0). With
    /// <paramref name="maxEntries"/>, an array longer than that is malformed, refused from its
    /// length alone before any entry is read or anything is allocated for it (L-055); what sits
    /// beyond the bound is never looked at.
    /// </summary>
    public static IReadOnlyList<string> ReadRoutingDids(JsonElement body, string message, int maxEntries = int.MaxValue)
    {
        var value = RequireMember(body, RoutingDid, message);
        // The 2.0 shape is a single string; 3.0 is an array. Both are accepted on receive.
        if (value.ValueKind == JsonValueKind.String)
            return new[] { ReadDid(value, RoutingDid, message) };
        if (value.ValueKind != JsonValueKind.Array)
            throw new MalformedMessageException($"{message} '{RoutingDid}' must be an array of DID strings.");
        if (value.GetArrayLength() > maxEntries)
            throw TooManyEntries(RoutingDid, maxEntries, message);

        var dids = new List<string>(value.GetArrayLength());
        foreach (var entry in value.EnumerateArray())
            dids.Add(ReadDid(entry, RoutingDid, message));
        if (dids.Count == 0)
            throw new MalformedMessageException($"{message} '{RoutingDid}' must name at least one DID.");
        return dids;
    }

    /// <summary>
    /// Refuse an <c>updates</c> list longer than <paramref name="maxEntries"/> from the body as it
    /// arrived, before the body is re-read and before any entry is looked at: the entries are
    /// counted, never decoded. A body without an <c>updates</c> array passes; the reader reports
    /// that.
    /// </summary>
    public static void RequireUpdatesWithin(JsonObject? body, int maxEntries, string message)
    {
        if (body is null)
            return;

        int count;
        try
        {
            if (!body.TryGetPropertyValue(Updates, out var node) || node is not JsonArray array)
                return;
            count = array.Count;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            // A body the node API cannot index (the same member name twice). Not a way round the
            // bound: ReadUpdates applies it again from the array's length, before it allocates.
            return;
        }

        if (count > maxEntries)
            throw TooManyUpdates(maxEntries, message);
    }

    public static IReadOnlyList<RecipientUpdate> ReadUpdates(JsonElement body, string message, int maxEntries = int.MaxValue)
    {
        var array = RequireArray(body, Updates, message);
        // The length is known without visiting the entries; nothing is allocated for a list that
        // is over the bound, and no entry of it is read.
        if (array.GetArrayLength() > maxEntries)
            throw TooManyUpdates(maxEntries, message);
        var updates = new List<RecipientUpdate>(array.GetArrayLength());
        foreach (var entry in array.EnumerateArray())
        {
            RequireEntryObject(entry, Updates, message);
            var did = ReadDid(RequireEntryMember(entry, RecipientDid, Updates, message), RecipientDid, message);
            var action = ReadAction(RequireEntryMember(entry, Action, Updates, message), message);
            updates.Add(new RecipientUpdate(did, action));
        }
        return updates;
    }

    /// <summary>
    /// Read <c>updated</c>. With <paramref name="maxEntries"/>, a longer list is malformed, refused
    /// from its length alone before any entry is read or allocated (L-055).
    /// </summary>
    public static IReadOnlyList<RecipientUpdateResult> ReadUpdated(JsonElement body, string message, int maxEntries = int.MaxValue)
    {
        var array = RequireArray(body, Updated, message);
        if (array.GetArrayLength() > maxEntries)
            throw TooManyEntries(Updated, maxEntries, message);
        var updated = new List<RecipientUpdateResult>(array.GetArrayLength());
        foreach (var entry in array.EnumerateArray())
        {
            RequireEntryObject(entry, Updated, message);
            var did = ReadDid(RequireEntryMember(entry, RecipientDid, Updated, message), RecipientDid, message);
            var action = ReadAction(RequireEntryMember(entry, Action, Updated, message), message);
            var result = ReadResult(RequireEntryMember(entry, Result, Updated, message), message);
            updated.Add(new RecipientUpdateResult(did, action, result));
        }
        return updated;
    }

    public static RecipientQueryPaging? ReadPaginate(JsonElement body, string message)
    {
        // Optional: absent, or an explicit null (serializers that write their unset members).
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(Paginate, out var paginate) || paginate.ValueKind == JsonValueKind.Null)
            return null;
        if (paginate.ValueKind != JsonValueKind.Object)
            throw new MalformedMessageException($"{message} '{Paginate}' must be a JSON object.");

        // "if present must include limit and offset" (the protocol page).
        var limit = ReadInteger(paginate, Limit, Paginate, message);
        var offset = ReadInteger(paginate, Offset, Paginate, message);
        if (offset < 0)
            throw new MalformedMessageException($"{message} '{Paginate}.{Offset}' must not be negative.");
        return new RecipientQueryPaging(limit, offset);
    }

    /// <summary>
    /// Read a page of registered DIDs (<paramref name="listName"/>: <c>dids</c> in 3.0, <c>keys</c>
    /// in 2.0) and its optional <c>pagination</c>. With <paramref name="maxEntries"/>, a longer list
    /// is malformed, refused from its length alone before any entry is read or allocated (L-055).
    /// </summary>
    public static (IReadOnlyList<string> Dids, RecipientPagination? Pagination) ReadRecipients(JsonElement body, string listName, string message, int maxEntries = int.MaxValue)
    {
        var array = RequireArray(body, listName, message);
        if (array.GetArrayLength() > maxEntries)
            throw TooManyEntries(listName, maxEntries, message);
        var dids = new List<string>(array.GetArrayLength());
        foreach (var entry in array.EnumerateArray())
        {
            RequireEntryObject(entry, listName, message);
            dids.Add(ReadDid(RequireEntryMember(entry, RecipientDid, listName, message), RecipientDid, message));
        }

        RecipientPagination? pagination = null;
        if (body.TryGetProperty(Pagination, out var page) && page.ValueKind != JsonValueKind.Null)
        {
            if (page.ValueKind != JsonValueKind.Object)
                throw new MalformedMessageException($"{message} '{Pagination}' must be a JSON object.");
            // "if present must include count, offset and remaining" (the protocol page).
            pagination = new RecipientPagination(
                ReadInteger(page, Count, Pagination, message),
                ReadInteger(page, Offset, Pagination, message),
                ReadInteger(page, Remaining, Pagination, message));
        }
        return (dids, pagination);
    }

    // ---- shared pieces --------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="value"/> is acceptable where an API takes a DID to put on the wire:
    /// non-empty and within the FR-API-09 bound.
    /// </summary>
    public static bool IsWithinDidLimit(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= IdentifierLimits.MaxDidLength;

    public static string ActionName(RecipientUpdateAction action) => action switch
    {
        RecipientUpdateAction.Add => ActionAdd,
        RecipientUpdateAction.Remove => ActionRemove,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static string ResultName(RecipientUpdateOutcome result) => result switch
    {
        RecipientUpdateOutcome.ClientError => ResultClientError,
        RecipientUpdateOutcome.ServerError => ResultServerError,
        RecipientUpdateOutcome.NoChange => ResultNoChange,
        RecipientUpdateOutcome.Success => ResultSuccess,
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static MalformedMessageException TooManyUpdates(int maxEntries, string message)
        => new($"{message} '{Updates}' has more than {maxEntries} entries.");

    private static MalformedMessageException TooManyEntries(string list, int maxEntries, string message)
        => new($"{message} '{list}' has more than {maxEntries} entries.");

    private static JsonElement RequireMember(JsonElement body, string name, string message)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var value))
            throw new MalformedMessageException($"{message} body is missing the REQUIRED '{name}' member.");
        return value;
    }

    private static JsonElement RequireArray(JsonElement body, string name, string message)
    {
        var value = RequireMember(body, name, message);
        if (value.ValueKind != JsonValueKind.Array)
            throw new MalformedMessageException($"{message} '{name}' must be an array.");
        return value;
    }

    private static void RequireEntryObject(JsonElement entry, string list, string message)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            throw new MalformedMessageException($"{message} '{list}' entries must be JSON objects.");
    }

    private static JsonElement RequireEntryMember(JsonElement entry, string name, string list, string message)
    {
        if (!entry.TryGetProperty(name, out var value))
            throw new MalformedMessageException($"{message} '{list}' entry is missing the REQUIRED '{name}' member.");
        return value;
    }

    private static string ReadDid(JsonElement value, string name, string message)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw new MalformedMessageException($"{message} '{name}' must be a string.");
        var did = value.GetString();
        if (string.IsNullOrEmpty(did))
            throw new MalformedMessageException($"{message} '{name}' must not be empty.");
        // FR-API-09: one length comparison, before any DID parse or resolver call; never quoted.
        if (did.Length > IdentifierLimits.MaxDidLength)
            throw new MalformedMessageException(
                $"{message} '{name}' exceeds the maximum length of {IdentifierLimits.MaxDidLength} characters (FR-API-09).");
        return did;
    }

    private static RecipientUpdateAction ReadAction(JsonElement value, string message)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.ValueEquals(ActionAdd)) return RecipientUpdateAction.Add;
            if (value.ValueEquals(ActionRemove)) return RecipientUpdateAction.Remove;
        }
        throw new MalformedMessageException($"{message} '{Action}' must be \"{ActionAdd}\" or \"{ActionRemove}\".");
    }

    private static RecipientUpdateOutcome ReadResult(JsonElement value, string message)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.ValueEquals(ResultClientError)) return RecipientUpdateOutcome.ClientError;
            if (value.ValueEquals(ResultServerError)) return RecipientUpdateOutcome.ServerError;
            if (value.ValueEquals(ResultNoChange)) return RecipientUpdateOutcome.NoChange;
            if (value.ValueEquals(ResultSuccess)) return RecipientUpdateOutcome.Success;
        }
        throw new MalformedMessageException(
            $"{message} '{Result}' must be one of \"{ResultClientError}\", \"{ResultServerError}\", \"{ResultNoChange}\", \"{ResultSuccess}\".");
    }

    // An integer JSON number, decided from the number's own text (WireJson.ReadInteger): a string
    // or a non-zero fraction is malformed, 10.0 and 1e1 read as 10, anything beyond int is clamped.
    private static int ReadInteger(JsonElement parent, string name, string parentName, string message)
        => WireJson.ReadRequiredInteger(parent, name, $"{parentName}.{name}", $"'{parentName}'", message);

    /// <summary>
    /// Read a JSON number literal as an integer clamped to <see cref="int"/>; <c>false</c> when the
    /// number has a non-zero fractional part. See <see cref="WireJson.TryReadIntegerText(ReadOnlySpan{char}, out int)"/>.
    /// </summary>
    internal static bool TryReadIntegerText(ReadOnlySpan<char> text, out int value)
        => WireJson.TryReadIntegerText(text, out value);
}
