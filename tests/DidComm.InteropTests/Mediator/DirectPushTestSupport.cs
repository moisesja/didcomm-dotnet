using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Transports;
using DidComm.InteropTests.Transports.Proxy;
using DidComm.Mediator;
using DidComm.Mediator.Pipeline;
using DidComm.TestSupport;
using DidComm.Transports.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DidComm.InteropTests.Mediator;

/// <summary>
/// A mediator host's container for the direct-push egress tests (#110): <c>AddDidComm</c> with
/// <c>UseHttpTransport</c>, every log line captured, and the mediator's own sender built from it
/// the way the mediator builds it (<see cref="DirectPushSender.Create"/>).
/// </summary>
internal sealed class DirectPushHost : IAsyncDisposable
{
    /// <summary>Bounds every wait; with a correct product each awaited event is certain.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>A stored message's bytes (a JSON object, as every DIDComm message is).</summary>
    public static readonly byte[] Payload = "{\"protected\":\"direct-push\"}"u8.ToArray();

    private DirectPushHost(ServiceProvider services, CapturingLoggerProvider logs)
    {
        Services = services;
        Logs = logs;
        Sender = DirectPushSender.Create(services);
    }

    /// <summary>The container.</summary>
    public ServiceProvider Services { get; }

    /// <summary>Every log line the container wrote, at every level.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>The mediator's sender, built from the container.</summary>
    public DirectPushSender Sender { get; }

    /// <summary>
    /// Build the host. Transport defaults: http and https allowed, a 1 ms retry back-off (so a retry
    /// the product must not make costs no time when a mutation makes it); <paramref name="http"/> runs after them.
    /// </summary>
    /// <param name="http">The host's HTTP transport settings.</param>
    /// <param name="core">The host's core settings (the outbound endpoint policy).</param>
    /// <param name="primary">When set, the named client's primary handler (bypasses the connect-time guard).</param>
    /// <param name="mediator">The mediator's settings the sender reads (the push deadline, <see cref="MediatorOptions.PushTimeout"/>).</param>
    /// <param name="more">Further registrations, last (the host's own client configuration).</param>
    public static DirectPushHost Build(
        Action<HttpTransportOptions>? http = null,
        Action<DidCommOptions>? core = null,
        HttpMessageHandler? primary = null,
        Action<MediatorOptions>? mediator = null,
        Action<IServiceCollection>? more = null)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(logs);
        });
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(new InMemorySecretsResolver());
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http", "https" };
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                http?.Invoke(o);
            });
            if (core is not null)
                b.Configure(core);
        });
        if (primary is not null)
            services.AddHttpClient(HttpDidCommTransport.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => primary);
        if (mediator is not null)
            services.Configure(mediator);
        more?.Invoke(services);
        return new DirectPushHost(services.BuildServiceProvider(), logs);
    }

    /// <summary>One push of <see cref="Payload"/>, bounded by the hang guard.</summary>
    /// <param name="endpoint">The endpoint.</param>
    public Task<bool> PushAsync(string endpoint) => PushAsync(new Uri(endpoint));

    /// <summary>One push of <see cref="Payload"/>, bounded by the hang guard: <c>true</c> when delivered.</summary>
    /// <param name="endpoint">The endpoint.</param>
    public async Task<bool> PushAsync(Uri endpoint) => await PushResultAsync(endpoint) == DirectSendResult.Delivered;

    /// <summary>One push of <see cref="Payload"/>, bounded by the hang guard: what the endpoint answered.</summary>
    /// <param name="endpoint">The endpoint.</param>
    public Task<DirectSendResult> PushResultAsync(Uri endpoint) => Sender.SendAsync(endpoint, Payload, CancellationToken.None).WaitAsync(HangGuard);

    /// <summary>
    /// No log line the library or the mediator wrote carries any of <paramref name="secrets"/> (an
    /// endpoint, a <c>Location</c>, an exception's message). The framework's own client logging
    /// (categories <c>System.Net.Http.HttpClient.*</c>) writes the full URI for every send of the host
    /// and is the host's to filter (documented on <see cref="HttpDidCommTransport"/>); it is left out.
    /// </summary>
    /// <param name="secrets">The values that must not appear.</param>
    public void AssertNoWireValuesLogged(params string[] secrets)
    {
        var ours = Logs.CategorizedRecords
            .Where(r => !r.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal))
            .ToArray();
        foreach (var secret in secrets.Where(s => s.Length > 0))
        {
            ours.Should().NotContain(r => r.Text.Contains(secret, StringComparison.OrdinalIgnoreCase),
                "no log line may carry '{0}'", secret);
        }
    }

    /// <summary>The Debug lines the sender itself wrote.</summary>
    public IReadOnlyList<string> SenderLines =>
        Logs.CategorizedRecords.Where(r => r.Category == typeof(DirectPushSender).FullName).Select(r => r.Text).ToArray();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Services.DisposeAsync();

    /// <summary>The exception and every inner exception, outermost first.</summary>
    /// <param name="ex">The outermost exception.</param>
    public static IEnumerable<Exception> Chain(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            yield return e;
    }

    /// <summary>Every message in the chain, for "never logged" assertions.</summary>
    /// <param name="ex">The outermost exception.</param>
    public static string[] Messages(Exception ex) => Chain(ex).Select(e => e.Message).ToArray();
}

/// <summary>
/// A primary handler for the named client that answers by script and records every request that
/// reaches it (each Polly attempt and each followed 307 is one). No network: it bypasses the
/// connect-time guard, so it serves the rows about retries, the breaker and redirects.
/// </summary>
internal sealed class ScriptedEndpoints : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly ConcurrentQueue<(Uri Uri, byte[] Body)> _posts = new();

    /// <summary>Answer each request with <paramref name="respond"/>.</summary>
    /// <param name="respond">The answer to each request.</param>
    public ScriptedEndpoints(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    /// <summary>Answer each request with <paramref name="respond"/>, which gets the attempt's cancellation token.</summary>
    /// <param name="respond">The answer to each request.</param>
    public ScriptedEndpoints(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    /// <summary>Every request that reached the handler, in order, with its body.</summary>
    public IReadOnlyList<(Uri Uri, byte[] Body)> Posts => _posts.ToArray();

    /// <summary>How many requests named <paramref name="host"/>.</summary>
    /// <param name="host">The host (an IP literal).</param>
    public int PostsTo(string host) => _posts.Count(p => string.Equals(p.Uri.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>A bodiless answer.</summary>
    /// <param name="status">The status.</param>
    /// <param name="location">The <c>Location</c> header, if any (absolute or relative).</param>
    public static HttpResponseMessage Answer(int status, string? location = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (location is not null)
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        _posts.Enqueue((request.RequestUri!, body));
        return await _respond(request, cancellationToken);
    }
}

/// <summary>Answers for a <c>StandInProxy</c> (or a stand-in endpoint) that a push talks to.</summary>
internal static class StandInAnswers
{
    /// <summary>
    /// Answer a request and close. A POST's body (<see cref="DirectPushHost.Payload"/>) is read
    /// first: closing with unread data resets the connection, and a reset can reach the client
    /// before it has read the answer.
    /// </summary>
    /// <param name="c">The connection.</param>
    /// <param name="status">For example <c>"202 Accepted"</c>.</param>
    /// <param name="headers">Extra header lines.</param>
    public static async Task AnswerAsync(StandInConnection c, string status, params string[] headers)
    {
        if (c.RequestLine.StartsWith("POST ", StringComparison.Ordinal))
            await c.Stream.ReadExactlyAsync(new byte[DirectPushHost.Payload.Length], c.Stopping);
        await c.StatusAsync(status, [.. headers, "Connection: close"]);
    }
}

/// <summary>
/// Kestrel on the IPv4 loopback with a scripted answer, for the rows that need the host's real
/// named client (the connect-time guard): the push really connects.
/// </summary>
internal sealed class LoopbackEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _posts;

    private LoopbackEndpoint(WebApplication app) => _app = app;

    /// <summary>POSTs that reached the endpoint.</summary>
    public int Posts => Volatile.Read(ref _posts);

    /// <summary>The endpoint's URI at <paramref name="path"/>.</summary>
    /// <param name="path">The path.</param>
    public Uri At(string path) => new(new Uri(_app.Urls.Single()), path);

    /// <summary>Start an endpoint that answers every POST with <paramref name="status"/> and, if given, <paramref name="location"/>.</summary>
    /// <param name="status">The status.</param>
    /// <param name="location">The <c>Location</c> header.</param>
    public static async Task<LoopbackEndpoint> StartAsync(int status, string? location = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        LoopbackEndpoint? self = null;
        app.MapPost("/{**path}", (HttpContext context) =>
        {
            Interlocked.Increment(ref self!._posts);
            if (location is not null)
                context.Response.Headers.Location = location;
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        self = new LoopbackEndpoint(app);
        await app.StartAsync();
        return self;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
