namespace DidComm.Samples.Cookbook.Sections;

/// <summary>Cookbook section CC: Enroll with a mediator. (Scaffold; filled in by #97.)</summary>
public static class Section_CC_EnrollWithAMediator
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("CC", "Enroll with a mediator");
        return Task.CompletedTask;
    }
}
