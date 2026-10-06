using System.Globalization;

namespace BmsMonitor.Ble;

public static class BleAddress
{
    public static string Format(ulong address) =>
        string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((address >> (i * 8)) & 0xFF).ToString("X2")));

    public static ulong Parse(string text)
    {
        var hex = text.Replace(":", "").Replace("-", "").Trim();
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            throw new FormatException($"Invalid Bluetooth address '{text}'. Expected format AA:BB:CC:DD:EE:FF.");
        return value;
    }

    public static string Hex(IEnumerable<byte> data) => string.Join(" ", data.Select(b => b.ToString("X2")));
}
