namespace DidComm.Samples.Cookbook.Sections;

/// <summary>Cookbook section DD: Pick up queued messages. (Scaffold; filled in by #97.)</summary>
public static class Section_DD_PickUpQueuedMessages
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("DD", "Pick up queued messages");
        return Task.CompletedTask;
    }
}
