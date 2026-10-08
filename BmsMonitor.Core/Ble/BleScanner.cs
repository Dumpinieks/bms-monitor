namespace BmsMonitor.Ble;

public static class BleScanner
{
    /// <summary>Listens for BLE advertisements for <paramref name="duration"/> or until <paramref name="stopWhen"/> matches.</summary>
    public static Task<IReadOnlyCollection<SeenDevice>> ScanAsync(
        TimeSpan duration, Func<SeenDevice, bool>? stopWhen = null, CancellationToken ct = default) =>
        BleBackend.Current.ScanAsync(duration, stopWhen, ct);
}
