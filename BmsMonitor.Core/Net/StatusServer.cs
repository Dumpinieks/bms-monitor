using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BmsMonitor.Net;

/// <summary>
/// Shares this instance's readings with others on the LAN: answers UDP discovery probes and
/// serves the latest snapshot over TCP as <c>GET /status</c>.
///
/// Deliberately a hand-rolled HTTP responder rather than <see cref="HttpListener"/>, which needs
/// an elevated URL ACL on Windows to bind anything but localhost.
/// </summary>
public sealed class StatusServer : IAsyncDisposable
{
    readonly Func<BmsStatusSnapshot?> _snapshot;
    readonly Action<string> _log;
    readonly int _statusPort;
    readonly int _discoveryPort;
    readonly string _name;
    // Identifies this instance across the several addresses it answers on.
    readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    CancellationTokenSource? _cts;
    Task _discovery = Task.CompletedTask;
    Task _status = Task.CompletedTask;
    TcpListener? _listener;
    UdpClient? _udp;

    public StatusServer(Func<BmsStatusSnapshot?> snapshot, Action<string> log, string? name = null,
        int statusPort = PeerProtocol.DefaultStatusPort, int discoveryPort = PeerProtocol.DefaultDiscoveryPort)
    {
        _snapshot = snapshot;
        _log = log;
        _statusPort = statusPort;
        _discoveryPort = discoveryPort;
        _name = name is { Length: > 0 } ? name : Environment.MachineName;
    }

    /// <summary>
    /// Starts both listeners. A port already in use is reported and left alone rather than
    /// thrown: sharing is a convenience, and must not take the poller down with it.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            _listener = new TcpListener(IPAddress.Any, _statusPort);
            _listener.Start();
            _status = ServeStatusAsync(ct);
            _log($"Sharing readings on TCP {_statusPort} as '{_name}'.");
        }
        catch (SocketException ex)
        {
            _log($"Not sharing readings: TCP port {_statusPort} unavailable ({ex.SocketErrorCode}).");
        }

        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
            _discovery = AnswerDiscoveryAsync(ct);
        }
        catch (SocketException ex)
        {
            _log($"Not answering discovery: UDP port {_discoveryPort} unavailable ({ex.SocketErrorCode}).");
        }
    }

    async Task AnswerDiscoveryAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var request = await udp.ReceiveAsync(ct);
                if (!PeerProtocol.IsProbe(Encoding.UTF8.GetString(request.Buffer)))
                    continue;
                // Only offer data we read ourselves, so a peer never relays second-hand readings.
                if (_snapshot() is not { Source: null })
                    continue;
                var offer = Encoding.UTF8.GetBytes(PeerProtocol.Offer(_statusPort, _instanceId, _name));
                await udp.SendAsync(offer, offer.Length, request.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    async Task ServeStatusAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = RespondAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    async Task RespondAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                await using var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, timeout.Token);

                var snapshot = _snapshot();
                var (status, body) = requestLine.StartsWith("GET /status", StringComparison.Ordinal) && snapshot is { Source: null }
                    ? ("200 OK", StatusFile.Serialize(snapshot))
                    : ("404 Not Found", "");

                var payload = Encoding.UTF8.GetBytes(body);
                var header = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status}\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n\r\n");

                await stream.WriteAsync(header, timeout.Token);
                await stream.WriteAsync(payload, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    /// <summary>Reads the request line and discards the headers; the body is never used.</summary>
    static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (got == 0)
                break;
            read += got;
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var end = text.IndexOf('\n');
            if (end >= 0)
                return text[..end].TrimEnd('\r');
        }
        return "";
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        _listener?.Stop();
        _udp?.Dispose();
        await Task.WhenAll(
            _status.ContinueWith(_ => { }, TaskScheduler.Default),
            _discovery.ContinueWith(_ => { }, TaskScheduler.Default));
        _cts.Dispose();
        _cts = null;
    }
}
