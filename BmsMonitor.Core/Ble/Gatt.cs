namespace BmsMonitor.Ble;

/// <summary>The subset of GATT characteristic properties this app cares about.</summary>
[Flags]
public enum GattProperties
{
    None = 0,
    Read = 1,
    Write = 2,
    WriteWithoutResponse = 4,
    Notify = 8,
    Indicate = 16,
}

public sealed record SeenDevice(ulong Address, string Name, short Rssi, IReadOnlyList<Guid> ServiceUuids);

/// <summary>A device the OS already knows about, without a fresh advertisement.</summary>
public sealed record KnownDevice(ulong Address, string Name);

/// <summary>
/// A single GATT characteristic. Notifications arrive on <see cref="ValueChanged"/> once
/// <see cref="SubscribeAsync"/> has succeeded; they may be raised on any thread.
/// </summary>
public interface IGattCharacteristic
{
    Guid Uuid { get; }
    GattProperties Properties { get; }
    event Action<byte[]>? ValueChanged;

    /// <summary>Reads the value, or returns null if the characteristic is not readable or the read fails.</summary>
    Task<byte[]?> TryReadAsync(CancellationToken ct);

    /// <summary>Writes the value, preferring write-without-response when the characteristic supports it.</summary>
    Task WriteAsync(byte[] data, CancellationToken ct);

    Task SubscribeAsync(CancellationToken ct);
    void Unsubscribe();
}

public interface IGattService
{
    Guid Uuid { get; }
    Task<IReadOnlyList<IGattCharacteristic>> GetCharacteristicsAsync(CancellationToken ct);
}

/// <summary>A connected BLE device and its discovered GATT services.</summary>
public interface IBleDevice : IDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    IReadOnlyList<IGattService> Services { get; }
    IGattService? FindService(Guid uuid);
}

/// <summary>
/// The platform's Bluetooth stack: WinRT on Windows, BlueZ over D-Bus on Linux.
/// Obtained from <see cref="BleBackend.Current"/>.
/// </summary>
public interface IBleBackend
{
    /// <summary>Short name of the stack, for diagnostics ("WinRT", "BlueZ").</summary>
    string Name { get; }

    /// <summary>Listens for advertisements for <paramref name="duration"/> or until <paramref name="stopWhen"/> matches.</summary>
    Task<IReadOnlyCollection<SeenDevice>> ScanAsync(TimeSpan duration, Func<SeenDevice, bool>? stopWhen, CancellationToken ct);

    /// <summary>
    /// Devices the stack already knows about (paired on Windows, paired or recently seen on Linux).
    /// A connected BMS stops advertising, so it can only be found this way.
    /// </summary>
    Task<IReadOnlyList<KnownDevice>> GetKnownDevicesAsync(CancellationToken ct);

    Task<IBleDevice> ConnectAsync(ulong address, CancellationToken ct);
}
