using System.Collections;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Protocols;
using DidComm.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DidComm.Mediator.Tests.Client.FakePickupExchange;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// The one-shot calls of <see cref="PickupClient"/> against a fake exchange (#96 design §2, §4.3):
/// <see cref="PickupClient.AckAsync"/> (chunks of 100, the last status, ids checked before anything
/// is sent and copied so what was checked is what is sent), <see cref="PickupClient.GetStatusAsync"/>
/// (the filter echo) and <see cref="PickupClient.SetLiveDeliveryAsync"/> (refused before sending on
/// a route that is not a WebSocket; the mediator's problem report surfaces with its code).
/// </summary>
public sealed class PickupClientAckTests
{
    // ---- AckAsync -----------------------------------------------------------------------------

    [Fact]
    public async Task Ids_are_sent_in_chunks_of_100_in_order_and_the_last_status_is_returned()
    {
        var chunk = 0;
        var exchange = new FakePickupExchange().On(MessagePickup.MessagesReceivedType, r => Status(r, count: 1000 - ++chunk));
        var ids = Enumerable.Range(0, 250).Select(i => $"q{i}").ToArray();

        var status = await PickupTest.Client(exchange).AckAsync(M, L, ids);

        var sent = exchange.SentOf(MessagePickup.MessagesReceivedType);
        sent.Select(s => s.Ids.Count).Should().Equal(100, 100, 50);
        exchange.AcknowledgedIds.Should().Equal(ids);
        sent.Should().OnlyContain(s => s.Spec == DeliveryReader.MessagesReceivedSpec && s.Request.From == L);
        sent.Select(s => s.Request.Id).Should().OnlyHaveUniqueItems();
        status.MessageCount.Should().Be(997, "the status answering the last message");
        exchange.RouteCalls.Should().ContainSingle("the route is resolved once per call").Which.RequireWebSocket.Should().BeFalse();
    }

    [Fact]
    public async Task One_id_is_one_message()
    {
        var exchange = new FakePickupExchange();
        await PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" });
        exchange.Sent.Should().ContainSingle().Which.Ids.Should().Equal("q1");
    }

    [Fact]
    public async Task Exactly_10_000_ids_are_accepted()
    {
        var exchange = new FakePickupExchange();
        await PickupTest.Client(exchange).AckAsync(M, L, Enumerable.Range(0, 10_000).Select(i => $"q{i}").ToArray());
        exchange.Sent.Should().HaveCount(100);
    }

    public static TheoryData<string, string?[], string> BadIds => new()
    {
        { "empty list", Array.Empty<string?>(), "Between 1 and 10 000" },
        { "null entry", new[] { "q0", null }, "Id 1 " },
        { "empty entry", new[] { "q0", "q1", string.Empty }, "Id 2 " },
        { "over-long entry", new[] { new string('i', PickupBodies.MaxIdLength + 1) }, "Id 0 " },
        { "space", new[] { "secret value" }, "Id 0 " },
        { "slash", new[] { "q0", "secret/value" }, "Id 1 " },
        { "non-ASCII", new[] { "secrét" }, "Id 0 " },
    };

    [Theory]
    [MemberData(nameof(BadIds))]
    public async Task Bad_ids_are_refused_naming_the_index_never_the_value_before_anything_is_sent(string name, string?[] ids, string expected)
    {
        var exchange = new FakePickupExchange();
        var act = () => PickupTest.Client(exchange).AckAsync(M, L, ids!);

        var thrown = (await act.Should().ThrowAsync<ArgumentException>(name)).Which;
        thrown.ParamName.Should().Be("ids");
        thrown.Message.Should().Contain(expected).And.NotContain("secret").And.NotContain(new string('i', 100));
        exchange.RouteCalls.Should().BeEmpty();
        exchange.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task More_than_10_000_ids_are_refused_from_the_count_before_any_entry_is_read()
    {
        // L-055: the entry beyond the bound is poisoned; the refusal must be the count.
        var ids = new PoisonedList(10_001);
        var act = () => PickupTest.Client(new FakePickupExchange()).AckAsync(M, L, ids);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("Between 1 and 10 000");
        ids.Reads.Should().Be(0, "no entry is looked at when the count is over the bound");
    }

    [Fact]
    public async Task What_is_checked_is_what_is_sent_even_when_the_callers_list_changes()
    {
        // L-039: a list that answers differently on a second read.
        var ids = new ShiftingList("q-checked", "has a space");
        var exchange = new FakePickupExchange();

        await PickupTest.Client(exchange).AckAsync(M, L, ids);

        exchange.AcknowledgedIds.Should().Equal("q-checked");
    }

    [Fact]
    public async Task A_failing_chunk_ends_the_call_with_its_exception_after_the_earlier_chunks_were_sent()
    {
        var chunk = 0;
        var exchange = new FakePickupExchange().On(MessagePickup.MessagesReceivedType, r => ++chunk == 2
            ? Task.FromException<InboundMessageSnapshot>(new TimeoutException("no answer"))
            : Task.FromResult(Status(r)));

        var act = () => PickupTest.Client(exchange).AckAsync(M, L, Enumerable.Range(0, 300).Select(i => $"q{i}").ToArray());

        await act.Should().ThrowAsync<TimeoutException>();
        exchange.Sent.Should().HaveCount(2, "the call stops at the failing chunk; repeating it is harmless");
    }

    [Fact]
    public async Task A_malformed_status_answering_an_acknowledgement_is_a_MediationProtocolException()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.MessagesReceivedType, r => Snapshot(new DidComm.Messages.Message
        {
            Id = "a1",
            Type = MessagePickup.StatusType,
            Thid = r.Request.Id,
            Body = new System.Text.Json.Nodes.JsonObject { ["message_count"] = -1 },
        }));
        var act = () => PickupTest.Client(exchange).AckAsync(M, L, new[] { "q1" });

        (await act.Should().ThrowAsync<MediationProtocolException>()).Which.RequestId.Should().Be(exchange.Sent.Single().Request.Id);
    }

    // ---- GetStatusAsync -----------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_sends_a_status_request_with_the_filter_and_returns_the_status()
    {
        const string carol = "did:example:carol";
        var exchange = new FakePickupExchange().On(MessagePickup.StatusRequestType, r => Status(r, count: 4, recipientDid: carol));

        var status = await PickupTest.Client(exchange).GetStatusAsync(M, L, recipientDid: carol);

        status.MessageCount.Should().Be(4);
        var request = exchange.Sent.Should().ContainSingle().Subject;
        request.Is(MessagePickup.StatusRequestType).Should().BeTrue();
        request.RecipientDid.Should().Be(carol);
        request.Spec.Should().BeSameAs(DeliveryReader.StatusRequestSpec);
        request.Request.CreatedTime.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_accepts_a_status_that_leaves_the_filter_out()
    {
        var exchange = new FakePickupExchange().On(MessagePickup.StatusRequestType, r => Status(r, count: 2));
        (await PickupTest.Client(exchange).GetStatusAsync(M, L, recipientDid: "did:example:carol")).MessageCount.Should().Be(2);
    }

    [Theory]
    [InlineData("did:example:carol", "did:example:dave")]
    [InlineData(null, "did:example:dave")]
    [InlineData("did:example:carol", "not a did")]
    public async Task GetStatus_refuses_a_status_about_another_DID(string? filter, string echoed)
    {
        var exchange = new FakePickupExchange().On(MessagePickup.StatusRequestType, r => Status(r, recipientDid: echoed));
        var act = () => PickupTest.Client(exchange).GetStatusAsync(M, L, recipientDid: filter);

        var thrown = (await act.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.Message.Should().NotContain(echoed).And.Contain("status-request");
    }

    [Fact]
    public async Task GetStatus_accepts_the_filter_echoed_as_a_DID_URL_of_the_same_DID()
    {
        // Compared as DID subjects (PRD §4.3), never as raw strings.
        const string carol = "did:example:carol";
        var exchange = new FakePickupExchange().On(MessagePickup.StatusRequestType, r => Status(r, count: 1, recipientDid: carol + "#key-1"));
        (await PickupTest.Client(exchange).GetStatusAsync(M, L, recipientDid: carol)).MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task GetStatus_propagates_the_exchange_exceptions_as_they_are()
    {
        var problem = new MediationProtocolException("problem", "r1", "e.p.x");
        var exchange = new FakePickupExchange().On(MessagePickup.StatusRequestType, _ => Task.FromException<InboundMessageSnapshot>(problem));
        var act = () => PickupTest.Client(exchange).GetStatusAsync(M, L);
        (await act.Should().ThrowAsync<MediationProtocolException>()).Which.Should().BeSameAs(problem);
    }

    [Fact]
    public async Task GetStatus_refuses_bad_arguments_before_anything_is_sent()
    {
        var exchange = new FakePickupExchange();
        var client = PickupTest.Client(exchange);
        await client.Invoking(c => c.GetStatusAsync(M, L, recipientDid: L + "#k")).Should().ThrowAsync<ArgumentException>();
        await client.Invoking(c => c.GetStatusAsync(M + "/x", L)).Should().ThrowAsync<ArgumentException>();
        await client.Invoking(c => c.GetStatusAsync(M, null!)).Should().ThrowAsync<ArgumentNullException>();
        await client.Invoking(c => c.GetStatusAsync(M, L, serviceEndpointOverride: new Uri("x/y", UriKind.Relative))).Should().ThrowAsync<ArgumentException>();
        exchange.RouteCalls.Should().BeEmpty();
        exchange.Sent.Should().BeEmpty();
    }

    // ---- SetLiveDeliveryAsync -----------------------------------------------------------------

    [Fact]
    public async Task SetLiveDelivery_over_HTTP_is_refused_before_anything_is_sent()
    {
        var exchange = new FakePickupExchange { Route = HttpRoute };
        var act = () => PickupTest.Client(exchange).SetLiveDeliveryAsync(M, L, liveDelivery: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
        exchange.RouteCalls.Should().ContainSingle().Which.RequireWebSocket.Should().BeTrue("the route rule is asked for a WebSocket");
        exchange.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetLiveDelivery_over_a_WebSocket_sends_the_change_and_returns_the_status(bool on)
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute };
        var status = await PickupTest.Client(exchange).SetLiveDeliveryAsync(M, L, on);

        status.LiveDelivery.Should().Be(on);
        var request = exchange.Sent.Should().ContainSingle().Subject;
        request.Is(MessagePickup.LiveDeliveryChangeType).Should().BeTrue();
        request.Live.Should().Be(on);
        request.Spec.Should().BeSameAs(DeliveryReader.LiveDeliveryChangeSpec);
        request.Route.Should().BeSameAs(WebSocketRoute);
    }

    [Fact]
    public async Task SetLiveDelivery_surfaces_live_mode_not_supported_with_its_code()
    {
        var exchange = new FakePickupExchange { Route = WebSocketRoute }.On(MessagePickup.LiveDeliveryChangeType,
            r => Task.FromException<InboundMessageSnapshot>(new MediationProtocolException("The mediator reported a problem.", r.Request.Id, MessagePickup.LiveModeNotSupportedCode)));
        var act = () => PickupTest.Client(exchange).SetLiveDeliveryAsync(M, L, true);

        var thrown = (await act.Should().ThrowAsync<MediationProtocolException>()).Which;
        thrown.ProblemCode.Should().Be(MessagePickup.LiveModeNotSupportedCode);
        thrown.RequestId.Should().Be(exchange.Sent.Single().Request.Id);
    }

    // ---- construction -------------------------------------------------------------------------

    [Fact]
    public async Task Without_DI_the_client_owns_a_private_channel_that_cannot_receive_and_keeps_a_copy_of_the_options()
    {
        await using var services = new ServiceCollection()
            .AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(new InMemorySecretsResolver()))
            .BuildServiceProvider();
        var options = new MediatorClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) };

        var client = new PickupClient(services.GetRequiredService<DidCommClient>(), options);
        options.RequestTimeout = TimeSpan.FromSeconds(50);

        var channel = client.Exchange.Should().BeOfType<MediatorClientChannel>().Subject;
        channel.ReceivesInbound.Should().BeFalse("nothing hands a non-DI client the answers that arrive on a WebSocket");
        channel.Options.RequestTimeout.Should().Be(TimeSpan.FromSeconds(5));
        var act = () => new PickupClient(services.GetRequiredService<DidCommClient>(), new MediatorClientOptions { LivePollInterval = TimeSpan.Zero });
        act.Should().Throw<ArgumentOutOfRangeException>();
        var none = () => new PickupClient(null!);
        none.Should().Throw<ArgumentNullException>();
    }

    // A list whose entries must not be read (it fails the test if one is).
    private sealed class PoisonedList(int count) : IReadOnlyList<string>
    {
        public int Reads { get; private set; }

        public int Count => count;

        public string this[int index]
        {
            get
            {
                Reads++;
                return index == count - 1 ? null! : $"q{index}";
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            for (var i = 0; i < count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // A one-entry list that gives `first` on the first read of the entry and `later` afterwards.
    private sealed class ShiftingList(string first, string later) : IReadOnlyList<string>
    {
        private int _reads;

        public int Count => 1;

        public string this[int index] => _reads++ == 0 ? first : later;

        public IEnumerator<string> GetEnumerator()
        {
            yield return this[0];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
