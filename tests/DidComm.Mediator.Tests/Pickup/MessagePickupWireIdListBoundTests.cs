using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using Xunit;
using static DidComm.Mediator.Tests.Pickup.MessagePickupWireSupport;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// The entry bound of <c>message_id_list</c> (FR-PICK-10, lesson L-055): the internal
/// <c>TryParseMessagesReceived(message, maxIds, out …)</c> the mediator role calls. A list over the
/// bound is refused from its length, before the body is re-read and before any entry is decoded.
/// The proof is a list one entry too long whose extra entry is itself invalid: the answer must be
/// the count, whatever the entry holds, and however the body reached the parser.
/// </summary>
public sealed class MessagePickupWireIdListBoundTests
{
    private const string Type = P + "/messages-received";

    // How a body reaches the parser.
    private const string FromText = "read from text";          // one strict deserialization, as the receive path does
    private const string ParsedLazily = "parsed lazily";       // JsonNode.Parse: nothing decoded yet (a host's own JSON)
    private const string BuiltInCode = "built in code";        // a JsonObject assembled member by member

    // What the entry beyond the bound is.
    private const string Number = "a number";
    private const string EmptyString = "an empty string";
    private const string OverCapString = "a string over the length bound";
    private const string AnObject = "an object";
    private const string Null = "a null";
    private const string NestedArray = "an array";
    private const string SurrogateEscape = "an unpaired surrogate escape";

    private static string TooMany(int maxIds) => $"messages-received 'message_id_list' has more than {maxIds} entries.";

    // ---- the bound itself -----------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(100)]    // the default: MaxDeliveryBatch (10) × 10
    [InlineData(1000)]
    public void Exactly_the_bound_is_accepted_and_one_more_is_refused_by_the_count(int maxIds)
    {
        foreach (var presentation in new[] { FromText, ParsedLazily, BuiltInCode })
        {
            MessagePickup.TryParseMessagesReceived(Ids(presentation, maxIds), maxIds, out var atBound).Should().BeTrue(presentation);
            atBound.Should().Equal(Enumerable.Range(0, maxIds).Select(i => "id-" + i), presentation);

            var ex = Refused(() => MessagePickup.TryParseMessagesReceived(Ids(presentation, maxIds + 1), maxIds, out _));
            ex.Message.Should().Be(TooMany(maxIds), presentation);

            if (maxIds > 0)
            {
                MessagePickup.TryParseMessagesReceived(Ids(presentation, maxIds - 1), maxIds, out var under).Should().BeTrue(presentation);
                under.Should().HaveCount(maxIds - 1);
            }
        }
    }

    [Fact]
    public void The_public_parser_has_no_bound_and_another_type_is_not_a_messages_received_whatever_the_bound()
    {
        // The bound is the mediator's; someone parsing with the public method gets every entry.
        MessagePickup.TryParseMessagesReceived(Ids(FromText, 5000), out var all).Should().BeTrue();
        all.Should().HaveCount(5000);
        MessagePickup.TryParseMessagesReceived(Ids(FromText, 5000), int.MaxValue, out var unbounded).Should().BeTrue();
        unbounded.Should().HaveCount(5000);

        var other = Raw(P + "/status-request", """{"message_id_list":["id-0","id-1","id-2"]}""");
        MessagePickup.TryParseMessagesReceived(other, 1, out var ids).Should().BeFalse();
        ids.Should().BeNull();
    }

    // ---- poison beyond the bound ----------------------------------------------------------

    public static TheoryData<string, string, int> PoisonBeyondTheBound
    {
        get
        {
            var data = new TheoryData<string, string, int>();
            foreach (var maxIds in new[] { 1, 100 })
            {
                foreach (var presentation in new[] { FromText, ParsedLazily, BuiltInCode })
                {
                    foreach (var poison in new[] { Number, EmptyString, OverCapString, AnObject, Null, NestedArray, SurrogateEscape })
                    {
                        // The strict deserialization of the receive path refuses a surrogate escape
                        // outright (see the last test of this file), so that one can only be
                        // presented the way a host-built message presents it.
                        if (poison == SurrogateEscape && presentation == FromText)
                            continue;
                        data.Add(poison, presentation, maxIds);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(PoisonBeyondTheBound))]
    public void An_oversized_list_is_refused_from_its_length_before_any_entry_is_read(string poison, string presentation, int maxIds)
    {
        // L-055 / the PR #106 regression, for pickup: maxIds good entries and then one that is
        // invalid in itself. Had the entries been decoded first, the refusal would name the entry
        // (or, for the surrogate, the body); it must name the count.
        var message = Poisoned(presentation, valid: maxIds, poison);

        var ex = Refused(() => MessagePickup.TryParseMessagesReceived(message, maxIds, out _));

        ex.Message.Should().Be(TooMany(maxIds), "{0}, {1}", poison, presentation);
    }

    [Theory]
    [MemberData(nameof(PoisonBeyondTheBound))]
    public void The_count_is_the_answer_wherever_the_bad_entry_sits_and_when_every_entry_is_bad(string poison, string presentation, int maxIds)
    {
        var first = Poisoned(presentation, valid: maxIds, poison, poisonFirst: true);
        Refused(() => MessagePickup.TryParseMessagesReceived(first, maxIds, out _)).Message.Should().Be(TooMany(maxIds), "the bad entry comes first");

        var all = Poisoned(presentation, valid: 0, poison, poisonCount: maxIds + 1);
        Refused(() => MessagePickup.TryParseMessagesReceived(all, maxIds, out _)).Message.Should().Be(TooMany(maxIds), "every entry is bad");
    }

    [Theory]
    [MemberData(nameof(PoisonBeyondTheBound))]
    public void Within_the_bound_a_bad_entry_is_reported_as_itself(string poison, string presentation, int maxIds)
    {
        // The same list with room for its last entry: now the entry is read, and it is what is wrong.
        // For the surrogate this is also the proof that re-reading such a body throws: the count in
        // the tests above cannot have been taken after it.
        var message = Poisoned(presentation, valid: maxIds, poison);

        if (poison == SurrogateEscape && presentation == BuiltInCode)
        {
            // No escape here: the string itself holds half a pair, and System.Text.Json writes
            // U+FFFD in its place when the body is re-read. So this entry is not an error for the
            // reader (refusing it would be a fair answer too): never a CLR exception, and never
            // the half pair handed back.
            try
            {
                MessagePickup.TryParseMessagesReceived(message, maxIds + 1, out var ids).Should().BeTrue();
                ids.Should().HaveCount(maxIds + 1);
                ids![^1].Should().StartWith(Marker).And.NotContain(LoneSurrogate);
            }
            catch (DidComm.Exceptions.MalformedMessageException malformed)
            {
                malformed.Message.Should().NotContain(Marker);
            }

            return;
        }

        var ex = Refused(() => MessagePickup.TryParseMessagesReceived(message, maxIds + 1, out _));

        ex.Message.Should().Be(poison switch
        {
            EmptyString => "messages-received 'message_id_list' must not be empty.",
            OverCapString => "messages-received 'message_id_list' exceeds the maximum length of 2048 characters (FR-API-09).",
            SurrogateEscape => "The message 'body' is not representable as JSON.",
            _ => "messages-received 'message_id_list' must be a string.",
        }, "{0}, {1}", poison, presentation);

        // The public parser, which has no bound, says the same.
        var again = Poisoned(presentation, valid: maxIds, poison);
        Refused(() => MessagePickup.TryParseMessagesReceived(again, out _)).Message.Should().Be(ex.Message);
    }

    [Fact]
    public void A_list_that_is_not_a_list_is_the_readers_finding_bound_or_no_bound()
    {
        foreach (var maxIds in new[] { 0, 1, int.MaxValue })
        {
            Refused(() => MessagePickup.TryParseMessagesReceived(Raw(Type, """{"message_id_list":"SENTINEL"}"""), maxIds, out _))
                .Message.Should().Be("messages-received 'message_id_list' must be an array.");
            Refused(() => MessagePickup.TryParseMessagesReceived(Raw(Type, """{"message_id_list":{"0":"SENTINEL","1":"SENTINEL"}}"""), maxIds, out _))
                .Message.Should().Be("messages-received 'message_id_list' must be an array.");
            Refused(() => MessagePickup.TryParseMessagesReceived(Raw(Type, """{"message_id_list":null}"""), maxIds, out _))
                .Message.Should().Be("messages-received 'message_id_list' must be an array.");
            Refused(() => MessagePickup.TryParseMessagesReceived(Raw(Type, "{}"), maxIds, out _))
                .Message.Should().Be("messages-received body is missing the REQUIRED 'message_id_list' member.");
            Refused(() => MessagePickup.TryParseMessagesReceived(Raw(Type, null), maxIds, out _))
                .Message.Should().Be("messages-received body is missing the REQUIRED 'message_id_list' member.");
        }
    }

    // ---- a body that names the list twice -------------------------------------------------

    [Fact]
    public void A_body_that_names_message_id_list_twice_cannot_slip_past_the_bound()
    {
        // The receive path refuses duplicate member names at unpack. A host that hands the parser
        // its own JSON can still present them; the node API cannot index such a body, and the
        // bound is then applied from the array the reader will use (the last one), before it
        // allocates anything.
        const string body = """{"message_id_list":["id-0"],"message_id_list":["id-0","id-1","id-2"]}""";

        Refused(() => MessagePickup.TryParseMessagesReceived(Lazy(Type, body), 1, out _)).Message.Should().Be(TooMany(1));
        Refused(() => MessagePickup.TryParseMessagesReceived(Lazy(Type, body), 2, out _)).Message.Should().Be(TooMany(2));

        MessagePickup.TryParseMessagesReceived(Lazy(Type, body), 3, out var ids).Should().BeTrue();
        ids.Should().Equal("id-0", "id-1", "id-2");
    }

    [Fact]
    public void A_twice_named_list_over_the_bound_is_refused_by_the_count_whatever_its_entries_hold()
    {
        var poisons = string.Join(",", "7", "\"\"", "{\"id\":\"SENTINEL\"}", "\"" + IdOverCap + "\"");
        var body = $$"""{"message_id_list":["id-0"],"message_id_list":[{{poisons}}]}""";

        Refused(() => MessagePickup.TryParseMessagesReceived(Lazy(Type, body), 3, out _)).Message.Should().Be(TooMany(3));
    }

    [Fact]
    public void A_twice_named_list_holding_a_surrogate_escape_is_refused_one_way_or_the_other()
    {
        // Both tricks at once: the node API cannot index the body (so the first count is skipped)
        // and the body cannot be re-read (so the second count is never reached). The message is
        // malformed either way; which of the two reasons is given is not pinned here.
        var body = $$"""{"message_id_list":["id-0"],"message_id_list":["id-0","id-1","{{Marker}}{{LoneSurrogateEscape}}"]}""";

        Refused(() => MessagePickup.TryParseMessagesReceived(Lazy(Type, body), 1, out _));
        Refused(() => MessagePickup.TryParseMessagesReceived(Lazy(Type, body), 3, out _));
    }

    [Fact]
    public void A_body_the_node_api_cannot_index_for_another_reason_is_refused_too()
    {
        // A member name that cannot be decoded stops the first count the same way a repeated name
        // does; the body then cannot be re-read either, so the message is malformed before any id
        // is looked at.
        var unreadableName = "{\"" + Marker + LoneSurrogateEscape + "\":1,\"message_id_list\":[\"id-0\",\"id-1\",\"id-2\"]}";
        Refused(() => MessagePickup.TryParseMessagesReceived(Lazy(Type, unreadableName), 1, out _));
        Refused(() => MessagePickup.TryParseMessagesReceived(Lazy(Type, unreadableName), 3, out _));

        // A repeated name whose last definition is not a list: the reader's finding.
        const string listThenText = """{"message_id_list":["id-0","id-1","id-2"],"message_id_list":"SENTINEL"}""";
        Refused(() => MessagePickup.TryParseMessagesReceived(Lazy(Type, listThenText), 1, out _))
            .Message.Should().Be("messages-received 'message_id_list' must be an array.");
    }

    [Fact]
    public void Only_the_last_of_two_lists_is_read_so_what_an_earlier_one_holds_is_never_looked_at()
    {
        // The mirror image: the list the reader will not use is long and invalid, the one it will
        // use is within the bound.
        var poisons = string.Join(",", "7", "\"\"", "{\"id\":\"SENTINEL\"}", "\"" + IdOverCap + "\"");
        var body = $$"""{"message_id_list":[{{poisons}}],"message_id_list":["id-0"]}""";

        MessagePickup.TryParseMessagesReceived(Lazy(Type, body), 1, out var ids).Should().BeTrue();

        ids.Should().Equal("id-0");
    }

    // ---- what the refusal costs -----------------------------------------------------------

    [Theory]
    [InlineData(FromText)]
    [InlineData(ParsedLazily)]
    public void Refusing_an_oversized_list_does_not_decode_its_entries(string presentation)
    {
        // 1000 ids of 2000 characters: 2 MB of text, 4 MB once decoded to strings. Refused with a
        // bound of 1, the parse may allocate a small node per entry (a body parsed lazily is
        // indexed to be counted) and nothing that grows with what the entries hold. Measured while
        // writing this: 2.3 KB for a body read from text, 59 KB for one parsed lazily, 6 to 7 MB
        // when the same list is accepted. The assertion leaves a wide margin on both sides.
        const int entries = 1000;
        var entry = "\"" + new string('x', 2000) + "\"";
        var body = "{\"message_id_list\":[" + string.Join(",", Enumerable.Repeat(entry, entries)) + "]}";
        Refused(() => MessagePickup.TryParseMessagesReceived(Present(presentation, body), 1, out _)); // warm-up: JIT and statics
        var message = Present(presentation, body);

        // Only the parse is inside the measured window; the assertions come after it.
        Exception? caught = null;
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            MessagePickup.TryParseMessagesReceived(message, 1, out _);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        caught.Should().NotBeNull();
        caught!.Message.Should().Be(TooMany(1));
        allocated.Should().BeLessThan(512 * 1024, "decoding the entries alone would take 4 MB");
    }

    [Fact]
    public void A_million_entry_list_is_refused_as_received_without_allocating_for_it()
    {
        // FR-PICK-10 acceptance, word for word: "a 10^6-entry message_id_list (past max_ack_ids) is
        // rejected as malformed without allocation". The message is read the way the receive path
        // reads a plaintext; every entry is a valid id, so only the count is wrong. The bound is
        // the default (MaxDeliveryBatch × 10 = 100). Slow for this project, and on purpose: building
        // four megabytes of JSON and reading them into a Message once takes about a quarter of a
        // second; the refusal itself, which is what is measured, takes none of it. Measured while
        // writing this: 2.3 KB allocated, whatever the length of the list.
        const int entries = 1_000_000;
        var body = new StringBuilder("{\"message_id_list\":[", (entries * 4) + 32);
        for (var i = 0; i < entries; i++)
            body.Append(i == 0 ? "\"i\"" : ",\"i\"");
        body.Append("]}");
        Refused(() => MessagePickup.TryParseMessagesReceived(Raw(Type, """{"message_id_list":["i","i"]}"""), 1, out _)); // warm-up
        var message = Raw(Type, body.ToString());

        Exception? caught = null;
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            MessagePickup.TryParseMessagesReceived(message, 100, out _);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        caught.Should().BeOfType<DidComm.Exceptions.MalformedMessageException>().Which.Message.Should().Be(TooMany(100));
        allocated.Should().BeLessThan(64 * 1024, "one list slot per entry alone would be 8 MB");
    }

    // ---- the premises of this file --------------------------------------------------------

    [Fact]
    public void The_surrogate_poison_is_what_it_claims_to_be()
    {
        // L-049: an escape that a tool decoded on its way into this file would test nothing.
        LoneSurrogateEscape.Should().HaveLength(6);
        Encoding.ASCII.GetBytes(LoneSurrogateEscape).Should().Equal(0x5C, 0x75, 0x64, 0x38, 0x30, 0x30); // \ u d 8 0 0
        LoneSurrogate.Should().HaveLength(1);
        char.IsHighSurrogate(LoneSurrogate[0]).Should().BeTrue();

        // As text it parses, and reading the entry back is what throws.
        var parsed = JsonNode.Parse("[\"x" + LoneSurrogateEscape + "\"]")!.AsArray();
        var read = () => parsed[0]!.GetValue<string>();
        read.Should().Throw<InvalidOperationException>();

        // A body holding the escape cannot be written out again: re-reading it is what a parser
        // that counted too late would trip over.
        var lazy = Poisoned(ParsedLazily, valid: 1, SurrogateEscape);
        var rewrite = () => JsonSerializer.Serialize(lazy.Body);
        rewrite.Should().Throw<JsonException>().WithInnerException<InvalidOperationException>();

        // A body built in code has no escape, only the half pair itself, and System.Text.Json
        // writes that as U+FFFD instead of refusing it. That is why "built in code" is a weaker
        // poison than "parsed lazily" in the tables above: it still must not be read past the bound.
        var inCode = Poisoned(BuiltInCode, valid: 1, SurrogateEscape);
        var rewritten = JsonSerializer.Serialize(inCode.Body);
        rewritten.Should().NotContain(LoneSurrogate).And.Contain(Marker);

        // And the receive path's own deserialization does not let it in at all.
        var receive = () => Raw(Type, "{\"message_id_list\":[\"id-0\",\"x" + LoneSurrogateEscape + "\"]}");
        receive.Should().Throw<JsonException>();
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary><paramref name="count"/> valid ids, presented as asked.</summary>
    private static Message Ids(string presentation, int count)
    {
        if (presentation == BuiltInCode)
        {
            var array = new JsonArray();
            for (var i = 0; i < count; i++)
                array.Add("id-" + i);
            return InCode(Type, new JsonObject { ["message_id_list"] = array });
        }

        var body = "{\"message_id_list\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => $"\"id-{i}\"")) + "]}";
        return Present(presentation, body);
    }

    /// <summary>
    /// <paramref name="valid"/> good ids and <paramref name="poisonCount"/> bad entries, the bad ones
    /// last unless <paramref name="poisonFirst"/>.
    /// </summary>
    private static Message Poisoned(string presentation, int valid, string poison, bool poisonFirst = false, int poisonCount = 1)
    {
        if (presentation == BuiltInCode)
        {
            var array = new JsonArray();
            if (poisonFirst)
                AddPoison(array);
            for (var i = 0; i < valid; i++)
                array.Add("id-" + i);
            if (!poisonFirst)
                AddPoison(array);
            return InCode(Type, new JsonObject { ["message_id_list"] = array });

            void AddPoison(JsonArray target)
            {
                for (var i = 0; i < poisonCount; i++)
                    target.Add(PoisonNode(poison));
            }
        }

        var good = Enumerable.Range(0, valid).Select(i => $"\"id-{i}\"");
        var bad = Enumerable.Repeat(PoisonText(poison), poisonCount);
        var entries = poisonFirst ? bad.Concat(good) : good.Concat(bad);
        return Present(presentation, "{\"message_id_list\":[" + string.Join(",", entries) + "]}");
    }

    private static Message Present(string presentation, string body) => presentation switch
    {
        FromText => Raw(Type, body),
        ParsedLazily => Lazy(Type, body),
        _ => throw new ArgumentOutOfRangeException(nameof(presentation)),
    };

    private static string PoisonText(string poison) => poison switch
    {
        Number => "7",
        EmptyString => "\"\"",
        OverCapString => "\"" + IdOverCap + "\"",
        AnObject => "{\"id\":\"" + Marker + "\"}",
        Null => "null",
        NestedArray => "[\"" + Marker + "\"]",
        SurrogateEscape => "\"" + Marker + LoneSurrogateEscape + "\"",
        _ => throw new ArgumentOutOfRangeException(nameof(poison)),
    };

    private static JsonNode? PoisonNode(string poison) => poison switch
    {
        Number => JsonValue.Create(7),
        EmptyString => JsonValue.Create(string.Empty),
        OverCapString => JsonValue.Create(IdOverCap),
        AnObject => new JsonObject { ["id"] = Marker },
        Null => null,
        NestedArray => new JsonArray(Marker),
        // In code there is no escape to write: the string itself holds half a pair.
        SurrogateEscape => JsonValue.Create(Marker + LoneSurrogate),
        _ => throw new ArgumentOutOfRangeException(nameof(poison)),
    };
}
