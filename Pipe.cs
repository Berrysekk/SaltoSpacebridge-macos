using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace SaltoBridge;

/// <summary>One device piped to SPACE: the bridge dials SPACE's WebSocket itself and relays raw bytes both ways.</summary>
public sealed class Pipe
{
    const int OpenTimeoutMs = 10_000, DefaultIdleMs = 60_000;

    static readonly Dictionary<string, Pipe> pipes = [];

    public static bool IsOpen(string id)
    {
        lock (pipes) return pipes.TryGetValue(id, out var p) && p.open;
    }

    public static async Task Create(Port port, string server, string token, int timeoutSec, bool forceTrusted)
    {
        Pipe pipe;
        lock (pipes)
        {
            if (pipes.TryGetValue(port.Id, out var old) && old.open) throw new RestError(409, 40, "Selected port is already in use");
            pipes[port.Id] = pipe = new Pipe(port.Id, token, timeoutSec > 0 ? timeoutSec * 1000 : DefaultIdleMs);
        }
        try
        {
            await pipe.Open(port.Open, Address(server, token, forceTrusted), forceTrusted);
        }
        catch
        {
            pipe.Close();
            throw;
        }
    }

    public static void Delete(string id, string token)
    {
        Pipe? pipe;
        lock (pipes) pipes.TryGetValue(id, out pipe);
        if (pipe is null) return;
        if (pipe.open && pipe.token != token) throw new RestError(400, 60, "Token does not match with the selected opened port");
        pipe.Close();
    }

    public static void CloseAll()
    {
        Pipe[] all;
        lock (pipes) all = [.. pipes.Values];
        foreach (var p in all) p.Close();
    }

    /// <summary>SPACE's address with the signed token as the last path segment: base64("token:HEX(HMAC-SHA1(token))").</summary>
    public static Uri Address(string server, string token, bool forceTrusted)
    {
        var uri = new UriBuilder(new Uri(server));
        // SPACE on this very machine is reached over loopback, as the Windows bridge does.
        if (!forceTrusted && IsThisMachine(uri.Host)) uri.Host = "127.0.0.1";
        var signature = Convert.ToHexString(HMACSHA1.HashData(SignConsts.KeyData, Encoding.UTF8.GetBytes(token)));
        uri.Path = uri.Path.TrimEnd('/') + "/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(token.TrimStart('/') + ":" + signature));
        if (uri.Port < 1) throw new ArgumentException("Wrong websocket port number");
        if (string.IsNullOrEmpty(uri.Host)) throw new ArgumentException("Wrong websocket hostname");
        return uri.Uri;
    }

    static bool IsThisMachine(string host)
    {
        try
        {
            var own = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToHashSet();
            return Dns.GetHostAddresses(host).Any(a => IPAddress.IsLoopback(a) || own.Contains(a));
        }
        catch
        {
            return false;
        }
    }

    readonly string id, token;
    readonly int idleMs;
    readonly ClientWebSocket socket = new();
    readonly Timer idle;
    ILocal? local;
    volatile bool open;
    int closed;

    Pipe(string id, string token, int idleMs)
    {
        (this.id, this.token, this.idleMs) = (id, token, idleMs);
        idle = new Timer(_ => Close("idle timeout"));
    }

    async Task Open(Func<ILocal> openLocal, Uri address, bool forceTrusted)
    {
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        // Unless the web app forces trust, SPACE's self-signed certificate is accepted, as on Windows.
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, errors) => !forceTrusted || errors == SslPolicyErrors.None;
        using var timeout = new CancellationTokenSource(OpenTimeoutMs);
        var device = Task.Run(openLocal);
        try
        {
            await Task.WhenAll(device.WaitAsync(timeout.Token), socket.ConnectAsync(address, timeout.Token));
        }
        catch (Exception e)
        {
            // A device that opens after the pipe gave up must not stay claimed.
            _ = device.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            throw e is OperationCanceledException ? new TimeoutException("Timeout") : e;
        }
        local = device.Result;
        open = true;
        KeepAlive();
        Log.Info($"pipe {id} open to {address.Host}:{address.Port}");
        new Thread(FromDevice) { IsBackground = true, Name = "pipe " + id }.Start();
        _ = FromSpace();
    }

    void KeepAlive() => idle.Change(idleMs, Timeout.Infinite);

    void FromDevice()
    {
        try
        {
            while (open)
            {
                if (local!.Read() is not { } data) continue;
                KeepAlive();
                socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception e)
        {
            Close("device: " + e.Message);
        }
    }

    async Task FromSpace()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (part.MessageType == WebSocketMessageType.Close)
                    {
                        Close("SPACE closed the connection");
                        return;
                    }
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                if (part.MessageType != WebSocketMessageType.Binary) continue;
                KeepAlive();
                local!.Write(message.ToArray());
            }
        }
        catch (Exception e)
        {
            Close("SPACE: " + e.Message);
        }
    }

    void Close(string why = "closed")
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        var wasOpen = open;
        open = false;
        lock (pipes)
        {
            if (pipes.TryGetValue(id, out var current) && current == this) pipes.Remove(id);
        }
        idle.Dispose();
        try { local?.Dispose(); } catch (Exception e) { Log.Info("closing device: " + e.Message); }
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(2000);
                socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).GetAwaiter().GetResult();
            }
        }
        catch { /* SPACE is already gone */ }
        socket.Dispose();
        if (wasOpen) Log.Info($"pipe {id} closed ({why})");
    }
}
