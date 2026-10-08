using BmsMonitor.Ble;
using BmsMonitor.Protocols;

namespace BmsMonitor;

public sealed record ConnectOptions(
    ulong? Address = null,
    string? Name = null,
    string? Protocol = null,
    bool Verbose = false)
{
    public TimeSpan ScanTime { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Finds and connects to a BMS, verifying candidates by reading a status from them.</summary>
public static class BmsConnector
{
    public static async Task<IReadOnlyList<FoundDevice>> FindCandidatesAsync(ConnectOptions o, Action<string> log, CancellationToken ct)
    {
        log(o.Name is null ? "Searching for a BMS..." : $"Searching for a device named like '{o.Name}'...");
        var candidates = await DeviceFinder.FindCandidatesAsync(o.Name, o.ScanTime, ct);
        if (candidates.Count == 0)
            throw new IOException(o.Name is null
                ? "No BMS found. Make sure it is powered and in range, or pass --address."
                : $"No BLE device with a name containing '{o.Name}' found.");
        return candidates;
    }

    /// <summary>
    /// Connects to <paramref name="address"/>, or searches and returns the first candidate that answers like a BMS.
    /// </summary>
    public static async Task<(BmsSession Session, BmsStatus Status, ulong Address)> ConnectAsync(
        ConnectOptions o, ulong? address, Action<string> log, CancellationToken ct)
    {
        if (address is { } known)
            return await OpenAsync(o, known, ct);

        foreach (var candidate in await FindCandidatesAsync(o, log, ct))
        {
            log($"Trying {candidate}...");
            try
            {
                var result = await OpenAsync(o, candidate.Address, ct);
                log($"Found {result.Session.Protocol.Name} BMS {candidate}.");
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"  not a BMS or not responding: {ex.Message}");
            }
        }
        throw new IOException("None of the found devices answered as a BMS. Pass --address to select it explicitly.");
    }

    static async Task<(BmsSession Session, BmsStatus Status, ulong Address)> OpenAsync(ConnectOptions o, ulong address, CancellationToken ct)
    {
        var session = await BmsSession.OpenAsync(address, o.Protocol, o.Verbose, ct);
        try
        {
            return (session, await session.ReadStatusAsync(ct), address);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}
