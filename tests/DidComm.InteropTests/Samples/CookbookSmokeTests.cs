using System.IO;
using DidComm.Samples.Cookbook;
using FluentAssertions;
using Xunit;

namespace DidComm.InteropTests.Samples;

/// <summary>
/// FR-DX-02 build+run gate for the <c>02-Cookbook</c> sample. Invokes
/// <see cref="Program.RunAsync"/> directly (no process spawn), capturing console output, and
/// asserts every Phase 3 section produced its banner.
/// </summary>
public sealed class CookbookSmokeTests
{
    [Fact]
    public async Task RunAsync_PrintsEveryPhase3SectionBanner()
    {
        var writer = new StringWriter();

        await Program.RunAsync(writer);

        var transcript = writer.ToString();
        transcript.Should().Contain("Section K — Unpack and inspect metadata");
        transcript.Should().Contain("Section N — DID rotation via from_prior");
        transcript.Should().Contain("Section AA — net-did integration & the did:web refusal");
        transcript.Should().Contain("FromPrior.Sub");
        transcript.Should().Contain("refused (web)");
    }
}
