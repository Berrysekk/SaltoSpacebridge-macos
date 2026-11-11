using System.Runtime.InteropServices;
using System.Text;
using HidSharp;

namespace SaltoBridge;

/// <summary>A device the bridge can pipe to SPACE. Read returns null when nothing arrived yet.</summary>
public interface ILocal : IDisposable
{
    byte[]? Read();
    void Write(byte[] data);
}

/// <summary>Type is the bridge's PortType: 0 Usb, 1 Serial, 2 Bluetooth. Device type as the web app's TargetDeviceType: 0 encoder, 1 PPD, 2 nCoder, 3 Bluetooth device, -1 unknown.</summary>
public sealed record Port(string Id, int DeviceType, string Name, string Info, Func<ILocal> Open, int Type = 0);

public static class Discovery
{
    public static List<Port> All(bool serialPorts = true)
    {
        var ports = new List<Port>();
        try
        {
            var usb = Cp210x.List();
            for (var i = 0; i < usb.Count; i++)
            {
                var (index, d) = (i, usb[i]);
                ports.Add(new Port(Id(i.ToString()), d.DeviceType, "usb" + i, d.Product + " " + d.Serial, () => new Cp210x(index)));
            }
        }
        catch (DllNotFoundException e)
        {
            Log.Once("USB encoders and PPDs are unavailable, libusb did not load: " + e.Message);
        }
        try
        {
            var hid = DeviceList.Local.GetHidDevices(0x307E, 0x0164).ToArray();
            for (var i = 0; i < hid.Length; i++)
            {
                var d = hid[i];
                if (!IsSaltoHid(d)) continue;
                var serial = d.GetSerialNumber();
                ports.Add(new Port(Id(serial), 2, "hid" + i, serial, () => new HidLocal(d)));
            }
        }
        catch (Exception e)
        {
            Log.Once("HID discovery failed: " + e.Message);
        }
        foreach (var path in serialPorts ? Directory.GetFiles("/dev", "cu.*").Order() : Enumerable.Empty<string>())
        {
            var name = Path.GetFileName(path);
            // macOS's own pseudo ports, never an encoder.
            if (name is "cu.Bluetooth-Incoming-Port" or "cu.debug-console" or "cu.wlan-debug") continue;
            ports.Add(new Port(Id(name, "Serial"), -1, name, "", () => new SerialLocal(path), 1));
        }
        return ports;
    }

    // The nCoder's vendor-defined interface, as the Windows bridge picks it.
    static bool IsSaltoHid(HidDevice d)
    {
        try
        {
            return d.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Any(u => u.ToString("X4").StartsWith("FF")));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The Windows bridge's port id: its Base62 of "&lt;port type&gt;-&lt;internal id&gt;".</summary>
    public static string Id(string internalId, string type = "Usb") => Base62(Encoding.UTF8.GetBytes(type + "-" + internalId));

    // 6 bits a character; 11110 and 11111 are 'y' and 'z' and take only 5.
    public static string Base62(byte[] b)
    {
        const string table = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder();
        long pos = 0, len = b.Length * 8L;
        int Bits(int n)
        {
            var v = 0;
            for (var i = 0; i < n; i++, pos++) v = v << 1 | b[pos >> 3] >> 7 - (int)(pos & 7) & 1;
            return v;
        }
        while (len - pos >= 6)
        {
            var v = Bits(6);
            if (v >> 1 >= 30)
            {
                sb.Append(table[30 + (v >> 1)]);
                pos--;
            }
            else sb.Append(table[v]);
        }
        return sb.Append(table[Bits((int)(len - pos))]).ToString();
    }
}

/// <summary>SALTO's USB encoder and PPD: a CP210x UART bridge under SALTO's own id 10C4:82E9.
/// Windows drives it through Silicon Labs USBXpress; macOS has no driver for that id, so libusb talks to it directly.</summary>
public sealed class Cp210x : ILocal
{
    const string Lib = "libusb-1.0.0.dylib";
    const int Timeout = -7, Interface = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct DeviceDescriptor
    {
        public byte bLength, bDescriptorType;
        public ushort bcdUSB;
        public byte bDeviceClass, bDeviceSubClass, bDeviceProtocol, bMaxPacketSize0;
        public ushort idVendor, idProduct, bcdDevice;
        public byte iManufacturer, iProduct, iSerialNumber, bNumConfigurations;
    }

    [DllImport(Lib)] static extern int libusb_init(IntPtr ctx);
    [DllImport(Lib)] static extern nint libusb_get_device_list(IntPtr ctx, out IntPtr list);
    [DllImport(Lib)] static extern void libusb_free_device_list(IntPtr list, int unref);
    [DllImport(Lib)] static extern int libusb_get_device_descriptor(IntPtr dev, out DeviceDescriptor d);
    [DllImport(Lib)] static extern byte libusb_get_bus_number(IntPtr dev);
    [DllImport(Lib)] static extern byte libusb_get_device_address(IntPtr dev);
    [DllImport(Lib)] static extern int libusb_open(IntPtr dev, out IntPtr handle);
    [DllImport(Lib)] static extern void libusb_close(IntPtr handle);
    [DllImport(Lib)] static extern int libusb_get_string_descriptor_ascii(IntPtr handle, byte index, byte[] data, int length);
    [DllImport(Lib)] static extern int libusb_claim_interface(IntPtr handle, int iface);
    [DllImport(Lib)] static extern int libusb_release_interface(IntPtr handle, int iface);
    [DllImport(Lib)] static extern int libusb_control_transfer(IntPtr handle, byte requestType, byte request, ushort value, ushort index, byte[]? data, ushort length, uint timeout);
    [DllImport(Lib)] static extern int libusb_bulk_transfer(IntPtr handle, byte endpoint, byte[] data, int length, out int transferred, uint timeout);
    [DllImport(Lib)] static extern IntPtr libusb_strerror(int code);

    public sealed record Info(string Serial, string Product, int DeviceType);

    static readonly object gate = new();
    static bool initialised;

    /// <summary>Runs <paramref name="use"/> over the attached SALTO devices, in bus order (the port number in the id).</summary>
    static T WithDevices<T>(Func<IntPtr[], T> use)
    {
        lock (gate)
        {
            if (!initialised) Check(libusb_init(IntPtr.Zero), "init");
            initialised = true;
            var n = libusb_get_device_list(IntPtr.Zero, out var list);
            if (n < 0) Check((int)n, "list devices");
            try
            {
                var devices = Enumerable.Range(0, (int)n)
                    .Select(i => Marshal.ReadIntPtr(list, i * IntPtr.Size))
                    .Where(d => libusb_get_device_descriptor(d, out var desc) == 0 && desc is { idVendor: 0x10C4, idProduct: 0x82E9 })
                    .OrderBy(libusb_get_bus_number).ThenBy(libusb_get_device_address)
                    .ToArray();
                return use(devices);
            }
            finally
            {
                libusb_free_device_list(list, 1);
            }
        }
    }

    public static List<Info> List() => WithDevices(devices => devices.Select(d =>
    {
        if (libusb_open(d, out var h) != 0) return new Info("", "", -1);
        try
        {
            return Describe(d, h);
        }
        finally
        {
            libusb_close(h);
        }
    }).ToList());

    static Info Describe(IntPtr device, IntPtr handle)
    {
        libusb_get_device_descriptor(device, out var desc);
        string Text(byte index)
        {
            var buf = new byte[256];
            var n = index == 0 ? 0 : libusb_get_string_descriptor_ascii(handle, index, buf, buf.Length);
            return n > 0 ? Encoding.ASCII.GetString(buf, 0, n) : "";
        }
        var serial = Text(desc.iSerialNumber);
        var type = serial.StartsWith("0400") ? 0 : serial.StartsWith("0200") || serial.StartsWith("0201") ? 1 : -1;
        return new Info(serial, Text(desc.iProduct), type);
    }

    readonly IntPtr handle;
    readonly byte epIn = 0x81, epOut = 0x01;
    int closed;

    public Cp210x(int index)
    {
        Info info;
        (handle, info) = WithDevices(devices =>
        {
            if (index >= devices.Length) throw new IOException("USB error: Device not found");
            Check(libusb_open(devices[index], out var h), "open");
            return (h, Describe(devices[index], h));
        });
        try
        {
            Check(libusb_claim_interface(handle, Interface), "claim interface");
            // The bulk endpoints differ between CP210x parts, so they come from the configuration descriptor.
            var cfg = new byte[256];
            var n = libusb_control_transfer(handle, 0x80, 0x06, 0x0200, 0, cfg, (ushort)cfg.Length, 1000);
            for (var i = 0; i + 1 < n && cfg[i] > 0; i += cfg[i])
            {
                if (cfg[i + 1] != 5 || (cfg[i + 3] & 3) != 2) continue;
                if ((cfg[i + 2] & 0x80) != 0) epIn = cfg[i + 2];
                else epOut = cfg[i + 2];
            }
            // As the Windows bridge: 8N1 at 38400, or 115200 for a PPD from serial 20000001371 up.
            var baud = info.DeviceType == 1 && (!ulong.TryParse(info.Serial, out var serial) || serial >= 20000001371) ? 115200 : 38400;
            Control(0x00, 1, null, "enable UART");
            Control(0x1E, 0, BitConverter.GetBytes(baud), "set baud rate");
            Control(0x03, 0x0800, null, "set line control");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void Control(byte request, ushort value, byte[]? data, string what) =>
        Check(libusb_control_transfer(handle, 0x41, request, value, Interface, data, (ushort)(data?.Length ?? 0), 5000), what);

    static void Check(int code, string what)
    {
        if (code < 0) throw new IOException($"USB error: {what}: {Marshal.PtrToStringAnsi(libusb_strerror(code))}");
    }

    public byte[]? Read()
    {
        var buf = new byte[4096];
        var rc = libusb_bulk_transfer(handle, epIn, buf, buf.Length, out var n, 100);
        if (rc != Timeout) Check(rc, "read");
        return n > 0 ? buf[..n] : null;
    }

    public void Write(byte[] data)
    {
        for (var sent = 0; sent < data.Length;)
        {
            var chunk = data[sent..Math.Min(data.Length, sent + 4096)];
            Check(libusb_bulk_transfer(handle, epOut, chunk, chunk.Length, out var n, 5000), "write");
            sent += n;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        libusb_control_transfer(handle, 0x41, 0x00, 0, Interface, null, 0, 1000);
        libusb_release_interface(handle, Interface);
        libusb_close(handle);
    }
}

/// <summary>The nCoder on USB (HID 307E:0164): a message is a BEGIN report carrying its length, then 63-byte FRAGMENT reports.</summary>
public sealed class HidLocal : ILocal
{
    const byte Begin = 1, Fragment = 2;
    const int FragmentSize = 63;

    readonly HidStream stream;
    readonly int reportLength;

    public HidLocal(HidDevice device)
    {
        reportLength = device.GetMaxOutputReportLength();
        stream = device.Open();
        stream.ReadTimeout = System.Threading.Timeout.Infinite;
        stream.WriteTimeout = System.Threading.Timeout.Infinite;
    }

    public byte[]? Read()
    {
        var report = stream.Read();
        if (report.Length <= 3 || report[0] != 0 || report[1] != Begin) return null;
        var length = report[2] << 8 | report[3];
        var data = new List<byte>(length);
        for (var i = 0; i < length; i += FragmentSize)
        {
            report = stream.Read();
            if (report.Length <= 2 || report[0] != 0 || report[1] != Fragment) throw new IOException("Invalid HID fragment");
            data.AddRange(report.Skip(2).Take(Math.Min(length - i, FragmentSize)));
        }
        return data.ToArray();
    }

    public void Write(byte[] data)
    {
        Report([0, Begin, (byte)(data.Length >> 8), (byte)data.Length]);
        for (var i = 0; i < data.Length; i += FragmentSize)
            Report([0, Fragment, .. data.AsSpan(i, Math.Min(FragmentSize, data.Length - i))]);
    }

    // Windows pads every report to the device's report length; macOS would send it short.
    void Report(byte[] report)
    {
        if (report.Length < reportLength) Array.Resize(ref report, reportLength);
        stream.Write(report);
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>An encoder on a serial port (/dev/cu.*), opened as the Windows bridge opens COM ports: 38400 8N1, raw.
/// macOS raises DTR and RTS on open, which is what the Windows bridge sets by hand.</summary>
public sealed class SerialLocal : ILocal
{
    [DllImport("libc")] static extern int tcgetattr(SafeHandle fd, byte[] termios);
    [DllImport("libc")] static extern int tcsetattr(SafeHandle fd, int when, byte[] termios);
    [DllImport("libc")] static extern void cfmakeraw(byte[] termios);
    [DllImport("libc")] static extern int cfsetspeed(byte[] termios, nint speed);

    readonly FileStream stream;
    readonly byte[] buffer = new byte[4096];

    public SerialLocal(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0);
        try
        {
            // macOS's struct termios: four 8-byte flag words (c_cflag third), c_cc[20] at 32, two 8-byte speeds.
            var termios = new byte[128];
            if (tcgetattr(stream.SafeFileHandle, termios) != 0) throw new IOException(path + " is not a serial port");
            cfmakeraw(termios);
            cfsetspeed(termios, 38400);
            termios[17] |= 0x88; // CLOCAL | CREAD: ignore modem lines, receive
            termios[32 + 16] = 0; // VMIN: a read returns what has arrived...
            termios[32 + 17] = 1; // VTIME: ...or nothing after 100 ms
            if (tcsetattr(stream.SafeFileHandle, 0, termios) != 0) throw new IOException("Could not configure " + path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public byte[]? Read() => stream.Read(buffer, 0, buffer.Length) is > 0 and var n ? buffer[..n] : null;

    public void Write(byte[] data) => stream.Write(data, 0, data.Length);

    public void Dispose() => stream.Dispose();
}
