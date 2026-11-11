// SALTO Local IO Bridge for macOS: the REST service SPACE's web app looks for on localhost
// (GET /info, GET /ports, POST and DELETE /ports/<id>/pipe), answering as the Windows bridge 2.1.0 does.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SaltoBridge;

if (args.Contains("--selftest")) return await SelfTest.Run();

// The web app scans the first ten ports of every 110-port block from 50000.
var ports = Enumerable.Range(0, 10).SelectMany(block => Enumerable.Range(50000 + 110 * block, 10)).Where(IsFree).Take(2).ToArray();
if (ports.Length < 2)
{
    Log.Info("No free port in the bridge's ranges (50000-50999).");
    return 1;
}
var certificate = Certificate.LoadOrCreate();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// A browser that does not trust the https certificate drops the connection before any request: only this shows it.
builder.Logging.AddSimpleConsole(console => console.SingleLine = true).AddFilter("Microsoft.AspNetCore.Server.Kestrel.Https", LogLevel.Debug);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    foreach (var loopback in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
    {
        kestrel.Listen(loopback, ports[0]);
        if (certificate is not null) kestrel.Listen(loopback, ports[1], listen => listen.UseHttps(certificate));
    }
});
var app = builder.Build();
var json = new JsonSerializerOptions { PropertyNamingPolicy = null };

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.AccessControlAllowOrigin = context.Request.Headers.Origin.FirstOrDefault() ?? "*";
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
    var path = new string(context.Request.Path.ToString().Select(c => char.IsControl(c) ? '?' : c).Take(120).ToArray());
    Log.Info($"{context.Request.Scheme} {context.Request.Method} {path} -> {context.Response.StatusCode} from {context.Request.Headers.Origin.FirstOrDefault() ?? "-"}");
});

app.MapGet("/info", () => Results.Json(new
{
    version = "2.1.0.0",
    timezone = WindowsTimeZone.Serialized.Value,
    machinename = Environment.MachineName,
    hasBluetoothAdapter = false,
    isBluetoothEnabled = false,
    isBLESupported = false,
}, json));

// type: 0 USB, 1 serial; 2 is Bluetooth, which this bridge does not have.
app.MapGet("/ports", (int? type, int? device_type) => Results.Json(
    Discovery.All()
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
    var port = Find(id, type);
    await Pipe.Create(port, server, token, timeout, forceTrusted);
    return Results.Json(Data(port), json);
});

app.MapDelete("/ports/{id}/pipe", async (string id, int? type, HttpRequest request) =>
{
    var token = Text(await Body(request), "token") ?? throw new RestError(400, 50, "The following parameter is missing: token");
    var port = Find(id, type);
    Pipe.Delete(id, token);
    return Results.Json(Data(port), json);
});

app.MapFallback(() => { throw new RestError(404, 21, "Endpoint not found"); });

app.Lifetime.ApplicationStopping.Register(Pipe.CloseAll);
Log.Info($"SALTO Local IO Bridge for macOS: http://localhost:{ports[0]}" + (certificate is null ? "" : $", https://localhost:{ports[1]}"));
app.Run();
return 0;

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
    (type == 2 ? null : Discovery.All().FirstOrDefault(p => p.Id == id)) ?? throw new RestError(404, 30, "Selected port could not be found");

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

namespace SaltoBridge
{
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
        public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/SaltoLocalIOBridge");

        /// <summary>This Mac's own certificate for https://localhost (install.sh trusts localhost.crt). Null leaves the bridge on http only.</summary>
        public static X509Certificate2? LoadOrCreate()
        {
            try
            {
                var pfx = Path.Combine(Folder, "localhost.pfx");
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

                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(pfx, created.Export(X509ContentType.Pfx));
                File.SetUnixFileMode(pfx, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.WriteAllText(Path.Combine(Folder, "localhost.crt"), created.ExportCertificatePem());
                Log.Info("New https certificate; run install.sh again to trust it.");
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
}
