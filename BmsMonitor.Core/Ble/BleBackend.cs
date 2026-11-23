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
        // net10.0 is one build for both Unix stacks, so this choice is made at run time.
        if (OperatingSystem.IsMacOS())
            return new MacOS.CoreBluetoothBackend();
        if (OperatingSystem.IsLinux())
            return new Linux.BlueZBleBackend();
        throw new PlatformNotSupportedException(
            "This build supports Linux (BlueZ) and macOS (CoreBluetooth). Build the net10.0-windows target to run on Windows.");
#endif
    }
}
