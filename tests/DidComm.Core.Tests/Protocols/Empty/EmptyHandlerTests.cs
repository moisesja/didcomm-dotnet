using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols;
using DidComm.Protocols.Empty;
using DidComm.Threading;
using FluentAssertions;
using Xunit;

namespace DidComm.Tests.Protocols.Empty;

public sealed class EmptyHandlerTests
{
    private static ProtocolContext Ctx(Message m)
    {
        var unpacked = new UnpackResult(
            m, Array.Empty<DidComm.Jose.EnvelopeKind>(),
            false, false, false, false, null, null, null, null, null, null,
            Array.Empty<string>(), null);
        return new ProtocolContext(unpacked, new DidComm.Threading.ThreadState(m.Thid ?? m.Id), Client: null, new DidCommOptions(), new InMemoryThreadStateStore());
    }

    [Fact]
    public async Task Returns_null_reply()
    {
        var handler = new EmptyHandler();
        var msg = Message.Empty().WithFrom("did:peer:alice").WithTo("did:peer:bob").WithAck("prev").Build();
        var reply = await handler.HandleAsync(msg, Ctx(msg), CancellationToken.None);
        reply.Should().BeNull();
    }

    [Fact]
    public void Message_Empty_builds_a_validated_empty_envelope()
    {
        var m = Message.Empty().WithFrom("did:peer:alice").WithTo("did:peer:bob").WithAck("prev").Build();
        m.Type.Should().Be(EmptyProtocol.MessageType);
        m.Body.Should().BeNull();
        m.Ack.Should().Equal("prev");
    }

    [Fact]
    public void ProtocolUri_matches_spec()
    {
        new EmptyHandler().ProtocolUri.Should().Be("https://didcomm.org/empty/1.0");
    }
}
