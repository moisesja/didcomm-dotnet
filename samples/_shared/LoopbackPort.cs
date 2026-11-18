using System.Net;
using System.Net.Sockets;

namespace DidComm.Samples.Shared;

/// <summary>
/// Finds a free TCP port on the loopback interface, for a sample server that must know its own
/// address before it starts listening.
/// </summary>
/// <remarks>
/// <para>
/// Most samples let the web server pick a port itself (<c>http://127.0.0.1:0</c>). A mediator
/// can't: its DID contains its own address, and <c>AddMediator()</c> checks that DID before the
/// server starts listening, so the port has to be known first.
/// </para>
/// <para>
/// The operating system hands out a free port here. Another program could take it in the short
/// gap before the server binds it, so <see cref="StartAsync{T}"/> tries again with a new port when
/// that happens. In production none of this is needed: a mediator's public address is fixed and
/// known before it starts.
/// </para>
/// </remarks>
public static class LoopbackPort
{
    /// <summary>Ask the operating system for a loopback port that is free right now.</summary>
    /// <returns>The port number.</returns>
    public static int Reserve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Start a server on a free loopback port, trying again with a new port if another program
    /// took the port first.
    /// </summary>
    /// <typeparam name="T">What <paramref name="start"/> returns (typically the running app).</typeparam>
    /// <param name="start">
    /// Builds and starts the server on the given port. When it throws, it must first dispose
    /// whatever it built.
    /// </param>
    /// <param name="attempts">How many ports to try before giving up.</param>
    /// <returns>The result of the successful <paramref name="start"/> call.</returns>
    public static async Task<T> StartAsync<T>(Func<int, Task<T>> start, int attempts = 3)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await start(Reserve()).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < attempts && IsAddressInUse(ex))
            {
                // Another program bound the port between Reserve() and the server's bind: try a new one.
            }
        }
    }

    // Kestrel reports a taken port as an IOException wrapping AddressInUseException (an ASP.NET
    // Core type this helper does not reference) or a SocketException; walk the chain for either.
    private static bool IsAddressInUse(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                || string.Equals(ex.GetType().Name, "AddressInUseException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
