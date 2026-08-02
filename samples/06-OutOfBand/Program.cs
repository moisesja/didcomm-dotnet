using System.IO;
using System.Net.Http;
using DidComm.AspNetCore;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Protocols.OutOfBand;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCrypto;
using NetDid.Core;

// Alias the static OutOfBand API class so the same-named namespace doesn't shadow it.
using OutOfBandApi = DidComm.Protocols.OutOfBand.OutOfBand;

namespace DidComm.Samples.OutOfBand;

/// <summary>
/// Out-of-Band 2.0, first contact to correlated reply (PRD §14.3 sample 06, task V,
/// FR-OOB-01..05): Alice builds an invitation and encodes it into the <c>?_oob=</c> URL that
/// sits behind a QR code; Bob — a second device with its own keys — decodes it, fetches the
/// short-URL form over HTTP (<c>MapDidCommOobEndpoint</c> + <c>IOobInvitationStore</c>), and
/// answers with an encrypted response whose <c>pthid</c> is the invitation's id, which is how
/// Alice correlates the stranger's reply back to the QR code she printed.
/// <see cref="Main"/> is the CLI; <see cref="RunAsync"/> is the testable seam invoked by the
/// InteropTests smoke test (FR-DX-02 — loopback only, dynamic port).
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
            Console.Error.WriteLine($"OutOfBand failed: {ex}");
            return 1;
        }
    }

    /// <summary>Run the whole flow, writing the narration to <paramref name="output"/>.</summary>
    /// <param name="output">Destination for narrator output. <c>null</c> uses <see cref="Console.Out"/>.</param>
    public static async Task RunAsync(TextWriter? output = null)
    {
        var narrator = output is null ? new Narrator() : new Narrator(output);

        // Two devices that have never met: each mints its own did:peer:2 and holds its own
        // keys. Nothing is shared between them except what travels in the invitation.
        var (aliceProvider, aliceClient, alice) = await BootDeviceAsync();
        var (bobProvider, bobClient, bob) = await BootDeviceAsync();
        await using var _a = aliceProvider;
        await using var _b = bobProvider;

        // ── 1. Build the invitation ──────────────────────────────────────────────────────
        narrator.Section("1", "Device 1 (Alice) builds an Out-of-Band invitation");

        // An invitation is deliberately public: it will be printed, e-mailed, or shown on a
        // screen, so it carries only what a stranger needs to start talking — Alice's DID, a
        // human-readable goal, and the DIDComm profiles she accepts. Never private data.
        var invitation = OutOfBandApi.CreateInvitation(
            from: alice.Did,
            goal: "Establish a DIDComm connection with Alice",
            goalCode: "connect",
            accept: new[] { "didcomm/v2" });

        narrator.Value("Invitation id", invitation.Id);
        narrator.Value("From", Trunc(invitation.From, 64));
        narrator.Value("Goal", invitation.Goal);
        narrator.Value("GoalCode", invitation.GoalCode);
        narrator.Value("Accept", string.Join(",", invitation.Accept));

        // Alice remembers which invitation ids she has issued. This — plain application
        // state — is the correlation surface for replies; the IOobInvitationStore further
        // down has a different job (hosting the short-URL form).
        var pendingInvitations = new Dictionary<string, OutOfBandInvitation>(StringComparer.Ordinal)
        {
            [invitation.Id] = invitation,
        };

        // ── 2. Encode to the URL behind the QR code ──────────────────────────────────────
        narrator.Section("2", "Encode to the ?_oob= URL (the QR code payload, FR-OOB-02)");

        // The whole plaintext invitation rides in the _oob query parameter, base64url-encoded
        // WITHOUT padding — that is the spec's wire form, checked here explicitly.
        var url = OutOfBandApi.ToUrl(invitation, "https://alice.example/invite");
        var oobValue = url[(url.IndexOf("_oob=", StringComparison.Ordinal) + "_oob=".Length)..];
        narrator.Value("URL (truncated)", Trunc(url, 96));
        narrator.Value("_oob payload length", oobValue.Length);
        narrator.Value("_oob is padding-free base64url", !oobValue.Contains('=') && !oobValue.Contains('+') && !oobValue.Contains('/'));

        // A real app renders this URL as a QR code (any QR library takes the string as-is).
        // The terminal stand-in below marks the spot; the URL is the payload either way.
        narrator.Step("Shown on Alice's screen:");
        RenderQrPlaceholder(narrator);

        // ── 3. Decode on the second device ───────────────────────────────────────────────
        narrator.Section("3", "Device 2 (Bob) scans and decodes the invitation");

        var scanned = OutOfBandApi.FromUrl(url);
        narrator.Value("Decoded id == original", scanned.Id == invitation.Id);
        narrator.Value("Decoded from == Alice", scanned.From == alice.Did);
        narrator.Value("Decoded goal", scanned.Goal);
        narrator.Note("FromUrl validates structure and requires 'from' (FR-OOB-01) — a malformed or fromless payload throws instead of half-parsing.");

        // ── 4. The short-URL form, served over HTTP ──────────────────────────────────────
        narrator.Section("4", "Short-URL form — ?_oobid= served by the inviter (FR-OOB-04)");

        // Long invitations make dense, hard-to-scan QR codes. The short form stores the full
        // plaintext under an opaque id and serves it on an HTTP GET from the INVITER's own
        // host — the spec forbids public URL shorteners (they would learn every connection).
        var store = new InMemoryOobInvitationStore();
        var oobId = Guid.NewGuid().ToString("D");
        store.Store(oobId, await aliceClient.PackPlaintextAsync(invitation.Message));

        var oobHost = WebApplication.CreateBuilder();
        oobHost.Logging.ClearProviders(); // keep the sample's console output to the narration
        oobHost.WebHost.UseUrls("http://127.0.0.1:0"); // dynamic port — the OS picks a free one
        var oobApp = oobHost.Build();
        oobApp.MapDidCommOobEndpoint("/oob", store);
        await oobApp.StartAsync();
        try
        {
            var shortUrl = OutOfBandApi.ToShortUrl($"{oobApp.Urls.Single()}/oob", oobId);
            narrator.Value("Short URL", shortUrl);

            // Bob's side: recognize the short form, dereference it, parse the plaintext.
            OutOfBandApi.TryGetShortFormId(shortUrl, out var parsedId);
            narrator.Value("Parsed _oobid == stored id", parsedId == oobId);

            using var http = new HttpClient();
            using var response = await http.GetAsync(shortUrl);
            narrator.Value("GET status", (int)response.StatusCode);
            narrator.Value("Content-Type", response.Content.Headers.ContentType?.MediaType);

            var fetched = OutOfBandApi.FromPlaintext(await response.Content.ReadAsStringAsync());
            narrator.Value("Fetched id == original invitation", fetched.Id == invitation.Id);
        }
        finally
        {
            await oobApp.StopAsync();
            await oobApp.DisposeAsync();
        }

        // ── 5. Respond, and correlate via pthid ──────────────────────────────────────────
        narrator.Section("5", "Bob responds; Alice correlates via pthid (FR-OOB-03/05)");

        // Bob's first real message starts a NEW thread that cites the invitation as its
        // parent: pthid = invitation.Id. That is the whole correlation contract — one QR code
        // can spawn many independent threads, each pointing back at the same invitation.
        // A concluding message may also carry a web_redirect so the inviter can send the
        // scanner somewhere friendly afterwards (FR-OOB-05).
        var responseMessage = Message.Empty()
            .WithFrom(bob.Did)
            .WithTo(scanned.From!)
            .WithPthid(scanned.Id)
            .Build();
        OutOfBandApi.AddWebRedirect(responseMessage, new WebRedirect("OK", "https://alice.example/welcome"));

        // The invitation was public plaintext; the RESPONSE is a normal protected envelope —
        // authcrypt from Bob to Alice, using the DID the invitation delivered.
        var packedResponse = (await bobClient.PackEncryptedAsync(responseMessage, new PackEncryptedOptions(
            Recipients: new[] { scanned.From! },
            From: bob.Did))).Message;
        narrator.Step("Bob packs his response as authcrypt to the invitation's from DID.");

        var received = await aliceClient.UnpackAsync(packedResponse);
        narrator.Value("Alice sees pthid", received.Message.Pthid);
        narrator.Value("pthid == invitation.id", received.Message.Pthid == invitation.Id);

        var correlated = received.Message.Pthid is { } pthid && pendingInvitations.TryGetValue(pthid, out _);
        narrator.Value("Correlated to a pending invitation", correlated);
        narrator.Value("Responder (authenticated)", Trunc(received.Message.From, 64));
        narrator.Value("web_redirect", OutOfBandApi.ReadWebRedirect(received.Message)?.RedirectUrl);
        narrator.Note("The invitation bootstrapped everything: Bob learned Alice's DID from a QR code, and Alice knows exactly which QR code this authenticated stranger scanned.");
    }

    private static async Task<(ServiceProvider Provider, DidCommClient Client, PeerIdentity Identity)> BootDeviceAsync()
    {
        var secrets = new InMemorySecretsResolver();
        var services = new ServiceCollection();
        services.AddDidComm(b => b.UseNetDidResolver().UseSecretsResolver(secrets));
        var provider = services.BuildServiceProvider();

        var identity = await PeerIdentityFactory.CreateAsync(
            provider.GetRequiredService<IDidManager>(),
            provider.GetRequiredService<IKeyGenerator>(),
            provider.GetRequiredService<ICryptoProvider>());
        foreach (var key in identity.Privates)
            secrets.Add(key);

        return (provider, provider.GetRequiredService<DidCommClient>(), identity);
    }

    /// <summary>
    /// A terminal stand-in for the QR code — real applications hand the URL to a QR library
    /// and render the result; the sample stays dependency-free on purpose.
    /// </summary>
    private static void RenderQrPlaceholder(Narrator narrator)
    {
        narrator.Note("┌───────────────────────────┐");
        narrator.Note("│ ▛▀▀▀▌▞▚▐▀▚▞▘▞▚▐▛▀▀▀▌      │");
        narrator.Note("│ ▌▓▓▓▐▚▘▘▙▞▚▐▘▚▞▌▓▓▓▐  QR  │");
        narrator.Note("│ ▌▓▓▓▐▞▚▖▘▐▙▘▚▘▖▌▓▓▓▐ code │");
        narrator.Note("│ ▙▄▄▄▌▘▞▚▐▘▚▞▖▚▘▙▄▄▄▌ here │");
        narrator.Note("│ ▘▚▞▘▚▖▞▘▚▐▘▞▚▘▞▖▚▘▞▖      │");
        narrator.Note("└───────────────────────────┘");
    }

    private static string Trunc(string? value, int max)
        => value is null ? "<null>" : value.Length <= max ? value : value[..(max - 1)] + "…";
}
