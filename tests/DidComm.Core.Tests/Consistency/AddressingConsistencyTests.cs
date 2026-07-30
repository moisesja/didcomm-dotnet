using System.Collections;
using DidComm.Consistency;
using DidComm.Exceptions;
using DidComm.Facade;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Consistency;

public sealed class AddressingConsistencyTests
{
    [Theory]
    [InlineData("did:example:alice", "did:example:alice#key-1")]
    [InlineData("did:example:alice", "did:example:alice?foo=bar#key-1")]
    [InlineData("did:example:alice?foo=bar", "did:example:alice#key-1")]
    [InlineData("did:example:alice/path", "did:example:alice#key-2")]
    public void Authcrypt_from_matches_skid_via_did_subject(string from, string skid)
    {
        // Must not throw.
        AddressingConsistency.CheckAuthcryptFromMatchesSkid(from, skid);
    }

    [Fact]
    public void Authcrypt_from_mismatched_skid_throws()
    {
        Action act = () => AddressingConsistency.CheckAuthcryptFromMatchesSkid(
            "did:example:alice", "did:example:carol#key-1");

        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-01*");
    }

    [Fact]
    public void Authcrypt_with_null_from_short_circuits()
    {
        // Anoncrypt-style: no 'from'. Check is a no-op.
        AddressingConsistency.CheckAuthcryptFromMatchesSkid(from: null, "did:example:alice#k");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Authenticated_decrypt_without_surfaced_skid_fails_closed(string? skid)
    {
        // Issue #52 — if the JOSE layer's IsAuthenticated ⟺ non-empty-skid invariant ever
        // regressed, the guard must reject rather than let the from↔skid binding no-op.
        Action act = () => AddressingConsistency.CheckAuthcryptSkidSurfaced(skid);
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-01*");
    }

    [Fact]
    public void Authenticated_decrypt_with_surfaced_skid_passes()
    {
        AddressingConsistency.CheckAuthcryptSkidSurfaced("did:example:alice#key-x25519-1");
    }

    [Fact]
    public void Recipient_kid_membership_succeeds_when_subject_matches_any_to_entry()
    {
        var to = new[] { "did:example:alice", "did:example:bob?service=agent" };
        AddressingConsistency.CheckRecipientKidInTo(to, "did:example:bob#key-x25519-1");
    }

    [Fact]
    public void Recipient_kid_not_in_to_throws()
    {
        var to = new[] { "did:example:alice", "did:example:bob" };
        Action act = () => AddressingConsistency.CheckRecipientKidInTo(to, "did:example:carol#k");
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-02*");
    }

    [Fact]
    public void Recipient_kid_null_or_unparseable_throws()
    {
        var to = new[] { "did:example:alice" };
        Action act = () => AddressingConsistency.CheckRecipientKidInTo(to, "not-a-did#k");
        act.Should().Throw<ConsistencyException>();
    }

    [Fact]
    public void Signed_from_matches_signer_kid()
    {
        AddressingConsistency.CheckSignedFromMatchesSignerKid(
            "did:example:alice", "did:example:alice#key-2");
    }

    [Fact]
    public void Signed_from_mismatched_signer_kid_throws()
    {
        Action act = () => AddressingConsistency.CheckSignedFromMatchesSignerKid(
            "did:example:alice", "did:example:mallory#key-2");
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-03*");
    }

    private static IReadOnlySet<string> Own(params string[] didSubjects)
        => new HashSet<string>(didSubjects, StringComparer.Ordinal);

    [Fact]
    public void Recipient_addressing_matches_on_decrypting_kid_or_declared_identifier()
    {
        AddressingConsistency.CheckRecipientAddressing(
                new[] { "did:example:alice", "did:example:bob" }, "did:example:bob#x", ownDidSubjects: null)
            .Should().Be(RecipientAddressing.Addressed);

        AddressingConsistency.CheckRecipientAddressing(
                new[] { "did:example:alice" }, recipientKid: null, Own("did:example:bob"))
            .Should().Be(RecipientAddressing.NotAddressed);
    }

    [Fact]
    public void Recipient_addressing_is_not_evaluated_without_a_to_header_or_an_own_identity()
    {
        AddressingConsistency.CheckRecipientAddressing(to: null, "did:example:bob#x", Own("did:example:bob"))
            .Should().Be(RecipientAddressing.NotEvaluated);

        AddressingConsistency.CheckRecipientAddressing(
                new[] { "did:example:alice" }, recipientKid: null, ownDidSubjects: null)
            .Should().Be(RecipientAddressing.NotEvaluated);

        // An empty declared set is the same "nothing to check against" state as a null one.
        AddressingConsistency.CheckRecipientAddressing(
                new[] { "did:example:alice" }, recipientKid: null, Own())
            .Should().Be(RecipientAddressing.NotEvaluated);
    }

    [Fact]
    public void Recipient_addressing_treats_unparseable_to_entries_as_non_matches()
    {
        // Malformed addressing on the wire must not decide the outcome; the parseable entries do.
        AddressingConsistency.CheckRecipientAddressing(
                new[] { "not-a-did", "did:example:bob" }, recipientKid: null, Own("did:example:bob"))
            .Should().Be(RecipientAddressing.Addressed);

        AddressingConsistency.CheckRecipientAddressing(
                new[] { "not-a-did" }, recipientKid: null, Own("did:example:bob"))
            .Should().Be(RecipientAddressing.NotAddressed);
    }

    [Fact]
    public void Recipient_addressing_consumes_the_whole_to_sequence_even_when_the_first_entry_matches()
    {
        // The no-early-exit property, asserted on behavior rather than inferred from the return
        // value: a short-circuiting implementation yields the same enum but stops enumerating, and
        // the number of entries it got through is what a remote sender could time. Counting the
        // enumeration is what actually pins the property.
        var to = new CountingSequence(new[] { "did:example:bob", "did:example:carol", "did:example:dave" });

        AddressingConsistency.CheckRecipientAddressing(to, recipientKid: null, Own("did:example:bob"))
            .Should().Be(RecipientAddressing.Addressed);

        to.Yielded.Should().Be(3, "every 'to' entry must be examined regardless of where the match is");
        to.EnumerationCount.Should().Be(1, "the sequence is walked exactly once, not once per identity");
    }

    [Theory]
    [InlineData("did:example:bob", "did:example:carol", "did:example:dave", RecipientAddressing.Addressed)]
    [InlineData("did:example:carol", "did:example:bob", "did:example:dave", RecipientAddressing.Addressed)]
    [InlineData("did:example:carol", "did:example:dave", "did:example:bob", RecipientAddressing.Addressed)]
    [InlineData("did:example:carol", "did:example:dave", "did:example:erin", RecipientAddressing.NotAddressed)]
    public void Recipient_addressing_probes_every_to_entry_without_enumerating_the_declared_identity_set(
        string first,
        string second,
        string third,
        RecipientAddressing expected)
    {
        // Per-message work must scale with the bounded wire input, not with the host's roster: an
        // agent declaring 100k identities must not pay 100k DID parses for a one-entry 'to'. That is
        // structural, not a timing measurement. The check may only probe the prebuilt set, so
        // enumerating it fails here. The exact Contains count also pins the non-short-circuiting
        // lookup shape: first/middle/last/miss must all perform one lookup per parseable wire entry.
        var own = new ProbeOnlySet("did:example:bob");
        var to = new[] { first, second, third };

        AddressingConsistency.CheckRecipientAddressing(to, "did:example:zoe#x", own)
            .Should().Be(expected);

        own.EnumerationCount.Should().Be(0, "the declared roster must never be walked per message");
        own.ContainsCalls.Should().Be(to.Length,
            "every parseable wire entry must be probed even after an earlier match");
    }

    /// <summary>Counts how many entries were pulled, and how many times the sequence was walked.</summary>
    private sealed class CountingSequence(IReadOnlyList<string> items) : IEnumerable<string>
    {
        public int Yielded { get; private set; }
        public int EnumerationCount { get; private set; }

        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            return Iterate();
        }

        private IEnumerator<string> Iterate()
        {
            foreach (var item in items)
            {
                Yielded++;
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// A declared-identity set that permits only <c>Count</c> and <c>Contains</c>. Enumeration is
    /// counted (and the returned enumerator is empty) so a per-message walk of the roster shows up as
    /// a failed assertion rather than as a silent performance regression.
    /// </summary>
    private sealed class ProbeOnlySet(params string[] subjects) : IReadOnlySet<string>
    {
        private readonly HashSet<string> _inner = new(subjects, StringComparer.Ordinal);

        public int ContainsCalls { get; private set; }
        public int EnumerationCount { get; private set; }

        public int Count => _inner.Count;

        public bool Contains(string item)
        {
            ContainsCalls++;
            return _inner.Contains(item);
        }

        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            return Enumerable.Empty<string>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<string> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<string> other) => throw new NotSupportedException();
    }

    [Fact]
    public void Authcrypt_inner_signer_mismatch_throws()
    {
        Action act = () => AddressingConsistency.CheckAuthcryptInnerSignerMatchesSkid(
            "did:example:bob#sig", "did:example:alice#enc");
        act.Should().Throw<ConsistencyException>().WithMessage("*FR-CONSIST-05*");
    }

    [Fact]
    public void Authcrypt_inner_signer_match_passes()
    {
        AddressingConsistency.CheckAuthcryptInnerSignerMatchesSkid(
            "did:example:alice#sig", "did:example:alice#enc");
    }

    [Fact]
    public async Task Resolver_authorization_with_null_resolver_short_circuits()
    {
        await AddressingConsistency.CheckResolverAuthorizationAsync(
            "did:example:alice", "did:example:alice#k", "keyAgreement", resolverCheck: null);
    }

    [Fact]
    public async Task Resolver_authorization_throws_when_resolver_says_no()
    {
        Func<Task> act = () => AddressingConsistency.CheckResolverAuthorizationAsync(
            "did:example:alice", "did:example:alice#k", "keyAgreement",
            resolverCheck: (_, _, _, _) => Task.FromResult(false));
        (await act.Should().ThrowAsync<ConsistencyException>()).WithMessage("*FR-CONSIST-06*");
    }
}
