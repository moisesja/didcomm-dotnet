namespace DidComm.Samples.Cookbook.Sections;

/// <summary>Cookbook section EE: Live delivery over WebSocket. (Scaffold; filled in by #97.)</summary>
public static class Section_EE_LiveDeliveryOverWebSocket
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("EE", "Live delivery over WebSocket");
        return Task.CompletedTask;
    }
}
