using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// PRD §14.2 — <strong>AA. net-did integration + the deliberate <c>did:web</c> rejection
/// (FR-DID-01 / FR-DID-06)</strong>. Every other section is already using net-did under the
/// hood (resolution + key extraction via <c>NetDidKeyService</c>). This section exercises the
/// rejection paths explicitly: per design decision <strong>DD-08</strong> the library
/// intentionally refuses <c>did:web</c> at every entry point with
/// <see cref="UnsupportedDidMethodException"/>.
/// </summary>
public static class Section_AA_NetDidAndDidWebRejection
{
    private const string DidWeb = "did:web:example.com";

    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("AA", "net-did integration + did:web rejection (FR-DID-01 / FR-DID-06)");

        ctx.Narrator.Step("Implicit integration: every prior section resolved did:peer DIDs via NetDidKeyService.");
        ctx.Narrator.Value("Resolver", "NetDidKeyService over CompositeDidResolver (did:key + did:peer)");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .Build();

        await ExpectDidWebRefusal(ctx, "PackEncryptedAsync (recipient)", () =>
            ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { DidWeb })));

        await ExpectDidWebRefusal(ctx, "PackEncryptedAsync (From)", () =>
            ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ctx.Bob.Did },
                From: DidWeb)));

        await ExpectDidWebRefusal(ctx, "PackEncryptedAsync (SignFrom)", () =>
            ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ctx.Bob.Did },
                SignFrom: DidWeb)));

        await ExpectDidWebRefusal(ctx, "PackSignedAsync (signFrom)", () =>
            ctx.Client.PackSignedAsync(message, DidWeb));
    }

    private static async Task ExpectDidWebRefusal(CookbookContext ctx, string label, Func<Task> action)
    {
        try
        {
            await action();
            ctx.Narrator.Value(label, "UNEXPECTED — no exception thrown");
        }
        catch (UnsupportedDidMethodException ex) when (string.Equals(ex.Method, "web", StringComparison.Ordinal))
        {
            ctx.Narrator.Value(label, $"refused ({ex.Method}) → {ex.Did}");
        }
    }
}
