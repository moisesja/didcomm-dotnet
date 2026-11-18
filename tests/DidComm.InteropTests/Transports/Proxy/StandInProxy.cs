using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DidComm.InteropTests.Transports.Proxy;

/// <summary>
/// A loopback stand-in for a forward proxy. It accepts connections, reads the first request head on
/// each (an absolute-form request such as <c>POST http://203.0.113.10/x HTTP/1.1</c>, or
/// <c>CONNECT host:port HTTP/1.1</c>), records its request line, and then hands the connection to the
/// test's <see cref="Respond"/> function. A connection that opens with a SOCKS greeting is recorded as
/// <c>SOCKS4</c> / <c>SOCKS5</c> and closed. Without a responder it closes every connection at once.
/// </summary>
/// <remarks>
/// L-065: tests wait on what they assert. <see cref="WaitForRequestsAsync"/> completes when the
/// stand-in has <em>recorded</em> that many request lines; <see cref="Accepted"/> counts connections
/// before anything is read, for "no connection at all" assertions. Real time is only a hang guard.
/// </remarks>
internal sealed class StandInProxy : IAsyncDisposable
{
    /// <summary>The longest request head the stand-in reads.</summary>
    private const int MaxHead = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = new();
    private readonly object _gate = new();
    private int _accepted;

    /// <summary>Start a stand-in on <paramref name="address"/> (IPv4 loopback by default), on a free port.</summary>
    /// <param name="respond">
    /// What to do after a request head was read: write a response, tunnel, or return to close. Called
    /// once per connection, with the recorded request line. <c>null</c> closes every connection.
    /// </param>
    /// <param name="address">The address to listen on.</param>
    public StandInProxy(Func<StandInConnection, Task>? respond = null, IPAddress? address = null)
    {
        Respond = respond;
        _listener = new TcpListener(address ?? IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The port the stand-in listens on.</summary>
    public int Port { get; }

    /// <summary>The responder (see the constructor).</summary>
    public Func<StandInConnection, Task>? Respond { get; set; }

    /// <summary>Connections accepted so far (counted before anything is read).</summary>
    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>Request lines recorded so far, in arrival order.</summary>
    public IReadOnlyList<string> Requests => _requests.ToArray();

    /// <summary>The proxy address for a <see cref="WebProxy"/>, e.g. <c>http://127.0.0.1:port</c>.</summary>
    /// <param name="scheme">The proxy scheme.</param>
    /// <param name="host">The host spelling to use (defaults to <c>127.0.0.1</c>).</param>
    public Uri Address(string scheme = "http", string host = "127.0.0.1") => new($"{scheme}://{host}:{Port}");

    /// <summary>
    /// Wait until at least <paramref name="count"/> request lines were recorded. <paramref name="hangGuard"/>
    /// bounds the wait only; with a correct product the event is certain.
    /// </summary>
    /// <param name="count">How many recorded requests to wait for.</param>
    /// <param name="hangGuard">The most time to wait before failing the test.</param>
    public Task WaitForRequestsAsync(int count, TimeSpan hangGuard)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_requests.Count >= count)
                return Task.CompletedTask;
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, signal));
        }
        return signal.Task.WaitAsync(hangGuard);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // The loop ends with the listener; nothing to keep.
        }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            Interlocked.Increment(ref _accepted);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var line = await ReadRequestLineAsync(stream, _stop.Token);
                if (line is null)
                    return;
                Record(line);
                if (Respond is { } respond && !line.StartsWith("SOCKS", StringComparison.Ordinal))
                    await respond(new StandInConnection(line, stream, _stop.Token));
            }
            catch (Exception)
            {
                // A client that hangs up, or a stopping stand-in: nothing to record.
            }
        }
    }

    private void Record(string line)
    {
        List<TaskCompletionSource>? ready = null;
        lock (_gate)
        {
            _requests.Enqueue(line);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_requests.Count >= _waiters[i].Count)
                {
                    (ready ??= new()).Add(_waiters[i].Signal);
                    _waiters.RemoveAt(i);
                }
            }
        }
        if (ready is not null)
        {
            foreach (var signal in ready)
                signal.TrySetResult();
        }
    }

    /// <summary>
    /// Read one request head and return its first line; a SOCKS greeting becomes <c>SOCKS4</c> or
    /// <c>SOCKS5</c>. <c>null</c> when the client closes first.
    /// </summary>
    internal static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken ct)
    {
        var head = new MemoryStream();
        var one = new byte[1];
        while (head.Length < MaxHead)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0)
                return null;
            if (head.Length == 0 && one[0] is 0x04 or 0x05)
                return one[0] == 0x05 ? "SOCKS5" : "SOCKS4";
            head.WriteByte(one[0]);
            var length = head.Length;
            if (length >= 4)
            {
                var buffer = head.GetBuffer();
                if (buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                    break;
            }
        }
        var text = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }
}

/// <summary>One connection the stand-in accepted, after its first request head was read.</summary>
/// <param name="RequestLine">The recorded request line.</param>
/// <param name="Stream">The connection.</param>
/// <param name="Stopping">Cancelled when the stand-in is disposed.</param>
internal sealed record StandInConnection(string RequestLine, NetworkStream Stream, CancellationToken Stopping)
{
    /// <summary>Write a raw response, e.g. <c>"HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\n\r\n"</c>.</summary>
    /// <param name="response">The response text.</param>
    public async Task WriteAsync(string response)
    {
        await Stream.WriteAsync(Encoding.ASCII.GetBytes(response), Stopping);
        await Stream.FlushAsync(Stopping);
    }

    /// <summary>Answer with a bodiless status line.</summary>
    /// <param name="status">For example <c>"202 Accepted"</c>.</param>
    /// <param name="headers">Extra header lines, each without its line ending.</param>
    public Task StatusAsync(string status, params string[] headers) =>
        WriteAsync($"HTTP/1.1 {status}\r\n{string.Concat(headers.Select(h => h + "\r\n"))}Content-Length: 0\r\n\r\n");

    /// <summary>Read the next request head on this connection (after a CONNECT was answered 200, the tunnelled request).</summary>
    public Task<string?> ReadNextRequestLineAsync() => StandInProxy.ReadRequestLineAsync(Stream, Stopping);
}
