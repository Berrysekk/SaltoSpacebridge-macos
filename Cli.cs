using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SaltoBridge;

/// <summary>The `spacebridge` command: sets the bridge up, shows what it is doing and holds its settings.</summary>
public static partial class Cli
{
    public static readonly string Version =
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    [DllImport("libc")] static extern uint getuid();

    static readonly bool color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
    static string Paint(string code, string text) => color ? $"\e[{code}m{text}\e[0m" : text;
    static string Bold(string s) => Paint("1", s);
    static string Dim(string s) => Paint("2", s);
    static string Green(string s) => Paint("32", s);
    static string Yellow(string s) => Paint("33", s);
    static string Red(string s) => Paint("31", s);

    static string Domain => $"gui/{getuid()}";
    static string Target => $"{Domain}/{Paths.Label}";

    const string Help = """
        Usage: spacebridge [command]

        Without a command, in a terminal: a menu of all of this. Arrows move, Enter picks, Esc goes back.

        Set up
          setup <SPACE address>       Everything in one go: SALTO's signing key from that SPACE, allow it, install the service
          install                     Install or update the background service and trust its https certificate
          uninstall [--all]           Remove the service (--all: settings and signing key too)
          key import <address|file>   Take the signing key from a SPACE server or from Setup_SaltoLocalIOBridge.exe

        Use
          status                      What is running, what is allowed, which devices are attached
          devices [--bluetooth]       Encoders, PPDs, nCoders and serial ports; --bluetooth listens 15 s for SALTO devices
          logs [-f] [-n N] [--all]    The service's log: which page asked for what
          start | stop | restart      The background service
          selftest                    Check the bridge's own workings (and open an attached device)

        Settings
          allow <SPACE address>...    Let only the listed SPACE servers use the bridge
          allow --any                 Let any website use it (what SALTO's Windows bridge does)
          deny <SPACE address>...     Take an address off the list
          origins                     Show the list
          config                      Show all settings
          config bluetooth on|off     SALTO devices over Bluetooth LE (off by default)
          config serial on|off        Offer the Mac's serial ports to SPACE (on by default)

          version | help
        """;

    public static async Task<int> Run(string[] args)
    {
        var rest = args.Skip(1).ToArray();
        try
        {
            if (args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected) return await Menu();
            switch (args.FirstOrDefault() ?? "status")
            {
                case "run": return Server.Run();
                case "status": return Status();
                case "menu": return await Menu();
                case "setup": return await Setup(rest);
                case "install": Install(); return Status();
                case "uninstall": return Uninstall(rest.Contains("--all"));
                case "start": Start(); return Status();
                case "stop": return Stop();
                case "restart": Restart(); return Status();
                case "devices": return await Devices(rest.Contains("--bluetooth"));
                case "logs": return Logs(rest);
                case "allow": return Allow(rest);
                case "deny": return Deny(rest);
                case "origins": Origins(Config.Load()); return 0;
                case "config": return Configure(rest);
                case "key": return await Key(rest);
                case "selftest": return await SelfTest.Run();
                case "version" or "--version" or "-v": Console.WriteLine($"spacebridge {Version} (stands in for SALTO Local IO Bridge {Server.BridgeVersion})"); return 0;
                case "help" or "--help" or "-h": Console.WriteLine(Help); return 0;
                default: Console.Error.WriteLine($"Unknown command '{args[0]}'.\n\n{Help}"); return 2;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine(Red("error: ") + e.Message);
            return 1;
        }
    }

    // ---- set up ----

    static async Task<int> Setup(string[] args)
    {
        var source = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (source is null && !SignKey.Present)
            throw new ArgumentException("Give the address of your SPACE, for example: spacebridge setup https://space.example:8100");
        if (source is not null && !SignKey.Present)
        {
            await SignKey.Import(source);
            Console.WriteLine("Signing key imported.");
        }
        if (source is not null && !File.Exists(source))
        {
            var config = Config.Load();
            var origin = Config.Origin(source);
            if (!config.Origins.Contains(origin)) config.Origins.Add(origin);
            config.Save();
        }
        Install();
        return Status();
    }

    /// <summary>The Spacebridge.app this program runs from.</summary>
    static string SourceApp()
    {
        var path = Environment.ProcessPath ?? "";
        path = File.ResolveLinkTarget(path, true)?.FullName ?? path;
        const string inside = "/Contents/MacOS/spacebridge";
        return path.EndsWith(inside) ? path[..^inside.Length]
            : throw new InvalidOperationException("`install` works from a release (Spacebridge.app). For a development build run ./build.sh and use dist/Spacebridge.app/Contents/MacOS/spacebridge.");
    }

    static void Install()
    {
        var source = SourceApp();
        Launchctl("bootout", Target);
        Launchctl("bootout", $"{Domain}/{Paths.OldLabel}");
        File.Delete(Paths.OldPlist);
        Directory.CreateDirectory(Paths.Folder);
        if (Directory.Exists(Paths.OldFolder))
        {
            // The first version's folder: its certificate is already trusted, so it moves along.
            foreach (var name in new[] { "localhost.pfx", "localhost.crt" })
                if (File.Exists(Path.Combine(Paths.OldFolder, name)) && !File.Exists(Path.Combine(Paths.Folder, name)))
                    File.Move(Path.Combine(Paths.OldFolder, name), Path.Combine(Paths.Folder, name));
            Directory.Delete(Paths.OldFolder, true);
        }
        if (!string.Equals(source, Paths.App, StringComparison.Ordinal))
        {
            if (Directory.Exists(Paths.App)) Directory.Delete(Paths.App, true);
            // ditto keeps the code signature intact.
            var copy = Shell.Run("/usr/bin/ditto", [source, Paths.App]);
            if (copy.Code != 0) throw new IOException("Copying the app failed: " + copy.Output);
            // Not notarised: a copy that arrived by browser download or AirDrop would be blocked.
            Shell.Run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", Paths.App]);
            // Homebrew rewrites the libraries' ids on install, which breaks the bundle's seal; macOS ties the Bluetooth permission to it.
            if (Shell.Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", Paths.App]).Code != 0)
                Shell.Run("/usr/bin/codesign", ["--force", "--deep", "--sign", "-", Paths.App]);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Paths.Plist)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.Log)!);
        File.WriteAllText(Paths.Plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{Paths.Label}</string>
              <key>ProgramArguments</key><array><string>{Paths.Binary}</string><string>run</string></array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>StandardOutPath</key><string>{Paths.Log}</string>
              <key>StandardErrorPath</key><string>{Paths.Log}</string>
            </dict>
            </plist>

            """);
        var load = Launchctl("bootstrap", Domain, Paths.Plist);
        if (load.Code != 0) throw new InvalidOperationException("launchd did not take the service: " + load.Output);

        // The service writes its certificate on its first start.
        WaitUntilUp();
        if (!File.Exists(Paths.CertificateFile)) Console.WriteLine(Yellow("No https certificate yet; run `spacebridge install` again."));
        else if (!CertificateTrusted())
        {
            Console.WriteLine("Trusting the bridge's own certificate for https://localhost (macOS asks for your password)...");
            var trust = Shell.Run("/usr/bin/security", ["add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-k", LoginKeychain, Paths.CertificateFile], show: true);
            if (trust.Code != 0) Console.WriteLine(Yellow("Not trusted: a SPACE on https will not reach the bridge. Run `spacebridge install` again to retry."));
        }
        Console.WriteLine(Green("Installed.") + "\n");
    }

    static int Uninstall(bool all)
    {
        Launchctl("bootout", Target);
        File.Delete(Paths.Plist);
        if (File.Exists(Paths.CertificateFile))
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(Paths.CertificateFile);
            Shell.Run("/usr/bin/security", ["delete-certificate", "-Z", certificate.Thumbprint, LoginKeychain]);
        }
        if (all && Directory.Exists(Paths.Folder)) Directory.Delete(Paths.Folder, true);
        else
        {
            if (Directory.Exists(Paths.App)) Directory.Delete(Paths.App, true);
            foreach (var name in new[] { "localhost.pfx", "localhost.crt", "state.json" }) File.Delete(Path.Combine(Paths.Folder, name));
        }
        Console.WriteLine(all ? "Removed the service, its certificate, settings and signing key." : $"Removed the service and its certificate. Settings and signing key stay in {Paths.Short(Paths.Folder)} (--all removes them).");
        return 0;
    }

    static string LoginKeychain => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Keychains/login.keychain-db");

    static bool CertificateTrusted() =>
        File.Exists(Paths.CertificateFile) && Shell.Run("/usr/bin/security", ["verify-cert", "-c", Paths.CertificateFile, "-p", "ssl", "-s", "localhost"]).Code == 0;

    static (int Code, string Output) Launchctl(params string[] arguments) => Shell.Run("/bin/launchctl", arguments);

    static bool Loaded => Launchctl("print", Target).Code == 0;

    static void Start()
    {
        if (!File.Exists(Paths.Plist)) throw new InvalidOperationException("The service is not installed: run `spacebridge install`.");
        if (!Loaded) Launchctl("bootstrap", Domain, Paths.Plist);
        else Launchctl("kickstart", Target);
        WaitUntilUp();
    }

    static int Stop()
    {
        Launchctl("bootout", Target);
        Console.WriteLine("Stopped (until the next login; `spacebridge uninstall` removes it).");
        return 0;
    }

    static void Restart()
    {
        if (!Loaded) Start();
        else Launchctl("kickstart", "-k", Target);
        WaitUntilUp();
    }

    /// <summary>A changed setting takes effect when the service starts.</summary>
    static void Apply()
    {
        if (!Loaded) return;
        Restart();
        Console.WriteLine(Dim("Service restarted."));
    }

    static void WaitUntilUp()
    {
        // The state file is rewritten by the new process.
        var since = DateTime.UtcNow.AddSeconds(-1);
        for (var i = 0; i < 75; i++)
        {
            if (File.Exists(Paths.StateFile) && File.GetLastWriteTimeUtc(Paths.StateFile) > since && Service() is { Answers: true }) return;
            Thread.Sleep(200);
        }
    }

    // ---- status ----

    sealed record ServiceState(int Pid, int Http, int Https, string Version, bool Answers);

    static ServiceState? Service()
    {
        var print = Launchctl("print", Target);
        if (print.Code != 0 || !File.Exists(Paths.StateFile)) return null;
        try
        {
            var state = JsonDocument.Parse(File.ReadAllText(Paths.StateFile)).RootElement;
            int pid = state.GetProperty("pid").GetInt32(), http = state.GetProperty("http").GetInt32();
            var running = print.Output.Contains($"pid = {pid}");
            return new(pid, http, state.GetProperty("https").GetInt32(), state.GetProperty("version").GetString() ?? "?", running && Get($"http://localhost:{http}/info", 1500) is not null);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException)
        {
            return null;
        }
    }

    static string? Get(string url, int timeoutMs)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            return http.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    static void Row(string name, string value) => Console.WriteLine($"  {name,-13}{value}");

    static int Status()
    {
        var config = Config.Load();
        Console.WriteLine($"{Bold("Spacebridge")} {Version} {Dim($"· SALTO Local IO Bridge {Server.BridgeVersion} for macOS")}\n");

        var service = Service();
        if (service is { Answers: true })
        {
            Row("Service", $"{Green("●")} running {Dim($"(pid {service.Pid})")}  http://localhost:{service.Http}" + (service.Https > 0 ? $"  https://localhost:{service.Https}" : ""));
            if (service.Version != Version) Row("", Yellow($"the service is version {service.Version}; `spacebridge install` updates it to {Version}"));
        }
        else if (File.Exists(Paths.Plist)) Row("Service", $"{Red("●")} not running  {Dim("spacebridge start · spacebridge logs")}");
        else Row("Service", $"{Dim("○")} not installed  {Dim("spacebridge setup <SPACE address>")}");

        if (File.Exists(Paths.CertificateFile))
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(Paths.CertificateFile);
            Row("Certificate", CertificateTrusted()
                ? $"{Green("●")} trusted for https://localhost {Dim($"until {certificate.NotAfter:yyyy-MM-dd}")}"
                : $"{Red("●")} not trusted: a SPACE on https cannot reach the bridge  {Dim("spacebridge install")}");
        }
        Row("Signing key", SignKey.Present ? $"{Green("●")} imported" : $"{Red("●")} missing: devices cannot be opened  {Dim("spacebridge key import <SPACE address>")}");
        Row("Allowed", config.Origins.Count == 0
            ? $"{Yellow("●")} any website {Dim("(as SALTO's bridge)")}  {Dim("spacebridge allow <SPACE address>")}"
            : $"{Green("●")} " + string.Join($"\n  {"",-13}  ", config.Origins));
        foreach (var refused in Refused().Except(config.Origins).TakeLast(3))
            Row("", Yellow($"refused {refused}") + Dim($"  spacebridge allow {refused}"));
        Row("Bluetooth", config.Bluetooth ? "on" + Dim(LastLogged(" Bluetooth: ") is { } state ? " · " + state : "") : Dim("off"));
        Row("Serial ports", config.Serial ? "on" : Dim("off"));

        Console.WriteLine();
        PrintDevices(Discovery.All(config.Serial));
        Console.WriteLine($"\n  {Dim("Log " + Paths.Short(Paths.Log) + " · spacebridge help")}");
        return service is { Answers: true } || !File.Exists(Paths.Plist) ? 0 : 1;
    }

    /// <summary>What the service last logged after a marker, since it last started.</summary>
    static string? LastLogged(string mark) => !File.Exists(Paths.Log) ? null
        : File.ReadLines(Paths.Log).Reverse().TakeWhile(l => !l.Contains(": http://localhost:")).FirstOrDefault(l => l.Contains(mark))?.Split(mark, 2)[1];

    /// <summary>Origins the service turned away, oldest first, from its log.</summary>
    static IEnumerable<string> Refused()
    {
        const string mark = " refused origin ";
        if (!File.Exists(Paths.Log)) return [];
        return File.ReadLines(Paths.Log).Where(l => l.Contains(mark)).Select(l => l[(l.IndexOf(mark) + mark.Length)..].Split(": `")[0]).Distinct().ToList();
    }

    static string Kind(Port p) => p.DeviceType switch { 0 => "encoder", 1 => "PPD", 2 => "nCoder", 3 => "Bluetooth", _ => p.Type == 1 ? "serial port" : "unknown" };

    static void PrintDevices(List<Port> ports)
    {
        if (ports.Count == 0)
        {
            Console.WriteLine($"  {Dim("No encoder, PPD or nCoder attached.")}");
            return;
        }
        foreach (var p in ports) Console.WriteLine($"  {Bold(p.Name.PadRight(22))} {Kind(p),-12} {p.Info}");
    }

    static async Task<int> Devices(bool bluetooth)
    {
        var config = Config.Load();
        PrintDevices(Discovery.All(config.Serial));
        if (!bluetooth) return 0;

        // Bluetooth goes through the service: the macOS permission belongs to it, not to this terminal.
        if (!config.Bluetooth) throw new InvalidOperationException("Bluetooth is off: `spacebridge config bluetooth on`.");
        var service = Service() ?? throw new InvalidOperationException("The service is not running: `spacebridge start`.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var info = JsonDocument.Parse(await http.GetStringAsync($"http://localhost:{service.Http}/info")).RootElement;
        if (!info.GetProperty("isBluetoothEnabled").GetBoolean())
        {
            Console.WriteLine(Yellow("\n  Bluetooth is " + (LastLogged(" Bluetooth: ") ?? "not ready") + "."));
            return 1;
        }
        Console.WriteLine(Dim("\n  Listening for SALTO Bluetooth devices for 15 s..."));
        var found = JsonDocument.Parse(await http.GetStringAsync($"http://localhost:{service.Http}/ports?type=2")).RootElement;
        foreach (var p in found.EnumerateArray()) Console.WriteLine($"  {Bold(p.GetProperty("name").GetString()!.PadRight(22))} {"Bluetooth",-12} {p.GetProperty("info").GetString()}");
        if (found.GetArrayLength() == 0) Console.WriteLine($"  {Dim("No SALTO device in programming mode heard.")}");
        return 0;
    }

    static int Logs(string[] args)
    {
        if (!File.Exists(Paths.Log)) throw new InvalidOperationException($"No log yet at {Paths.Short(Paths.Log)}.");
        var count = Array.IndexOf(args, "-n") is >= 0 and var at && at + 1 < args.Length && int.TryParse(args[at + 1], out var n) ? n : 40;
        bool all = args.Contains("--all"), follow = args.Contains("-f");
        // The web server's own debug lines (every TLS connection) only with --all.
        bool Shown(string line) => all || !(line.StartsWith("dbug:") || line.StartsWith("fail:") || line.StartsWith("warn:"));

        using var reader = new StreamReader(new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) if (Shown(line)) lines.Add(line);
        foreach (var line in lines.TakeLast(count)) Console.WriteLine(line);
        while (follow)
        {
            while (reader.ReadLine() is { } line) if (Shown(line)) Console.WriteLine(line);
            Thread.Sleep(300);
        }
        return 0;
    }

    // ---- settings ----

    static void Origins(Config config)
    {
        if (config.Origins.Count == 0) Console.WriteLine($"Any website may use the bridge {Dim("(as SALTO's Windows bridge). Restrict it: spacebridge allow <SPACE address>")}");
        else Console.WriteLine("Only these SPACE servers may use the bridge:\n" + string.Join("\n", config.Origins.Select(o => "  " + o)));
    }

    static int Allow(string[] args)
    {
        var config = Config.Load();
        if (args.Contains("--any")) config.Origins.Clear();
        else if (args.Length == 0) throw new ArgumentException("Which address? For example: spacebridge allow https://space.example:8100");
        else foreach (var origin in args.Select(Config.Origin)) if (!config.Origins.Contains(origin)) config.Origins.Add(origin);
        config.Save();
        Origins(config);
        Apply();
        return 0;
    }

    static int Deny(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Which address? `spacebridge origins` shows the list.");
        var config = Config.Load();
        foreach (var origin in args.Select(Config.Origin))
            if (!config.Origins.Remove(origin)) Console.WriteLine(Yellow($"{origin} was not on the list."));
        config.Save();
        Origins(config);
        Apply();
        return 0;
    }

    static int Configure(string[] args)
    {
        var config = Config.Load();
        if (args.Length == 0)
        {
            Row("origins", config.Origins.Count == 0 ? "any website" : string.Join(" ", config.Origins));
            Row("bluetooth", config.Bluetooth ? "on" : "off");
            Row("serial", config.Serial ? "on" : "off");
            Console.WriteLine($"\n  {Dim(Paths.Short(Paths.ConfigFile))}");
            return 0;
        }
        if (args.Length != 2 || args[1] is not ("on" or "off")) throw new ArgumentException("Usage: spacebridge config bluetooth|serial on|off");
        var on = args[1] == "on";
        switch (args[0])
        {
            case "bluetooth":
                config.Bluetooth = on;
                if (on) Console.WriteLine("Bluetooth on. macOS will ask once to let Spacebridge use Bluetooth.\n"
                    + Yellow("Ported from SALTO's Windows bridge and not yet tried against a SALTO Bluetooth device."));
                break;
            case "serial": config.Serial = on; break;
            default: throw new ArgumentException($"No setting '{args[0]}': bluetooth or serial.");
        }
        config.Save();
        Apply();
        return 0;
    }

    static async Task<int> Key(string[] args)
    {
        if (args is ["import", var source])
        {
            await SignKey.Import(source);
            Console.WriteLine("Signing key imported.");
            Apply();
            return 0;
        }
        if (args.Length > 0 && args[0] != "status") throw new ArgumentException("Usage: spacebridge key import <SPACE address | Setup_SaltoLocalIOBridge.exe>");
        Console.WriteLine(SignKey.Present ? $"Signing key present: {Paths.Short(Paths.KeyFile)}" : "No signing key: spacebridge key import <SPACE address>");
        return SignKey.Present ? 0 : 1;
    }
}
