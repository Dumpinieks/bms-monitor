using BmsMonitor.Ble;

namespace BmsMonitor.Protocols;

/// <summary>
/// Daly BMS ("SMART BMS" app by Daly).
/// Request:  A5 addr cmd 08 [8 bytes] sum   (addr 0x40; some firmwares expect 0x80 over Bluetooth)
/// Response: A5 01 cmd 08 [8 bytes] sum, sum = low byte of the sum of all preceding bytes
/// </summary>
public sealed class DalyProtocol : IBmsProtocol
{
    const byte Start = 0xA5, FrameLength = 13;
    const byte CmdSoc = 0x90, CmdTemperature = 0x92, CmdMosfet = 0x93, CmdStatus = 0x94;
    static readonly byte[] CandidateAddresses = [0x40, 0x80];
    static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(4);

    byte? _address;
    // Optional commands that timed out once are not asked again on this connection.
    readonly HashSet<byte> _unsupported = [];

    public string Name => "Daly";
    public Guid ServiceUuid => BmsProtocols.Uuid16(0xFFF0);
    public Guid NotifyUuid => BmsProtocols.Uuid16(0xFFF1);
    public Guid WriteUuid => BmsProtocols.Uuid16(0xFFF2);

    public async Task<BmsStatus> ReadStatusAsync(GattChannel channel, CancellationToken ct)
    {
        // 0x90: total voltage (0.1 V), current (0.1 A, offset 30000), SOC (0.1 %)
        var soc = await RequestSocAsync(channel, ct);
        var status = new BmsStatus(
            SocPercent: U16(soc, 6) / 10.0,
            VoltageV: U16(soc, 0) / 10.0,
            CurrentA: (U16(soc, 4) - 30000) / 10.0);

        // 0x93: state, charge MOS, discharge MOS, life, remaining capacity (mAh, u32)
        if (await RequestOptionalAsync(channel, CmdMosfet, ct) is { } mos)
            status = status with { RemainingAh = (uint)(mos[4] << 24 | mos[5] << 16 | mos[6] << 8 | mos[7]) / 1000.0 };

        // 0x92: max temperature, its sensor, min temperature, its sensor (°C, offset 40)
        if (await RequestOptionalAsync(channel, CmdTemperature, ct) is { } temp)
            status = status with { TemperaturesC = temp[0] == temp[2] ? [temp[0] - 40] : [temp[0] - 40, temp[2] - 40] };

        // 0x94: cell count, sensor count, charger, load, DIO, cycles (u16)
        if (await RequestOptionalAsync(channel, CmdStatus, ct) is { } st)
            status = status with { Cycles = U16(st, 5) };

        return status;
    }

    async Task<byte[]> RequestSocAsync(GattChannel channel, CancellationToken ct)
    {
        foreach (var address in _address is { } known ? [known] : CandidateAddresses)
        {
            if (await RequestAsync(channel, address, CmdSoc, ct) is { } data)
            {
                _address = address;
                return data;
            }
        }
        throw new TimeoutException("Daly BMS did not answer the SOC request.");
    }

    async Task<byte[]?> RequestOptionalAsync(GattChannel channel, byte cmd, CancellationToken ct)
    {
        if (_address is not { } address || _unsupported.Contains(cmd))
            return null;
        var data = await RequestAsync(channel, address, cmd, ct);
        if (data is null)
            _unsupported.Add(cmd);
        return data;
    }

    static async Task<byte[]?> RequestAsync(GattChannel channel, byte address, byte cmd, CancellationToken ct)
    {
        await channel.SendAsync(BuildRequest(address, cmd), ct);
        var frame = await channel.ReceiveFrameAsync(b => ExtractFrame(b, cmd), ResponseTimeout, ct);
        return frame?[4..12];
    }

    static int U16(byte[] d, int i) => d[i] << 8 | d[i + 1];

    static byte[] BuildRequest(byte address, byte cmd)
    {
        var frame = new byte[FrameLength];
        frame[0] = Start;
        frame[1] = address;
        frame[2] = cmd;
        frame[3] = 0x08;
        frame[12] = Checksum(frame);
        return frame;
    }

    static byte Checksum(byte[] frame)
    {
        var sum = 0;
        for (var i = 0; i < FrameLength - 1; i++) sum += frame[i];
        return (byte)sum;
    }

    static byte[]? ExtractFrame(List<byte> buf, byte cmd)
    {
        while (true)
        {
            var start = buf.IndexOf(Start);
            if (start < 0) { buf.Clear(); return null; }
            buf.RemoveRange(0, start);
            if (buf.Count < FrameLength) return null;

            var frame = buf.GetRange(0, FrameLength).ToArray();
            if (frame[2] == cmd && frame[3] == 0x08 && frame[12] == Checksum(frame))
            {
                buf.RemoveRange(0, FrameLength);
                return frame;
            }
            buf.RemoveAt(0);
        }
    }
}
