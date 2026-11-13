using AwesomeAssertions;
using DidComm.Mediator.Coordination;
using Xunit;

namespace DidComm.Mediator.Tests.Pipeline;

/// <summary><see cref="MediatorIdentity"/>: at least one bare DID, first-occurrence order, a frozen copy, refusals that quote nothing.</summary>
public sealed class MediatorIdentityTests
{
    [Fact]
    public void Keeps_the_dids_in_order_and_each_once_at_its_first_position()
    {
        var identity = new MediatorIdentity(new[] { "did:example:a", "did:example:b", "did:example:a", "did:example:c", "did:example:b" });

        identity.RoutingDids.Should().Equal("did:example:a", "did:example:b", "did:example:c");
    }

    [Fact]
    public void The_list_is_a_copy_the_caller_cannot_change_afterwards()
    {
        var source = new List<string> { "did:example:a" };
        var identity = new MediatorIdentity(source);

        source.Add("did:example:b");
        source[0] = "did:example:z";

        identity.RoutingDids.Should().Equal("did:example:a");
        identity.RoutingDids.Should().NotBeAssignableTo<List<string>>();
        ((ICollection<string>)identity.RoutingDids).IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void The_params_constructor_builds_the_same_identity()
        => new MediatorIdentity("did:example:a", "did:example:b").RoutingDids.Should().Equal("did:example:a", "did:example:b");

    [Fact]
    public void An_empty_list_is_refused()
    {
        new Action(() => _ = new MediatorIdentity(Array.Empty<string>())).Should().Throw<ArgumentException>();
        new Action(() => _ = new MediatorIdentity()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_null_list_is_refused()
        => new Action(() => _ = new MediatorIdentity((IEnumerable<string>)null!)).Should().Throw<ArgumentNullException>();

    [Theory]
    [InlineData("did:example:secret-mediator#key-1")]
    [InlineData("did:example:secret-mediator/path")]
    [InlineData("did:example:secret-mediator?query=1")]
    [InlineData("https://secret-mediator.example")]
    [InlineData("")]
    public void An_entry_that_is_not_a_bare_did_is_refused_by_index_without_quoting_it(string bad)
    {
        var refusal = new Action(() => _ = new MediatorIdentity("did:example:ok", bad)).Should().Throw<ArgumentException>().Which;

        refusal.Message.Should().Contain("index 1");
        if (bad.Length > 0)
            refusal.Message.Should().NotContain("secret-mediator");
    }

    [Fact]
    public void A_null_entry_is_refused_by_index()
        => new Action(() => _ = new MediatorIdentity(new[] { (string)null! })).Should().Throw<ArgumentException>().Which.Message.Should().Contain("index 0");

    [Fact]
    public void A_did_over_2048_characters_is_refused_without_quoting_it()
    {
        var longDid = "did:example:" + new string('x', 2048);

        new Action(() => _ = new MediatorIdentity(longDid)).Should().Throw<ArgumentException>().Which.Message.Should().NotContain("xxxxxxxx");
    }

    [Fact]
    public void Reading_the_list_never_throws_and_returns_the_same_instance()
    {
        var identity = new MediatorIdentity("did:example:a");

        identity.RoutingDids.Should().BeSameAs(identity.RoutingDids);
    }
}
