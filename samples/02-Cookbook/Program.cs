using System.IO;
using DidComm.Samples.Cookbook.Sections;

namespace DidComm.Samples.Cookbook;

/// <summary>
/// Cookbook entry point. <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the
/// testable seam invoked by the InteropTests smoke test (no process spawn).
/// </summary>
public static class Program
{
    /// <summary>CLI entry point — writes to <see cref="Console.Out"/> and exits 0 on success.</summary>
    public static async Task<int> Main()
    {
        try
        {
            await RunAsync(Console.Out).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cookbook failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run every cookbook section in §14.2 letter order against the supplied writer.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        await using var ctx = await CookbookContext.BuildAsync(output);

        ctx.Narrator.Step($"Minted alice = {Truncate(ctx.Alice.Did)}");
        ctx.Narrator.Step($"Minted bob   = {Truncate(ctx.Bob.Did)}");
        ctx.Narrator.Step($"Minted alice2 (rotation target) = {Truncate(ctx.Alice2.Did)}");

        await Section_K_UnpackMetadata.RunAsync(ctx);
        await Section_N_FromPriorRotation.RunAsync(ctx);
        await Section_AA_NetDidAndDidWebRejection.RunAsync(ctx);
    }

    private static string Truncate(string did)
        => did.Length <= 64 ? did : did[..61] + "…";
}
