using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace BmsMonitor.Net;

public sealed record BmsPeer(IPAddress Address, int StatusPort, string InstanceId, string Name)
{
    public override string ToString() => $"{(Name.Length > 0 ? Name : "(unnamed)")} at {Address}:{StatusPort}";
}

/// <summary>
/// Finds other instances of this app on the LAN and reads their latest BMS snapshot.
/// Used as a fallback when the BMS's single Bluetooth connection is held elsewhere.
/// </summary>
public sealed class PeerFinder(Action<string> log, int discoveryPort = PeerProtocol.DefaultDiscoveryPort)
{
    static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(4);

    /// <summary>A snapshot older than this is treated as useless rather than shown as current.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    readonly HttpClient _http = new() { Timeout = FetchTimeout };

    /// <summary>
    /// Instance to ignore when discovering. An instance that both shares and consumes answers
    /// its own probe - set this to its <see cref="StatusServer.InstanceId"/> so it does not
    /// serve itself its own last reading and call it a peer's.
    /// </summary>
    public string? ExcludeInstanceId { get; set; }

    /// <summary>Broadcasts a probe and collects every instance that answers.</summary>
    public async Task<IReadOnlyList<BmsPeer>> DiscoverAsync(TimeSpan? timeout, CancellationToken ct)
    {
        var found = new Dictionary<string, BmsPeer>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.EnableBroadcast = true;

        var probe = Encoding.UTF8.GetBytes(PeerProtocol.Probe);
        var sent = 0;
        foreach (var target in ProbeTargets(discoveryPort))
        {
            try
            {
                await udp.SendAsync(probe, probe.Length, target);
                sent++;
            }
            catch (SocketException)
            {
                // An interface that refuses broadcast is not fatal; others may still answer.
            }
        }
        if (sent == 0)
        {
            log("Peer discovery could not send on any interface.");
            return [];
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? DefaultDiscoveryTimeout);
        try
        {
            while (true)
            {
                var reply = await udp.ReceiveAsync(deadline.Token);
                var text = Encoding.UTF8.GetString(reply.Buffer);
                if (!PeerProtocol.TryParseOffer(text, out var port, out var id, out var name))
                    continue;
                if (id == ExcludeInstanceId)
                    continue;
                // Keyed by instance, so a host answering on loopback, LAN and VPN counts once.
                // The first reply wins: it came back on the fastest path.
                var peer = new BmsPeer(reply.RemoteEndPoint.Address, port, id, name);
                found.TryAdd(id, peer);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Discovery window elapsed; whatever answered is the answer.
        }
        catch (SocketException)
        {
        }
        return found.Values.ToList();
    }

    /// <summary>
    /// Where to send the probe. Plain 255.255.255.255 is dropped by some routers and host
    /// firewalls, so each interface's directed broadcast is tried too, plus loopback so two
    /// instances on one machine find each other.
    /// </summary>
    static IEnumerable<IPEndPoint> ProbeTargets(int port)
    {
        var seen = new HashSet<string>();
        foreach (var address in Candidates())
        {
            if (seen.Add(address.ToString()))
                yield return new IPEndPoint(address, port);
        }

        static IEnumerable<IPAddress> Candidates()
        {
            yield return IPAddress.Broadcast;
            yield return IPAddress.Loopback;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                        continue;
                    if (DirectedBroadcast(unicast.Address, unicast.IPv4Mask) is { } broadcast)
                        yield return broadcast;
                }
            }
        }
    }

    /// <summary>address | ~mask - the all-hosts address of that subnet.</summary>
    static IPAddress? DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4)
            return null;
        var result = new byte[4];
        for (var i = 0; i < 4; i++)
            result[i] = (byte)(a[i] | ~m[i]);
        return new IPAddress(result);
    }

    /// <summary>Reads one peer's snapshot, or null if it cannot be reached or does not have one.</summary>
    public async Task<BmsStatusSnapshot?> FetchAsync(BmsPeer peer, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"http://{peer.Address}:{peer.StatusPort}/status", ct);
            return StatusFile.Deserialize(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Discovers peers and returns the freshest usable snapshot, with the peer that served it.
    /// Stale or offline readings are skipped - a peer that cannot reach the BMS either is no help.
    /// </summary>
    public async Task<(BmsStatusSnapshot Snapshot, BmsPeer Peer)?> TryFetchAsync(CancellationToken ct)
    {
        var peers = await DiscoverAsync(null, ct);
        if (peers.Count == 0)
            return null;

        (BmsStatusSnapshot Snapshot, BmsPeer Peer)? best = null;
        foreach (var peer in peers)
        {
            if (await FetchAsync(peer, ct) is not { Online: true, Source: null } snapshot)
                continue;
            if (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(snapshot.UpdatedUnix) > MaxAge)
                continue;
            if (best is null || snapshot.UpdatedUnix > best.Value.Snapshot.UpdatedUnix)
                best = (snapshot, peer);
        }
        return best;
    }
}
