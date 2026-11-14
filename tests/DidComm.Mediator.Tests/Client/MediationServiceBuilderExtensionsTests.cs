using System.Text.Json;
using AwesomeAssertions;
using DidComm.Mediator.Client;
using DidComm.Mediator.Coordination;
using DidComm.Resolution;
using NetDid.Core.Model;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <c>DidCommServiceBuilder.ForMediation(grant)</c>, the C# 14 static extension member (#96 design
/// D-21, §6.4): the call compiles against the receiver type, builds exactly what
/// <c>ForMediator(grant.RoutingDids[0])</c> builds, and refuses a grant that names no routing DID.
/// The A0 stub commit's proof, extended by engineer B: a first routing DID that is not a bare DID
/// (a DID URL, over-long, null), only the first routing DID read, and <c>WithRoutingKeys</c>
/// counting the mediator's own layer toward the 8-layer bound.
/// </summary>
public sealed class MediationServiceBuilderExtensionsTests
{
    private const string Peer = "did:example:bob";
    private const string RoutingDid = "did:example:mediator";

    [Fact]
    public void ForMediation_builds_the_same_entry_as_ForMediator_with_the_first_routing_did()
    {
        var grant = new MediationGrant(Peer, new[] { RoutingDid, "did:example:mediator-backup" });

        var viaGrant = DidCommServiceBuilder.ForMediation(grant).Build();
        var direct = DidCommServiceBuilder.ForMediator(RoutingDid).Build();

        Describe(viaGrant).Should().Be(Describe(direct));
        Describe(viaGrant).Should().Contain("\"uri\":\"did:example:mediator\"", "the first routing DID is the entry's uri");
    }

    [Fact]
    public void ForMediation_refuses_a_null_grant()
    {
        var act = () => DidCommServiceBuilder.ForMediation(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("grant");
    }

    [Fact]
    public void ForMediation_refuses_a_grant_whose_routing_dids_are_null()
    {
        var act = () => DidCommServiceBuilder.ForMediation(new MediationGrant(Peer, null!));

        act.Should().Throw<ArgumentException>().Where(e => e.GetType() == typeof(ArgumentException)).WithParameterName("grant");
    }

    [Fact]
    public void ForMediation_refuses_a_grant_with_no_routing_did()
    {
        var act = () => DidCommServiceBuilder.ForMediation(new MediationGrant(Peer, Array.Empty<string>()));

        act.Should().Throw<ArgumentException>().Where(e => e.GetType() == typeof(ArgumentException)).WithParameterName("grant");
    }

    [Theory]
    [InlineData("did:example:mediator#key-1")]
    [InlineData("did:example:mediator?service=a")]
    [InlineData("did:example:mediator/path")]
    [InlineData("not-a-did")]
    [InlineData("")]
    public void ForMediation_refuses_a_first_routing_did_that_is_not_a_bare_did_without_quoting_it(string routingDid)
    {
        var act = () => DidCommServiceBuilder.ForMediation(new MediationGrant(Peer, new[] { routingDid }));

        var thrown = act.Should().Throw<ArgumentException>().Which;
        thrown.Should().NotBeOfType<ArgumentNullException>();
        if (routingDid.Length > 0)
            thrown.Message.Should().NotContain(routingDid);
    }

    [Fact]
    public void ForMediation_refuses_an_over_long_first_routing_did()
    {
        var act = () => DidCommServiceBuilder.ForMediation(new MediationGrant(Peer, new[] { "did:example:" + new string('m', 2100) }));

        act.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("mmmm");
    }

    [Fact]
    public void ForMediation_refuses_a_null_first_routing_did()
    {
        var act = () => DidCommServiceBuilder.ForMediation(new MediationGrant(Peer, new string[] { null! }));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ForMediation_uses_only_the_first_routing_did()
    {
        var service = DidCommServiceBuilder.ForMediation(new MediationGrant(Peer, new[] { RoutingDid, "not even a DID" })).Build();

        Describe(service).Should().Contain("\"uri\":\"did:example:mediator\"");
    }

    [Fact]
    public void ForMediation_then_WithRoutingKeys_counts_the_mediators_layer_toward_the_8_layer_bound()
    {
        MediatorEndpointExpander.MaxForwardLayers.Should().Be(8);
        var grant = new MediationGrant(Peer, new[] { RoutingDid });
        var seven = Enumerable.Range(0, 7).Select(i => $"did:example:relay{i}#key-x25519-1").ToArray();

        var service = DidCommServiceBuilder.ForMediation(grant).WithRoutingKeys(seven).Build();
        JsonSerializer.Serialize(service.ServiceEndpoint!.Map).Should().Contain("\"routingKeys\"").And.Contain("relay6");

        // Eight more keys plus the mediator's own layer is nine: refused from the count, before the
        // poisoned ninth key is looked at (L-055).
        var eight = seven.Append("not a routing key").ToArray();
        var act = () => DidCommServiceBuilder.ForMediation(grant).WithRoutingKeys(eight);
        act.Should().Throw<ArgumentException>().WithMessage("*9 forward layers*");
    }

    [Fact]
    public void ForMediation_then_WithRoutingKeys_still_checks_each_key()
    {
        var act = () => DidCommServiceBuilder.ForMediation(new MediationGrant(Peer, new[] { RoutingDid })).WithRoutingKeys("did:example:relay#");

        act.Should().Throw<ArgumentException>().WithMessage("*Routing key 0*");
    }

    [Fact]
    public void ForMediation_is_compiled_to_the_static_implementation_method()
    {
        // What the DX gate counts (design §6.4): call sites reference the implementation method on
        // the static class, not the compiler's grouping type.
        var method = typeof(MediationServiceBuilderExtensions).GetMethod(
            nameof(MediationServiceBuilderExtensions.ForMediation), new[] { typeof(MediationGrant) });

        method.Should().NotBeNull();
        method!.IsStatic.Should().BeTrue();
        method.IsPublic.Should().BeTrue();
        method.ReturnType.Should().Be(typeof(DidCommServiceBuilder));
    }

    private static string Describe(Service service)
        => $"{service.Id}|{service.Type}|{JsonSerializer.Serialize(service.ServiceEndpoint!.Map)}";
}
