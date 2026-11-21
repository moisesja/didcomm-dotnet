using System.IO;
using AwesomeAssertions;
using Xunit;

namespace DidComm.InteropTests.DxCoverage;

/// <summary>
/// Red-team reproduction against owner decision OD-6 (#100, PR #143 review round 2): the CHANGELOG
/// and the architecture map say that, because an unanswered acknowledgement can be lost when the
/// connection closes right after <c>AckAsync</c>, cookbook section EE and sample 11 now poll
/// <c>GetStatusAsync</c> until the count drops. Neither does: both call <c>AckAsync</c>, print its
/// (null, under 4.0) answer, and go on; sample 11 then disposes its container, which closes the
/// connection, the exact pattern the new <c>AckAsync</c> remarks warn can lose the acknowledgement.
/// Fails on commit 2e3b3d0. Either fix passes it: make the samples poll, or drop the claim.
/// </summary>
public sealed class Od6SampleClaimDriftAdvTests
{
    [Fact]
    public void OD6_ADV_3_the_docs_say_section_EE_and_sample_11_poll_GetStatusAsync_after_AckAsync_so_they_must()
    {
        var root = ShippedProjects.FindRepoRoot();
        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        var architecture = File.ReadAllText(Path.Combine(root, "docs", "codebase-architecture.md"));
        var claimed = changelog.Contains("the cookbook's section EE and sample 11 now do", StringComparison.Ordinal)
            | architecture.Contains("as cookbook section EE and sample 11 do", StringComparison.Ordinal);
        if (!claimed)
            return; // the claim was dropped: nothing to hold the samples to

        var sectionEe = AfterAck(Path.Combine(root, "samples", "02-Cookbook", "Sections", "Section_EE_LiveDeliveryOverWebSocket.cs"), "private static");
        var sample11 = AfterAck(Path.Combine(root, "samples", "11-MediatedAgent", "Wallet", "LiveConnection.cs"), "/// <summary>Close the connection.</summary>");

        sectionEe.Should().Contain("GetStatusAsync", "CHANGELOG.md and docs/codebase-architecture.md say cookbook section EE polls GetStatusAsync after its AckAsync");
        sample11.Should().Contain("GetStatusAsync", "CHANGELOG.md and docs/codebase-architecture.md say sample 11 polls GetStatusAsync after its AckAsync, before it disposes its container");
    }

    // The source from the sample's AckAsync call to the end of the method that makes it.
    private static string AfterAck(string path, string endMarker)
    {
        var text = File.ReadAllText(path);
        var start = text.IndexOf(".AckAsync(", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{path} calls AckAsync");
        var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }
}
