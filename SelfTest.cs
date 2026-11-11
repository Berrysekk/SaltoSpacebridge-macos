using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SaltoBridge;

/// <summary>`spacebridge selftest`: the parts SPACE depends on, against a fake device and a fake SPACE.</summary>
public static class SelfTest
{
    sealed class FakeDevice : ILocal
    {
        public readonly BlockingCollection<byte[]> Incoming = [], Written = [];
        public bool Disposed;

        public byte[]? Read() => Disposed ? throw new IOException("closed") : Incoming.TryTake(out var data, 50) ? data : null;
        public void Write(byte[] data) => Written.Add(data);
        public void Dispose() => Disposed = true;
    }

    [DllImport("libc")] static extern int openpty(out int master, out int slave, byte[] name, nint termios, nint window);

    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("FAILED: " + what);
        Console.WriteLine("ok  " + what);
    }

    public static async Task<int> Run()
    {
        try
        {
            // The real key stays out of it: what is checked is the signing, not the key.
            SignKey.Override = "selftest"u8.ToArray();
            // Port ids as the Windows bridge 2.1.0 encodes them (its own Base62Encoding, run on the same input).
            Check(Discovery.Id("0") == "LNDYBJ0" && Discovery.Id("04A3F2B19C5E80") == "LNDYBJ0qGJD6Ca8nEKCrHJWm0", "port ids match the Windows bridge");

            var prague = WindowsTimeZone.Serialize(TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"), 2026);
            Check(prague.StartsWith("Central Europe Standard Time;60;") && prague.EndsWith(";[01:01:0001;12:31:9999;60;[0;02:00:00;3;5;0;];[0;03:00:00;10;5;0;];];"), "timezone is a Windows one: " + prague);
            Check(TimeZoneInfo.FromSerializedString(prague).IsDaylightSavingTime(new DateTime(2026, 7, 1)), "timezone parses back");

            // A fake SPACE: takes the bridge's WebSocket, sends three bytes, reports what it got.
            var builder = WebApplication.CreateBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            await using var space = builder.Build();
            space.UseWebSockets();
            string? path = null;
            var received = new TaskCompletionSource<byte[]>();
            space.Run(async context =>
            {
                path = context.Request.Path;
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, default);
                var buffer = new byte[100];
                var message = await socket.ReceiveAsync(buffer, default);
                received.TrySetResult(buffer[..message.Count]);
                await socket.ReceiveAsync(buffer, default);
            });
            await space.StartAsync();
            var server = $"ws://127.0.0.1:{new Uri(space.Urls.First()).Port}/localiobridge/";

            var device = new FakeDevice();
            var port = new Port(Discovery.Id("0"), 0, "usb0", "test", () => device);
            const string token = "/tok+en";
            await Pipe.Create(port, server, token, 1, false);
            Check(Pipe.IsOpen(port.Id), "pipe opens");

            var signature = Convert.ToHexString(HMACSHA1.HashData(SignKey.Data, Encoding.UTF8.GetBytes(token)));
            Check(signature.Length == 40 && path == "/localiobridge/" + Convert.ToBase64String(Encoding.UTF8.GetBytes("tok+en:" + signature)), "SPACE gets the signed token: " + path);
            Check(device.Written.TryTake(out var down, 2000) && down.SequenceEqual(new byte[] { 1, 2, 3 }), "SPACE -> device");
            device.Incoming.Add([9, 8]);
            Check((await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).SequenceEqual(new byte[] { 9, 8 }), "device -> SPACE");

            Check(await Fails(() => Pipe.Create(port, server, token, 1, false)) is { Status: 409, Code: 40 }, "a second pipe on the port is refused");
            Check(await Fails(() => { Pipe.Delete(port.Id, "other"); return Task.CompletedTask; }) is { Status: 400, Code: 60 }, "closing needs the pipe's token");

            await Task.Delay(1600);
            Check(!Pipe.IsOpen(port.Id) && device.Disposed, "an idle pipe closes and frees the device");

            var gone = new FakeDevice();
            Check(await Fails(() => Pipe.Create(port with { Open = () => gone }, "ws://127.0.0.1:9/x", token, 1, false)) is not null && SpinWait.SpinUntil(() => gone.Disposed, 2000) && !Pipe.IsOpen(port.Id), "unreachable SPACE fails and frees the device");

            // The serial path against a pseudo terminal standing in for the adapter.
            var name = new byte[128];
            Check(openpty(out var master, out _, name, 0, 0) == 0, "pseudo terminal");
            using (var adapter = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(master, true), FileAccess.ReadWrite, 0))
            using (var serial = new SerialLocal(Encoding.ASCII.GetString(name).TrimEnd('\0')))
            {
                serial.Write([5, 6]);
                var got = new byte[2];
                adapter.ReadExactly(got);
                adapter.Write([7]);
                Check(got.SequenceEqual(new byte[] { 5, 6 }) && serial.Read() is [7] && serial.Read() is null, "serial port: both directions, and an empty read returns");
            }

            Check(Config.Origin("HTTPS://Space.Example:8100/ProAccessSpace/") == "https://space.example:8100" && Config.Origin("http://10.0.0.5:8100") == "http://10.0.0.5:8100", "SPACE addresses become origins");
            var listed = new Config { Origins = ["https://space.example:8100"] };
            Check(listed.Allows("https://space.example:8100") && !listed.Allows("https://evil.example") && listed.Allows(null) && new Config().Allows("https://evil.example"), "the allow-list lets in only listed pages");

            // Bluetooth: SALTO's advertisement and its packet framing (a device is needed for the rest).
            Check(Ble.SaltoDevice([0x99, 0x01, 1, 0, 0x80, 1, 2, 3, 4, 5, 6, 0xAB]) == "010203040506AB" && Ble.SaltoDevice([0x99, 0x01, 1, 0, 0x00, 1, 2, 3, 4, 5, 6, 7]) is null
                && Ble.SaltoDevice([0x4C, 0x00, 1, 0, 0x80, 1, 2, 3, 4, 5, 6, 7]) is null, "Bluetooth advertisement: a ready SALTO device and its id");
            var packets = new List<byte[]>();
            var sender = new LowPan(packets.Add, 23);
            var receiver = new LowPan(_ => { }, 23);
            var longMessage = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
            sender.Send([1, 2, 3]);
            sender.Send(longMessage);
            sender.Send(longMessage);
            var composed = packets.Select(receiver.Compose).Where(m => m is not null).ToList();
            Check(packets.All(p => p.Length <= 23) && packets.Count > 10 && composed.Count == 3 && composed[0]!.SequenceEqual(new byte[] { 1, 2, 3 })
                && composed[1]!.SequenceEqual(longMessage) && composed[2]!.SequenceEqual(longMessage), "Bluetooth framing: short and fragmented messages");

            // With an encoder, PPD or nCoder on USB: that it can be claimed (nothing is sent to it).
            var attached = Discovery.All().Where(p => p.Type == 0).ToList();
            foreach (var p in attached)
            {
                p.Open().Dispose();
                Console.WriteLine($"ok  {p.Name} opens: {p.Info}");
            }
            if (attached.Count == 0) Console.WriteLine("--  no SALTO USB device attached: opening one was not checked");
            Console.WriteLine("selftest passed");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            return 1;
        }
    }

    static async Task<RestError?> Fails(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception e)
        {
            return e as RestError ?? new RestError(500, 10, e.Message);
        }
    }
}
