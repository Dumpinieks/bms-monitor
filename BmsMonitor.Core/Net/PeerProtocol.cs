namespace BmsMonitor.Net;

/// <summary>
/// The LAN protocol used when the BMS itself is unreachable - typically because another
/// machine (or the phone app) already holds its single Bluetooth connection.
///
/// Discovery is a UDP broadcast probe answered by every instance that is sharing:
///   probe  "BMSMON/1 DISCOVER"
///   offer  "BMSMON/1 OFFER &lt;tcpPort&gt; &lt;instanceId&gt; &lt;name&gt;"
/// The instance id is what identifies a peer: one host answers the probe once per address it
/// holds (loopback, LAN, VPN), and those replies are all the same instance.
/// The reading itself is then fetched over TCP with a plain GET /status, so the payload
/// is the same JSON the status file holds.
/// </summary>
public static class PeerProtocol
{
    public const int DefaultDiscoveryPort = 17646;
    public const int DefaultStatusPort = 17645;

    const string Prefix = "BMSMON/1";
    public const string Probe = Prefix + " DISCOVER";

    public static string Offer(int statusPort, string instanceId, string name) =>
        $"{Prefix} OFFER {statusPort} {instanceId} {name}";

    /// <summary>Parses an offer, returning false for anything that is not one.</summary>
    public static bool TryParseOffer(string message, out int statusPort, out string instanceId, out string name)
    {
        statusPort = 0;
        instanceId = "";
        name = "";
        var parts = message.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || parts[0] != Prefix || parts[1] != "OFFER")
            return false;
        if (!int.TryParse(parts[2], out statusPort) || statusPort is <= 0 or > 65535)
            return false;
        instanceId = parts[3];
        name = parts.Length > 4 ? parts[4].Trim() : "";
        return true;
    }

    public static bool IsProbe(string message) =>
        message.AsSpan().Trim().SequenceEqual(Probe);
}
