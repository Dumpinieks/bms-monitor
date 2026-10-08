using BmsMonitor.Protocols;

namespace BmsMonitor.Ble;

public sealed record FoundDevice(ulong Address, string Name, string Source)
{
    public override string ToString() => $"{(Name.Length > 0 ? Name : "(no name)")} at {BleAddress.Format(Address)} (via {Source})";
}

/// <summary>Locates a BMS without a configured address.</summary>
public static class DeviceFinder
{
    // Default advertised names: Daly "DL-<mac>", JBD "xiaoxiang..." / "JBD-...".
    static readonly string[] BmsNamePrefixes = ["DL-", "xiaoxiang", "JBD"];
    static readonly HashSet<Guid> BmsServices = BmsProtocols.CreateAll().Select(p => p.ServiceUuid).ToHashSet();
    const int MaxServiceOnlyCandidates = 3;

    /// <summary>
    /// Returns likely BMS devices, best first: name matches from the scan, devices the stack already knows
    /// with a matching name (a connected BMS stops advertising), then devices that only advertise a BMS
    /// service UUID. The service UUIDs are generic (FF00/FFF0 are used by unrelated gadgets too), so
    /// callers should verify each candidate.
    /// </summary>
    /// <param name="nameFilter">If set, match devices whose name contains this text instead of the built-in heuristics.</param>
    public static async Task<IReadOnlyList<FoundDevice>> FindCandidatesAsync(string? nameFilter, TimeSpan scanTime, CancellationToken ct)
    {
        bool NameMatches(string name) => nameFilter is not null
            ? name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)
            : BmsNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        var seen = await BleScanner.ScanAsync(scanTime, d => NameMatches(d.Name), ct);
        var candidates = seen.Where(d => NameMatches(d.Name))
            .OrderByDescending(d => d.Rssi)
            .Select(d => new FoundDevice(d.Address, d.Name, "scan"))
            .ToList();

        foreach (var known in await BleBackend.Current.GetKnownDevicesAsync(ct))
        {
            if (NameMatches(known.Name) && candidates.All(c => c.Address != known.Address))
                candidates.Add(new FoundDevice(known.Address, known.Name, "known devices"));
        }

        if (nameFilter is null)
            candidates.AddRange(seen
                .Where(d => !NameMatches(d.Name) && d.ServiceUuids.Any(BmsServices.Contains))
                .OrderByDescending(d => d.Rssi)
                .Take(MaxServiceOnlyCandidates)
                .Select(d => new FoundDevice(d.Address, d.Name, "advertised service")));

        return candidates;
    }
}
