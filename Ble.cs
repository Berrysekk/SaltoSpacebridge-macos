using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SaltoBridge;

/// <summary>SALTO devices over Bluetooth LE, through CoreBluetooth (ble/spacebridge_ble.m). Ported from the Windows bridge
/// 2.1.0; off unless `spacebridge config bluetooth on`.</summary>
public static unsafe class Ble
{
    // CoreBluetooth's CBManagerState.
    public const int Unknown = 0, Unsupported = 2, Unauthorized = 3, PoweredOff = 4, PoweredOn = 5;

    const string Library = "libspacebridge-ble.dylib";
    [DllImport(Library)] static extern int sb_ble_state(int waitMs);
    [DllImport(Library)] static extern int sb_ble_authorization();
    [DllImport(Library)] static extern int sb_ble_scan(delegate* unmanaged<byte*, byte*, int, void> advert);
    [DllImport(Library)] static extern void sb_ble_scan_stop();
    [DllImport(Library)] internal static extern nint sb_ble_open(string peripheral, delegate* unmanaged<nint, byte*, int, void> data, nint context, int timeoutMs, byte[] error, int errorSize);
    [DllImport(Library)] internal static extern int sb_ble_mtu(nint link);
    [DllImport(Library)] internal static extern int sb_ble_write(nint link, byte[] data, int length, int withResponse, int timeoutMs);
    [DllImport(Library)] internal static extern int sb_ble_read(nint link, byte[] buffer, int capacity, int timeoutMs);
    [DllImport(Library)] internal static extern int sb_ble_notify(nint link, int timeoutMs);
    [DllImport(Library)] internal static extern void sb_ble_close(nint link);

    public sealed record Device(string DeviceId, string Peripheral);

    static readonly object scanning = new();
    static readonly Dictionary<string, Device> found = [];
    static readonly HashSet<string> heard = [];

    /// <summary>The adapter's state; the first call makes macOS ask for the Bluetooth permission.</summary>
    public static int State(int waitMs = 0)
    {
        try
        {
            return sb_ble_state(waitMs);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Once("Bluetooth is unavailable: " + e.Message);
            return Unsupported;
        }
    }

    public static string StateName(int state) => state switch
    {
        PoweredOn => "ready",
        PoweredOff => "switched off in macOS",
        Unauthorized => "not allowed: System Settings > Privacy & Security > Bluetooth",
        Unsupported => "not available on this Mac",
        _ => sb_ble_authorization() == 0 ? "waiting for your answer to the macOS question whether Spacebridge may use Bluetooth" : "not ready",
    };

    /// <summary>How many Bluetooth devices of any make the last scan heard.</summary>
    public static int Heard { get { lock (found) return heard.Count; } }

    /// <summary>Listens for SALTO devices ready for a programming session: 15 s for all (as Windows), or until the one asked for is heard.</summary>
    public static List<Device> Discover(string? deviceId, int ms = 15_000)
    {
        lock (scanning)
        {
            lock (found)
            {
                found.Clear();
                heard.Clear();
            }
            if (State(3000) != PoweredOn || sb_ble_scan(&OnAdvert) != 0) return [];
            try
            {
                for (var until = Environment.TickCount64 + ms; Environment.TickCount64 < until;)
                {
                    Thread.Sleep(250);
                    lock (found) if (deviceId is not null && found.ContainsKey(deviceId.ToUpperInvariant())) break;
                }
            }
            finally
            {
                sb_ble_scan_stop();
            }
            lock (found) return deviceId is null ? [.. found.Values] : found.Values.Where(d => d.DeviceId == deviceId.ToUpperInvariant()).ToList();
        }
    }

    [UnmanagedCallersOnly]
    static void OnAdvert(byte* peripheral, byte* manufacturer, int length)
    {
        var id = Marshal.PtrToStringUTF8((nint)peripheral)!;
        var device = SaltoDevice(new ReadOnlySpan<byte>(manufacturer, length));
        lock (found)
        {
            heard.Add(id);
            if (device is not null) found[device] = new(device, id);
        }
    }

    /// <summary>The device id in an advertisement's manufacturer data, if it is SALTO's (company 409), carries a device id
    /// (format 1) and the device says it is ready (bit 7 of the flags).</summary>
    public static string? SaltoDevice(ReadOnlySpan<byte> m) =>
        m.Length >= 12 && m[0] == 0x99 && m[1] == 0x01 && m[2] == 1 && (m[4] & 0x80) != 0 ? Convert.ToHexString(m.Slice(5, 7)) : null;
}

/// <summary>6LoWPAN-style framing SALTO puts on its GATT characteristics: a message that fits one packet gets a 4-byte
/// header, a longer one is cut into FRAG1/FRAGN packets sharing a tag.</summary>
public sealed class LowPan(Action<byte[]> write, int mtu)
{
    static readonly byte[] Iphc = [0x7D, 0x33], NhcUdp = [0xF7, 0x00];

    readonly int mtu = mtu == 0 ? 127 : mtu;
    ushort writeTag = (ushort)Random.Shared.Next(0, 65535);
    ushort readTag;
    int fragment, fragments;
    List<byte> message = [];

    public void Send(byte[] data)
    {
        if (data.Length <= mtu - 4)
        {
            write([.. Iphc, .. NhcUdp, .. data]);
            return;
        }
        byte sizeHigh = (byte)(data.Length >> 8), sizeLow = (byte)data.Length, tagHigh = (byte)(writeTag >> 8), tagLow = (byte)writeTag;
        writeTag = (ushort)((writeTag + 1) % 65535);
        byte[] header = [(byte)(sizeHigh | 0xC0), sizeLow, tagHigh, tagLow, .. Iphc, .. NhcUdp];
        byte index = 0;
        for (var at = 0; at < data.Length;)
        {
            var count = Math.Min(mtu - header.Length, data.Length - at);
            write([.. header, .. data.AsSpan(at, count)]);
            Thread.Sleep(25);
            at += count;
            header = [(byte)(sizeHigh | 0xE0), sizeLow, tagHigh, tagLow, index++];
        }
    }

    /// <summary>Takes one received packet; returns the message once it is whole.</summary>
    public byte[]? Compose(byte[] packet)
    {
        if (packet.Length >= 4 && packet[0] == Iphc[0] && packet[1] == Iphc[1]) return packet[4..];
        if (packet.Length < 5) throw new IOException("lowpan: short packet");
        var tag = BitConverter.ToUInt16(packet, 2);
        byte[] body;
        switch (packet[0] & 0xF8)
        {
            case 0xC0 when packet.Length >= 8:
                body = packet[8..];
                var size = (packet[0] << 8 | packet[1]) & 0x7FF;
                fragment = 1;
                fragments = 1 + (int)Math.Ceiling((double)(size - body.Length) / (mtu - 5));
                message = [];
                // Windows insists on the tag it predicts and so fails where the device counts differently; a new message simply sets it.
                readTag = tag;
                break;
            case 0xE0:
                body = packet[5..];
                fragment++;
                if (fragment - 2 != packet[4]) throw new IOException($"lowpan: unexpected index: {packet[4]}, expected: {fragment - 2}");
                if (tag != readTag) throw new IOException("lowpan: unexpected tag");
                break;
            default:
                throw new IOException($"lowpan: unexpected header: {packet[0]:X2}{packet[1]:X2}");
        }
        message.AddRange(body);
        return fragment == fragments ? [.. message] : null;
    }
}

/// <summary>An open session with a SALTO device over Bluetooth LE.</summary>
public sealed unsafe class BleLocal : ILocal
{
    readonly BlockingCollection<byte[]> incoming = [];
    readonly GCHandle self;
    readonly nint link;
    LowPan? lowPan;
    volatile string? failure;
    int closed;

    public BleLocal(string peripheral)
    {
        self = GCHandle.Alloc(this);
        var error = new byte[256];
        link = Ble.sb_ble_open(peripheral, &OnData, GCHandle.ToIntPtr(self), 10_000, error, error.Length);
        if (link == 0)
        {
            self.Free();
            throw new IOException("Communication error: " + System.Text.Encoding.UTF8.GetString(error).TrimEnd('\0'));
        }
        try
        {
            // Select SALTO's mobile PPD protocol (BER-TLV: private tag 0, value 2), then the device must answer version 1.
            Check(Ble.sb_ble_write(link, [0xC0, 0x01, 0x02], 3, 1, 5000), "Salto Write Protocol selection failed");
            var answer = new byte[512];
            var length = Ble.sb_ble_read(link, answer, answer.Length, 5000);
            if (length < 3 || answer[length - 1] != 1 || answer[length - 3] != 1 || answer[length - 2] != 0)
                throw new IOException("Communication error: Salto Read Protocol Version expected 1, received " + Convert.ToHexString(answer.AsSpan(0, Math.Max(length, 0))));
            Thread.Sleep(25);
            lowPan = new LowPan(packet => Check(Ble.sb_ble_write(link, packet, packet.Length, 0, 5000), "Write failed"), Ble.sb_ble_mtu(link));
            Check(Ble.sb_ble_notify(link, 5000), "Write Client Characteristic Configuration Description process failed");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    static void Check(int result, string what)
    {
        if (result != 0) throw new IOException("Communication error: " + what);
    }

    [UnmanagedCallersOnly]
    static void OnData(nint context, byte* data, int length)
    {
        if (GCHandle.FromIntPtr(context).Target is not BleLocal local) return;
        try
        {
            if (length < 0) local.failure = "the Bluetooth device disconnected";
            else if (local.lowPan?.Compose(new ReadOnlySpan<byte>(data, length).ToArray()) is { } message) local.incoming.Add(message);
        }
        catch (Exception e)
        {
            local.failure = e.Message;
        }
    }

    public byte[]? Read() => failure is { } why ? throw new IOException(why) : incoming.TryTake(out var data, 100) ? data : null;

    public void Write(byte[] data) => (lowPan ?? throw new IOException("closed")).Send(data);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        Ble.sb_ble_close(link);
        self.Free();
    }
}
