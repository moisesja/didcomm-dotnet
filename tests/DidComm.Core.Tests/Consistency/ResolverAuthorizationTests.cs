using DidComm.Consistency;
using DidComm.Exceptions;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Consistency;

/// <summary>
/// Direct unit coverage for <see cref="AddressingConsistency.CheckResolverAuthorizationAsync"/>
/// (FR-CONSIST-06). The full pipeline integration through <c>EnvelopeReader.UnpackAsync</c> is
/// covered by the interop round-trip tests, which exercise every legal envelope shape with a
/// real resolver-backed predicate.
/// </summary>
public sealed class ResolverAuthorizationTests
{
    [Fact]
    public async Task Predicate_Authorized_DoesNotThrow()
    {
        Func<Task> act = () => AddressingConsistency.CheckResolverAuthorizationAsync(
            "did:example:alice",
            "did:example:alice#key-1",
            "authentication",
            (_, _, _, _) => Task.FromResult(true));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Predicate_Unauthorized_ThrowsConsistency()
    {
        Func<Task> act = () => AddressingConsistency.CheckResolverAuthorizationAsync(
            "did:example:alice",
            "did:example:alice#stolen",
            "authentication",
            (_, _, _, _) => Task.FromResult(false));

        (await act.Should().ThrowAsync<ConsistencyException>())
            .Where(e => e.Message.Contains("FR-CONSIST-06"));
    }

    [Fact]
    public async Task Predicate_Null_ShortCircuitsToAuthorized()
    {
        Func<Task> act = () => AddressingConsistency.CheckResolverAuthorizationAsync(
            "did:example:alice",
            "did:example:alice#any",
            "authentication",
            null);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Predicate_PassesAssertedTriple_Exactly()
    {
        (string did, string kid, string rel)? observed = null;

        await AddressingConsistency.CheckResolverAuthorizationAsync(
            "did:example:alice",
            "did:example:alice#key-2",
            "keyAgreement",
            (d, k, r, _) =>
            {
                observed = (d, k, r);
                return Task.FromResult(true);
            });

        observed.Should().Be(("did:example:alice", "did:example:alice#key-2", "keyAgreement"));
    }
}
