namespace DidComm.Samples.Cookbook.Sections;

/// <summary>Cookbook section FF: Run a minimal mediator in 20 lines. (Scaffold; filled in by #97.)</summary>
public static class Section_FF_MinimalMediator
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("FF", "Run a minimal mediator in 20 lines");
        return Task.CompletedTask;
    }
}
