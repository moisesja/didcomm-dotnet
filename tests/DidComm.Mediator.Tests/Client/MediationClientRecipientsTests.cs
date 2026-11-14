using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Exceptions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Coordination;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// Listing the DIDs registered under a grant (#96 design §4.5; FR-MED-09): the page asked for is
/// range-checked before anything is sent; an answer's list is bounded by the limit asked for from
/// its count before any entry is read (L-055); a reported position must agree with the page and the
/// request; the walker pages by 100 from offset 0, advances by what it received, stops on no
/// position, nothing remaining, an empty page or 1000 pages, checks its arguments when called and
/// sends nothing before the first <c>MoveNextAsync</c>.
/// </summary>
public sealed class MediationClientRecipientsTests
{
    private const string Mediator = FakeMediatorExchange.MediatorDid;
    private const string Me = FakeMediatorExchange.LocalDid;

    // ---- one page -------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1001, 0)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(10, -1)]
    [InlineData(10, int.MinValue)]
    public async Task A_page_out_of_range_is_refused_before_anything_is_sent(int limit, int offset)
    {
        var exchange = new FakeMediatorExchange();

        (await FluentActions.Awaiting(() => exchange.Client().QueryRecipientsAsync(Mediator, Me, new RecipientQueryPaging(limit, offset)))
            .Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("paginate");
        exchange.SendCount.Should().Be(0);
        exchange.RouteCalls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 0)]
    [InlineData(5, 2)]
    public async Task The_page_asked_for_is_sent_as_paginate_and_comes_back_with_its_position(int limit, int offset)
    {
        var exchange = new FakeMediatorExchange();
        exchange.Mediator.Registered.AddRange(Enumerable.Range(0, 10).Select(i => $"did:example:r{i}"));

        var page = await exchange.Client().QueryRecipientsAsync(Mediator, Me, new RecipientQueryPaging(limit, offset));

        exchange.Operations.Single().Message.Body!.ToJsonString().Should().Be($$$"""{"paginate":{"limit":{{{limit}}},"offset":{{{offset}}}}}""");
        page.Dids.Should().Equal(Enumerable.Range(offset, Math.Max(0, Math.Min(limit, 10 - offset))).Select(i => $"did:example:r{i}"));
        page.Pagination.Should().NotBeNull();
        page.Pagination!.Offset.Should().Be(offset);
    }

    [Fact]
    public async Task Without_paginate_the_body_is_empty_and_a_page_without_pagination_is_accepted()
    {
        var exchange = new FakeMediatorExchange();
        exchange.Respond = r => FakeMediatorExchange.IsQuery(r.Message)
            ? exchange.Mediator.Answer(r)
            : ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id, MediationBodies.WriteRecipients("dids", new[] { "did:example:a" }, null));

        var page = await exchange.Client().QueryRecipientsAsync(Mediator, Me);

        exchange.Operations.Single().Message.Body!.ToJsonString().Should().Be("{}");
        page.Dids.Should().Equal("did:example:a");
        page.Pagination.Should().BeNull();
    }

    [Theory]
    [InlineData(CoordinateMediationVersion.V3)]
    [InlineData(CoordinateMediationVersion.V2)]
    public async Task A_page_longer_than_the_limit_is_refused_from_the_count_before_the_poisoned_extra_entry_is_read(CoordinateMediationVersion version)
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(version));
        var (type, list) = version == CoordinateMediationVersion.V3
            ? (CoordinateMediation.RecipientType, "dids")
            : (CoordinateMediation20.KeylistType, CoordinateMediation20.KeysList);
        exchange.Respond = r => ScriptedMediator.Raw(type, Mediator, Me, r.Message.Id,
            new JsonObject { [list] = JsonNode.Parse("""[{"recipient_did":"did:example:a"},{"recipient_did":"did:example:b"},{"recipient_did":99}]""") });

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().QueryRecipientsAsync(Mediator, Me, new RecipientQueryPaging(2, 0)))
            .Should().ThrowAsync<MediationProtocolException>()).Which;

        thrown.InnerException.Should().BeOfType<MalformedMessageException>().Which.Message.Should().Contain("more than 2 entries");
    }

    [Fact]
    public async Task Without_paginate_a_page_of_1000_is_accepted_and_one_of_1001_refused()
    {
        foreach (var (size, accepted) in new[] { (1000, true), (1001, false) })
        {
            var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
            var dids = Enumerable.Range(0, size).Select(i => $"did:example:r{i}").ToArray();
            exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id, MediationBodies.WriteRecipients("dids", dids, null));

            var act = () => exchange.Client().QueryRecipientsAsync(Mediator, Me);

            if (accepted)
                (await act()).Dids.Should().HaveCount(1000);
            else
                await act.Should().ThrowAsync<MediationProtocolException>();
        }
    }

    public static TheoryData<string, int, int, int> InconsistentPositions => new()
    {
        { "count larger than the page", 3, 5, 0 },
        { "count smaller than the page", 1, 5, 0 },
        { "offset ignored (the mediator answered page 0)", 2, 0, 3 },
        { "offset moved", 2, 6, 3 },
        { "negative remaining", 2, 5, -1 },
    };

    [Theory]
    [MemberData(nameof(InconsistentPositions))]
    public async Task A_position_that_disagrees_with_the_page_or_the_request_is_refused(string because, int count, int offset, int remaining)
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id,
            MediationBodies.WriteRecipients("dids", new[] { "did:example:a", "did:example:b" }, new RecipientPagination(count, offset, remaining)));

        var thrown = (await FluentActions.Awaiting(() => exchange.Client().QueryRecipientsAsync(Mediator, Me, new RecipientQueryPaging(10, 5)))
            .Should().ThrowAsync<MediationProtocolException>(because)).Which;

        thrown.Message.Should().Contain("pagination").And.NotContain("did:example");
        thrown.RequestId.Should().Be(exchange.Operations.Single().Message.Id);
    }

    [Fact]
    public async Task Without_paginate_a_reported_offset_must_be_zero()
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id,
            MediationBodies.WriteRecipients("dids", new[] { "did:example:a" }, new RecipientPagination(1, 1, 0)));

        await FluentActions.Awaiting(() => exchange.Client().QueryRecipientsAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>();
    }

    [Fact]
    public async Task A_2_0_keylist_is_read_from_keys()
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V2));
        exchange.Mediator.Registered.AddRange(new[] { "did:example:a", "did:example:b" });

        var page = await exchange.Client().QueryRecipientsAsync(Mediator, Me, new RecipientQueryPaging(30, 0));

        exchange.Operations.Single().Message.Type.Should().Be(CoordinateMediation20.KeylistQueryType);
        page.Dids.Should().Equal("did:example:a", "did:example:b");
        page.Pagination.Should().Be(new RecipientPagination(2, 0, 0));
    }

    [Fact]
    public async Task A_3_0_answer_with_the_2_0_list_name_is_malformed()
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id,
            MediationBodies.WriteRecipients(CoordinateMediation20.KeysList, new[] { "did:example:a" }, null));

        (await FluentActions.Awaiting(() => exchange.Client().QueryRecipientsAsync(Mediator, Me)).Should().ThrowAsync<MediationProtocolException>())
            .Which.InnerException.Should().BeOfType<MalformedMessageException>();
    }

    // ---- the walker -----------------------------------------------------------------------

    [Theory]
    [InlineData(CoordinateMediationVersion.V3)]
    [InlineData(CoordinateMediationVersion.V2)]
    public async Task The_walker_pages_by_100_from_0_until_nothing_remains(CoordinateMediationVersion version)
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(version));
        var all = Enumerable.Range(0, 250).Select(i => $"did:example:r{i}").ToArray();
        exchange.Mediator.Registered.AddRange(all);

        var listed = await Collect(exchange.Client().QueryAllRecipientsAsync(Mediator, Me));

        listed.Should().Equal(all);
        exchange.Operations.Select(o => o.Message.Body!.ToJsonString()).Should().Equal(
            """{"paginate":{"limit":100,"offset":0}}""",
            """{"paginate":{"limit":100,"offset":100}}""",
            """{"paginate":{"limit":100,"offset":200}}""");
        exchange.RouteCalls.Should().ContainSingle("the walk is one operation on one route");
    }

    [Fact]
    public async Task The_walker_negotiates_once_for_the_whole_walk()
    {
        var exchange = new FakeMediatorExchange();
        exchange.Versions = new NegotiatedVersionCache(exchange.Time, capacity: 0); // nothing is remembered
        exchange.Mediator.Registered.AddRange(Enumerable.Range(0, 150).Select(i => $"did:example:r{i}"));

        (await Collect(exchange.Client().QueryAllRecipientsAsync(Mediator, Me))).Should().HaveCount(150);

        exchange.Queries.Should().ContainSingle();
        exchange.Operations.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_walker_advances_by_what_it_received_when_the_mediator_pages_smaller()
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        var all = Enumerable.Range(0, 25).Select(i => $"did:example:r{i}").ToArray();
        exchange.Respond = r =>
        {
            var asked = MediationBodies.ReadPaginate(MediationBodies.ToElement(r.Message.Body), "q")!;
            var page = all.Skip(asked.Offset).Take(10).ToArray();
            return ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id,
                MediationBodies.WriteRecipients("dids", page, new RecipientPagination(page.Length, asked.Offset, all.Length - asked.Offset - page.Length)));
        };

        (await Collect(exchange.Client().QueryAllRecipientsAsync(Mediator, Me))).Should().Equal(all);
        exchange.Operations.Should().HaveCount(3);
    }

    [Fact]
    public async Task The_walker_stops_after_a_page_without_pagination()
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id,
            MediationBodies.WriteRecipients("dids", new[] { "did:example:a" }, null));

        (await Collect(exchange.Client().QueryAllRecipientsAsync(Mediator, Me))).Should().Equal("did:example:a");
        exchange.Operations.Should().ContainSingle();
    }

    [Fact]
    public async Task The_walker_stops_on_an_empty_page_whatever_it_says_remains()
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id,
            MediationBodies.WriteRecipients("dids", Array.Empty<string>(), new RecipientPagination(0, 0, 5000)));

        (await Collect(exchange.Client().QueryAllRecipientsAsync(Mediator, Me))).Should().BeEmpty();
        exchange.Operations.Should().ContainSingle("no progress, no second request");
    }

    [Fact]
    public async Task The_walker_stops_after_1000_pages_and_says_so()
    {
        var sink = new HandlerLogSink();
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        exchange.Respond = r =>
        {
            var asked = MediationBodies.ReadPaginate(MediationBodies.ToElement(r.Message.Body), "q")!;
            return ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id,
                MediationBodies.WriteRecipients("dids", new[] { $"did:example:r{asked.Offset}" }, new RecipientPagination(1, asked.Offset, 1)));
        };

        var listed = await Collect(exchange.Client(new HandlerCapturingLogger<MediationClient>(sink)).QueryAllRecipientsAsync(Mediator, Me));

        listed.Should().HaveCount(1000);
        exchange.Operations.Should().HaveCount(1000);
        sink.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Which.Everything.Should().Contain("1000").And.NotContain("did:");
    }

    [Fact]
    public async Task The_walker_refuses_a_mediator_that_ignores_the_offset()
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        var first = Enumerable.Range(0, 100).Select(i => $"did:example:r{i}").ToArray();
        exchange.Respond = r => ScriptedMediator.Raw(CoordinateMediation.RecipientType, Mediator, Me, r.Message.Id,
            MediationBodies.WriteRecipients("dids", first, new RecipientPagination(100, 0, 50)));
        var listed = new List<string>();

        var act = async () =>
        {
            await foreach (var did in exchange.Client().QueryAllRecipientsAsync(Mediator, Me))
                listed.Add(did);
        };

        await act.Should().ThrowAsync<MediationProtocolException>();
        listed.Should().Equal(first, "the first page was consistent; the second repeats offset 0");
        exchange.Operations.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_walker_checks_its_arguments_when_called_and_sends_nothing_before_the_first_MoveNextAsync()
    {
        var exchange = new FakeMediatorExchange();
        var client = exchange.Client();

        FluentActions.Invoking(() => client.QueryAllRecipientsAsync("did:example:m#frag", Me)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => client.QueryAllRecipientsAsync(Mediator, Me, new Uri("relative", UriKind.Relative))).Should().Throw<ArgumentException>();

        var walk = client.QueryAllRecipientsAsync(Mediator, Me);
        await using var enumerator = walk.GetAsyncEnumerator();
        exchange.RouteCalls.Should().BeEmpty();
        exchange.SendCount.Should().Be(0);

        (await enumerator.MoveNextAsync()).Should().BeFalse("nothing is registered");
        exchange.Operations.Should().ContainSingle();
    }

    [Fact]
    public async Task The_walker_honours_cancellation_from_WithCancellation_and_from_its_argument()
    {
        var exchange = new FakeMediatorExchange(MediationClientTests.Pinned(CoordinateMediationVersion.V3));
        exchange.Mediator.Registered.AddRange(Enumerable.Range(0, 250).Select(i => $"did:example:r{i}"));
        var client = exchange.Client();

        using (var cts = new CancellationTokenSource())
        {
            var seen = 0;
            var act = async () =>
            {
                await foreach (var _ in client.QueryAllRecipientsAsync(Mediator, Me).WithCancellation(cts.Token))
                {
                    if (++seen == 100)
                        cts.Cancel();
                }
            };
            await act.Should().ThrowAsync<OperationCanceledException>();
            seen.Should().Be(100);
            exchange.Operations.Should().HaveCount(2, "the second page's request saw the cancellation");
        }

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            var act = async () =>
            {
                await foreach (var _ in client.QueryAllRecipientsAsync(Mediator, Me, ct: cts.Token))
                {
                }
            };
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> dids)
    {
        var list = new List<string>();
        await foreach (var did in dids)
            list.Add(did);
        return list;
    }
}
