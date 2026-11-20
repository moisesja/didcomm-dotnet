using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// QA permutations for the mediator client's case-insensitive id matching (DIDComm v2.1
/// threading.md L9, L15, L21). In the HTTP response — the only channel for an answer over HTTP —
/// a problem report naming the request in another letter case, by <c>pthid</c> or by <c>thid</c>,
/// fails the request with its code. Controls: an answer whose thread differs from the request id by
/// another letter or by a non-letter (each upper-cased too) completes nothing, on either path; and
/// the duplicate filter keeps such ids apart.
/// </summary>
public sealed class MediatorClientIdCaseQaTests : IAsyncLifetime
{
    private ClientWorld _w = null!;
    private readonly FakeSend _send = new();
    private readonly ManualTimeProvider _time = new();

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    private string M => _w.Mediator.Did;

    private string L => _w.Local.Did;

    private static string Upper(string id)
    {
        var upper = id.ToUpperInvariant();
        upper.Should().NotBe(id, "precondition: the request id has letters to re-case");
        return upper;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_the_HTTP_response_a_problem_report_naming_the_request_id_in_upper_case_fails_the_request_with_its_code(bool inThid)
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Problem(Upper(request.Id), code: "e.p.req.not-enrolled", inThid: inThid)));

        var thrown = await Record.ExceptionAsync(() => channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, M, L, default));

        thrown.Should().BeOfType<MediationProtocolException>().Which.ProblemCode.Should().Be("e.p.req.not-enrolled");
        ((MediationProtocolException)thrown!).RequestId.Should().Be(request.Id, "the request is named as it was sent");
        channel.PendingCount.Should().Be(0);
    }

    // ---- controls: only letter case is ignored ----------------------------------------------------

    public static TheoryData<string> OtherThreads => new()
    {
        "another-letter",
        "hyphen-to-underscore",
        "hyphen-to-dot",
        "trailing-tilde",
    };

    [Theory]
    [MemberData(nameof(OtherThreads))]
    public async Task Through_the_correlator_an_answer_on_a_thread_that_differs_in_more_than_case_completes_nothing(string variant)
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        var exchange = channel.ExchangeAsync(WebSocketRoute, request, ClientSpecs.StatusRequest, M, L, default).Observed();
        await _send.FirstCall;
        var other = Variant(request.Id, variant).ToUpperInvariant();
        var otherStatus = await _w.SnapshotAsync(_w.Status(other, count: 9));
        var otherProblem = await _w.SnapshotAsync(_w.Problem(other));
        var genuine = await _w.SnapshotAsync(_w.Status(request.Id, count: 1));

        var statusClaimed = channel.AsCorrelator().OnInbound(otherStatus);
        var problemClaimed = channel.AsCorrelator().OnInbound(otherProblem);
        var waitingAfterBoth = !exchange.IsCompleted;
        var genuineClaimed = channel.AsCorrelator().OnInbound(genuine);
        var answer = await PickupTest.Within(exchange);

        statusClaimed.Should().BeFalse();
        problemClaimed.Should().BeFalse();
        waitingAfterBoth.Should().BeTrue();
        genuineClaimed.Should().BeTrue();
        answer.Thid.Should().Be(request.Id);
    }

    [Theory]
    [MemberData(nameof(OtherThreads))]
    public async Task In_the_HTTP_response_an_answer_on_a_thread_that_differs_in_more_than_case_is_no_answer(string variant)
    {
        await using var channel = Create(_w, _send, time: _time);
        var request = _w.Request();
        _send.ReturnWith(async _ => await _w.ReceiveAsync(_w.Status(Variant(request.Id, variant).ToUpperInvariant(), count: 2)));

        var thrown = await Record.ExceptionAsync(() => channel.ExchangeAsync(HttpRoute, request, ClientSpecs.StatusRequest, M, L, default));

        thrown.Should().BeOfType<TimeoutException>().Which.Message.Should().Contain("no answer in the HTTP response");
        channel.PendingCount.Should().Be(0);
    }

    public static TheoryData<string, string> DistinctIdPairs => new()
    {
        { "inner-abc", "INNER-ABD" },
        { "inner-1", "INNER_1" },
        { "inner-1", "INNER.1" },
        { "inner_1", "INNER~1" },
        { "inner-1", "INNER-1~" },
        { "inner-o", "INNER-0" },
    };

    [Theory]
    [MemberData(nameof(DistinctIdPairs))]
    public void The_duplicate_filter_keeps_ids_that_differ_in_more_than_case_apart(string a, string b)
    {
        var first = DuplicateFilter.KeyOf(Authcrypted("did:example:alice", "did:example:bob", a), "packed-1");
        var second = DuplicateFilter.KeyOf(Authcrypted("did:example:alice", "did:example:bob", b), "packed-2");
        var recased = DuplicateFilter.KeyOf(Authcrypted("did:example:alice", "did:example:bob", a.ToUpperInvariant()), "packed-3");

        second.Should().NotBe(first);
        recased.Should().Be(first, "sanity: the same id in other letter case is one message");
    }

    /// <summary>A different id derived from <paramref name="id"/> (a UUID): another letter, or a non-letter swapped or added.</summary>
    private static string Variant(string id, string variant)
    {
        switch (variant)
        {
            case "another-letter":
            {
                var at = id.LastIndexOfAny("abcdef".ToCharArray());
                at.Should().BeGreaterThanOrEqualTo(0, "precondition: the id has a hex letter");
                return id[..at] + (id[at] == 'a' ? 'b' : 'a') + id[(at + 1)..];
            }
            case "hyphen-to-underscore":
                return ReplaceFirst(id, '-', '_');
            case "hyphen-to-dot":
                return ReplaceFirst(id, '-', '.');
            case "trailing-tilde":
                return id + "~";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    private static string ReplaceFirst(string s, char from, char to)
    {
        var at = s.IndexOf(from);
        at.Should().BeGreaterThanOrEqualTo(0, "precondition: the id has the character to swap");
        return s[..at] + to + s[(at + 1)..];
    }

    private static UnpackResult Authcrypted(string sender, string recipient, string id) => new(
        Message: new Message { Id = id, Type = "https://didcomm.org/basicmessage/2.0/message" },
        Stack: Array.Empty<EnvelopeKind>(),
        Encrypted: true,
        Authenticated: true,
        NonRepudiation: false,
        AnonymousSender: false,
        ContentEncryption: null,
        KeyWrap: null,
        SignatureAlgorithm: null,
        SignerKid: null,
        SenderKid: sender + "#key-1",
        RecipientKid: recipient + "#key-1",
        AllRecipientKids: new[] { recipient + "#key-1" },
        FromPrior: null);
}
