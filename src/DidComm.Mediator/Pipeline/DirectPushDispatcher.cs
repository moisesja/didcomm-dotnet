using System.Threading.Channels;
using DidComm.Facade;
using DidComm.Mediator.Coordination;
using DidComm.Mediator.Internal;
using DidComm.Mediator.Pickup;
using DidComm.Messages;
using DidComm.Protocols.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DidComm.Mediator.Pipeline;

/// <summary>
/// A queued message to push straight to its recipient's endpoint (FR-PICK-09).
/// </summary>
/// <param name="GrantPeerDid">The grant the message is held under: the key the endpoint directory is asked with, and the queue's key for removal.</param>
/// <param name="RecipientDid">The registered recipient DID the message is for; re-checked against the grant before the push.</param>
/// <param name="MessageId">The queue's id of the stored message, for removal after a delivered push.</param>
/// <param name="Payload">The stored payload, sent as is.</param>
/// <param name="ForwardId">The id of the forward that carried it, for log lines only.</param>
internal sealed record PushJob(string GrantPeerDid, string RecipientDid, string MessageId, byte[] Payload, string ForwardId)
{
    /// <summary>Nothing identifying: no DID, no queue id, no payload.</summary>
    public override string ToString() => nameof(PushJob);
}

/// <summary>
/// Pushes queued messages straight to their recipients' own endpoints when
/// <see cref="MediatorOptions.DirectPush"/> is on (FR-PICK-09, JC-4, JC-5), on a few background
/// workers, after the message has been stored and the forward acknowledged — never on the request
/// or on the forward pump.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Bounded.</strong> At most <see cref="MediatorOptions.DirectPushConcurrency"/> pushes run
/// at once; at most <see cref="MaxBacklog"/> jobs, holding at most <see cref="ByteBudget"/> payload
/// bytes, wait or run; and one grant may hold at most <see cref="MaxPerGrant"/> of them, so one slow
/// recipient cannot take the backlog from the others. <see cref="TryEnqueue"/> never blocks: a job
/// beyond any bound is refused (<c>Push-Skipped-Saturated</c>) and its message simply stays in the
/// queue for pickup. Every job is a message already admitted under the queue's quota, so the work
/// is bounded by what the mediator stored, 1:1.
/// </para>
/// <para>
/// <strong>Per push.</strong> The endpoint comes only from the host's
/// <see cref="IRecipientEndpointDirectory"/> (absent, <c>null</c> or a throw: skipped), and only an
/// absolute <c>http</c> or <c>https</c> endpoint is pushed to: a WebSocket write gives no receipt.
/// The registration is checked again (a recipient removed or a grant revoked since the message was
/// stored is not pushed to), then the payload goes out through
/// <see cref="DidCommClient.SendPackedAsync"/>, which always applies the outbound endpoint guard,
/// under a per-push deadline of <see cref="MediatorOptions.DirectPushTimeout"/> that covers the
/// re-check, the send and the removal (the directory lookup is the host's own, synchronous code and
/// is not bounded: a slow directory delays only pushes). Only an accepted
/// answer with an HTTP 2xx status counts as delivered; the queued copy is then removed. Anything
/// else leaves the message queued: one attempt per message.
/// </para>
/// <para>
/// <strong>Dependencies (L-017).</strong> The directory and <see cref="DidCommClient"/> are resolved
/// from the container on the first push and kept, never at construction, so nothing here can close
/// a cycle through the facade, the transports or the dispatcher, and nothing is built when direct
/// push is off. The deadline is a timer whose callback cancels inside a <c>try</c>, never
/// <c>CancelAfter</c>: a callback host code registered on the token must not take the process down.
/// </para>
/// <para>
/// <strong>Logging.</strong> Lines carry the forward's id (shortened when long) and exception type
/// names, never a DID, a queue id, the endpoint or an exception message (a transport error's text
/// contains the URL). Failures are rate-limited per reason: the first and every 1000th at Warning,
/// the rest at Debug.
/// </para>
/// <para>
/// <strong>Shutdown.</strong> Disposing stops admission, cancels the running pushes and discards the
/// waiting jobs (<c>Push-Skipped-Shutdown</c>); their messages stay queued, so nothing is lost.
/// <see cref="DisposeAsync"/> also waits up to five seconds for the workers.
/// </para>
/// </remarks>
internal sealed class DirectPushDispatcher : IAsyncDisposable, IDisposable
{
    /// <summary>Jobs waiting or running, at most (the forward handler's own backlog bound).</summary>
    internal const int MaxBacklog = ForwardHandler.DefaultCapacity;

    /// <summary>Payload bytes held by waiting or running jobs, at most (the forward handler's byte budget, 64 MiB).</summary>
    internal const long ByteBudget = ForwardHandler.DefaultByteBudget;

    /// <summary>Jobs one grant may have waiting or running, at most.</summary>
    internal const int MaxPerGrant = 16;

    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly IServiceProvider _services;
    private readonly MessagePickupQueue _queue;
    private readonly IMediationStore _mediation;
    private readonly MediatorMetrics _metrics;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly bool _enabled;
    private readonly TimeSpan _timeout;
    private readonly int _capacity;
    private readonly long _byteBudget;
    private readonly int _perGrant;
    private readonly Channel<PushJob> _channel;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _grantJobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Task[] _workers;
    private TaskCompletionSource _idle = NewCompletedSignal();
    private int _outstanding;
    private long _outstandingBytes;
    private int _stopped;
    private DidCommClient? _client;
    private IRecipientEndpointDirectory? _directory;
    private bool _directoryResolved;
    private long _saturatedLines;
    private long _failedLines;
    private long _schemeLines;
    private long _directoryFaultLines;

    /// <summary>Create the dispatcher; its workers start here when direct push is on.</summary>
    /// <param name="services">The container, for the directory and the facade, resolved on the first push.</param>
    /// <param name="options">The mediator settings; copied here.</param>
    /// <param name="queue">The pickup queue, to remove a delivered message.</param>
    /// <param name="mediation">The grant store, to check the registration again before a push.</param>
    /// <param name="metrics">The direct-push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the per-push deadline.</param>
    public DirectPushDispatcher(
        IServiceProvider services,
        IOptions<MediatorOptions> options,
        MessagePickupQueue queue,
        IMediationStore mediation,
        MediatorMetrics metrics,
        ILogger<DirectPushDispatcher>? logger,
        TimeProvider timeProvider)
        : this(services, options, queue, mediation, metrics, logger, timeProvider, MaxBacklog, ByteBudget, MaxPerGrant)
    {
    }

    /// <summary>Test seam: the dispatcher with explicit bounds.</summary>
    /// <param name="services">The container.</param>
    /// <param name="options">The mediator settings.</param>
    /// <param name="queue">The pickup queue.</param>
    /// <param name="mediation">The grant store.</param>
    /// <param name="metrics">The direct-push counter.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="timeProvider">The clock behind the per-push deadline.</param>
    /// <param name="capacity">Jobs waiting or running, at most.</param>
    /// <param name="byteBudget">Payload bytes of waiting or running jobs, at most.</param>
    /// <param name="perGrant">Jobs one grant may have waiting or running, at most.</param>
    internal DirectPushDispatcher(
        IServiceProvider services,
        IOptions<MediatorOptions> options,
        MessagePickupQueue queue,
        IMediationStore mediation,
        MediatorMetrics metrics,
        ILogger<DirectPushDispatcher>? logger,
        TimeProvider timeProvider,
        int capacity,
        long byteBudget,
        int perGrant)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(mediation);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteBudget, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perGrant, 1);

        // Read once, after validation: a host that changes the options object later changes nothing here.
        var settings = options.Value;
        settings.ValidatePipelineRanges();
        _enabled = settings.DirectPush;
        _timeout = settings.DirectPushTimeout;
        var concurrency = settings.DirectPushConcurrency;

        _services = services;
        _queue = queue;
        _mediation = mediation;
        _metrics = metrics;
        _logger = logger;
        _time = timeProvider;
        _capacity = capacity;
        _byteBudget = byteBudget;
        _perGrant = perGrant;
        _shutdownToken = _shutdown.Token;
        _channel = Channel.CreateBounded<PushJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

        if (!_enabled)
        {
            _workers = Array.Empty<Task>();
            return;
        }

        // The workers must not carry the context of whoever happened to build this singleton (a
        // request's activity, its async-locals): start them with the flow suppressed.
        _workers = new Task[concurrency];
        var suppressed = ExecutionContext.IsFlowSuppressed() ? (AsyncFlowControl?)null : ExecutionContext.SuppressFlow();
        try
        {
            for (var i = 0; i < _workers.Length; i++)
                _workers[i] = Task.Run(WorkerAsync);
        }
        finally
        {
            suppressed?.Undo();
        }
    }

    /// <summary>Whether direct push is on (workers running).</summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// Hand a stored message to the push workers. Never blocks and never throws: when direct push
    /// is off it returns <c>false</c> and records nothing; when a bound is reached, or the
    /// dispatcher is shutting down, the job is refused and counted, and the message stays queued.
    /// </summary>
    /// <param name="job">The stored message.</param>
    /// <returns><c>true</c> when a worker will push it.</returns>
    public bool TryEnqueue(PushJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!_enabled)
            return false;

        string refusal;
        lock (_gate)
        {
            if (_stopped != 0)
            {
                refusal = PushOutcomes.SkippedShutdown;
            }
            else
            {
                var grantJobs = _grantJobs.GetValueOrDefault(job.GrantPeerDid);
                if (_outstanding >= _capacity || job.Payload.Length > _byteBudget - _outstandingBytes || grantJobs >= _perGrant)
                {
                    refusal = PushOutcomes.SkippedSaturated;
                }
                else if (_channel.Writer.TryWrite(job))
                {
                    if (_outstanding++ == 0)
                        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _outstandingBytes += job.Payload.Length;
                    _grantJobs[job.GrantPeerDid] = grantJobs + 1;
                    return true;
                }
                else
                {
                    refusal = PushOutcomes.SkippedShutdown; // the writer was completed by a concurrent shutdown
                }
            }
        }

        _metrics.RecordPush(refusal);
        if (refusal == PushOutcomes.SkippedSaturated)
        {
            var level = MediatorLog.RateLimited(ref _saturatedLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} skipped: the push backlog is full; the message stays queued for pickup. {Count} such skips.", MediatorLog.LogId(job.ForwardId), n);
        }
        else
        {
            MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} skipped: the mediator is shutting down; the message stays queued for pickup.", MediatorLog.LogId(job.ForwardId));
        }
        return false;
    }

    /// <summary>Test seam: complete when no job is waiting or running.</summary>
    /// <param name="timeout">How long to wait.</param>
    internal Task WaitForIdleAsync(TimeSpan timeout)
    {
        Task idle;
        lock (_gate)
            idle = _idle.Task;
        return idle.WaitAsync(timeout);
    }

    /// <summary>Jobs waiting or running.</summary>
    internal int Outstanding
    {
        get
        {
            lock (_gate)
                return _outstanding;
        }
    }

    private async Task WorkerAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_shutdownToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var job))
                {
                    try
                    {
                        if (_shutdownToken.IsCancellationRequested)
                            _metrics.RecordPush(PushOutcomes.SkippedShutdown);
                        else
                            await PushOneAsync(job).ConfigureAwait(false);
                    }
                    finally
                    {
                        Release(job);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            MediatorLog.Write(_logger, LogLevel.Error, "Direct push worker stopped with {ExceptionType}.", ex.GetType().Name);
        }
    }

    private async Task PushOneAsync(PushJob job)
    {
        var forwardId = MediatorLog.LogId(job.ForwardId);

        // Where to: only the host's directory, never anything the message says (JC-5).
        Uri? endpoint;
        try
        {
            endpoint = Directory()?.Find(job.GrantPeerDid);
        }
        catch (Exception ex)
        {
            _metrics.RecordPush(PushOutcomes.SkippedNoEndpoint);
            var level = MediatorLog.RateLimited(ref _directoryFaultLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} skipped: the endpoint directory failed with {ExceptionType}; the message stays queued for pickup. {Count} such faults.", forwardId, ex.GetType().Name, n);
            return;
        }

        if (endpoint is null)
        {
            _metrics.RecordPush(PushOutcomes.SkippedNoEndpoint);
            MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} skipped: no endpoint for the recipient; the message stays queued for pickup.", forwardId);
            return;
        }

        if (!endpoint.IsAbsoluteUri
            || !(string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            _metrics.RecordPush(PushOutcomes.SkippedScheme);
            var level = MediatorLog.RateLimited(ref _schemeLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} skipped: the directory's endpoint is not an absolute http or https URI (a WebSocket write gives no receipt); the message stays queued for pickup. {Count} such skips.", forwardId, n);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        using var timer = _time.CreateTimer(static state => CancelQuietly((CancellationTokenSource)state!), deadline, _timeout, Timeout.InfiniteTimeSpan);
        try
        {
            // The registration may have ended since the message was stored (a remove, a revoke).
            var grant = await _mediation.FindGrantByRecipientDidAsync(job.RecipientDid, deadline.Token).ConfigureAwait(false);
            if (!string.Equals(grant?.PeerDid, job.GrantPeerDid, StringComparison.Ordinal))
            {
                _metrics.RecordPush(PushOutcomes.SkippedUnregistered);
                MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} skipped: the recipient is no longer registered under the grant.", forwardId);
                return;
            }

            var result = await Client().SendPackedAsync(job.Payload, endpoint, MediaTypes.Encrypted, deadline.Token).ConfigureAwait(false);
            if (result is { Accepted: true, HttpStatusCode: >= 200 and <= 299 })
            {
                // Delivered: counted as pushed whatever the removal says (the recipient may already
                // have acknowledged the copy through pickup, or the store failed and logged it).
                _metrics.RecordPush(PushOutcomes.Pushed);
                var removed = false;
                try
                {
                    // Under the same deadline: a store that hangs must not hold a worker for good.
                    removed = await _queue.RemoveAsync(job.GrantPeerDid, job.MessageId, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The deadline or a shutdown after the delivery: the copy stays queued until the
                    // recipient acknowledges it or it expires (delivery is at-least-once).
                }
                MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId}: delivered (HTTP {StatusCode}); queued copy removed: {Removed}.", forwardId, result.HttpStatusCode, removed);
                return;
            }

            _metrics.RecordPush(PushOutcomes.Failed);
            var level = MediatorLog.RateLimited(ref _failedLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} not delivered: the endpoint did not accept it (HTTP {StatusCode}); the message stays queued for pickup. {Count} such failures.", forwardId, result.HttpStatusCode, n);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
        {
            _metrics.RecordPush(PushOutcomes.SkippedShutdown);
            MediatorLog.Write(_logger, LogLevel.Debug, "Direct push of forward {ForwardId} abandoned: the mediator is shutting down; the message stays queued for pickup.", forwardId);
        }
        catch (Exception ex)
        {
            // A transport error, a non-2xx answer the transport turned into an exception, the
            // deadline, a store or facade fault. Type name only: a transport error names the URL.
            var reason = deadline.IsCancellationRequested ? "Timeout" : ex.GetType().Name;
            _metrics.RecordPush(PushOutcomes.Failed);
            var level = MediatorLog.RateLimited(ref _failedLines, out var n);
            MediatorLog.Write(_logger, level, "Direct push of forward {ForwardId} failed ({ExceptionType}); the message stays queued for pickup. {Count} such failures.", forwardId, reason, n);
        }
    }

    // Resolved on the first push and kept (L-017): never at construction. A directory that is not
    // registered is remembered as absent.
    private IRecipientEndpointDirectory? Directory()
    {
        if (Volatile.Read(ref _directoryResolved))
            return _directory;
        var directory = _services.GetService<IRecipientEndpointDirectory>();
        _directory = directory;
        Volatile.Write(ref _directoryResolved, true);
        return directory;
    }

    private DidCommClient Client() => _client ??= _services.GetRequiredService<DidCommClient>();

    private void Release(PushJob job)
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            _outstandingBytes -= job.Payload.Length;
            var left = _grantJobs.GetValueOrDefault(job.GrantPeerDid) - 1;
            if (left <= 0)
                _grantJobs.Remove(job.GrantPeerDid);
            else
                _grantJobs[job.GrantPeerDid] = left;
            if (--_outstanding == 0)
                idle = _idle;
        }
        idle?.TrySetResult();
    }

    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch
        {
            // A callback host code registered on the token threw, or the source is already
            // disposed: neither may escape onto a timer thread.
        }
    }

    private static TaskCompletionSource NewCompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
        return signal;
    }

    // Stop admission, cancel the running pushes, discard what waits. Idempotent.
    private void Stop()
    {
        lock (_gate)
        {
            if (_stopped != 0)
                return;
            _stopped = 1;
            _channel.Writer.TryComplete();
        }

        CancelQuietly(_shutdown);

        var discarded = 0;
        while (_channel.Reader.TryRead(out var job))
        {
            _metrics.RecordPush(PushOutcomes.SkippedShutdown);
            Release(job);
            discarded++;
        }
        if (discarded > 0)
            MediatorLog.Write(_logger, LogLevel.Information, "Direct push stopped: {Discarded} waiting pushes discarded; their messages stay queued for pickup.", discarded);
    }

    /// <summary>Stop admission, cancel the running pushes and discard the waiting ones; does not wait.</summary>
    public void Dispose() => Stop();

    /// <summary>Stop as <see cref="Dispose"/> does, then wait up to five seconds for the workers to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_workers.Length == 0)
            return;
        try
        {
            await Task.WhenAll(_workers).WaitAsync(ShutdownGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            MediatorLog.Write(_logger, LogLevel.Warning, "Direct push workers did not stop within {Grace}; their messages stay queued for pickup.", ShutdownGrace);
        }
        catch
        {
            // The workers never fault (they log and stop); nothing to add here.
        }
    }
}
