using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using DataProofsDotnet.Jose.Encryption;
using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Jose;
using DidComm.Mediator.Coordination;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Routing;
using DidComm.Samples.Shared;
using DidComm.Secrets;
using DidComm.Transports.ReturnRoute;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using JoseCryptoProvider = DataProofsDotnet.Jose.JoseCryptoProvider;

namespace DidComm.Mediator.Tests.Coordination;

/// <summary>What happened to one hand-written envelope: where it stopped and with what.</summary>
internal sealed record AdvDelivery(Exception? UnpackError, Exception? DispatchError, DispatchOutcome? Outcome)
{
    /// <summary>"ok" when the only things thrown are the ones the handler contract allows.</summary>
    public string Verdict
    {
        get
        {
            if (UnpackError is not null)
                return UnpackError is DidCommException ? "ok" : "unpack threw " + UnpackError.GetType().FullName;
            if (DispatchError is not null)
                return DispatchError is MalformedMessageException ? "ok" : "dispatch threw " + DispatchError.GetType().FullName;
            return "ok";
        }
    }
}

/// <summary>
/// Builds authcrypt envelopes over plaintext bytes written by hand, so a test can send JSON that
/// the library's own packer would never emit (escapes, duplicate members, odd numbers, a
/// <c>from</c> that disagrees with the sender key).
/// </summary>
internal static class AdvWire
{
    public static string Plaintext(string type, string? from, string to, string? bodyJson, string? id = null, string? extraMembers = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"id\":\"").Append(id ?? Guid.NewGuid().ToString()).Append('"');
        sb.Append(",\"typ\":\"application/didcomm-plain+json\"");
        sb.Append(",\"type\":\"").Append(type).Append('"');
        if (from is not null)
            sb.Append(",\"from\":\"").Append(from).Append('"');
        sb.Append(",\"to\":[\"").Append(to).Append("\"]");
        if (extraMembers is not null)
            sb.Append(',').Append(extraMembers);
        if (bodyJson is not null)
            sb.Append(",\"body\":").Append(bodyJson);
        sb.Append('}');
        return sb.ToString();
    }

    public static Jwk KeyAgreementKey(PeerIdentity identity) => identity.Privates.First(j => j.Crv == "X25519");

    public static async Task<string> AuthcryptAsync(HandlerFixture fx, string plaintextJson, PeerIdentity sender, PeerIdentity recipient)
    {
        var crypto = new JoseCryptoProvider();
        var secrets = fx.Host.Secrets;
        var keyOps = new KeyOperationResolver(secrets, null, crypto);
        var senderJwk = KeyAgreementKey(sender);
        var recipientJwk = KeyAgreementKey(recipient);
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

    /// <summary>Unpack as the mediator and dispatch, recording where anything was thrown.</summary>
    public static async Task<AdvDelivery> DeliverPackedAsync(HandlerFixture fx, string packed)
    {
        UnpackResult received;
        try
        {
            received = await fx.Host.Client.UnpackAsync(packed);
        }
        catch (Exception ex)
        {
            return new AdvDelivery(ex, null, null);
        }

        try
        {
            var outcome = await fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, CancellationToken.None);
            return new AdvDelivery(null, null, outcome);
        }
        catch (Exception ex)
        {
            return new AdvDelivery(null, ex, null);
        }
    }

    public static async Task<AdvDelivery> DeliverAsync(HandlerFixture fx, string plaintextJson, PeerIdentity sender, PeerIdentity mediator)
        => await DeliverPackedAsync(fx, await AuthcryptAsync(fx, plaintextJson, sender, mediator));

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

/// <summary>A list a store hands back that faults when it is read (a dead cursor, a list changed underneath).</summary>
internal sealed class AdvFaultyList<T>(int count) : IReadOnlyList<T>
{
    public int Count { get; } = count;

    public T this[int index] => throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");

    public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A host directory that knows nobody.</summary>
internal sealed class AdvEmptyDirectory : IForwardRecipientRegistry
{
    public ValueTask<bool> IsRegisteredAsync(string recipientDid, CancellationToken ct) => new(false);
}

/// <summary>
/// Red-team pass over the mediator role of Coordinate Mediation 3.0 (#93). A test whose comment
/// starts "Was a FINDING" failed against the first implementation and records the defect that was
/// fixed in the same change; the others are attacks that were tried and are defeated. All are kept
/// as regression tests.
/// </summary>
public sealed class CoordinateMediationAdversarialTests
{
    private const string Request = CoordinateMediation.MediateRequestType;
    private const string Update = CoordinateMediation.RecipientUpdateType;
    private const string Query = CoordinateMediation.RecipientQueryType;

    // ---- 5. faults contained: JSON a hostile sender writes by hand ---------------------------

    public static TheoryData<string, string, string> HandWrittenBodies => new()
    {
        // An escaped lone surrogate is legal JSON text that System.Text.Json refuses to turn into a
        // string (InvalidOperationException from GetString / WriteTo / ValueEquals).
        { "lone surrogate in an unknown member of mediate-request", Request, """{"x":"\ud800"}""" },
        { "lone surrogate in recipient_did", Update, """{"updates":[{"recipient_did":"did:example:\udc00","action":"add"}]}""" },
        { "lone surrogate in action", Update, """{"updates":[{"recipient_did":"did:example:a","action":"ad\ud800"}]}""" },
        { "lone surrogate in an unknown member of an entry", Update, """{"updates":[{"recipient_did":"did:example:a","action":"add","note":"\ud800"}]}""" },
        { "lone surrogate in an unknown member of recipient-query", Query, """{"paginate":{"limit":1,"offset":0},"x":"\udfff"}""" },
        { "lone surrogate in a member name that starts like a known one", Update, """{"updates":[],"update\ud800":1}""" },
        { "duplicate recipient_did in one entry", Update, """{"updates":[{"recipient_did":"did:example:a","recipient_did":"did:example:b","action":"add"}]}""" },
        { "duplicate updates member", Update, """{"updates":[],"updates":[{"recipient_did":"did:example:a","action":"add"}]}""" },
        { "escaped known member name", Update, """{"updates":[{"recipient_did":"did:example:a","action":"add"}]}""" },
        { "escaped action value", Update, """{"updates":[{"recipient_did":"did:example:a","action":"add"}]}""" },
        { "body nested 60 deep", Request, AdvWire.Nested(60) },
        { "body nested 200 deep", Request, AdvWire.Nested(200) },
        { "paginate nested 60 deep", Query, "{\"paginate\":" + AdvWire.Nested(60) + "}" },
        { "body is an array", Request, "[]" },
        { "body is a string", Query, "\"x\"" },
        { "limit with a huge exponent", Query, """{"paginate":{"limit":1e999999,"offset":0}}""" },
        { "offset with a negative exponent", Query, """{"paginate":{"limit":1,"offset":1e-999999}}""" },
        { "NUL inside recipient_did", Update, """{"updates":[{"recipient_did":"did:example:a\u0000","action":"add"}]}""" },
    };

    [Theory]
    [MemberData(nameof(HandWrittenBodies))]
    public async Task A_hand_written_body_from_an_enrolled_peer_makes_dispatch_throw_nothing_but_malformed(string what, string type, string body)
    {
        // Defeated. MediationBodies.ToElement re-serialises the body and ReadDid / ReadAction read
        // strings from it, all of which throw InvalidOperationException for an escaped lone
        // surrogate; the unpack path refuses such a plaintext first, and duplicate members, deep
        // nesting and non-object bodies with it. The handler depends on that: these rows pin it.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);

        var delivery = await AdvWire.DeliverAsync(fx, AdvWire.Plaintext(type, bob.Did, mediator.Did, body), bob, mediator);

        delivery.Verdict.Should().Be("ok", what);
    }

    [Fact]
    public async Task A_stranger_cannot_make_dispatch_throw_with_an_escaped_surrogate_in_a_mediate_request()
    {
        // Defeated (at unpack). The body of a mediate-request carries nothing the protocol defines,
        // yet it is re-serialised to check that it is an object.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();

        var delivery = await AdvWire.DeliverAsync(fx, AdvWire.Plaintext(Request, mallory.Did, mediator.Did, """{"x":"\ud800"}"""), mallory, mediator);

        delivery.Verdict.Should().Be("ok");
        fx.Memory.GrantCount.Should().Be(0);
    }

    // ---- 5. faults contained: host collaborators ------------------------------------------------

    [Theory]
    [InlineData("request-store-get")]
    [InlineData("request-policy")]
    [InlineData("request-store-grant")]
    [InlineData("update-store-get")]
    [InlineData("update-policy")]
    [InlineData("update-store-write")]
    [InlineData("query-store-list")]
    public async Task A_fault_thrown_while_the_request_is_being_cancelled_leaves_dispatch_as_cancellation_only(string where)
    {
        // Was a FINDING (guarantee 5): the fault boundaries in MediationCoordinator stood aside
        // whenever the request token was cancelled, so whatever a store or policy threw at that
        // moment (a driver's own "cancelled by user" error, an ObjectDisposedException) left
        // dispatch with its text. Fixed: once the token is cancelled the only thing that leaves is
        // an OperationCanceledException; the collaborator's exception is replaced.
        await using var fx = HandlerFixture.Create(fakeStore: true, fakePolicy: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        if (!where.StartsWith("request", StringComparison.Ordinal))
            await fx.EnrolAsync(bob.Did, mediator.Did);

        using var cts = new CancellationTokenSource();
        Exception Boom()
        {
            cts.Cancel();
            return new HandlerBoomException(HandlerBoomException.Marker);
        }

        Message message;
        switch (where)
        {
            case "request-store-get":
                fx.FakeStore!.OnGetGrant = (_, _) => throw Boom();
                message = CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did);
                break;
            case "request-policy":
                fx.Policy!.OnRequest = (_, _, _) => throw Boom();
                message = CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did);
                break;
            case "request-store-grant":
                fx.FakeStore!.OnGrant = (_, _) => throw Boom();
                message = CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did);
                break;
            case "update-store-get":
                fx.FakeStore!.OnGetGrant = (_, _) => throw Boom();
                message = CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { HandlerMessages.Add("did:example:r1") });
                break;
            case "update-policy":
                fx.Policy!.OnRegister = (_, _, _, _) => throw Boom();
                message = CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { HandlerMessages.Add("did:example:r1") });
                break;
            case "update-store-write":
                fx.FakeStore!.OnUpdateRecipients = (_, _, _) => throw Boom();
                message = CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { HandlerMessages.Add("did:example:r1") });
                break;
            default:
                fx.FakeStore!.OnListRecipients = (_, _, _, _) => throw Boom();
                message = CoordinateMediation.CreateRecipientQuery(bob.Did, mediator.Did);
                break;
        }

        var received = await fx.ReceiveAsync(message, mediator.Did);
        var thrown = await Record.ExceptionAsync(async () => await fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, cts.Token));

        var verdict = thrown is null or OperationCanceledException ? "ok" : "dispatch threw " + thrown.GetType().Name;
        verdict.Should().Be("ok");
    }

    [Theory]
    [InlineData("page-dids")]
    [InlineData("update-outcomes")]
    [InlineData("stored-routing-dids-on-request")]
    [InlineData("stored-routing-dids-on-update")]
    public async Task A_store_result_that_faults_when_it_is_read_never_escapes_dispatch(string what)
    {
        // Was a FINDING (guarantee 5; needs a custom store): the store's return values were read
        // outside the fault boundaries, and the reply builders caught ArgumentException only. A
        // list that throws when read (a cursor that died, a live List<T> changed by another
        // request) left dispatch as InvalidOperationException. Fixed: every list a store returns is
        // copied inside the boundary, and a reply that cannot be built is a logged server fault.
        await using var fx = HandlerFixture.Create(fakeStore: true);
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);

        Message message;
        switch (what)
        {
            case "page-dids":
                fx.FakeStore!.OnListRecipients = (_, _, _, _) => new ValueTask<RecipientPage>(new RecipientPage(new AdvFaultyList<string>(1), 1));
                message = CoordinateMediation.CreateRecipientQuery(bob.Did, mediator.Did);
                break;
            case "update-outcomes":
                fx.FakeStore!.OnUpdateRecipients = (_, updates, _) => new ValueTask<IReadOnlyList<RecipientStoreOutcome>>(new AdvFaultyList<RecipientStoreOutcome>(updates.Count));
                message = CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { HandlerMessages.Add("did:example:r1") });
                break;
            case "stored-routing-dids-on-request":
                fx.FakeStore!.OnGetGrant = (peer, _) => new ValueTask<MediationGrant?>(new MediationGrant(peer, new AdvFaultyList<string>(1)));
                message = CoordinateMediation.CreateMediateRequest(bob.Did, mediator.Did);
                break;
            default:
                fx.FakeStore!.OnGetGrant = (peer, _) => new ValueTask<MediationGrant?>(new MediationGrant(peer, new AdvFaultyList<string>(1)));
                message = CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { HandlerMessages.Add("did:example:r1") });
                break;
        }

        var thrown = await Record.ExceptionAsync(async () => await fx.SendAsync(message, mediator.Did));

        (thrown?.GetType().Name ?? "ok").Should().Be("ok");
    }

    // ---- 4. pagination integers --------------------------------------------------------------------

    public static TheoryData<string, string, int> PagingIntegers => new()
    {
        // Were FINDING rows, now fixed: an integer too large for System.Decimal was refused as "must be an integer".
        { "limit of 30 digits", """{"limit":100000000000000000000000000000,"offset":0}""", 2 },
        { "offset of 30 digits", """{"limit":10,"offset":100000000000000000000000000000}""", 0 },
        { "limit 1e40", """{"limit":1e40,"offset":0}""", 2 },
        // Defeated rows: no wrap-around, no negative page.
        { "limit 2^32+1 does not wrap to 1", """{"limit":4294967297,"offset":0}""", 2 },
        { "limit long.MaxValue", """{"limit":9223372036854775807,"offset":0}""", 2 },
        { "limit ulong.MaxValue", """{"limit":18446744073709551615,"offset":0}""", 2 },
        { "limit long.MinValue", """{"limit":-9223372036854775808,"offset":0}""", 0 },
        { "limit -1", """{"limit":-1,"offset":0}""", 0 },
        { "limit 1e2", """{"limit":1e2,"offset":0}""", 2 },
        { "offset 2^31", """{"limit":10,"offset":2147483648}""", 0 },
        { "offset 2^32+1 does not wrap to 1", """{"limit":10,"offset":4294967297}""", 0 },
        { "offset -0", """{"limit":10,"offset":-0}""", 2 },
    };

    [Theory]
    [MemberData(nameof(PagingIntegers))]
    public async Task An_out_of_range_paging_integer_is_clamped_never_wrapped_and_never_refused(string what, string paginate, int expected)
    {
        // Was a FINDING (guarantee 4 / JC-6, low): "integers beyond int are clamped" held only up
        // to decimal.MaxValue (about 7.9e28), so 28 digits was a page and 30 digits a 400. Fixed:
        // the number is decided from its own text, whatever its size.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:r1"), HandlerMessages.Add("did:example:r2"));

        var delivery = await AdvWire.DeliverAsync(fx, AdvWire.Plaintext(Query, bob.Did, mediator.Did, "{\"paginate\":" + paginate + "}"), bob, mediator);

        (delivery.UnpackError?.GetType().Name ?? delivery.DispatchError?.GetType().Name ?? "answered").Should().Be("answered", what);
        CoordinateMediation.TryParseRecipient(delivery.Outcome!.Reply!, out var dids, out var pagination).Should().BeTrue();
        dids!.Count.Should().Be(expected, what);
        pagination!.Count.Should().Be(expected);
        pagination.Offset.Should().BeGreaterThanOrEqualTo(0);
        pagination.Remaining.Should().BeGreaterThanOrEqualTo(0);
    }

    [Theory]
    [InlineData("1e-999999")]
    [InlineData("0.00000000000000000000000000001")]
    [InlineData("1.00000000000000000000000000001")]
    public async Task A_paging_number_with_a_fractional_part_is_malformed_however_small_the_fraction(string offset)
    {
        // Was a FINDING (JC-6, very low): "1.5 is malformed" held only while the fraction survived
        // the conversion to System.Decimal, so these read as the integers 0, 0 and 1. Fixed: a
        // non-zero digit right of the decimal point is refused, however far right.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);

        var delivery = await AdvWire.DeliverAsync(fx, AdvWire.Plaintext(Query, bob.Did, mediator.Did, "{\"paginate\":{\"limit\":1,\"offset\":" + offset + "}}"), bob, mediator);

        (delivery.DispatchError?.GetType().Name ?? "answered").Should().Be(nameof(MalformedMessageException));
    }

    // ---- 1/6. which message types reach the registry ----------------------------------------------

    public static TheoryData<string> TypeSpellings => new()
    {
        "https://didcomm.org/coordinate-mediation/3.0/recipient-update",
        "https://didcomm.org/coordinate-mediation/3.1/recipient-update",
        "https://didcomm.org/coordinate-mediation/3.999999999/recipient-update",
        "https://didcomm.org/coordinate-mediation/3.0.0/recipient-update",
        "https://didcomm.org/coordinate-mediation/03.0/recipient-update",
        "https://didcomm.org/coordinate-mediation/3.00/recipient-update",
        "https://didcomm.org/coordinate-mediation/3/recipient-update",
        "https://didcomm.org/coordinate-mediation/2.0/keylist-update",
        "https://didcomm.org/coordinate-mediation/4.0/recipient-update",
        "https://DIDCOMM.org/coordinate-mediation/3.0/recipient-update",
        "https://didcomm.org/Coordinate-Mediation/3.0/recipient-update",
        "https://didcomm.org/coordinate-mediation/3.0/Recipient-Update",
        "HTTPS://didcomm.org/coordinate-mediation/3.0/recipient-update",
        "http://didcomm.org/coordinate-mediation/3.0/recipient-update",
        "https://didcomm.org/coordinate-mediation/3.0/recipient-update/",
        "https://didcomm.org/coordinate-mediation/3.0/recipient-update?x=1",
        "https://didcomm.org/coordinate-mediation/3.0/recipient-update#x",
        "https://didcomm.org//coordinate-mediation/3.0/recipient-update",
        "https://didcomm.org/x/../coordinate-mediation/3.0/recipient-update",
        "https://didcomm.org/x/coordinate-mediation/3.0/recipient-update",
        "https://didcomm.org:443/coordinate-mediation/3.0/recipient-update",
        "https://user@didcomm.org/coordinate-mediation/3.0/recipient-update",
        "https://didcomm.org.evil.example/coordinate-mediation/3.0/recipient-update",
        "https://evil.example/coordinate-mediation/3.0/recipient-update",
        "https://evil.example/https://didcomm.org/coordinate-mediation/3.0/recipient-update",
        "did:sov:BzCbsNYhMrjHiqZDTUASHg;spec/coordinate-mediation/3.0/recipient-update",
        "coordinate-mediation/3.0/recipient-update",
    };

    [Theory]
    [MemberData(nameof(TypeSpellings))]
    public async Task A_type_the_handler_acts_on_is_the_didcomm_org_protocol_and_is_in_replay_scope(string type)
    {
        // The endpoint's replay guard (InboundReplayGate, FR-RR-08) and the handler decide
        // separately whether a type is Coordinate Mediation. A spelling the handler applies but the
        // gate calls out of scope would let a captured recipient-update be replayed.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        var packed = await AdvWire.AuthcryptAsync(
            fx, AdvWire.Plaintext(type, bob.Did, mediator.Did, """{"updates":[{"recipient_did":"did:example:probe","action":"add"}]}"""), bob, mediator);

        UnpackResult? received = null;
        var unpackError = await Record.ExceptionAsync(async () => received = await fx.Host.Client.UnpackAsync(packed));
        if (unpackError is not null)
        {
            unpackError.Should().BeAssignableTo<DidCommException>();
            return;
        }
        var dispatchError = await Record.ExceptionAsync(async () => await fx.Host.Dispatcher.DispatchAsync(received!, fx.Host.Client, fx.Host.Options, CancellationToken.None));
        (dispatchError is null or MalformedMessageException).Should().BeTrue();

        if ((await fx.RegisteredAsync(bob.Did)).Count == 0)
            return;
        type.Contains("://didcomm.org/coordinate-mediation/3.", StringComparison.OrdinalIgnoreCase).Should().BeTrue("only the didcomm.org protocol may change the registry");
        InboundReplayGate.IsInScope(received!, ReturnRouteHeader.Read(received!.Message)).Should().BeTrue("a message that changes mediator state must pass the replay guard");
    }

    // ---- 7. DI --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_directory_registered_after_AddCoordinateMediation_does_not_silently_hide_enrolled_recipients()
    {
        // Was a FINDING (guarantee 7): the fail-fast of JC-8 looks only at registrations made before
        // the call. A host directory registered afterwards wins container resolution, and every
        // forward for a recipient enrolled through Coordinate Mediation would be dropped without a
        // word. Fixed: building the handler re-checks the registrations, so the mediator cannot
        // start dispatching at all in that configuration.
        await using var host = MediatorTestHost.Create(after: b =>
        {
            b.Services.AddSingleton<IForwardRecipientRegistry>(new AdvEmptyDirectory());
            b.AddRoutingMediator();
        });

        var firstUse = Record.Exception(() => host.Dispatcher);

        firstUse.Should().BeOfType<InvalidOperationException>("a mediator that enrols recipients and then drops every forward for them must not be a silent outcome")
            .Which.Message.Should().Contain("registered after AddCoordinateMediation()").And.Contain("UseAsForwardRecipientRegistry");
    }

    // ---- 1. identity -----------------------------------------------------------------------------------

    [Fact]
    public async Task Mallory_authcrypting_a_plaintext_whose_from_is_bob_gets_nothing_for_either_of_them()
    {
        // Self-consistent forgery: every byte is validly encrypted by Mallory; only the identity
        // fields disagree (`from` = Bob, sender key = Mallory).
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:bobs"));

        var remove = """{"updates":[{"recipient_did":"did:example:bobs","action":"remove"}]}""";
        var forged = await AdvWire.DeliverAsync(fx, AdvWire.Plaintext(Update, bob.Did, mediator.Did, remove), mallory, mediator);
        var forgedRequest = await AdvWire.DeliverAsync(fx, AdvWire.Plaintext(Request, bob.Did, mediator.Did, "{}"), mallory, mediator);
        var forgedQuery = await AdvWire.DeliverAsync(fx, AdvWire.Plaintext(Query, bob.Did, mediator.Did, "{}"), mallory, mediator);

        forged.UnpackError.Should().BeAssignableTo<DidCommException>();
        forgedRequest.UnpackError.Should().BeAssignableTo<DidCommException>();
        forgedQuery.UnpackError.Should().BeAssignableTo<DidCommException>();
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:bobs");
        (await fx.Memory.GetGrantAsync(mallory.Did, default)).Should().BeNull();
        fx.Memory.GrantCount.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mallory_wrapping_a_request_signed_by_bob_in_her_own_authcrypt_changes_nothing(bool plaintextNamesBob)
    {
        // authcrypt(sign): the signature is genuinely Bob's (a JWS he made for this mediator and
        // Mallory captured), the sender key is Mallory's.
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:bobs"));
        await fx.EnrolAsync(mallory.Did, mediator.Did);

        var builder = new MessageBuilder().WithType(Update).WithTo(mediator.Did)
            .WithBody(HandlerMessages.UpdatesBody(("did:example:bobs", "remove"), ("did:example:planted", "add")));
        if (plaintextNamesBob)
            builder.WithFrom(bob.Did);
        var message = builder.Build();

        var thrown = await Record.ExceptionAsync(async () =>
        {
            var packed = (await fx.Host.Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: mallory.Did, SignFrom: bob.Did))).Message;
            var received = await fx.Host.Client.UnpackAsync(packed);
            var outcome = await fx.Host.Dispatcher.DispatchAsync(received, fx.Host.Client, fx.Host.Options, CancellationToken.None);
            outcome.Reply.Should().BeNull("a sender key and a signer that disagree are nobody");
        });

        (thrown is null or DidCommException or ArgumentException).Should().BeTrue();
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:bobs");
        (await fx.RegisteredAsync(mallory.Did)).Should().BeEmpty();
        (await fx.Memory.FindGrantByRecipientDidAsync("did:example:planted", default)).Should().BeNull();
    }

    [Fact]
    public async Task Rewriting_the_sender_key_of_bobs_envelope_after_packing_is_rejected()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        await fx.EnrolAsync(mallory.Did, mediator.Did);
        await fx.EnrolAsync(bob.Did, mediator.Did);

        var genuine = (await fx.Host.Client.PackEncryptedAsync(
            CoordinateMediation.CreateRecipientUpdate(bob.Did, mediator.Did, new[] { HandlerMessages.Add("did:example:planted") }),
            new PackEncryptedOptions(Recipients: new[] { mediator.Did }, From: bob.Did))).Message;

        // Post-pack tampering: point skid (and apu, which is derived from it) at Mallory's key.
        var jwe = JsonNode.Parse(genuine)!.AsObject();
        var header = JsonNode.Parse(AdvBase64Url.Decode(jwe["protected"]!.GetValue<string>()))!.AsObject();
        var malloryKid = AdvWire.KeyAgreementKey(mallory).Kid!;
        header["skid"] = malloryKid;
        header["apu"] = AdvBase64Url.Encode(Encoding.UTF8.GetBytes(malloryKid));
        jwe["protected"] = AdvBase64Url.Encode(Encoding.UTF8.GetBytes(header.ToJsonString()));

        var delivery = await AdvWire.DeliverPackedAsync(fx, jwe.ToJsonString());

        delivery.UnpackError.Should().BeAssignableTo<DidCommException>();
        (await fx.RegisteredAsync(mallory.Did)).Should().BeEmpty();
        (await fx.RegisteredAsync(bob.Did)).Should().BeEmpty();
    }

    // ---- 2. ownership --------------------------------------------------------------------------------

    public static TheoryData<string, string> Spellings => new()
    {
        { "fragment", "did:example:Bob-1#key-1" },
        { "empty fragment", "did:example:Bob-1#" },
        { "query", "did:example:Bob-1?versionId=1" },
        { "path", "did:example:Bob-1/" },
        { "parameter", "did:example:Bob-1;service=x" },
        { "leading space", " did:example:Bob-1" },
        { "trailing space", "did:example:Bob-1 " },
        { "trailing newline", "did:example:Bob-1\n" },
        { "trailing tab", "did:example:Bob-1\t" },
        { "trailing NUL", "did:example:Bob-1\0" },
        { "trailing colon", "did:example:Bob-1:" },
        { "upper-case scheme", "DID:example:Bob-1" },
        { "upper-case method", "did:EXAMPLE:Bob-1" },
        { "lower-case id", "did:example:bob-1" },
        { "percent-encoded letter", "did:example:%42ob-1" },
        { "percent-encoded hyphen", "did:example:Bob%2D1" },
        { "full-width letter", "did:example:Ｂob-1" },
        { "cyrillic look-alike", "did:example:Вob-1" },
        { "zero-width joiner", "did:example:Bob‍-1" },
        { "combining mark", "did:example:Bob́-1" },
        { "byte-order mark", "﻿did:example:Bob-1" },
    };

    [Theory]
    [MemberData(nameof(Spellings))]
    public async Task No_spelling_of_bobs_did_lets_mallory_touch_bobs_registration(string what, string spelling)
    {
        const string bobs = "did:example:Bob-1";
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.EnrolAsync(mallory.Did, mediator.Did);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add(bobs));

        var results = await fx.UpdateAsync(mallory.Did, mediator.Did,
            HandlerMessages.Remove(spelling), HandlerMessages.Add(spelling), HandlerMessages.Add(spelling), HandlerMessages.Remove(bobs), HandlerMessages.Add(bobs));

        results[0].Result.Should().Be(RecipientUpdateOutcome.ClientError, what);
        results[3].Result.Should().Be(RecipientUpdateOutcome.ClientError);
        results[4].Result.Should().Be(RecipientUpdateOutcome.ClientError);
        (await fx.RegisteredAsync(bob.Did)).Should().Equal(bobs);
        (await fx.Memory.FindGrantByRecipientDidAsync(bobs, default))!.PeerDid.Should().Be(bob.Did);
        var mallorys = await fx.RegisteredAsync(mallory.Did);
        mallorys.Should().NotContain(bobs);
        // Whatever she did get is the literal string she sent (a different DID), once, and the
        // forward path cannot reach it through Bob's DID.
        mallorys.Should().OnlyContain(d => d == spelling);
        mallorys.Count.Should().BeLessThanOrEqualTo(1);
        fx.Memory.RecipientCount.Should().Be(1 + mallorys.Count);
        (await fx.QueryAsync(bob.Did, mediator.Did)).Dids.Should().Equal(bobs);
    }

    [Fact]
    public async Task Duplicates_and_reversals_inside_one_batch_leave_the_counters_exact()
    {
        await using var fx = HandlerFixture.Create(o =>
        {
            o.MaxRecipientsPerGrant = 2;
            o.MaxRecipients = 3;
        });
        var mediator = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();
        await fx.EnrolAsync(bob.Did, mediator.Did);
        await fx.EnrolAsync(mallory.Did, mediator.Did);
        await fx.UpdateAsync(bob.Did, mediator.Did, HandlerMessages.Add("did:example:b1"));

        var batch = new List<RecipientUpdate>();
        for (var i = 0; i < 150; i++)
        {
            batch.Add(HandlerMessages.Add("did:example:m1"));
            batch.Add(HandlerMessages.Add("did:example:m1"));
            batch.Add(HandlerMessages.Add("did:example:b1"));
            batch.Add(HandlerMessages.Remove("did:example:b1"));
            batch.Add(HandlerMessages.Remove("did:example:m1"));
            batch.Add(HandlerMessages.Remove("did:example:m1"));
        }
        batch.Add(HandlerMessages.Add("did:example:m1"));
        batch.Add(HandlerMessages.Add("did:example:m2"));
        batch.Add(HandlerMessages.Add("did:example:m3")); // over the per-grant bound

        var results = await fx.UpdateAsync(mallory.Did, mediator.Did, batch.ToArray());

        results.Take(6).Select(r => r.Result).Should().Equal(
            RecipientUpdateOutcome.Success, RecipientUpdateOutcome.NoChange, RecipientUpdateOutcome.ClientError,
            RecipientUpdateOutcome.ClientError, RecipientUpdateOutcome.Success, RecipientUpdateOutcome.ClientError);
        results[^1].Result.Should().Be(RecipientUpdateOutcome.ClientError);
        (await fx.RegisteredAsync(mallory.Did)).Should().Equal("did:example:m1", "did:example:m2");
        (await fx.RegisteredAsync(bob.Did)).Should().Equal("did:example:b1");
        fx.Memory.RecipientCount.Should().Be(3);

        // Revoked and re-issued: nothing of hers comes back, nothing of Bob's moves.
        (await fx.Memory.RevokeAsync(mallory.Did, default)).Should().BeTrue();
        fx.Memory.RecipientCount.Should().Be(1);
        await fx.EnrolAsync(mallory.Did, mediator.Did);
        (await fx.RegisteredAsync(mallory.Did)).Should().BeEmpty();
        (await fx.Memory.FindGrantByRecipientDidAsync("did:example:m1", default)).Should().BeNull();
        (await fx.Memory.FindGrantByRecipientDidAsync("did:example:b1", default))!.PeerDid.Should().Be(bob.Did);
        (await fx.UpdateAsync(mallory.Did, mediator.Did, HandlerMessages.Remove("did:example:b1")))[0].Result.Should().Be(RecipientUpdateOutcome.ClientError);
        fx.Memory.RecipientCount.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_grant_revoke_add_and_remove_never_break_a_bound_or_the_index()
    {
        const int maxGrants = 3, perGrant = 5, maxTotal = 8;
        var store = new InMemoryMediationStore(new CoordinateMediationOptions { MaxGrants = maxGrants, MaxRecipientsPerGrant = perGrant, MaxRecipients = maxTotal });
        var peers = Enumerable.Range(0, 5).Select(i => "did:example:peer" + i).ToArray();
        var pool = Enumerable.Range(0, 12).Select(i => "did:example:r" + i).ToArray();
        var violations = new ConcurrentQueue<string>();

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 3000; i++)
            {
                var peer = peers[random.Next(peers.Length)];
                switch (random.Next(10))
                {
                    case 0:
                        await store.RevokeAsync(peer, default);
                        break;
                    case 1:
                    case 2:
                        await store.GrantAsync(new MediationGrant(peer, new[] { "did:example:mediator" }), default);
                        break;
                    default:
                        var batch = Enumerable.Range(0, random.Next(1, 6))
                            .Select(_ => new RecipientUpdate(pool[random.Next(pool.Length)], random.Next(3) == 0 ? RecipientUpdateAction.Remove : RecipientUpdateAction.Add))
                            .ToArray();
                        await store.UpdateRecipientsAsync(peer, batch, default);
                        break;
                }

                var count = store.RecipientCount;
                if (count < 0 || count > maxTotal)
                    violations.Enqueue("recipient count " + count);
                var grants = store.GrantCount;
                if (grants > maxGrants)
                    violations.Enqueue("grant count " + grants);
                var hit = await store.FindGrantByRecipientDidAsync(pool[random.Next(pool.Length)], default);
                if (hit is not null && Array.IndexOf(peers, hit.PeerDid) < 0)
                    violations.Enqueue("index returned a grant nobody made");
            }
        })).ToArray();
        await Task.WhenAll(workers);

        violations.Should().BeEmpty();
        var total = 0;
        foreach (var peer in peers)
        {
            var page = await store.ListRecipientsAsync(peer, 0, int.MaxValue, default);
            page.Dids.Count.Should().Be(page.Total);
            page.Total.Should().BeLessThanOrEqualTo(perGrant);
            page.Dids.Should().OnlyHaveUniqueItems();
            total += page.Total;
            foreach (var did in page.Dids)
                (await store.FindGrantByRecipientDidAsync(did, default))!.PeerDid.Should().Be(peer);
            if (page.Total > 0)
                (await store.GetGrantAsync(peer, default)).Should().NotBeNull();
        }
        store.RecipientCount.Should().Be(total);
        foreach (var did in pool)
        {
            var owner = await store.FindGrantByRecipientDidAsync(did, default);
            if (owner is not null)
                (await store.ListRecipientsAsync(owner.PeerDid, 0, int.MaxValue, default)).Dids.Should().Contain(did);
        }

        foreach (var peer in peers)
            await store.RevokeAsync(peer, default);
        store.RecipientCount.Should().Be(0);
        store.GrantCount.Should().Be(0);
        foreach (var did in pool)
            (await store.FindGrantByRecipientDidAsync(did, default)).Should().BeNull();
    }

    // ---- 8. replies ----------------------------------------------------------------------------------

    [Fact]
    public async Task Headers_that_ask_for_the_reply_to_go_elsewhere_are_not_obeyed_or_echoed()
    {
        await using var fx = HandlerFixture.Create();
        var mediator = await fx.NewPeerAsync();
        var other = await fx.NewPeerAsync();
        var bob = await fx.NewPeerAsync();
        var mallory = await fx.NewPeerAsync();

        var thid = new string('t', 5000) + ".-_~";
        var extra = "\"thid\":\"" + thid + "\",\"pthid\":\"parent-1\",\"reply_to\":\"" + bob.Did + "\",\"reply-to\":[\"" + bob.Did + "\"]," +
                    "\"return_route\":\"all\",\"please_ack\":[\"x\"],\"x-evil\":\"did:example:evil\",\"from_prior\":null";
        // Addressed to the mediator and, in the plaintext only, to Bob as well.
        var plaintext = AdvWire.Plaintext(Request, mallory.Did, mediator.Did, "{}", extraMembers: extra)
            .Replace("\"to\":[\"" + mediator.Did + "\"]", "\"to\":[\"" + bob.Did + "\",\"" + mediator.Did + "\",\"" + other.Did + "\"]", StringComparison.Ordinal);

        var delivery = await AdvWire.DeliverAsync(fx, plaintext, mallory, mediator);

        delivery.Verdict.Should().Be("ok");
        var reply = delivery.Outcome!.Reply!;
        reply.To.Should().Equal(mallory.Did);
        reply.From.Should().Be(mediator.Did);
        reply.Thid.Should().Be(thid);
        reply.Pthid.Should().BeNull();
        reply.PleaseAck.Should().BeNull();
        (reply.AdditionalHeaders?.Count ?? 0).Should().Be(0);
        CoordinateMediation.TryParseGrant(reply, out var routing).Should().BeTrue();
        routing.Should().Equal(mediator.Did);
        (await fx.Memory.GetGrantAsync(bob.Did, default)).Should().BeNull();
        foreach (var line in fx.Logs.Handler)
            line.Everything.Should().NotContain("did:").And.NotContain("tttttttt");
    }
}

internal static class AdvBase64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}
