// The bridge itself: the REST service SPACE's web app looks for on localhost
// (GET /info, GET /ports, POST and DELETE /ports/<id>/pipe), answering as the Windows bridge 2.1.0 does.
using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaltoBridge;

public static class Server
{
    /// <summary>The version of SALTO's bridge this one stands in for; SPACE shows it.</summary>
    public const string BridgeVersion = "2.1.0.0";

    static Config config = new();
    static DateTime settingsWritten;
    static readonly object settingsGate = new();

    /// <summary>Takes the settings from disk when they have changed, so that `spacebridge allow` or `config` needs no restart.
    /// Only a file that reads whole replaces them: never the defaults, which let any website in.</summary>
    static void Settings()
    {
        lock (settingsGate)
        {
            var written = File.GetLastWriteTimeUtc(Paths.ConfigFile);
            if (written == settingsWritten) return;
            var first = settingsWritten == default;
            settingsWritten = written;
            if (first)
            {
                config = Config.Read() ?? Config.Nobody;
                if (config.Origins is ["(settings unreadable)"]) Log.Info("The settings cannot be read: no page may use the bridge. `spacebridge doctor` puts them right.");
                return;
            }
            // Gone or broken while running: the last ones stay.
            if (!File.Exists(Paths.ConfigFile) || Config.Read() is not { } read)
            {
                Log.Info("The settings are gone or cannot be read; the last ones stay. `spacebridge doctor` puts them right.");
                return;
            }
            var bluetooth = config.Bluetooth;
            config = read;
            Log.Info("Settings changed: " + Allowed() + $", Bluetooth {(config.Bluetooth ? "on" : "off")}, serial ports {(config.Serial ? "on" : "off")}");
            if (config.Bluetooth && !bluetooth) AskBluetooth();
        }
    }

    static string Allowed() => config.Origins.Count == 0 ? "any website" : "only " + string.Join(" ", config.Origins);

    /// <summary>Asks macOS for the Bluetooth permission now, not in the middle of SPACE's two-second search for the bridge.</summary>
    static void AskBluetooth() => Task.Run(() =>
    {
        var state = Ble.State(3000);
        Log.Info("Bluetooth: " + Ble.StateName(state));
        // Unknown while the question is open; say what came of it.
        if (state == Ble.Unknown) Log.Info("Bluetooth: " + Ble.StateName(Ble.State(600_000)));
    });

    public static int Run()
    {
        Settings();
        // The web app scans the first ten ports of every 110-port block from 50000.
        var ports = Enumerable.Range(0, 10).SelectMany(block => Enumerable.Range(50000 + 110 * block, 10)).Where(IsFree).Take(2).ToArray();
        if (ports.Length < 2)
        {
            Log.Info("No free port in the bridge's ranges (50000-50999).");
            return 1;
        }
        var certificate = Certificate.Renew();

        // The slim builder and plain request delegates: nothing here needs reflection, so the program compiles to native code.
        // The service's own folder is `/` under launchd, and ASP.NET watches its content root for a changed appsettings.json:
        // the whole disk, where every renamed folder costs a walk through it. There are no such settings to watch.
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelHttpsConfiguration();
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
                // Asked for at every connection: a renewed certificate is served without a restart.
                if (certificate is not null) kestrel.Listen(loopback, ports[1], listen => listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => Certificate.Current));
            }
        });
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            Settings();
            var headers = context.Response.Headers;
            var origin = context.Request.Headers.Origin.FirstOrDefault();
            if (!config.Allows(origin))
            {
                // No CORS headers: the page cannot even read this answer.
                Log.Info($"refused origin {Clean(origin!)}: `spacebridge allow {Clean(origin!)}` lets it in");
                context.Response.StatusCode = 403;
                await Send(context, new JsonObject { ["error"] = "External access forbidden", ["code"] = 20 });
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
                await Send(context, new JsonObject { ["error"] = error.Message, ["code"] = error.Code });
            }
            // Which page asked for what: the only trace of a browser that found the bridge but could not use it.
            // Without an Origin, /info is the spacebridge command looking whether the service answers: not worth a line each time.
            if (origin is not null || context.Request.Path != "/info")
                Log.Info($"{context.Request.Scheme} {context.Request.Method} {Clean(context.Request.Path)} -> {context.Response.StatusCode} from {Clean(origin ?? "-")}");
        });

        app.MapGet("/info", context =>
        {
            var bluetooth = config.Bluetooth ? Ble.State() : Ble.Unsupported;
            return Send(context, new JsonObject
            {
                ["version"] = BridgeVersion,
                ["timezone"] = WindowsTimeZone.Serialized.Value,
                ["machinename"] = Environment.MachineName,
                ["hasBluetoothAdapter"] = bluetooth is not (Ble.Unsupported or Ble.Unknown),
                ["isBluetoothEnabled"] = bluetooth == Ble.PoweredOn,
                ["isBLESupported"] = bluetooth is not (Ble.Unsupported or Ble.Unknown),
            });
        });

        // type: 0 USB, 1 serial, 2 Bluetooth. As on Windows, Bluetooth devices are only looked for when asked for (a 15 s scan).
        app.MapGet("/ports", context =>
        {
            int? type = Number(context, "type"), deviceType = Number(context, "device_type");
            return Send(context, new JsonArray((type == 2 ? Bluetooth(null) : Discovery.All(config.Serial))
                .Where(p => type is null or < 0 || p.Type == type)
                .Where(p => deviceType is null or < 0 || p.DeviceType == deviceType)
                .Select(Data).ToArray()));
        });

        app.MapGet("/ports/{id}", context => Send(context, Data(Find(Id(context), Number(context, "type")))));

        app.MapPost("/ports/{id}/pipe", async context =>
        {
            var request = context.Request;
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
            var port = Find(Id(context), Number(context, "type"));
            await Pipe.Create(port, server, token, timeout, forceTrusted);
            await Send(context, Data(port));
        });

        app.MapDelete("/ports/{id}/pipe", async context =>
        {
            var token = Text(await Body(context.Request), "token") ?? throw new RestError(400, 50, "The following parameter is missing: token");
            // A Bluetooth pipe is addressed by the device id; closing must not wait for the device to advertise again.
            var port = Number(context, "type") == 2 ? BluetoothPort(Id(context), "") : Find(Id(context), Number(context, "type"));
            Pipe.Delete(port.Id, token);
            await Send(context, Data(port));
        });

        app.MapFallback(_ => throw new RestError(404, 21, "Endpoint not found"));

        app.Lifetime.ApplicationStopping.Register(Pipe.CloseAll);
        Directory.CreateDirectory(Paths.Folder);
        File.WriteAllText(Paths.StateFile, new JsonObject { ["pid"] = Environment.ProcessId, ["http"] = ports[0], ["https"] = certificate is null ? 0 : ports[1], ["version"] = Cli.Version }.ToJsonString());
        Log.Info($"Spacebridge {Cli.Version}: http://localhost:{ports[0]}" + (certificate is null ? "" : $", https://localhost:{ports[1]}")
            + ", " + Allowed() + (SignKey.Present ? "" : ", NO SIGNING KEY"));
        if (config.Bluetooth) AskBluetooth();
        // The certificate lasts 90 days and is replaced with 30 to go; a Mac that slept through that gets a new one on waking.
        using var renewal = new Timer(_ => Certificate.Renew(), null, TimeSpan.FromHours(12), TimeSpan.FromHours(12));
        // A new release installs itself; the first look waits until the bridge has settled.
        using var updates = new Timer(_ =>
        {
            Settings();
            Update.Automatic(config.AutoUpdate);
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(6));
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

    static JsonNode Data(Port p) => new JsonObject { ["id"] = p.Id, ["type"] = p.Type, ["device_type"] = p.DeviceType, ["name"] = p.Name, ["info"] = p.Info, ["pipe"] = Pipe.IsOpen(p.Id) };

    static Task Send(HttpContext context, JsonNode body)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(body.ToJsonString());
    }

    static string Id(HttpContext context) => (string)context.Request.RouteValues["id"]!;

    static int? Number(HttpContext context, string name) => int.TryParse(context.Request.Query[name], out var value) ? value : null;

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

/// <summary>https://localhost without a password every time the certificate runs out: this Mac gets its own small authority,
/// trusted once by `spacebridge install`, and the service issues itself short-lived certificates from it.</summary>
public static class Certificate
{
    static readonly string authorityPfx = Path.Combine(Paths.Folder, "authority.pfx");
    static readonly string leafPfx = Path.Combine(Paths.Folder, "localhost.pfx");
    static readonly object gate = new();

    /// <summary>What the https port presents.</summary>
    public static X509Certificate2? Current { get; private set; }

    /// <summary>The certificate for https://localhost, issued anew when it has less than 30 days left. Null leaves the bridge on http only.</summary>
    public static X509Certificate2? Renew(int days = 90)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.Folder);
                var authority = Authority();
                if (File.Exists(leafPfx))
                {
                    var saved = X509CertificateLoader.LoadPkcs12FromFile(leafPfx, null);
                    if (saved.NotAfter > DateTime.Now.AddDays(30) && saved.IssuerName.RawData.AsSpan().SequenceEqual(authority.SubjectName.RawData)) return Current ??= saved;
                    // Versions up to 1.1 trusted a self-signed certificate directly; `spacebridge install` takes that trust back.
                    if (saved.Subject == saved.Issuer && File.Exists(Paths.CertificateFile)) File.Move(Paths.CertificateFile, Paths.PreviousCertificateFile, true);
                }

                using var whole = Issue(authority, "localhost", days);
                Save(leafPfx, whole.Export(X509ContentType.Pfx));
                File.WriteAllText(Paths.CertificateFile, whole.ExportCertificatePem());
                Log.Info($"New https certificate, good until {whole.NotAfter:yyyy-MM-dd}.");
                return Current = X509CertificateLoader.LoadPkcs12FromFile(leafPfx, null);
            }
            catch (Exception e)
            {
                Log.Info("No https certificate, the bridge answers on http only: " + e.Message);
                return Current;
            }
        }
    }

    /// <summary>A certificate for a host name (and, for localhost, its addresses), signed by the authority.</summary>
    public static X509Certificate2 Issue(X509Certificate2 authority, string host, int days)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        if (host == "localhost")
        {
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
        }
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(authority, DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(days), serial);
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>This Mac's own authority: it may only vouch for localhost, and only its certificate file ever leaves this folder (into the keychain).</summary>
    static X509Certificate2 Authority()
    {
        if (File.Exists(authorityPfx) && X509CertificateLoader.LoadPkcs12FromFile(authorityPfx, null) is { } saved && saved.NotAfter > DateTime.Now.AddDays(90)) return saved;

        using var created = NewAuthority();
        Save(authorityPfx, created.Export(X509ContentType.Pfx));
        File.WriteAllText(Paths.AuthorityFile, created.ExportCertificatePem());
        Log.Info("New certificate authority for https://localhost; `spacebridge install` trusts it.");
        return X509CertificateLoader.LoadPkcs12FromFile(authorityPfx, null);
    }

    public static X509Certificate2 NewAuthority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Spacebridge localhost authority ({Environment.MachineName})", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(LocalhostOnly());
        return request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
    }

    /// <summary>Name constraints: whoever got hold of the authority's key still could not pass for any site but localhost.</summary>
    static X509Extension LocalhostOnly()
    {
        var name = new Asn1Tag(TagClass.ContextSpecific, 2);
        var address = new Asn1Tag(TagClass.ContextSpecific, 7);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            using (writer.PushSequence()) writer.WriteCharacterString(UniversalTagNumber.IA5String, "localhost", name);
            // An address and its mask: exactly 127.0.0.1 and ::1.
            using (writer.PushSequence()) writer.WriteOctetString([127, 0, 0, 1, 255, 255, 255, 255], address);
            using (writer.PushSequence()) writer.WriteOctetString([.. IPAddress.IPv6Loopback.GetAddressBytes(), .. Enumerable.Repeat((byte)255, 16)], address);
        }
        return new X509Extension("2.5.29.30", writer.Encode(), true);
    }

    static void Save(string path, byte[] secret)
    {
        File.WriteAllBytes(path, secret);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
