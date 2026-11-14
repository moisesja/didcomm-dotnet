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
/// The A0 stub commit's proof; engineer B extends it (DID URL, <c>WithRoutingKeys</c> bound).
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
