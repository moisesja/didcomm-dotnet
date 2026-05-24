using DidComm.Exceptions;
using DidComm.Facade;
using DidComm.Messages;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Shows the two halves of how this library handles DID resolution: which methods it
/// supports out of the box, and which one it deliberately refuses.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What you get for free.</strong> Every other cookbook section is already going
/// through the same resolver pipeline: <c>UseNetDidResolver()</c> wires up net-did's
/// composite resolver with the <c>did:key</c> and <c>did:peer</c> methods registered, and
/// the <c>NetDidKeyService</c> adapter inside <c>DidComm.Core</c> turns whatever it returns
/// into the JWKs the JOSE layer needs. Resolution "just happens" — you don't see it called
/// directly anywhere in the cookbook because the facade does it for you on every pack and
/// unpack.
/// </para>
/// <para>
/// <strong>What is intentionally blocked.</strong> The library refuses to talk to
/// <c>did:web</c>. The reason is security: <c>did:web</c>'s trust model rests on DNS plus
/// the web PKI plus continuous control of a domain name, and the DID Document on the
/// resource has no verifiable history or pre-rotation defense. A registrar takeover or DNS
/// hijack can silently substitute the keys you think you're talking to. Because that
/// failure mode is invisible to applications, the safest thing is to reject the method at
/// every entry point before any envelope work happens. This section confirms that all four
/// pack entry points — recipient list, sender DID, signer DID, signed-only DID — throw
/// <see cref="UnsupportedDidMethodException"/> when given a <c>did:web</c> value.
/// </para>
/// <para>
/// Applications that need a web-resolvable DID should use <c>did:webvh</c> (web with
/// verifiable history) instead. It addresses the same use case while adding a hash-chained
/// log that detects key substitution.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>AA</strong>. The refusal policy is design decision
/// <strong>DD-08</strong>; the requirement is <strong>FR-DID-06</strong>.
/// </para>
/// </remarks>
public static class Section_AA_NetDidAndDidWebRejection
{
    private const string DidWeb = "did:web:example.com";

    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("AA", "net-did integration & the did:web refusal");

        ctx.Narrator.Step("Implicit integration: every prior section resolved did:peer DIDs through this pipeline.");
        ctx.Narrator.Value("Resolver", "NetDidKeyService over CompositeDidResolver (did:key + did:peer)");

        var message = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice.Did)
            .WithTo(ctx.Bob.Did)
            .Build();

        // Every public pack entry point must refuse did:web before doing any envelope work.
        // Cover all four sites where a DID can sneak in: recipient list, sender, signer (encrypted),
        // and signer (signed-only).
        await ExpectDidWebRefusal(ctx, "Encrypt to a did:web recipient", () =>
            ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(Recipients: new[] { DidWeb })));

        await ExpectDidWebRefusal(ctx, "Authcrypt as a did:web sender", () =>
            ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ctx.Bob.Did },
                From: DidWeb)));

        await ExpectDidWebRefusal(ctx, "Sign-then-encrypt with a did:web signer", () =>
            ctx.Client.PackEncryptedAsync(message, new PackEncryptedOptions(
                Recipients: new[] { ctx.Bob.Did },
                SignFrom: DidWeb)));

        await ExpectDidWebRefusal(ctx, "Standalone signed envelope from did:web", () =>
            ctx.Client.PackSignedAsync(message, DidWeb));
    }

    /// <summary>
    /// Invokes <paramref name="action"/>, asserts the call throws
    /// <see cref="UnsupportedDidMethodException"/> identifying <c>did:web</c>, and narrates
    /// the outcome. Any other result is surfaced as an unexpected pass.
    /// </summary>
    /// <param name="ctx">Cookbook context (used only for the narrator).</param>
    /// <param name="label">Human-readable description of the entry point under test.</param>
    /// <param name="action">The pack call expected to throw.</param>
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
