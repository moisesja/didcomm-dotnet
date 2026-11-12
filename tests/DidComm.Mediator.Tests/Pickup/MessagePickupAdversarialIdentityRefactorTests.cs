using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Tests.Coordination;
using DidComm.Messages;
using Microsoft.Extensions.Logging;
using Xunit;
using Main = DidComm.Mediator.Tests.Pickup.MessagePickupAdversarialIdentityMainIntegers;

namespace DidComm.Mediator.Tests.Pickup;

/// <summary>
/// Red-team pass ADV-1 over #94, part 3: the refactor of #93. <c>MediationCoordinator</c>,
/// <c>MediationBodies</c> and <c>CoordinateMediation</c> now call the shared
/// <c>DidComm.Mediator.Internal</c> helpers, and the integer text reader was generalised to a
/// 64-bit range with a different overflow test. The plan claims "no behaviour change". These
/// tests compare the new code with the reader exactly as <c>main</c> has it
/// (<see cref="MessagePickupAdversarialIdentityMainIntegers"/>) and with the texts and log
/// behaviour of <c>main</c>. Every attack here is <strong>defeated</strong>: no difference found.
/// </summary>
public sealed class MessagePickupAdversarialIdentityRefactorTests
{
    private const string Query = CoordinateMediation.RecipientQueryType;

    public static TheoryData<string> NamedEdges => new()
    {
        "0", "-0", "0.0", "-0.0", "0e0", "0E-0", "-0e99999999999", "0.000e-5",
        "1", "-1", "1.0", "1.0e0", "1e0", "1E+0", "1e-0", "10e-1", "100e-2", "0.1e1", "0.01e2", "1.5", "1e-1", "0.5e0",
        "2147483646", "2147483647", "2147483648", "2147483649", "-2147483647", "-2147483648", "-2147483649",
        "2147483647.0", "2147483648.0", "-2147483648.0", "-2147483649.0", "2.147483647e9", "2.147483648e9", "-2.147483648e9", "-2.147483649e9",
        "21474836470e-1", "21474836480e-1", "214748364.7e1", "214748364.75e1",
        "4294967296", "4294967297", "-4294967296", "9223372036854775807", "9223372036854775808", "-9223372036854775808", "-9223372036854775809",
        "18446744073709551616", "18446744073709551617.0", "1e10", "-1e10", "1e19", "1e20", "1e400", "-1e400", "1e-400", "1e99999999", "1e100000000", "1e100000001", "1e999999999999",
        "1e-99999999", "1e-100000001", "0.0000000000000000000000000000000000000001e40", "0.0000000000000000000000000000000000000001e39",
        "1000000000000000000000000000000000000000e-39", "1000000000000000000000000000000000000001e-39",
        // Not JSON numbers: the reader is internal and is handed raw text; old and new must still agree.
        "", "-", "+1", ".5", "5.", "1e", "1e+", "1e-", "1e1.5", "1x", "0x10", "1_000", " 1", "1 ", "--1", "1e--1", "e5", "-e5", "1.2.3", "1e5e5", "NaN", "Infinity", ((char)0x0661).ToString(), // an Arabic-Indic digit one
    };

    [Theory]
    [MemberData(nameof(NamedEdges))]
    public void The_shared_reader_gives_what_main_gave_for_every_edge_anyone_named(string literal)
    {
        var expectedRead = Main.TryReadIntegerText(literal, out var expected);

        WireJson.TryReadIntegerText(literal, out int shared).Should().Be(expectedRead, literal);
        shared.Should().Be(expected, literal);
        MediationBodies.TryReadIntegerText(literal, out var forwarded).Should().Be(expectedRead, literal);
        forwarded.Should().Be(expected, literal);
    }

    [Fact]
    public void A_literal_of_400_digits_is_clamped_as_on_main_whatever_its_sign_point_or_exponent()
    {
        var digits = "1" + new string('7', 399);
        foreach (var literal in new[]
                 {
                     digits, "-" + digits, digits + ".0", digits + ".000", digits + "e0", digits + "e-399", digits[..200] + "." + digits[200..], digits + "e-400",
                     "0." + digits, "0." + new string('0', 399) + "1", new string('0', 400), "-" + new string('0', 400) + ".0", "1" + new string('0', 399) + "e-399",
                 })
        {
            var expectedRead = Main.TryReadIntegerText(literal, out var expected);

            WireJson.TryReadIntegerText(literal, out int shared).Should().Be(expectedRead, literal);
            shared.Should().Be(expected, literal);
        }
    }

    [Fact]
    public void The_shared_reader_gives_what_main_gave_on_sixty_thousand_generated_literals()
    {
        // A fixed seed, so a failure names a literal that fails again. The generator leans on the
        // places the two overflow tests differ in form: values around 2^31, 2^32 and 2^63, long
        // runs of zeros on either side of the point, and exponents that move the point across them.
        var random = new Random(940_093);
        var seeds = new[] { "2147483647", "2147483648", "2147483649", "214748364", "4294967295", "4294967296", "9223372036854775807", "9223372036854775808", "1", "9", "0" };
        var differences = new List<string>();
        var read = 0;
        for (var i = 0; i < 60_000; i++)
        {
            var literal = Generate(random, seeds);
            var expectedRead = Main.TryReadIntegerText(literal, out var expected);
            var actualRead = WireJson.TryReadIntegerText(literal, out int actual);
            if (expectedRead)
                read++;
            if (actualRead != expectedRead || actual != expected)
                differences.Add($"{literal}: main {expectedRead}/{expected}, now {actualRead}/{actual}");
        }

        differences.Should().BeEmpty();
        read.Should().BeGreaterThan(10_000, "the generator must produce integers, not only refusals");
    }

    [Fact]
    public void A_recipient_query_reads_and_refuses_its_paging_integers_exactly_as_on_main()
    {
        // One level up: the public parser, with the body as it comes off the wire. For every
        // JSON-valid literal the value is main's (TryGetInt64 and clamp, else the text reader), and
        // a refusal carries main's exact text.
        var random = new Random(930_106);
        var seeds = new[] { "2147483647", "2147483648", "9223372036854775807", "9223372036854775808", "1", "0", "100" };
        var literals = new List<string>(((IEnumerable<object[]>)NamedEdges).Select(row => (string)row[0]));
        for (var i = 0; i < 4_000; i++)
            literals.Add(Generate(random, seeds));

        var checkedAsLimit = 0;
        foreach (var literal in literals)
        {
            JsonElement value;
            try
            {
                using var document = JsonDocument.Parse(literal);
                value = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue; // not a JSON value: the receive path refuses the whole message
            }
            if (value.ValueKind != JsonValueKind.Number)
                continue;
            checkedAsLimit++;
            var expected = Main.ReadInteger(value);

            // As paginate.limit (any integer is accepted; a negative one is clamped by the handler later).
            var asLimit = ParseQuery("{\"paginate\":{\"limit\":" + literal + ",\"offset\":0}}");
            if (expected is { } limit)
                asLimit.Paging!.Limit.Should().Be(limit, literal);
            else
                asLimit.Error.Should().Be("recipient-query 'paginate.limit' must be an integer.", literal);

            // As paginate.offset (main: the same reader, then "must not be negative").
            var asOffset = ParseQuery("{\"paginate\":{\"limit\":1,\"offset\":" + literal + "}}");
            if (expected is not { } offset)
                asOffset.Error.Should().Be("recipient-query 'paginate.offset' must be an integer.", literal);
            else if (offset < 0)
                asOffset.Error.Should().Be("recipient-query 'paginate.offset' must not be negative.", literal);
            else
                asOffset.Paging!.Offset.Should().Be(offset, literal);
        }
        checkedAsLimit.Should().BeGreaterThan(2_000);

        // The other texts of main that the shared helper now writes.
        ParseQuery("{\"paginate\":{\"offset\":0}}").Error.Should().Be("recipient-query 'paginate' is missing the REQUIRED 'limit' member.");
        ParseQuery("{\"paginate\":{\"limit\":1}}").Error.Should().Be("recipient-query 'paginate' is missing the REQUIRED 'offset' member.");
        ParseQuery("{\"paginate\":{\"limit\":\"10\",\"offset\":0}}").Error.Should().Be("recipient-query 'paginate.limit' must be an integer.");
        ParseQuery("{\"paginate\":{\"limit\":null,\"offset\":0}}").Error.Should().Be("recipient-query 'paginate.limit' must be an integer.");
        ParseQuery("{\"paginate\":[1,0]}").Error.Should().Be("recipient-query 'paginate' must be a JSON object.");
        ParseQuery("{\"paginate\":null}").Paging.Should().BeNull();
        ParseQuery("{}").Paging.Should().BeNull();
    }

    [Fact]
    public async Task The_paging_edges_main_answered_are_answered_the_same_way_through_the_real_handler()
    {
        // End to end, the values of the issue's list: clamped, never wrapped, never refused.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:a"), HandlerMessages.Add("did:example:b"), HandlerMessages.Add("did:example:c"));

        async Task<string> AskAsync(string paginate)
        {
            var body = JsonNode.Parse("{\"paginate\":" + paginate + "}")!.AsObject();
            var outcome = await fx.SendAsync(HandlerMessages.Raw(Query, bob.Did, mediator.Did, body), mediator.Did);
            CoordinateMediation.TryParseRecipient(outcome.Reply!, out var dids, out var pagination).Should().BeTrue();
            return string.Join(",", dids!.Select(d => d[^1])) + " " + pagination!.Count + "/" + pagination.Offset + "/" + pagination.Remaining;
        }

        // Relative, so that nothing here guesses a number main did not print: every spelling of a
        // value is answered like its plainest one.
        var everything = await AskAsync("{\"limit\":100,\"offset\":0}");
        everything.Should().StartWith("a,b,c ");
        foreach (var hugeLimit in new[] { "2147483647", "2147483648", "1e10", "9223372036854775808", new string('9', 400) })
            (await AskAsync("{\"limit\":" + hugeLimit + ",\"offset\":0}")).Should().Be(everything, hugeLimit);
        (await AskAsync("{\"limit\":1.0e0,\"offset\":1.0e0}")).Should().Be(await AskAsync("{\"limit\":1,\"offset\":1}")).And.StartWith("b ");
        var firstTwo = await AskAsync("{\"limit\":2,\"offset\":0}");
        firstTwo.Should().StartWith("a,b ");
        foreach (var zero in new[] { "-0", "0.0", "-0.0", "0e0", "0E+5" })
            (await AskAsync("{\"limit\":2,\"offset\":" + zero + "}")).Should().Be(firstTwo, zero);
        var pastTheEnd = await AskAsync("{\"limit\":2,\"offset\":2147483647}");
        pastTheEnd.Should().StartWith(" ", "an offset past the end is an empty page, not an error");
        foreach (var hugeOffset in new[] { "2147483648", "1e10", "9223372036854775808", new string('9', 400) })
            (await AskAsync("{\"limit\":2,\"offset\":" + hugeOffset + "}")).Should().Be(pastTheEnd, hugeOffset);
        foreach (var negativeLimit in new[] { "-1", "-2147483648", "-2147483649", "-1e10", "-" + new string('9', 400) })
        {
            // main clamps a negative limit to an empty first page; whatever it does, every spelling of "very negative" does the same.
            (await AskAsync("{\"limit\":" + negativeLimit + ",\"offset\":0}")).Should().Be(await AskAsync("{\"limit\":-1,\"offset\":0}"), negativeLimit);
        }
        foreach (var negativeOffset in new[] { "-1", "-2147483648", "-2147483649", "-1e10" })
        {
            var thrown = await Record.ExceptionAsync(() => AskAsync("{\"limit\":1,\"offset\":" + negativeOffset + "}"));
            thrown.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Be("recipient-query 'paginate.offset' must not be negative.");
        }
    }

    [Fact]
    public async Task The_unauthenticated_drop_line_of_coordinate_mediation_keeps_its_levels_its_text_and_its_128_character_cut()
    {
        // The rate limit, LogId and the peer rule moved to MediatorLog / MediatorPeer. main: the
        // first and every 1000th drop at Warning, the rest at Debug; an id of 128 characters is
        // written, one of 129 is replaced; anoncrypt is nobody.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        var idAtTheCut = new string('a', 128);
        var idPastTheCut = new string('b', 129);

        // Three real anoncrypt envelopes, unpacked once each; the dispatcher then sees them 1001
        // times (the counter is the handler's, so the same message counts again).
        async Task<DidComm.Facade.UnpackResult> ReceiveAsync(string id)
            => await fx.ReceiveAsync(HandlerMessages.Raw(CoordinateMediation.MediateRequestType, mallory.Did, mediator.Did, new JsonObject(), id), mediator.Did, HandlerEnvelope.Anoncrypt);
        var atTheCut = await ReceiveAsync(idAtTheCut);
        var pastTheCut = await ReceiveAsync(idPastTheCut);
        var ordinary = await ReceiveAsync("an-ordinary-id");

        for (var i = 1; i <= 1001; i++)
        {
            var received = i == 1 ? atTheCut : i == 1000 ? pastTheCut : ordinary;
            (await fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, CancellationToken.None)).Reply.Should().BeNull();
        }

        var drops = fx.Logs.Handler.Where(e => e.Formatted.Contains("no authcrypt sender", StringComparison.Ordinal)).ToArray();
        drops.Should().HaveCount(1001);
        drops.Where(e => e.Level == LogLevel.Warning).Select(e => Array.IndexOf(drops, e) + 1).Should().Equal(1, 1000);
        drops.Where(e => e.Level != LogLevel.Warning).Should().OnlyContain(e => e.Level == LogLevel.Debug);
        drops[0].Formatted.Should().Be($"Coordinate-mediation message {idAtTheCut} dropped: no authcrypt sender (FR-MED-05); not applied, not answered. 1 such drops.");
        drops[999].Formatted.Should().Be("Coordinate-mediation message (id of 129 characters) dropped: no authcrypt sender (FR-MED-05); not applied, not answered. 1000 such drops.");
        fx.Memory.GrantCount.Should().Be(0);
        MediationCoordinator.LogId(null).Should().Be("(none)");
        MediationCoordinator.MaxLoggedIdLength.Should().Be(128);
    }

    private static (RecipientQueryPaging? Paging, string? Error) ParseQuery(string body)
    {
        var json = "{\"id\":\"q-1\",\"type\":\"" + Query + "\",\"from\":\"did:example:alice\",\"to\":[\"did:example:mediator\"],\"body\":" + body + "}";
        var message = JsonSerializer.Deserialize<Message>(json, DidComm.Json.DidCommJson.Default)!;
        try
        {
            CoordinateMediation.TryParseRecipientQuery(message, out var paging).Should().BeTrue();
            return (paging, null);
        }
        catch (MalformedMessageException ex)
        {
            return (null, ex.Message);
        }
    }

    // A number-shaped literal: optional sign, digits built from seeds, zeros and noise, an optional
    // fraction and an optional exponent chosen to land near the point; now and then a defect.
    private static string Generate(Random random, string[] seeds)
    {
        var sb = new StringBuilder();
        if (random.Next(3) == 0)
            sb.Append('-');

        var whole = new StringBuilder();
        if (random.Next(8) != 0)
            whole.Append(seeds[random.Next(seeds.Length)]);
        for (var n = random.Next(4); n > 0; n--)
            whole.Append((char)('0' + random.Next(10)));
        whole.Append('0', random.Next(3) == 0 ? random.Next(25) : 0);
        if (whole.Length == 0)
            whole.Append('0');
        // JSON forbids a leading zero before another digit; keep most literals valid, not all.
        if (whole.Length > 1 && whole[0] == '0' && random.Next(10) != 0)
            whole.Remove(0, 1).Insert(0, (char)('1' + random.Next(9)));
        sb.Append(whole);

        var fractionLength = 0;
        if (random.Next(2) == 0)
        {
            sb.Append('.');
            fractionLength = 1 + random.Next(22);
            var nonZeroAt = random.Next(3) == 0 ? random.Next(fractionLength) : -1;
            for (var i = 0; i < fractionLength; i++)
                sb.Append(i == nonZeroAt ? (char)('1' + random.Next(9)) : '0');
        }

        if (random.Next(2) == 0)
        {
            sb.Append(random.Next(2) == 0 ? 'e' : 'E');
            var exponent = random.Next(6) switch
            {
                0 => -whole.Length + random.Next(-2, 3),
                1 => fractionLength + random.Next(-2, 3),
                2 => random.Next(-25, 26),
                3 => random.Next(2) == 0 ? 99_999_999 : 100_000_001,
                4 => -random.Next(2) * 100_000_000 - 1,
                _ => random.Next(0, 12),
            };
            if (exponent < 0)
                sb.Append('-');
            else if (random.Next(3) == 0)
                sb.Append('+');
            sb.Append(Math.Abs((long)exponent));
        }

        if (random.Next(40) == 0)
            sb.Insert(random.Next(sb.Length + 1), "x.e-+ "[random.Next(6)]);
        return sb.ToString();
    }
}
