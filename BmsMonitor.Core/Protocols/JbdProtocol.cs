using BmsMonitor.Ble;

namespace BmsMonitor.Protocols;

/// <summary>
/// JBD / Jiabaida / Xiaoxiang BMS ("Smart BMS" app by JBD).
/// Request:  DD A5 cmd len [data] chkH chkL 77
/// Response: DD cmd status len [data] chkH chkL 77, checksum = 0x10000 - sum(status, len, data)
/// </summary>
public sealed class JbdProtocol : IBmsProtocol
{
    const byte Start = 0xDD, End = 0x77, CmdBasicInfo = 0x03;

    public string Name => "JBD";
    public Guid ServiceUuid => BmsProtocols.Uuid16(0xFF00);
    public Guid NotifyUuid => BmsProtocols.Uuid16(0xFF01);
    public Guid WriteUuid => BmsProtocols.Uuid16(0xFF02);

    public async Task<BmsStatus> ReadStatusAsync(GattChannel channel, CancellationToken ct)
    {
        await channel.SendAsync(BuildRead(CmdBasicInfo));
        var frame = await channel.ReceiveFrameAsync(ExtractFrame, TimeSpan.FromSeconds(5), ct)
            ?? throw new TimeoutException("JBD BMS did not answer the basic info request.");
        if (frame[2] != 0x00)
            throw new IOException($"JBD BMS returned error status 0x{frame[2]:X2}.");
        return ParseBasicInfo(frame.AsSpan(4, frame[3]));
    }

    static byte[] BuildRead(byte cmd)
    {
        var checksum = (ushort)(0x10000 - cmd);
        return [Start, 0xA5, cmd, 0x00, (byte)(checksum >> 8), (byte)checksum, End];
    }

    static byte[]? ExtractFrame(List<byte> buf)
    {
        while (true)
        {
            var start = buf.IndexOf(Start);
            if (start < 0) { buf.Clear(); return null; }
            buf.RemoveRange(0, start);
            if (buf.Count < 4) return null;

            var total = 4 + buf[3] + 3;
            if (buf.Count < total) return null;

            var frame = buf.GetRange(0, total).ToArray();
            if (frame[1] == CmdBasicInfo && frame[^1] == End && ChecksumOk(frame))
            {
                buf.RemoveRange(0, total);
                return frame;
            }
            buf.RemoveAt(0); // not a valid frame start, resync
        }
    }

    static bool ChecksumOk(byte[] frame)
    {
        var len = frame[3];
        var sum = 0;
        for (var i = 2; i < 4 + len; i++) sum += frame[i];
        var expected = (ushort)(0x10000 - sum);
        return frame[4 + len] == (byte)(expected >> 8) && frame[5 + len] == (byte)expected;
    }

    static BmsStatus ParseBasicInfo(ReadOnlySpan<byte> d)
    {
        if (d.Length < 23)
            throw new IOException($"JBD basic info payload too short ({d.Length} bytes).");

        ushort U16(ReadOnlySpan<byte> s, int i) => (ushort)(s[i] << 8 | s[i + 1]);

        var ntcCount = d[22];
        var temps = new List<double>();
        for (var i = 0; i < ntcCount && 23 + i * 2 + 1 < d.Length; i++)
            temps.Add((U16(d, 23 + i * 2) - 2731) / 10.0);

        return new BmsStatus(
            SocPercent: d[19],
            VoltageV: U16(d, 0) / 100.0,
            CurrentA: (short)U16(d, 2) / 100.0,
            RemainingAh: U16(d, 4) / 100.0,
            NominalAh: U16(d, 6) / 100.0,
            Cycles: U16(d, 8),
            TemperaturesC: temps);
    }
}
