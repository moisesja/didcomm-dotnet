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
/// The strict parsers of <see cref="MessagePickup"/> (FR-PICK-02..07, FR-API-09, JC-11): what each
/// accepts, and the rejection table per message and per member. Every refusal is a
/// <see cref="DidComm.Exceptions.MalformedMessageException"/> whose text names the message, the
/// member and the rule and quotes nothing from the message (each bad value below carries the word
/// SENTINEL; <see cref="MessagePickupWireSupport.Refused"/> checks for it). The entry bound of
/// <c>message_id_list</c> has its own file.
/// </summary>
public sealed class MessagePickupWireParserTests
{
    // ---- type matching --------------------------------------------------------------------

    public static TheoryData<string, string, bool> TypeMatches
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (var name in MessageNames)
            {
                data.Add(name, $"https://didcomm.org/messagepickup/3.0/{name}", true);
                data.Add(name, $"https://didcomm.org/messagepickup/3.1/{name}", true);                      // any 3.x (FR-PROTO-02)
                data.Add(name, $"https://didcomm.org/messagepickup/3.99/{name}", true);
                data.Add(name, $"https://didcomm.org/messagepickup/3.0/{name.ToUpperInvariant()}", true);    // case (FR-PROTO-01)
                data.Add(name, $"HTTPS://DIDCOMM.ORG/MessagePickup/3.0/{name}", true);
                data.Add(name, $"https://didcomm.org/messagepickup/3.0/{name.Replace('-', '_')}", true);     // punctuation
                data.Add(name, $"https://didcomm.org/message-pickup/3.0/{name.Replace("-", string.Empty)}", true);
                data.Add(name, $"https://didcomm.org/message_pickup/3.0/{name}", true);
                data.Add(name, $"https://didcomm.org/messagepickup/2.0/{name}", false);                      // other major
                data.Add(name, $"https://didcomm.org/messagepickup/4.0/{name}", false);
                data.Add(name, $"https://didcomm.org/messagepickup/30.0/{name}", false);
                data.Add(name, $"https://didcomm.org/messagepickup/03.0/{name}", false);                     // not a canonical version
                data.Add(name, $"https://didcomm.org/messagepickup/3/{name}", false);
                data.Add(name, $"https://example.org/messagepickup/3.0/{name}", false);                      // other doc URI
                data.Add(name, $"https://didcomm.org/pickup/3.0/{name}", false);                             // other protocol
                data.Add(name, $"https://didcomm.org/messagepickup/3.0/{name}/", false);
                data.Add(name, $"https://didcomm.org/messagepickup/3.0/{name}s", false);
                data.Add(name, "https://didcomm.org/messagepickup/3.0/", false);
                data.Add(name, name, false);
                data.Add(name, string.Empty, false);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(TypeMatches))]
    public void Types_match_as_the_handler_registry_matches_them(string name, string type, bool expected)
        => Parse(name, Raw(type, ValidBody(name))).Should().Be(expected);

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in MessageNames)
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_message_is_claimed_by_its_own_parser_and_by_no_other(string name)
    {
        var message = Raw(P + "/" + name, ValidBody(name));

        MessageNames.Where(parser => Parse(parser, message)).Should().Equal(new[] { name },
            "`status` is not `status-request`, `delivery` is not `delivery-request`");
    }

    [Fact]
    public void A_parser_returns_false_for_another_type_whatever_the_message_holds()
    {
        // Garbage that is malformed for every parser; none may throw for a type it does not own.
        const string body = """{"recipient_did":7,"message_count":"SENTINEL","limit":"SENTINEL","message_id_list":7,"live_delivery":"SENTINEL","total_bytes":-1}""";
        const string attachments = """[{"data":{"base64":"SENTINEL!"}},null]""";
        var foreign = new[]
        {
            "https://didcomm.org/trust-ping/2.0/ping",
            "https://didcomm.org/coordinate-mediation/3.0/recipient-update",
            "https://didcomm.org/messagepickup/4.0/status-request",
            "https://didcomm.org/messagepickup/4.0/delivery-request",
            "https://didcomm.org/messagepickup/2.0/status",
            "https://didcomm.org/messagepickup/3.0/message-received", // the name the page's prose uses once; not a message type
            "not a type",
            string.Empty,
        };

        foreach (var type in foreign)
        {
            var other = Raw(type, body, attachments);

            MessagePickup.TryParseStatusRequest(other, out var statusFilter).Should().BeFalse(type);
            statusFilter.Should().BeNull();
            MessagePickup.TryParseStatus(other, out var status).Should().BeFalse(type);
            status.Should().BeNull();
            MessagePickup.TryParseDeliveryRequest(other, out var limit, out var deliveryFilter).Should().BeFalse(type);
            limit.Should().Be(0);
            deliveryFilter.Should().BeNull();
            MessagePickup.TryParseDelivery(other, out var messages, out var echoed).Should().BeFalse(type);
            messages.Should().BeNull();
            echoed.Should().BeNull();
            MessagePickup.TryParseMessagesReceived(other, out var ids).Should().BeFalse(type);
            ids.Should().BeNull();
            MessagePickup.TryParseMessagesReceived(other, 1, out var boundedIds).Should().BeFalse(type);
            boundedIds.Should().BeNull();
            MessagePickup.TryParseLiveDeliveryChange(other, out var live).Should().BeFalse(type);
            live.Should().BeFalse();
        }
    }

    [Fact]
    public void A_parser_does_not_read_the_body_of_another_pickup_message()
    {
        // The same garbage under each pickup type: only the owner of the type may object.
        const string body = """{"recipient_did":7,"message_count":"SENTINEL","limit":"SENTINEL","message_id_list":7,"live_delivery":"SENTINEL"}""";
        const string attachments = """[{"data":{"base64":"SENTINEL!"}}]""";

        foreach (var owner in MessageNames)
        {
            var message = Raw(P + "/" + owner, body, attachments);
            foreach (var parser in MessageNames.Where(n => n != owner))
                Parse(parser, message).Should().BeFalse("{0} is not a {1}", owner, parser);
            Refused(() => Parse(owner, message));
        }
    }

    [Fact]
    public void A_body_no_json_text_can_hold_is_not_read_for_another_type()
    {
        var other = InCode("https://didcomm.org/trust-ping/2.0/ping", new JsonObject { ["recipient_did"] = Marker + LoneSurrogate });

        foreach (var parser in MessageNames)
            Parse(parser, other).Should().BeFalse();
    }

    [Fact]
    public void A_null_message_is_an_argument_error_and_a_message_without_a_type_is_not_a_pickup_message()
    {
        var parsers = new Action<Message>[]
        {
            m => MessagePickup.TryParseStatusRequest(m, out _),
            m => MessagePickup.TryParseStatus(m, out _),
            m => MessagePickup.TryParseDeliveryRequest(m, out _, out _),
            m => MessagePickup.TryParseDelivery(m, out _, out _),
            m => MessagePickup.TryParseMessagesReceived(m, out _),
            m => MessagePickup.TryParseMessagesReceived(m, 1, out _),
            m => MessagePickup.TryParseLiveDeliveryChange(m, out _),
        };

        foreach (var parser in parsers)
        {
            var act = () => parser(null!);
            act.Should().Throw<ArgumentNullException>();
        }

        var untyped = new Message { Id = "m-1", Type = null! };
        foreach (var name in MessageNames)
            Parse(name, untyped).Should().BeFalse();
    }

    // ---- recipient_did, in the four messages that carry it --------------------------------

    private static readonly (string Name, string Template)[] RecipientDidCarriers =
    {
        ("status-request", """{"recipient_did":VALUE}"""),
        ("status", """{"message_count":1,"recipient_did":VALUE}"""),
        ("delivery-request", """{"limit":1,"recipient_did":VALUE}"""),
        ("delivery", """{"recipient_did":VALUE}"""),
    };

    private static string? RecipientDidOf(string name, Message message)
    {
        string? recipientDid;
        switch (name)
        {
            case "status-request":
                MessagePickup.TryParseStatusRequest(message, out recipientDid).Should().BeTrue();
                return recipientDid;
            case "status":
                MessagePickup.TryParseStatus(message, out var status).Should().BeTrue();
                return status!.RecipientDid;
            case "delivery-request":
                MessagePickup.TryParseDeliveryRequest(message, out _, out recipientDid).Should().BeTrue();
                return recipientDid;
            case "delivery":
                MessagePickup.TryParseDelivery(message, out _, out recipientDid).Should().BeTrue();
                return recipientDid;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    public static TheoryData<string, string, string?> RecipientDidValues
    {
        get
        {
            var data = new TheoryData<string, string, string?>();
            foreach (var (name, _) in RecipientDidCarriers)
            {
                data.Add(name, "\"did:example:r1\"", "did:example:r1");
                data.Add(name, "null", null);                                   // an explicit null is "not present"
                data.Add(name, "\"did:example:r1#key-1\"", "did:example:r1#key-1"); // whether it is a bare DID is the handler's question
                data.Add(name, "\"not a did\"", "not a did");
                data.Add(name, "\" \"", " ");                                    // only emptiness and length are the parser's
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RecipientDidValues))]
    public void A_recipient_did_is_read_as_given_and_null_reads_as_absent(string name, string json, string? expected)
    {
        var template = RecipientDidCarriers.Single(c => c.Name == name).Template;

        RecipientDidOf(name, Raw(P + "/" + name, template.Replace("VALUE", json))).Should().Be(expected);
    }

    public static TheoryData<string, string> BadRecipientDids
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (name, _) in RecipientDidCarriers)
            {
                foreach (var json in new[]
                {
                    "7", "1.5", "true", "false", "[]", "[\"did:example:SENTINEL\"]", "{}", "{\"did\":\"did:example:SENTINEL\"}", "\"\"",
                })
                {
                    data.Add(name, json);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BadRecipientDids))]
    public void A_recipient_did_of_the_wrong_shape_is_malformed(string name, string json)
    {
        var template = RecipientDidCarriers.Single(c => c.Name == name).Template;
        var message = Raw(P + "/" + name, template.Replace("VALUE", json));

        var ex = Refused(() => Parse(name, message));

        ex.Message.Should().StartWith(name + " ").And.Contain("'recipient_did'");
    }

    [Fact]
    public void A_recipient_did_is_accepted_at_2048_characters_and_refused_beyond_without_being_echoed()
    {
        DidAtCap.Length.Should().Be(2048);
        DidOverCap.Length.Should().Be(2049);

        foreach (var (name, template) in RecipientDidCarriers)
        {
            RecipientDidOf(name, Raw(P + "/" + name, template.Replace("VALUE", "\"" + DidAtCap + "\""))).Should().Be(DidAtCap, name);

            foreach (var tooLong in new[] { DidOverCap, NearMegabyte })
            {
                var message = Raw(P + "/" + name, template.Replace("VALUE", "\"" + tooLong + "\""));
                var ex = Refused(() => Parse(name, message));
                ex.Message.Should().Be($"{name} 'recipient_did' exceeds the maximum length of 2048 characters (FR-API-09).");
            }
        }
    }

    // ---- status-request -------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"recipient_did":null}""")]
    [InlineData("""{"RECIPIENT_DID":"did:example:r1"}""")]   // member names are case-sensitive: this is an unknown member
    [InlineData("""{"recipientDid":"did:example:r1"}""")]
    public void A_status_request_without_recipient_did_asks_about_everything(string? body)
    {
        MessagePickup.TryParseStatusRequest(Raw(P + "/status-request", body), out var recipientDid).Should().BeTrue();
        recipientDid.Should().BeNull();
    }

    [Fact]
    public void A_status_request_ignores_members_the_protocol_does_not_define()
    {
        var message = Raw(P + "/status-request", """{"future":{"a":[1,2]},"recipient_did":"did:example:r1","limit":"SENTINEL","message_id_list":7}""");

        MessagePickup.TryParseStatusRequest(message, out var recipientDid).Should().BeTrue();
        recipientDid.Should().Be("did:example:r1");
    }

    // ---- status ---------------------------------------------------------------------------

    public static TheoryData<string, PickupStatus> Statuses => new()
    {
        { """{"message_count":0}""", new PickupStatus(0) },
        { """{"message_count":7}""", new PickupStatus(7) },
        { """{"message_count":7.0}""", new PickupStatus(7) },                        // JSON has one number type
        { """{"message_count":7e0}""", new PickupStatus(7) },
        { """{"message_count":70e-1}""", new PickupStatus(7) },
        { """{"message_count":0.7e1}""", new PickupStatus(7) },
        { """{"message_count":-0}""", new PickupStatus(0) },
        { """{"message_count":-0.0}""", new PickupStatus(0) },
        { """{"message_count":2147483647}""", new PickupStatus(int.MaxValue) },
        { """{"message_count":2147483648}""", new PickupStatus(int.MaxValue) },      // the count is an int: clamped
        { """{"message_count":9223372036854775808}""", new PickupStatus(int.MaxValue) },
        { """{"message_count":1e400}""", new PickupStatus(int.MaxValue) },
        // every optional member as an explicit null: "not present"
        { """{"message_count":1,"recipient_did":null,"longest_waited_seconds":null,"newest_received_time":null,"oldest_received_time":null,"total_bytes":null,"live_delivery":null}""", new PickupStatus(1) },
        // unknown members, and names that differ only in case, are ignored
        { """{"message_count":1,"future":{"a":[1]},"MESSAGE_COUNT":9,"Live_Delivery":true,"totalBytes":5}""", new PickupStatus(1) },
        // member order is free
        { """{"live_delivery":true,"total_bytes":5,"message_count":2}""", new PickupStatus(2, TotalBytes: 5, LiveDelivery: true) },
        { """{"message_count":1,"live_delivery":false}""", new PickupStatus(1, LiveDelivery: false) },
        // each 64-bit member, on its own
        { """{"message_count":1,"longest_waited_seconds":0}""", new PickupStatus(1, LongestWaitedSeconds: 0) },
        { """{"message_count":1,"longest_waited_seconds":36e2}""", new PickupStatus(1, LongestWaitedSeconds: 3600) },
        { """{"message_count":1,"newest_received_time":1658085169.0}""", new PickupStatus(1, NewestReceivedTime: 1658085169) },
        { """{"message_count":1,"oldest_received_time":16580842930e-1}""", new PickupStatus(1, OldestReceivedTime: 1658084293) },
        { """{"message_count":1,"total_bytes":-0}""", new PickupStatus(1, TotalBytes: 0) },
        // beyond 32 bits is not clamped ...
        { """{"message_count":1,"total_bytes":2147483648}""", new PickupStatus(1, TotalBytes: 2147483648) },
        { """{"message_count":1,"newest_received_time":1658085169123}""", new PickupStatus(1, NewestReceivedTime: 1658085169123) }, // a 4.0-style millisecond value fits
        { """{"message_count":1,"total_bytes":9223372036854775807}""", new PickupStatus(1, TotalBytes: long.MaxValue) },
        { """{"message_count":1,"total_bytes":9223372036854775807.0}""", new PickupStatus(1, TotalBytes: long.MaxValue) },
        { """{"message_count":1,"total_bytes":9.223372036854775807e18}""", new PickupStatus(1, TotalBytes: long.MaxValue) },
        // ... beyond 64 bits is
        { """{"message_count":1,"total_bytes":9223372036854775808}""", new PickupStatus(1, TotalBytes: long.MaxValue) },
        { """{"message_count":1,"longest_waited_seconds":18446744073709551616}""", new PickupStatus(1, LongestWaitedSeconds: long.MaxValue) },
        { """{"message_count":1,"newest_received_time":1e19}""", new PickupStatus(1, NewestReceivedTime: long.MaxValue) },
        { """{"message_count":1,"oldest_received_time":1e400}""", new PickupStatus(1, OldestReceivedTime: long.MaxValue) },
        { """{"message_count":1,"total_bytes":123456789012345678901234567890.0}""", new PickupStatus(1, TotalBytes: long.MaxValue) },
    };

    [Theory]
    [MemberData(nameof(Statuses))]
    public void A_status_is_read_member_by_member(string body, PickupStatus expected)
    {
        MessagePickup.TryParseStatus(Raw(P + "/status", body), out var status).Should().BeTrue();
        status.Should().Be(expected);
    }

    public static TheoryData<string?, string> BadStatuses
    {
        get
        {
            var data = new TheoryData<string?, string>
            {
                // message_count is the one REQUIRED member
                { null, "message_count" },
                { "{}", "message_count" },
                { """{"MESSAGE_COUNT":1}""", "message_count" },
                { """{"messageCount":1}""", "message_count" },
                { """{"recipient_did":"did:example:SENTINEL","total_bytes":1,"live_delivery":true}""", "message_count" },
            };

            foreach (var count in new[]
            {
                "null", "\"7\"", "\"SENTINEL\"", "true", "false", "[7]", "{}", "1.5", "0.5", "1e-3", "7.000000000000000000000000000001",
                "-1", "-0.5", "-2147483648", "-2147483649", "-9223372036854775809", "-1e400",
            })
            {
                data.Add($$"""{"message_count":{{count}}}""", "message_count");
            }

            foreach (var member in new[] { "longest_waited_seconds", "newest_received_time", "oldest_received_time", "total_bytes" })
            {
                foreach (var value in new[]
                {
                    "-1", "-9223372036854775808", "-9223372036854775809", "-1e400", "-0.5",
                    "1.5", "0.5", "1e-1", "1e-400", "9223372036854775807.5", "123456789012345678901234567890.5",
                    "\"1\"", "\"SENTINEL\"", "true", "false", "[1]", "{}",
                })
                {
                    data.Add($$"""{"message_count":1,"{{member}}":{{value}}}""", member);
                }
            }

            foreach (var live in new[] { "\"true\"", "\"SENTINEL\"", "1", "0", "[]", "[true]", "{}" })
                data.Add($$"""{"message_count":1,"live_delivery":{{live}}}""", "live_delivery");

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BadStatuses))]
    public void A_malformed_status_is_refused_naming_the_member(string? body, string member)
    {
        var message = Raw(P + "/status", body);

        var ex = Refused(() => MessagePickup.TryParseStatus(message, out _));

        ex.Message.Should().StartWith("status ").And.Contain($"'{member}'");
    }

    [Fact]
    public void A_status_integer_of_a_thousand_digits_is_clamped_not_an_error()
    {
        var huge = new string('9', 1000);

        MessagePickup.TryParseStatus(Raw(P + "/status", $$"""{"message_count":{{huge}},"total_bytes":{{huge}},"longest_waited_seconds":{{huge}}.000}"""), out var status).Should().BeTrue();

        status.Should().Be(new PickupStatus(int.MaxValue, TotalBytes: long.MaxValue, LongestWaitedSeconds: long.MaxValue));
        Refused(() => MessagePickup.TryParseStatus(Raw(P + "/status", $$"""{"message_count":1,"total_bytes":-{{huge}}}"""), out _))
            .Message.Should().Be("status 'total_bytes' must not be negative.");
        Refused(() => MessagePickup.TryParseStatus(Raw(P + "/status", $$"""{"message_count":1,"total_bytes":{{huge}}.5}"""), out _))
            .Message.Should().Be("status 'total_bytes' must be an integer.");
    }

    // ---- delivery-request -----------------------------------------------------------------

    [Theory]
    [InlineData("""{"limit":0}""", 0)]                               // zero is valid: it asks for nothing
    [InlineData("""{"limit":-0}""", 0)]
    [InlineData("""{"limit":0.0}""", 0)]
    [InlineData("""{"limit":0e5}""", 0)]
    [InlineData("""{"limit":1}""", 1)]
    [InlineData("""{"limit":10}""", 10)]
    [InlineData("""{"limit":10.0}""", 10)]                           // JSON does not tell 10 from 10.0 ...
    [InlineData("""{"limit":10.000}""", 10)]
    [InlineData("""{"limit":1e1}""", 10)]                            // ... or from 1e1
    [InlineData("""{"limit":1E1}""", 10)]
    [InlineData("""{"limit":1e+1}""", 10)]
    [InlineData("""{"limit":100e-1}""", 10)]
    [InlineData("""{"limit":0.1e2}""", 10)]
    [InlineData("""{"limit":2147483647}""", int.MaxValue)]
    [InlineData("""{"limit":2147483647.0}""", int.MaxValue)]
    [InlineData("""{"limit":2147483648}""", int.MaxValue)]           // beyond 32 bits: clamped
    [InlineData("""{"limit":2147483648.0}""", int.MaxValue)]
    [InlineData("""{"limit":99999999999}""", int.MaxValue)]
    [InlineData("""{"limit":9223372036854775807}""", int.MaxValue)]
    [InlineData("""{"limit":9223372036854775808}""", int.MaxValue)]  // beyond 64 bits
    [InlineData("""{"limit":100000000000000000000000000000}""", int.MaxValue)] // beyond System.Decimal
    [InlineData("""{"limit":1e400}""", int.MaxValue)]                // beyond System.Double: still an integer
    [InlineData("""{"limit":1e99999999999999999999}""", int.MaxValue)]
    public void A_limit_is_read_as_an_integer_and_clamped(string body, int expected)
    {
        MessagePickup.TryParseDeliveryRequest(Raw(P + "/delivery-request", body), out var limit, out var recipientDid).Should().BeTrue();
        limit.Should().Be(expected);
        recipientDid.Should().BeNull();
    }

    [Fact]
    public void A_delivery_request_reads_limit_and_filter_in_any_order_and_ignores_the_rest()
    {
        MessagePickup.TryParseDeliveryRequest(Raw(P + "/delivery-request", """{"recipient_did":"did:example:r1","future":[1],"limit":3,"message_count_limit":99}"""), out var limit, out var recipientDid).Should().BeTrue();
        limit.Should().Be(3);
        recipientDid.Should().Be("did:example:r1");
    }

    public static TheoryData<string?> BadLimits
    {
        get
        {
            var data = new TheoryData<string?>
            {
                // limit is REQUIRED
                null,
                "{}",
                """{"recipient_did":"did:example:SENTINEL"}""",
                """{"Limit":10}""",
                """{"LIMIT":10}""",
                """{"message_count_limit":10}""",   // the 4.0 name is not the 3.0 name
            };

            foreach (var limit in new[]
            {
                "-1", "-10", "-2147483648", "-2147483649", "-9223372036854775809", "-1e400", "-0.5", "-1.0", "-1e0", // negative
                "\"10\"", "\"SENTINEL\"", "\"\"",                                                                      // a string
                "1.5", "10.5", "0.1", "1e-3", "15e-1", "1e-400", "1.00000000000000000000000000001", "2147483647.5", // a fraction
                "true", "false", "null", "[10]", "[]", "{}", "{\"value\":10}",                                       // not a number
            })
            {
                data.Add($$"""{"limit":{{limit}}}""");
                data.Add($$"""{"recipient_did":"did:example:SENTINEL","limit":{{limit}}}""");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BadLimits))]
    public void A_missing_negative_fractional_or_non_numeric_limit_is_malformed(string? body)
    {
        var message = Raw(P + "/delivery-request", body);

        var ex = Refused(() => MessagePickup.TryParseDeliveryRequest(message, out _, out _));

        ex.Message.Should().StartWith("delivery-request ").And.Contain("'limit'");
    }

    [Fact]
    public void A_limit_says_which_rule_it_broke()
    {
        string Why(string? body) => Refused(() => MessagePickup.TryParseDeliveryRequest(Raw(P + "/delivery-request", body), out _, out _)).Message;

        Why(null).Should().Be("delivery-request body is missing the REQUIRED 'limit' member.");
        Why("{}").Should().Be("delivery-request body is missing the REQUIRED 'limit' member.");
        Why("""{"limit":-1}""").Should().Be("delivery-request 'limit' must not be negative.");
        Why("""{"limit":-1e400}""").Should().Be("delivery-request 'limit' must not be negative.");
        Why("""{"limit":"10"}""").Should().Be("delivery-request 'limit' must be an integer.");
        Why("""{"limit":1.5}""").Should().Be("delivery-request 'limit' must be an integer.");
        Why("""{"limit":null}""").Should().Be("delivery-request 'limit' must be an integer.");
        Why("""{"limit":true}""").Should().Be("delivery-request 'limit' must be an integer.");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2147483647")]
    [InlineData("2147483648")]
    [InlineData("1e400")]
    [InlineData("1e99999999999999999999")]
    [InlineData("thousand-digits")]
    public void A_huge_limit_costs_no_more_to_read_than_a_small_one(string literal)
    {
        // Adversarial acceptance of #94: limit 2^31-1 and beyond is clamped "without allocation".
        // The parser hands back an int and sizes nothing by it: what it allocates is bounded by the
        // length of the number's text (the raw text is copied once when the value is beyond 64
        // bits), never by the number. Measured, not assumed: an allocation that followed the value
        // would be at least 2 GiB for int.MaxValue and impossible for 1e400; the bound below is
        // 64 KiB. (Measured while writing this: about 1.3 KiB, and 4.3 KiB for a thousand digits.)
        if (literal == "thousand-digits")
            literal = new string('9', 1000);
        var message = Raw(P + "/delivery-request", $$"""{"limit":{{literal}}}""");
        MessagePickup.TryParseDeliveryRequest(message, out _, out _).Should().BeTrue(); // warm-up: JIT and statics

        var before = GC.GetAllocatedBytesForCurrentThread();
        var parsed = MessagePickup.TryParseDeliveryRequest(message, out var limit, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        parsed.Should().BeTrue();
        limit.Should().Be(literal == "1" ? 1 : int.MaxValue);
        allocated.Should().BeLessThan(64 * 1024);
    }

    // ---- delivery -------------------------------------------------------------------------

    /// <summary>One attachment descriptor as JSON text: <c>{"id": …, "data": {"base64": …}}</c>.</summary>
    private static string Base64Attachment(string id, string base64)
        => "{\"id\":" + JsonSerializer.Serialize(id) + ",\"data\":{\"base64\":" + JsonSerializer.Serialize(base64) + "}}";

    [Fact]
    public void A_delivery_without_attachments_is_an_empty_batch()
    {
        foreach (var message in new[]
        {
            Raw(P + "/delivery", null),
            Raw(P + "/delivery", "{}"),
            Raw(P + "/delivery", "{}", "[]"),
            Raw(P + "/delivery", "{}", "null"),
            Raw(P + "/delivery", """{"future":1}""", "[]"),
        })
        {
            MessagePickup.TryParseDelivery(message, out var messages, out var recipientDid).Should().BeTrue();
            messages.Should().BeEmpty();
            recipientDid.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("-_8", "FBFF")]      // base64url without padding: what this library writes
    [InlineData("-_8=", "FBFF")]     // base64url, padded
    [InlineData("+/8=", "FBFF")]     // standard base64, padded
    [InlineData("+/8", "FBFF")]      // standard base64 without padding
    [InlineData("+_8", "FBFF")]      // the two alphabets mixed: unambiguous, so accepted
    [InlineData("-/8=", "FBFF")]
    [InlineData("-_-_", "FBFFBF")]
    [InlineData("+/+/", "FBFFBF")]
    [InlineData("QQ", "41")]
    [InlineData("QQ==", "41")]
    [InlineData("QUI", "4142")]
    [InlineData("QUI=", "4142")]
    [InlineData("QUJD", "414243")]
    [InlineData("QUJDRA", "41424344")]
    [InlineData("QUJDRA==", "41424344")]
    public void Attachment_data_is_decoded_in_either_alphabet_padded_or_not(string base64, string expectedHex)
    {
        // JC-11: lenient on receive. Other mediators pad, and some write the standard alphabet.
        var message = Raw(P + "/delivery", "{}", "[" + Base64Attachment("id-1", base64) + "]");

        MessagePickup.TryParseDelivery(message, out var messages, out _).Should().BeTrue();

        var delivered = messages.Should().ContainSingle().Subject;
        delivered.Id.Should().Be("id-1");
        Convert.ToHexString(delivered.Payload.Span).Should().Be(expectedHex);
    }

    [Theory]
    [InlineData("!!!!")]
    [InlineData("QUJD!")]
    [InlineData("SENTINEL*")]
    [InlineData("QUJD.QUJD")]     // two segments, as in a compact JWS
    [InlineData("QUJD,QUJD")]
    [InlineData("Q")]             // no byte is one character long
    [InlineData("QUJDR")]
    [InlineData("=")]
    [InlineData("====")]
    [InlineData("QQ==QQ==")]      // two padded strings glued together
    public void Attachment_data_that_is_not_base64_is_malformed(string base64)
    {
        var message = Raw(P + "/delivery", "{}", "[" + Base64Attachment(Marker + "-id", base64) + "]");

        var ex = Refused(() => MessagePickup.TryParseDelivery(message, out _, out _));

        ex.Message.Should().Be("delivery attachment 'data.base64' is not valid base64.");
    }

    [Fact]
    public void Attachment_data_may_be_inline_json_instead()
    {
        // An encrypted message is JSON, so a mediator may attach it as data.json.
        const string jwe = """{"protected":"eyJhIjoiYiJ9","recipients":[{"header":{"kid":"did:example:alice#key-1"},"encrypted_key":"AAEC"}],"iv":"AAEC","ciphertext":"AAEC","tag":"AAEC"}""";
        var message = Raw(P + "/delivery", "{}", "[{\"id\":\"id-1\",\"data\":{\"json\":" + jwe + "}}," + """{"id":"id-2","data":{"json":[1,"two",null]}},{"id":"id-3","data":{"json":"text"}}]""");

        MessagePickup.TryParseDelivery(message, out var messages, out _).Should().BeTrue();

        messages!.Select(m => m.Id).Should().Equal("id-1", "id-2", "id-3");
        JsonNode.DeepEquals(JsonNode.Parse(messages![0].Payload.Span), JsonNode.Parse(jwe)).Should().BeTrue("the payload is the JSON text of data.json");
        JsonNode.DeepEquals(JsonNode.Parse(messages![1].Payload.Span), JsonNode.Parse("""[1,"two",null]""")).Should().BeTrue();
        Encoding.UTF8.GetString(messages![2].Payload.Span).Should().Be("\"text\"");
    }

    [Fact]
    public void Base64_data_wins_when_an_attachment_carries_both_forms()
    {
        var message = Raw(P + "/delivery", "{}", """[{"id":"id-1","data":{"json":{"ignored":true},"base64":"QUJD"}}]""");

        MessagePickup.TryParseDelivery(message, out var messages, out _).Should().BeTrue();

        messages!.Single().Payload.ToArray().Should().Equal(0x41, 0x42, 0x43);
    }

    public static TheoryData<string, byte[]> Payloads => new()
    {
        { "a plaintext DIDComm message", Encoding.UTF8.GetBytes("""{"id":"1","type":"https://didcomm.org/basicmessage/2.0/message","body":{"content":"hi"}}""") },
        { "a signed message", Encoding.UTF8.GetBytes("""{"payload":"e30","signatures":[{"protected":"e30","signature":"AA"}]}""") },
        { "text that is not JSON", Encoding.UTF8.GetBytes("this is not a DIDComm message") },
        { "truncated JSON", Encoding.UTF8.GetBytes("""{"protected":"eyJ""") },
        { "one zero byte", new byte[] { 0 } },
        { "bytes that are not UTF-8", new byte[] { 0xFF, 0xFE, 0xC0, 0x80, 0xED, 0xA0, 0x80 } },
        { "another delivery", Encoding.UTF8.GetBytes("""{"id":"2","type":"https://didcomm.org/messagepickup/3.0/delivery","attachments":[{"id":"x","data":{"base64":"!!"}}]}""") },
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void The_payload_is_decoded_and_never_inspected(string what, byte[] payload)
    {
        // JC-11 and the issue's adversarial acceptance: the mediator relays bytes. Whether an
        // attachment is an encrypted DIDComm message is for the recipient's unpack to say, one
        // attachment at a time; tightening it here would cost a whole batch for one bad entry.
        var message = Raw(P + "/delivery", "{}", "[" + Base64Attachment("id-1", System.Buffers.Text.Base64Url.EncodeToString(payload)) + "]");

        MessagePickup.TryParseDelivery(message, out var messages, out _).Should().BeTrue(what);

        messages!.Single().Payload.ToArray().Should().Equal(payload, what);
    }

    [Fact]
    public void Attachments_are_read_in_wire_order_with_repeats_and_with_members_the_protocol_does_not_use()
    {
        const string attachments = """
            [
              {"id":"id-2","data":{"base64":"QQ"}},
              {"id":"id-1","description":"SENTINEL","filename":"m.json","media_type":"application/didcomm-encrypted+json","format":"x","lastmod_time":1,"byte_count":999999,"future":{"a":1},"data":{"base64":"QUI","hash":"SENTINEL","jws":{"signature":"AA"},"future":1}},
              {"id":"id-2","data":{"base64":"QUJD"}},
              {"id":"an id / with # reserved ? characters","data":{"base64":"QQ"}}
            ]
            """;
        var message = Raw(P + "/delivery", """{"recipient_did":"did:example:r1"}""", attachments);

        MessagePickup.TryParseDelivery(message, out var messages, out var recipientDid).Should().BeTrue();

        recipientDid.Should().Be("did:example:r1");
        messages!.Select(m => m.Id).Should().Equal("id-2", "id-1", "id-2", "an id / with # reserved ? characters");
        messages!.Select(m => m.Payload.Length).Should().Equal(new[] { 1, 2, 3, 1 }, "byte_count is the sender's claim; the bytes are what was sent");
    }

    public static TheoryData<string, string> BadAttachments => new()
    {
        { "[null]", "attachments must be JSON objects" },
        { """[{"data":{"base64":"QQ"}}]""", "'id'" },
        { """[{"id":null,"data":{"base64":"QQ"}}]""", "'id'" },
        { """[{"id":"","data":{"base64":"QQ"}}]""", "'id'" },
        { """[{"ID":"SENTINEL","data":{"base64":"QQ"}}]""", "'id'" },                       // member names are case-sensitive
        { """[{"id":"SENTINEL"}]""", "'data.base64'" },
        { """[{"id":"SENTINEL","data":null}]""", "'data.base64'" },
        { """[{"id":"SENTINEL","data":{}}]""", "'data.base64'" },
        { """[{"id":"SENTINEL","data":{"base64":null}}]""", "'data.base64'" },
        { """[{"id":"SENTINEL","data":{"json":null}}]""", "'data.json'" },
        { """[{"id":"SENTINEL","data":{"base64":null,"json":null}}]""", "'data.base64'" },
        { """[{"id":"SENTINEL","data":{"BASE64":"QQ"}}]""", "'data.base64'" },
        { """[{"id":"SENTINEL","data":{"links":["https://example.com/SENTINEL"],"hash":"SENTINEL"}}]""", "'data.base64'" }, // by reference: nothing inline to deliver
        { """[{"id":"SENTINEL","data":{"jws":{"signature":"SENTINEL"}}}]""", "'data.base64'" },
        { """[{"id":"SENTINEL","data":{"hash":"SENTINEL"}}]""", "'data.base64'" },
        // the first attachment is fine; the batch is refused for the second
        { """[{"id":"id-1","data":{"base64":"QQ"}},{"data":{"base64":"QQ"}}]""", "'id'" },
        { """[{"id":"id-1","data":{"base64":"QQ"}},{"id":"SENTINEL","data":{"base64":"SENTINEL!"}}]""", "'data.base64'" },
        { """[{"id":"id-1","data":{"base64":"QQ"}},null]""", "attachments must be JSON objects" },
    };

    [Theory]
    [MemberData(nameof(BadAttachments))]
    public void A_malformed_attachment_makes_the_delivery_malformed(string attachments, string names)
    {
        var message = Raw(P + "/delivery", "{}", attachments);

        var ex = Refused(() => MessagePickup.TryParseDelivery(message, out _, out _));

        ex.Message.Should().StartWith("delivery ").And.Contain(names);
    }

    [Fact]
    public void An_attachment_id_is_accepted_at_2048_characters_and_refused_beyond_without_being_echoed()
    {
        var atCap = Raw(P + "/delivery", "{}", "[" + Base64Attachment(IdAtCap, "QQ") + "]");
        MessagePickup.TryParseDelivery(atCap, out var messages, out _).Should().BeTrue();
        messages!.Single().Id.Should().Be(IdAtCap);

        IdOverCap.Length.Should().Be(2049);
        foreach (var tooLong in new[] { IdOverCap, NearMegabyte })
        {
            var message = Raw(P + "/delivery", "{}", "[" + Base64Attachment("id-1", "QQ") + "," + Base64Attachment(tooLong, "QQ") + "]");
            Refused(() => MessagePickup.TryParseDelivery(message, out _, out _))
                .Message.Should().Be("delivery attachment 'id' exceeds the maximum length of 2048 characters (FR-API-09).");
        }
    }

    [Theory]
    [InlineData(null, "7")]
    [InlineData(null, "\"SENTINEL\"")]
    [InlineData(null, "{}")]
    [InlineData(null, "[7]")]
    [InlineData(null, "[\"SENTINEL\"]")]
    [InlineData(null, "[[]]")]
    [InlineData(null, """[{"id":7,"data":{"base64":"QQ"}}]""")]
    [InlineData(null, """[{"id":"a","data":7}]""")]
    [InlineData(null, """[{"id":"a","data":"QQ"}]""")]
    [InlineData(null, """[{"id":"a","data":{"base64":7}}]""")]
    [InlineData(null, """[{"id":"a","data":{"base64":["QQ"]}}]""")]
    [InlineData("7", null)]
    [InlineData("\"SENTINEL\"", null)]
    [InlineData("[]", null)]
    public void A_wrong_json_type_for_body_or_attachments_never_reaches_a_parser(string? body, string? attachments)
    {
        // These members are typed by the message model: text that puts another JSON type there does
        // not deserialize into a Message at all (the receive path reports it as a malformed
        // message). Listed so the per-member tables above are complete: the parsers cannot be
        // handed these shapes.
        var act = () => Raw(P + "/delivery", body, attachments);

        act.Should().Throw<JsonException>().Which.Message.Should().NotContain(Marker);
    }

    // ---- messages-received (without a bound: the public parser) ----------------------------

    [Fact]
    public void A_message_id_list_is_read_in_wire_order_with_repeats_and_ids_are_opaque()
    {
        MessagePickup.TryParseMessagesReceived(Raw(P + "/messages-received", """{"message_id_list":[]}"""), out var none).Should().BeTrue();
        none.Should().BeEmpty();

        MessagePickup.TryParseMessagesReceived(Raw(P + "/messages-received", """{"future":1,"message_id_list":["id-2","id-1","id-2","has space","a/b#c?d"," "]}"""), out var ids).Should().BeTrue();
        ids.Should().Equal("id-2", "id-1", "id-2", "has space", "a/b#c?d", " ");
    }

    public static TheoryData<string?> BadIdLists => new()
    {
        null,
        "{}",
        """{"MESSAGE_ID_LIST":["SENTINEL"]}""",
        """{"message_ids":["SENTINEL"]}""",
        """{"message_id_list":null}""",
        """{"message_id_list":"SENTINEL"}""",
        """{"message_id_list":7}""",
        """{"message_id_list":true}""",
        """{"message_id_list":{"0":"SENTINEL"}}""",
        """{"message_id_list":[7]}""",
        """{"message_id_list":[null]}""",
        """{"message_id_list":[""]}""",
        """{"message_id_list":[true]}""",
        """{"message_id_list":[["SENTINEL"]]}""",
        """{"message_id_list":[{"id":"SENTINEL"}]}""",
        """{"message_id_list":["id-1",7]}""",
        """{"message_id_list":["id-1",""]}""",
        """{"message_id_list":["id-1",null,"SENTINEL"]}""",
    };

    [Theory]
    [MemberData(nameof(BadIdLists))]
    public void A_malformed_message_id_list_is_refused(string? body)
    {
        var message = Raw(P + "/messages-received", body);

        var ex = Refused(() => MessagePickup.TryParseMessagesReceived(message, out _));

        ex.Message.Should().StartWith("messages-received ").And.Contain("'message_id_list'");
    }

    [Fact]
    public void A_message_id_is_accepted_at_2048_characters_and_refused_beyond_without_being_echoed()
    {
        MessagePickup.TryParseMessagesReceived(Raw(P + "/messages-received", $$"""{"message_id_list":["id-1","{{IdAtCap}}"]}"""), out var ids).Should().BeTrue();
        ids.Should().Equal("id-1", IdAtCap);

        foreach (var tooLong in new[] { IdOverCap, NearMegabyte })
        {
            var message = Raw(P + "/messages-received", $$"""{"message_id_list":["id-1","{{tooLong}}"]}""");
            Refused(() => MessagePickup.TryParseMessagesReceived(message, out _))
                .Message.Should().Be("messages-received 'message_id_list' exceeds the maximum length of 2048 characters (FR-API-09).");
        }
    }

    // ---- what the length bound counts -----------------------------------------------------

    [Fact]
    public void The_length_bound_counts_the_utf16_code_units_of_the_decoded_value()
    {
        // FR-API-09 is "one length comparison" on the string the library holds: not the bytes on
        // the wire, not the spelling in the JSON text, not the number of characters a reader sees.
        var astral = char.ConvertFromUtf32(0x1F600);            // one character, two UTF-16 code units, four UTF-8 bytes
        var escapedA = "\\" + "u0041";                             // six characters of JSON text that spell "A"
        escapedA.Should().HaveLength(6);

        var accepted = new[]
        {
            new string((char)0xE9, 2048),                           // 2048 code units, 4096 bytes
            string.Concat(Enumerable.Repeat(astral, 1024)),         // 2048 code units, 1024 characters
            string.Concat(Enumerable.Repeat(escapedA, 2048)),       // 12288 characters of text, 2048 once decoded
        };
        foreach (var value in accepted)
        {
            MessagePickup.TryParseMessagesReceived(Raw(P + "/messages-received", "{\"message_id_list\":[\"" + value + "\"]}"), out var ids).Should().BeTrue();
            ids!.Single().Should().HaveLength(2048);
            MessagePickup.TryParseStatusRequest(Raw(P + "/status-request", "{\"recipient_did\":\"" + value + "\"}"), out var recipientDid).Should().BeTrue();
            recipientDid.Should().HaveLength(2048);
            MessagePickup.TryParseDelivery(Raw(P + "/delivery", "{}", "[" + Base64Attachment("ID", "QQ").Replace("ID", value) + "]"), out var messages, out _).Should().BeTrue();
            messages!.Single().Id.Should().HaveLength(2048);
        }

        var refused = new[]
        {
            new string((char)0xE9, 2049),
            string.Concat(Enumerable.Repeat(astral, 1025)),         // 2050 code units, although only 1025 characters
            string.Concat(Enumerable.Repeat(escapedA, 2049)),
        };
        foreach (var value in refused)
        {
            Refused(() => MessagePickup.TryParseMessagesReceived(Raw(P + "/messages-received", "{\"message_id_list\":[\"" + value + "\"]}"), out _))
                .Message.Should().Contain("2048");
            Refused(() => MessagePickup.TryParseStatusRequest(Raw(P + "/status-request", "{\"recipient_did\":\"" + value + "\"}"), out _))
                .Message.Should().Contain("2048");
            Refused(() => MessagePickup.TryParseDelivery(Raw(P + "/delivery", "{}", "[" + Base64Attachment("ID", "QQ").Replace("ID", value) + "]"), out _, out _))
                .Message.Should().Contain("2048");
        }
    }

    // ---- live-delivery-change -------------------------------------------------------------

    [Theory]
    [InlineData("""{"live_delivery":true}""", true)]
    [InlineData("""{"live_delivery":false}""", false)]
    [InlineData("""{"future":"x","live_delivery":true,"Live_Delivery":false}""", true)]
    public void A_live_delivery_change_reads_its_boolean(string body, bool expected)
    {
        MessagePickup.TryParseLiveDeliveryChange(Raw(P + "/live-delivery-change", body), out var liveDelivery).Should().BeTrue();
        liveDelivery.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"LIVE_DELIVERY":true}""")]
    [InlineData("""{"liveDelivery":true}""")]
    [InlineData("""{"live_delivery":null}""")]     // REQUIRED: null is not a state
    [InlineData("""{"live_delivery":"true"}""")]
    [InlineData("""{"live_delivery":"SENTINEL"}""")]
    [InlineData("""{"live_delivery":1}""")]
    [InlineData("""{"live_delivery":0}""")]
    [InlineData("""{"live_delivery":[]}""")]
    [InlineData("""{"live_delivery":[true]}""")]
    [InlineData("""{"live_delivery":{}}""")]
    public void A_live_delivery_change_without_a_boolean_is_malformed(string? body)
    {
        var message = Raw(P + "/live-delivery-change", body);

        var ex = Refused(() => MessagePickup.TryParseLiveDeliveryChange(message, out _));

        ex.Message.Should().StartWith("live-delivery-change ").And.Contain("'live_delivery'");
    }

    // ---- bodies no JSON text can hold ------------------------------------------------------

    public static TheoryData<string, string> SurrogateBodies => new()
    {
        { "status-request", """{"recipient_did":"did:example:SENTINEL@"}""" },
        { "status", """{"message_count":1,"recipient_did":"did:example:SENTINEL@"}""" },
        { "delivery-request", """{"limit":1,"recipient_did":"did:example:SENTINEL@"}""" },
        { "delivery", """{"recipient_did":"did:example:SENTINEL@"}""" },
        { "messages-received", """{"message_id_list":["SENTINEL@"]}""" },
        { "live-delivery-change", """{"live_delivery":true,"note":"SENTINEL@"}""" },
        // in a member the protocol does not define, and in a member name
        { "status-request", """{"future":"SENTINEL@"}""" },
        { "status", """{"message_count":1,"SENTINEL@":1}""" },
        { "delivery-request", """{"limit":1,"future":["SENTINEL@"]}""" },
        { "messages-received", """{"message_id_list":[],"SENTINEL@":[]}""" },
    };

    [Theory]
    [MemberData(nameof(SurrogateBodies))]
    public void A_body_holding_an_unpaired_surrogate_escape_is_malformed_not_a_clr_exception(string name, string template)
    {
        // System.Text.Json accepts an escape for half a surrogate pair when it parses and fails
        // only when the string is read back (InvalidOperationException). The receive path refuses
        // such text at unpack; a host that builds a Message from its own JSON can still hand it to
        // the parsers. '@' in the rows above stands for the six-character escape.
        var body = template.Replace("@", LoneSurrogateEscape);
        body.Should().Contain("\\ud800").And.NotContain("@");
        var message = Lazy(P + "/" + name, body);

        var ex = Refused(() => Parse(name, message));

        ex.Message.Should().Be("The message 'body' is not representable as JSON.");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_string_built_in_code_around_half_a_surrogate_pair_is_never_a_clr_exception_and_never_comes_back_as_it_was(string name)
    {
        // No escape this time: a .NET string that itself holds half a pair. System.Text.Json does
        // not refuse it when the body is re-read; it writes U+FFFD in its place. So for the parsers
        // such a body is not malformed (and refusing it would be a fair answer too). What must hold
        // either way: no raw CLR exception, and whatever is handed back can be written out again.
        var body = JsonNode.Parse(ValidBody(name)!)!.AsObject();
        body["recipient_did"] = "did:example:" + Marker + LoneSurrogate;
        var message = InCode(P + "/" + name, body);

        if (!ParsesOrIsMalformed(() => Parse(name, message)))
            return;

        if (RecipientDidCarriers.Any(c => c.Name == name))
            RecipientDidOf(name, message).Should().NotContain(LoneSurrogate).And.StartWith("did:example:" + Marker);
    }

    [Fact]
    public void Attachment_json_holding_an_unpaired_surrogate_escape_is_malformed_not_a_clr_exception()
    {
        var message = InCode(P + "/delivery", new JsonObject());
        message.Attachments = new List<Attachment>
        {
            new() { Id = "id-1", Data = new AttachmentData { Json = JsonNode.Parse("{\"x\":\"" + Marker + LoneSurrogateEscape + "\"}") } },
        };

        Refused(() => MessagePickup.TryParseDelivery(message, out _, out _))
            .Message.Should().Be("delivery attachment 'data.json' is not representable as JSON.");

        // The same half pair as a .NET string (no escape) is written as U+FFFD, as in a body.
        message.Attachments[0].Data.Json = new JsonObject { ["x"] = Marker + LoneSurrogate };
        if (ParsesOrIsMalformed(() => MessagePickup.TryParseDelivery(message, out _, out _)))
        {
            MessagePickup.TryParseDelivery(message, out var messages, out _).Should().BeTrue();
            Encoding.UTF8.GetString(messages!.Single().Payload.Span).Should().NotContain(LoneSurrogate);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(64)]      // the depth System.Text.Json reads back by default
    [InlineData(1000)]    // the depth its writer stops at
    [InlineData(5000)]
    public void A_body_or_an_attachment_nested_deeper_than_json_readers_go_is_never_a_clr_exception(int depth)
    {
        // Only code can build this: the receive path's own reader stops at depth 64. Whether such
        // a message is read or called malformed, nothing else may come out (no InvalidOperation
        // from the writer's depth check, no JsonException from the reader's).
        static JsonNode Nested(int levels)
        {
            JsonNode node = new JsonArray();
            for (var i = 0; i < levels; i++)
                node = new JsonArray(node);
            return node;
        }

        foreach (var name in MessageNames)
        {
            var body = JsonNode.Parse(ValidBody(name)!)!.AsObject();
            body["future"] = Nested(depth);
            var message = InCode(P + "/" + name, body);
            ParsesOrIsMalformed(() => Parse(name, message));
        }

        var delivery = InCode(P + "/delivery", new JsonObject());
        delivery.Attachments = new List<Attachment> { new() { Id = "id-1", Data = new AttachmentData { Json = Nested(depth) } } };
        ParsesOrIsMalformed(() => MessagePickup.TryParseDelivery(delivery, out _, out _));
    }

    /// <summary>
    /// <c>true</c> when <paramref name="act"/> returns, <c>false</c> when it refuses the message as
    /// malformed (without quoting it). Any other exception fails the test.
    /// </summary>
    private static bool ParsesOrIsMalformed(Action act)
    {
        try
        {
            act();
            return true;
        }
        catch (DidComm.Exceptions.MalformedMessageException ex)
        {
            ex.Message.Should().NotContain(Marker);
            ex.InnerException.Should().BeNull();
            return false;
        }
    }

    // ---- bodies built in code --------------------------------------------------------------

    [Fact]
    public void Bodies_built_in_code_parse_like_bodies_read_from_text()
    {
        // JsonObject values created from CLR numbers are not backed by a JsonElement; the parsers
        // must not depend on which kind they get.
        MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = 7L, ["recipient_did"] = "did:example:r1" }), out var limit, out var recipientDid).Should().BeTrue();
        limit.Should().Be(7);
        recipientDid.Should().Be("did:example:r1");

        MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = (short)3 }), out limit, out _).Should().BeTrue();
        limit.Should().Be(3);
        MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = 4.0 }), out limit, out _).Should().BeTrue();
        limit.Should().Be(4);
        MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = 10.0m }), out limit, out _).Should().BeTrue();
        limit.Should().Be(10);
        MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = ulong.MaxValue }), out limit, out _).Should().BeTrue();
        limit.Should().Be(int.MaxValue);
        MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = 1e30 }), out limit, out _).Should().BeTrue();
        limit.Should().Be(int.MaxValue);

        Refused(() => MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = 1.5 }), out _, out _));
        Refused(() => MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = -1 }), out _, out _));
        Refused(() => MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = "10" }), out _, out _));
        Refused(() => MessagePickup.TryParseDeliveryRequest(InCode(P + "/delivery-request", new JsonObject { ["limit"] = null }), out _, out _));

        var status = new JsonObject
        {
            ["message_count"] = 3u,
            ["longest_waited_seconds"] = 60,
            ["newest_received_time"] = 1658085169.0,
            ["oldest_received_time"] = 1658084293m,
            ["total_bytes"] = ulong.MaxValue,
            ["live_delivery"] = true,
        };
        MessagePickup.TryParseStatus(InCode(P + "/status", status), out var parsed).Should().BeTrue();
        parsed.Should().Be(new PickupStatus(3, null, 60, 1658085169, 1658084293, long.MaxValue, true));

        MessagePickup.TryParseMessagesReceived(InCode(P + "/messages-received", new JsonObject { ["message_id_list"] = new JsonArray("id-1", "id-2") }), out var ids).Should().BeTrue();
        ids.Should().Equal("id-1", "id-2");

        MessagePickup.TryParseLiveDeliveryChange(InCode(P + "/live-delivery-change", new JsonObject { ["live_delivery"] = true }), out var live).Should().BeTrue();
        live.Should().BeTrue();

        var delivery = InCode(P + "/delivery", new JsonObject { ["recipient_did"] = "did:example:r1" });
        delivery.Attachments = new List<Attachment> { new() { Id = "id-1", Data = new AttachmentData { Base64 = "-_8" } } };
        MessagePickup.TryParseDelivery(delivery, out var messages, out var echoed).Should().BeTrue();
        echoed.Should().Be("did:example:r1");
        messages!.Single().Payload.ToArray().Should().Equal(0xFB, 0xFF);
    }

    [Fact]
    public void A_message_whose_attachment_list_holds_no_data_object_is_malformed_not_a_null_reference()
    {
        // Attachment.Data is non-nullable in the model, and a host can still null it.
        var delivery = InCode(P + "/delivery", new JsonObject());
        delivery.Attachments = new List<Attachment> { new() { Id = Marker, Data = null! } };

        Refused(() => MessagePickup.TryParseDelivery(delivery, out _, out _)).Message.Should().Contain("'data.base64'");
    }
}
