using System.Threading.Channels;
using AwesomeAssertions;
using DidComm.Facade;
using DidComm.Mediator.Client;
using DidComm.Mediator.Client.Internal;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Tests.Pipeline;
using DidComm.Messages;
using DidComm.Protocols.DiscoverFeatures;
using Xunit;
using static DidComm.Mediator.Tests.Client.ClientChannels;

namespace DidComm.Mediator.Tests.Client;

/// <summary>
/// What <see cref="MediationClient"/>'s docs say about timeouts, over the real channel and a manual
/// clock: a call has up to three steps (finding the endpoint, the Discover Features query, the
/// request), each bounded by its own <see cref="MediatorClientOptions.RequestTimeout"/>; a timeout
/// while the endpoint is being found comes before anything is sent and names no request id.
/// Rendezvous only (L-044): every step is held open by a gate the test releases.
/// </summary>
public sealed class MediationClientTimeoutTests : IAsyncLifetime
{
    private readonly ManualTimeProvider _time = new();
    private ClientWorld _w = null!;

    public async Task InitializeAsync() => _w = await ClientWorld.CreateAsync();

    public async Task DisposeAsync() => await _w.DisposeAsync();

    [Fact]
    public async Task A_timeout_while_the_endpoint_is_found_names_no_request_id_and_nothing_is_sent()
    {
        var send = new FakeSend();
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = Create(_w, send, time: _time, resolveEndpoint: (_, _) =>
        {
            resolving.TrySetResult();
            return new TaskCompletionSource<Uri>().Task; // never found
        });
        var client = new MediationClient(channel, logger: null);

        var call = client.RequestMediationAsync(_w.Mediator.Did, _w.Local.Did);
        await resolving.Task;
        _time.Advance(channel.Options.RequestTimeout);

        var thrown = (await FluentActions.Awaiting(() => call).Should().ThrowAsync<TimeoutException>()).Which;
        thrown.Message.Should().NotContain("request '", "no request exists yet").And.NotContain(_w.Mediator.Did);
        send.Count.Should().Be(0);
    }

    [Fact]
    public async Task Each_step_has_its_own_RequestTimeout_so_a_call_can_outlast_one()
    {
        var send = new FakeSend();
        var sends = Channel.CreateUnbounded<(Message Request, TaskCompletionSource<UnpackResult> Answer)>();
        send.Respond = async (message, options, _) =>
        {
            var answer = new TaskCompletionSource<UnpackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            sends.Writer.TryWrite((message, answer));
            return FakeSend.Accepted(options.ServiceEndpointOverride!, await answer.Task, status: 200);
        };
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoint = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = Create(_w, send, time: _time, resolveEndpoint: (_, _) =>
        {
            resolving.TrySetResult();
            return endpoint.Task;
        });
        var client = new MediationClient(channel, logger: null);
        var almost = channel.Options.RequestTimeout - TimeSpan.FromSeconds(1);

        var call = client.RequestMediationAsync(_w.Mediator.Did, _w.Local.Did);

        // Step 1: the endpoint, found just before its deadline.
        await resolving.Task;
        _time.Advance(almost);
        endpoint.SetResult(Https);

        // Step 2: the query, answered just before its own deadline (a shared one would have fired).
        var (query, answerQuery) = await NextSendAsync(sends, call);
        query.Type.Should().Be(DiscoverFeatures.QueriesType);
        _time.Advance(almost);
        answerQuery.SetResult(await _w.ReceiveAsync(DiscoverFeatures.CreateDisclose(_w.Mediator.Did, _w.Local.Did, query.Id, new[]
        {
            new FeatureDisclosure { FeatureType = "protocol", Id = CoordinateMediation.ProtocolUri, Roles = new[] { CoordinateMediation.MediatorRole } },
        })));

        // Step 3: the request, answered just before its own deadline.
        var (request, answerRequest) = await NextSendAsync(sends, call);
        request.Type.Should().Be(CoordinateMediation.MediateRequestType);
        _time.Advance(almost);
        answerRequest.SetResult(await _w.ReceiveAsync(_w.Grant(request.Id)));

        (await call)!.RoutingDids.Should().Equal(_w.Mediator.Did);
    }

    // The next send, or the call's own outcome if it ended first (a deadline that fired early).
    private static async Task<(Message Request, TaskCompletionSource<UnpackResult> Answer)> NextSendAsync(
        Channel<(Message Request, TaskCompletionSource<UnpackResult> Answer)> sends, Task call)
    {
        var next = sends.Reader.ReadAsync().AsTask();
        if (await Task.WhenAny(next, call) == call)
        {
            await call;
            throw new InvalidOperationException("The call ended before its next request was sent.");
        }
        return await next;
    }
}
