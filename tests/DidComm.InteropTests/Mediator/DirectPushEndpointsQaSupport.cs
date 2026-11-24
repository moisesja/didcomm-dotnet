using System.Collections.Concurrent;
using DidComm.Facade;
using DidComm.Messages;
using DidComm.Transports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// A recipient's server on the test network (QA of #110), at any host name: a TestServer whose every
/// request is recorded (method and path, before it is answered) and answered by <see cref="Respond"/>.
/// The mediator's named client reaches it through <see cref="MediatorNetwork"/>.
/// </summary>
internal sealed class ScriptedSite : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _requests = new();
    private int _ended;
    private long _bodyBytesWritten;

    private ScriptedSite(WebApplication app, string host)
    {
        App = app;
        Host = host;
    }

    public WebApplication App { get; }

    public string Host { get; }

    /// <summary>The answer to each request (202 by default).</summary>
    public Func<HttpContext, Task> Respond { get; set; } = context =>
    {
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        return Task.CompletedTask;
    };

    /// <summary>"POST /path" per request, in arrival order.</summary>
    public IReadOnlyList<string> Requests => _requests.ToArray();

    public int Posts => _requests.Count(r => r.StartsWith("POST ", StringComparison.Ordinal));

    /// <summary>Requests answered or abandoned.</summary>
    public int Ended => Volatile.Read(ref _ended);

    /// <summary>Response body bytes the site managed to write (a client that never reads the body leaves this small).</summary>
    public long BodyBytesWritten => Interlocked.Read(ref _bodyBytesWritten);

    public Uri At(string path, string scheme = "http") => new($"{scheme}://{Host}{path}");

    public void CountBodyBytes(int n) => Interlocked.Add(ref _bodyBytesWritten, n);

    public static async Task<ScriptedSite> StartAsync(MediatorNetwork network, string host)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer(o => o.BaseAddress = new Uri($"http://{host}/"));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        ScriptedSite? site = null;
        app.Run(async context =>
        {
            using (var ms = new MemoryStream())
                await context.Request.Body.CopyToAsync(ms, context.RequestAborted);
            site!._requests.Enqueue($"{context.Request.Method} {context.Request.Path}");
            try
            {
                await site.Respond(context);
            }
            finally
            {
                Interlocked.Increment(ref site._ended);
            }
        });
        site = new ScriptedSite(app, host);
        await app.StartAsync();
        network.Add(app.GetTestServer());
        return site;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>The mediator host's own sends (not a push): what a direct push must never stop.</summary>
internal static class HostEgress
{
    /// <summary>One send of the host's own <see cref="DidCommClient"/> through its own HTTP transport.</summary>
    public static Task<TransportResult> SendAsync(MediatorTestServer mediator, Uri endpoint)
        => mediator.App.Services.GetRequiredService<DidCommClient>()
            .SendPackedAsync(DirectPushHost.Payload, endpoint, MediaTypes.Encrypted)
            .WaitAsync(MediatorTestServer.Timeout);

    /// <summary>The lines the library and the mediator wrote (the framework's own client logging left out: it is the host's to filter).</summary>
    public static IReadOnlyList<string> OwnLines(MediatorTestServer mediator)
        => mediator.Logs.CategorizedRecords
            .Where(r => !r.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal))
            .Select(r => r.Text)
            .ToArray();
}
