using System.Text.Json.Nodes;
using AwesomeAssertions;
using DidComm.Extensions.DependencyInjection;
using DidComm.Facade;
using DidComm.InteropTests.Mediator;
using DidComm.InteropTests.Transports;
using DidComm.InteropTests.Transports.Proxy;
using DidComm.Mediator.Client;
using DidComm.Messages;
using DidComm.Samples.Shared;
using DidComm.TestSupport;
using DidComm.Transports;
using DidComm.Transports.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace DidComm.InteropTests.Adv98.D6;

/// <summary>
/// #98 D6 hypothesis 2: <c>HttpDidCommTransport</c> puts the endpoint URI and the peer's reason
/// phrase in <c>TransportException</c> text (documented on the transport: "log such an exception by
/// its type"), while its own log lines never carry the URI. This drives failing sends through
/// <see cref="DidCommClient"/>, <see cref="MediationClient"/> and <see cref="PickupClient"/> to an
/// endpoint whose path, query and reason phrase carry a marker, captures every log line at Trace
/// (with exception text), and asserts that no LIBRARY category writes the marker. The framework's
/// <c>System.Net.Http.HttpClient.*</c> request logging is excluded: it writes the URI by design and
/// the transport remarks say to filter it. The exception texts are reported, not judged.
/// </summary>
public sealed class D6TransportExceptionEchoTests(ITestOutputHelper output)
{
    private const string Marker = "D6ECHOMARK";

    [Fact]
    public async Task Failing_sends_never_write_the_endpoint_or_the_reason_phrase_to_a_library_log_line()
    {
        await using var origin = new StandInProxy(async c =>
        {
            await c.StatusAsync($"503 {Marker}-reason", "Connection: close");
            // Drain the request body until the client hangs up, so closing never resets the answer.
            var buffer = new byte[4096];
            while (await c.Stream.ReadAsync(buffer, c.Stopping) > 0)
            {
            }
        });
        var endpoint = $"http://127.0.0.1:{origin.Port}/{Marker}-path?cap={Marker}-query";
        var mediator = await MediatorCast.MintAsync(new DidCommServiceSpec(endpoint, Accept: new[] { "didcomm/v2" }));
        var bob = await MediatorCast.MintAsync();
        var secrets = new InMemorySecretsResolver();
        foreach (var key in bob.Privates)
            secrets.Add(key);
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddDidComm(b =>
        {
            b.UseNetDidResolver();
            b.UseSecretsResolver(secrets);
            b.UseHttpTransport(o =>
            {
                o.AllowedSchemes = new[] { "http" };
                o.MaxRetryAttempts = 0;
            });
            b.AddMediatorClient();
            b.Configure(o => o.OutboundEndpointPolicy.AllowedHosts.Add("127.0.0.1"));
        });
        await using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<DidCommClient>();
        var mediation = sp.GetRequiredService<MediationClient>();
        var pickup = sp.GetRequiredService<PickupClient>();
        var note = new MessageBuilder()
            .WithType("https://didcomm.org/basicmessage/2.0/message")
            .WithFrom(bob.Did)
            .WithTo(mediator.Did)
            .WithBody(new JsonObject { ["content"] = "x" })
            .Build();

        var thrown = new (string Where, Exception? Error)[]
        {
            ("DidCommClient.SendAsync", await Record.ExceptionAsync(() => client.SendAsync(note, new SendOptions(Recipients: new[] { mediator.Did }, From: bob.Did)).WaitAsync(MediatorTestServer.Timeout))),
            ("MediationClient.RequestMediationAsync", await Record.ExceptionAsync(() => mediation.RequestMediationAsync(mediator.Did, bob.Did).WaitAsync(MediatorTestServer.Timeout))),
            ("PickupClient.GetStatusAsync", await Record.ExceptionAsync(() => pickup.GetStatusAsync(mediator.Did, bob.Did).WaitAsync(MediatorTestServer.Timeout))),
            ("PickupClient.FetchAsync", await Record.ExceptionAsync(() => pickup.FetchAsync(mediator.Did, bob.Did, limit: 5).WaitAsync(MediatorTestServer.Timeout))),
        };

        var records = logs.CategorizedRecords.ToArray();
        var library = records.Where(r => !r.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)).ToArray();
        var leaking = library.Where(r => r.Text.Contains(Marker, StringComparison.Ordinal)).ToArray();
        foreach (var (where, error) in thrown)
            output.WriteLine($"{where}: {error?.GetType().Name}; message carries the marker: {error?.ToString().Contains(Marker, StringComparison.Ordinal)}");
        output.WriteLine($"log records: {records.Length} (library {library.Length}); framework records carrying the marker: {records.Count(r => r.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal) && r.Text.Contains(Marker, StringComparison.Ordinal))}");
        foreach (var r in leaking)
            output.WriteLine($"LEAK [{r.Category}] {r.Level}: {r.Text}");
        origin.Requests.Should().HaveCount(4, "precondition: each of the four sends reached the endpoint once");
        thrown.Should().OnlyContain(t => t.Error != null, "precondition: every send failed");
        leaking.Should().BeEmpty("library log lines carry types and ids, never the endpoint or the peer's reason phrase");
    }
}
