namespace BmsMonitor.Ble;

/// <summary>Selects the Bluetooth stack for the platform this build targets.</summary>
public static class BleBackend
{
    static IBleBackend? _current;

    public static IBleBackend Current => _current ??= Create();

    static IBleBackend Create()
    {
#if WINDOWS
        return new Windows.WinRtBleBackend();
#else
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "This build supports Linux (BlueZ) only. Build the net10.0-windows target to run on Windows.");
        return new Linux.BlueZBleBackend();
#endif
    }
}
