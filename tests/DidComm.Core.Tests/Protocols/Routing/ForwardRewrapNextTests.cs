using DidComm.Exceptions;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using AwesomeAssertions;
using Xunit;
using static DidComm.Tests.Protocols.Routing.ForwardTestEnvelopes;

namespace DidComm.Tests.Protocols.Routing;

/// <summary>
/// #95 Q-3: in rewrap mode (FR-ROUTE-06) the processor reduces <c>next</c> to its DID subject
/// (PRD §4.3) — the DID the handler found registered — whether the DID URL carries a fragment, a
/// path or a query. Before the fix only a fragment was stripped, so a path or a query reached the
/// keyAgreement lookup and the forward, already answered 202, was lost as a fault. The key service
/// here resolves nothing, so the lookup's <see cref="DidResolutionException.Did"/> shows which DID
/// was looked up.
/// </summary>
public sealed class ForwardRewrapNextTests
{
    private const string Mediator = "did:example:mediator";
    private const string Next = "did:example:next";
    private const string Inner = """{"protected":"abc","ciphertext":"x","iv":"","tag":"","recipients":[]}""";

    [Theory]
    [InlineData("")]
    [InlineData("#key-1")]
    [InlineData("/some/path")]
    [InlineData("?service=x")]
    [InlineData("/p?q=1#k")]
    public async Task Rewrap_looks_up_the_DID_subject_of_next(string suffix)
    {
        var processor = NewProcessor(new ForwardProcessorOptions(Mode: RewrapMode.ReanoncryptToNext));

        var act = async () => await processor.ProcessAsync(AsDecryptedByMediator(ForwardMessage.Create(Mediator, Next + suffix, new[] { Inner })));

        (await act.Should().ThrowAsync<DidResolutionException>()).Which.Did.Should().Be(Next);
    }
}
