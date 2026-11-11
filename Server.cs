// The bridge itself: the REST service SPACE's web app looks for on localhost
// (GET /info, GET /ports, POST and DELETE /ports/<id>/pipe), answering as the Windows bridge 2.1.0 does.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SaltoBridge;

public static class Server
{
    /// <summary>The version of SALTO's bridge this one stands in for; SPACE shows it.</summary>
    public const string BridgeVersion = "2.1.0.0";

    static readonly JsonSerializerOptions json = new() { PropertyNamingPolicy = null };
    static Config config = new();

    public static int Run()
    {
        config = Config.Load();
        // The web app scans the first ten ports of every 110-port block from 50000.
        var ports = Enumerable.Range(0, 10).SelectMany(block => Enumerable.Range(50000 + 110 * block, 10)).Where(IsFree).Take(2).ToArray();
        if (ports.Length < 2)
        {
            Log.Info("No free port in the bridge's ranges (50000-50999).");
            return 1;
        }
        var certificate = Certificate.LoadOrCreate();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // A browser that does not trust the https certificate drops the connection before any request: only this shows it.
        builder.Logging.AddSimpleConsole(console => console.SingleLine = true).AddFilter("Microsoft.AspNetCore.Server.Kestrel.Https", LogLevel.Debug);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // HTTP/1.1 only, as the Windows bridge: HTTP/2 logged a failed GOAWAY write for every closed connection.
            kestrel.ConfigureEndpointDefaults(listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
            foreach (var loopback in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                kestrel.Listen(loopback, ports[0]);
                if (certificate is not null) kestrel.Listen(loopback, ports[1], listen => listen.UseHttps(certificate));
            }
        });
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            var origin = context.Request.Headers.Origin.FirstOrDefault();
            if (!config.Allows(origin))
            {
                // No CORS headers: the page cannot even read this answer.
                Log.Info($"refused origin {Clean(origin!)}: `spacebridge allow {Clean(origin!)}` lets it in");
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "External access forbidden", code = 20 }, json);
                return;
            }
            headers.AccessControlAllowOrigin = origin ?? "*";
            headers.AccessControlAllowCredentials = "true";
            headers.AccessControlAllowMethods = "POST,GET,PUT,DELETE,OPTIONS";
            headers.AccessControlAllowHeaders = "Origin, X-Requested-With, Content-Type, Accept, Cache-Control, Authorization, Client-Security-Token, Accept-Encoding, *";
            // Chrome asks for this before a page on the network may call localhost.
            headers["Access-Control-Allow-Private-Network"] = "true";
            try
            {
                // Only a browser on this Mac addressing localhost itself: a foreign Host header is DNS rebinding.
                if (context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]")) throw new RestError(403, 20, "External access forbidden");
                if (context.Request.Method != "OPTIONS") await next();
            }
            catch (Exception e)
            {
                var error = e as RestError ?? new RestError(500, 10, e.Message);
                if (error.Status == 500) Log.Info($"{context.Request.Method} {context.Request.Path}: {e.Message}");
                context.Response.StatusCode = error.Status;
                await context.Response.WriteAsJsonAsync(new { error = error.Message, code = error.Code }, json);
            }
            // Which page asked for what: the only trace of a browser that found the bridge but could not use it.
            // Without an Origin, /info is the spacebridge command looking whether the service answers: not worth a line each time.
            if (origin is not null || context.Request.Path != "/info")
                Log.Info($"{context.Request.Scheme} {context.Request.Method} {Clean(context.Request.Path)} -> {context.Response.StatusCode} from {Clean(origin ?? "-")}");
        });

        app.MapGet("/info", () =>
        {
            var bluetooth = config.Bluetooth ? Ble.State() : Ble.Unsupported;
            return Results.Json(new
            {
                version = BridgeVersion,
                timezone = WindowsTimeZone.Serialized.Value,
                machinename = Environment.MachineName,
                hasBluetoothAdapter = bluetooth is not (Ble.Unsupported or Ble.Unknown),
                isBluetoothEnabled = bluetooth == Ble.PoweredOn,
                isBLESupported = bluetooth is not (Ble.Unsupported or Ble.Unknown),
            }, json);
        });

        // type: 0 USB, 1 serial, 2 Bluetooth. As on Windows, Bluetooth devices are only looked for when asked for (a 15 s scan).
        app.MapGet("/ports", (int? type, int? device_type) => Results.Json(
            (type == 2 ? Bluetooth(null) : Discovery.All(config.Serial))
                .Where(p => type is null or < 0 || p.Type == type)
                .Where(p => device_type is null or < 0 || p.DeviceType == device_type)
                .Select(Data), json));

        app.MapGet("/ports/{id}", (string id, int? type) => Results.Json(Data(Find(id, type)), json));

        app.MapPost("/ports/{id}/pipe", async (string id, int? type, HttpRequest request) =>
        {
            var body = await Body(request);
            var token = Text(body, "token") ?? throw new RestError(400, 50, "The following parameter is missing: token");
            var server = Text(body, "server") ?? throw new RestError(400, 50, "The following parameter is missing: server");
            var options = body.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
            var timeout = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("timeout", out var t) && t.TryGetInt32(out var seconds) ? seconds : 0;
            var forceTrusted = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("forceTrusted", out var f) && f.ValueKind == JsonValueKind.True;
            // With a list of allowed SPACE addresses, the pipe may only lead to the page's own server (where the web app always points it).
            if (config.Origins.Count > 0 && request.Headers.Origin.FirstOrDefault() is { } origin
                && !(Uri.TryCreate(server, UriKind.Absolute, out var target) && target.Host.Equals(new Uri(origin).Host, StringComparison.OrdinalIgnoreCase)))
                throw new RestError(403, 20, "The pipe's server is not the page's server");
            var port = Find(id, type);
            await Pipe.Create(port, server, token, timeout, forceTrusted);
            return Results.Json(Data(port), json);
        });

        app.MapDelete("/ports/{id}/pipe", async (string id, int? type, HttpRequest request) =>
        {
            var token = Text(await Body(request), "token") ?? throw new RestError(400, 50, "The following parameter is missing: token");
            // A Bluetooth pipe is addressed by the device id; closing must not wait for the device to advertise again.
            var port = type == 2 ? BluetoothPort(id, "") : Find(id, type);
            Pipe.Delete(port.Id, token);
            return Results.Json(Data(port), json);
        });

        app.MapFallback(() => { throw new RestError(404, 21, "Endpoint not found"); });

        app.Lifetime.ApplicationStopping.Register(Pipe.CloseAll);
        Directory.CreateDirectory(Paths.Folder);
        File.WriteAllText(Paths.StateFile, JsonSerializer.Serialize(new { pid = Environment.ProcessId, http = ports[0], https = certificate is null ? 0 : ports[1], version = Cli.Version }));
        Log.Info($"Spacebridge {Cli.Version}: http://localhost:{ports[0]}" + (certificate is null ? "" : $", https://localhost:{ports[1]}")
            + (config.Origins.Count == 0 ? ", any website" : ", only " + string.Join(" ", config.Origins)) + (SignKey.Present ? "" : ", NO SIGNING KEY"));
        // Asks macOS for the Bluetooth permission now, not in the middle of SPACE's two-second search for the bridge.
        if (config.Bluetooth) _ = Task.Run(() =>
        {
            var state = Ble.State(3000);
            Log.Info("Bluetooth: " + Ble.StateName(state));
            // Unknown while the question is open; say what came of it.
            if (state == Ble.Unknown) Log.Info("Bluetooth: " + Ble.StateName(Ble.State(600_000)));
        });
        app.Run();
        return 0;
    }

    static string Clean(string text) => new(text.Select(c => char.IsControl(c) ? '?' : c).Take(120).ToArray());

    static Port BluetoothPort(string deviceId, string peripheral) =>
        new(Discovery.Id(deviceId, "Bluetooth"), 3, "ble-" + deviceId, deviceId, () => new BleLocal(peripheral), 2);

    // deviceId null: every SALTO device heard in 15 s; otherwise as soon as that one is heard.
    static List<Port> Bluetooth(string? deviceId) =>
        config.Bluetooth ? Ble.Discover(deviceId).Select(d => BluetoothPort(d.DeviceId, d.Peripheral)).ToList() : [];

    static bool IsFree(int port)
    {
        foreach (var loopback in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try
            {
                using var probe = new TcpListener(loopback, port);
                probe.Start();
            }
            catch (SocketException)
            {
                return false;
            }
        }
        return true;
    }

    static Port Find(string id, int? type) =>
        (type == 2 ? Bluetooth(id).FirstOrDefault() : Discovery.All(config.Serial).FirstOrDefault(p => p.Id == id)) ?? throw new RestError(404, 30, "Selected port could not be found");

    static object Data(Port p) => new { id = p.Id, type = p.Type, device_type = p.DeviceType, name = p.Name, info = p.Info, pipe = Pipe.IsOpen(p.Id) };

    static async Task<JsonElement> Body(HttpRequest request)
    {
        try
        {
            return (await JsonDocument.ParseAsync(request.Body)).RootElement;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

}

/// <summary>An error in the Windows bridge's shape: HTTP status plus { error, code }.</summary>
public sealed class RestError(int status, int code, string message) : Exception(message)
{
    public int Status => status;
    public int Code => code;
}

public static class Log
{
    static readonly HashSet<string> said = [];

    public static void Info(string message) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");

    public static void Once(string message)
    {
        lock (said)
        {
            if (said.Add(message)) Info(message);
        }
    }
}

public static class Certificate
{
    /// <summary>This Mac's own certificate for https://localhost (`spacebridge install` trusts localhost.crt). Null leaves the bridge on http only.</summary>
    public static X509Certificate2? LoadOrCreate()
    {
        try
        {
            var pfx = Path.Combine(Paths.Folder, "localhost.pfx");
            if (File.Exists(pfx) && X509CertificateLoader.LoadPkcs12FromFile(pfx, null) is { } saved && saved.NotAfter > DateTime.Now) return saved;

            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            // macOS refuses TLS server certificates valid for more than 825 days.
            using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(820));

            Directory.CreateDirectory(Paths.Folder);
            File.WriteAllBytes(pfx, created.Export(X509ContentType.Pfx));
            File.SetUnixFileMode(pfx, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(Paths.CertificateFile, created.ExportCertificatePem());
            Log.Info("New https certificate; `spacebridge install` trusts it.");
            return X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
        }
        catch (Exception e)
        {
            Log.Info("HTTPS server not started: " + e.Message);
            return null;
        }
    }
}

public static class WindowsTimeZone
{
    /// <summary>The web app hands /info's timezone to SPACE, which reads it with .NET Framework's
    /// TimeZoneInfo.FromSerializedString: so a Windows id and one floating daylight rule, as Windows
    /// reports it, not macOS's rule per year.</summary>
    public static readonly Lazy<string> Serialized = new(() =>
    {
        try
        {
            return Serialize(TimeZoneInfo.Local, DateTime.UtcNow.Year);
        }
        catch (Exception e)
        {
            Log.Info("Local time zone is not available: " + e.Message);
            return "";
        }
    });

    public static string Serialize(TimeZoneInfo zone, int year)
    {
        var id = TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) ? windows : zone.Id;
        TimeZoneInfo.TransitionTime? start = null, end = null;
        var delta = TimeSpan.Zero;
        var previous = zone.GetUtcOffset(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        for (var utc = new DateTime(year, 1, 1, 1, 0, 0, DateTimeKind.Utc); utc.Year == year; utc = utc.AddHours(1))
        {
            var offset = zone.GetUtcOffset(utc);
            if (offset == previous) continue;
            var wall = utc + previous;
            var week = wall.Day + 7 > DateTime.DaysInMonth(year, wall.Month) ? 5 : (wall.Day - 1) / 7 + 1;
            var transition = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, wall.Hour, wall.Minute, 0), wall.Month, week, wall.DayOfWeek);
            if (offset > previous) (start, delta) = (transition, offset - previous);
            else end = transition;
            previous = offset;
        }
        var custom = start is null || end is null
            ? TimeZoneInfo.CreateCustomTimeZone(id, zone.BaseUtcOffset, zone.DisplayName, zone.StandardName)
            : TimeZoneInfo.CreateCustomTimeZone(id, zone.BaseUtcOffset, zone.DisplayName, zone.StandardName, zone.DaylightName,
                [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, delta, start.Value, end.Value)]);
        return custom.ToSerializedString();
    }
}
