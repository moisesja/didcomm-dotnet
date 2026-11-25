using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using DidComm.Mediator.Client.Internal;
using Xunit;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// <see cref="IMediatorExchange.ExchangeAllowingNoAnswerAsync"/> has a default implementation, kept
/// for test exchanges, which tells a cancellation from its own window's end by the exception's type
/// (#111). This pins who can run it: the interface is internal, the channel is the only type of a
/// production assembly that implements it, and the channel implements that member itself. A client
/// built by a public constructor or by <c>AddMediatorClient()</c> is over the channel
/// (<see cref="MediatorClientDiTests"/>: "The non-DI clients each own a channel" and "One channel
/// ... with both clients over it"), and the member is called through the client's exchange only
/// (<c>PickupClient</c>, for a <c>messages-received</c> under Message Pickup 3.0).
/// </summary>
public sealed class MediatorClientChannelDefaultMemberTests
{
    [Fact]
    public void The_channel_is_the_only_production_type_behind_the_exchange_seam_and_implements_the_member_that_has_a_default_itself()
    {
        var seam = typeof(IMediatorExchange);
        var member = seam.GetMethod(nameof(IMediatorExchange.ExchangeAllowingNoAnswerAsync))!;
        seam.IsNotPublic.Should().BeTrue("the set-up: only the client's assembly and the assemblies it names can implement the seam");
        member.IsAbstract.Should().BeFalse("the set-up: the member has a default implementation");

        // Who can see the seam: one production assembly besides its own, and the two test assemblies.
        var named = seam.Assembly.GetCustomAttributes<InternalsVisibleToAttribute>().Select(a => a.AssemblyName).ToArray();
        named.Should().BeEquivalentTo("DidComm.Mediator", "DidComm.Mediator.Tests", "DidComm.InteropTests");
        var production = new[] { seam.Assembly, typeof(MediatorOptions).Assembly };
        production.Select(a => a.GetName().Name).Should().Equal("DidComm.Mediator.Client", "DidComm.Mediator");

        var implementers = production.SelectMany(a => a.GetTypes()).Where(t => !t.IsInterface && seam.IsAssignableFrom(t)).ToArray();
        implementers.Should().Equal(new[] { typeof(MediatorClientChannel) }, "no other production type is an exchange");

        foreach (var type in implementers)
        {
            var map = type.GetInterfaceMap(seam);
            var target = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, member)];
            target.DeclaringType.Should().Be(type, "{0} implements the member itself, so the interface's default never runs for it", type.Name);
        }
    }
}
