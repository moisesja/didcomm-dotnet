using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.Rotation;

namespace DidComm.Samples.Cookbook.Sections;

/// <summary>
/// Shows how Alice rotates from her original DID to a new one without breaking Bob's trust.
/// </summary>
/// <remarks>
/// <para>
/// "DID rotation" is the situation where a party changes the DID they identify as — for
/// example because they lost a device, generated stronger keys, or migrated DID methods.
/// Bob already trusts <c>alice</c>; he has never heard of <c>alice2</c>. So when Alice sends
/// her first message under the new DID, she needs to prove to Bob that the new identity is
/// really her.
/// </para>
/// <para>
/// The mechanism is a small JWT called <c>from_prior</c>: Alice signs it with a key her old
/// DID had advertised under <c>authentication</c>, and attaches it to the rotation message.
/// Bob's unpack resolves the old DID, verifies the JWT, and surfaces the claims on
/// <c>UnpackResult.FromPrior</c>. Bob can then bind the new DID to the conversation he
/// already had with the old one.
/// </para>
/// <para>
/// The section also demonstrates the safety rule that protects this flow: rotation messages
/// MUST travel inside an encrypted envelope. If the application tries to pack a plaintext
/// message that carries a <c>from_prior</c> token, the facade throws before anything is
/// written to the wire.
/// </para>
/// <para>
/// Maps to PRD §14.2 task <strong>N</strong> (FR-ROT-01..04 — JWT issuance and validation;
/// FR-ROT-03 — encrypted-only emission).
/// </para>
/// </remarks>
public static class Section_N_FromPriorRotation
{
    /// <summary>Run this section against the shared <see cref="CookbookContext"/>.</summary>
    /// <param name="ctx">The shared cookbook context.</param>
    public static async Task RunAsync(CookbookContext ctx)
    {
        ctx.Narrator.Section("N", "DID rotation via from_prior");

        // The from_prior JWT must be signed by a key the OLD DID listed under `authentication`.
        // Pick Alice's Ed25519 auth key (the second key PeerIdentityFactory generated).
        var alicePriorAuthKey = ctx.Alice.Privates.First(k => string.Equals(k.Crv, "Ed25519", StringComparison.Ordinal));

        var claims = new FromPriorClaims(
            Sub: ctx.Alice2.Did,                           // sub = the new DID Alice is rotating to
            Iss: ctx.Alice.Did,                            // iss = the old DID that's vouching for it
            Iat: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        ctx.Narrator.Step("Build the from_prior JWT — Alice signs it with her OLD authentication key.");
        var jwt = FromPriorBuilder.Build(claims, alicePriorAuthKey);
        ctx.Narrator.Value("jwt.length", jwt.Length);
        ctx.Narrator.Value("jwt.head", jwt[..Math.Min(60, jwt.Length)] + "…");

        var rotationMessage = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(ctx.Alice2.Did)                      // message is under the NEW DID
            .WithTo(ctx.Bob.Did)
            .WithFromPrior(jwt)                            // proof of continuity from the OLD DID
            .Build();

        // Rotation messages must travel encrypted — the facade rejects plaintext/signed envelopes
        // that carry from_prior. This first pack uses authcrypt from the new identity to Bob.
        ctx.Narrator.Step("Pack the rotation message as an encrypted envelope from alice2 to bob.");
        var packed = (await ctx.Client.PackEncryptedAsync(rotationMessage, new PackEncryptedOptions(
            Recipients: new[] { ctx.Bob.Did },
            From: ctx.Alice2.Did))).Message;

        ctx.Narrator.Step("Bob unpacks. The library verifies the JWT against alice's old DID Document.");
        var result = await ctx.Client.UnpackAsync(packed);

        ctx.Narrator.Value("FromPrior.Sub", result.FromPrior?.Sub);                  // = alice2
        ctx.Narrator.Value("FromPrior.Iss", result.FromPrior?.Iss);                  // = alice
        ctx.Narrator.Value("FromPrior.Iat", result.FromPrior?.Iat);                  // when alice issued the token
        ctx.Narrator.Value("Sub == message.From", result.FromPrior?.Sub == result.Message.From);

        ctx.Narrator.Step("Safety demo: a plaintext envelope cannot carry from_prior — the pack call must throw.");
        try
        {
            var unwrappedAttempt = new MessageBuilder()
                .WithType("https://didcomm.org/basicmessage/2.0/message")
                .WithFrom(ctx.Alice2.Did)
                .WithFromPrior(jwt)
                .Build();
            _ = await ctx.Client.PackPlaintextAsync(unwrappedAttempt);
            ctx.Narrator.Note("UNEXPECTED — PackPlaintextAsync did not throw.");
        }
        catch (InvalidOperationException ex)
        {
            ctx.Narrator.Note(ex.Message);
        }
    }
}
