using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaltoBridge;

public static class Paths
{
    static readonly string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public const string Label = "cz.totosoft.spacebridge";
    public static readonly string Folder = Path.Combine(home, "Library/Application Support/Spacebridge");
    public static readonly string App = Path.Combine(Folder, "Spacebridge.app");
    public static readonly string Binary = Path.Combine(App, "Contents/MacOS/spacebridge");
    public static readonly string ConfigFile = Path.Combine(Folder, "config.json");
    public static readonly string KeyFile = Path.Combine(Folder, "sign.key");
    public static readonly string StateFile = Path.Combine(Folder, "state.json");
    public static readonly string CertificateFile = Path.Combine(Folder, "localhost.crt");
    public static readonly string AuthorityFile = Path.Combine(Folder, "authority.crt");
    public static readonly string PreviousCertificateFile = Path.Combine(Folder, "localhost.previous.crt");
    public static readonly string Log = Path.Combine(home, "Library/Logs/Spacebridge.log");
    public static readonly string Plist = Path.Combine(home, $"Library/LaunchAgents/{Label}.plist");

    // The first version's names, taken over by `spacebridge install`.
    public const string OldLabel = "cz.totosoft.salto-local-io-bridge";
    public static readonly string OldFolder = Path.Combine(home, "Library/Application Support/SaltoLocalIOBridge");
    public static readonly string OldPlist = Path.Combine(home, $"Library/LaunchAgents/{OldLabel}.plist");

    public static string Short(string path) => path.StartsWith(home) ? "~" + path[home.Length..] : path;
}

/// <summary>What `spacebridge allow` and `spacebridge config` change; the service reads it again whenever the file changes.</summary>
public sealed class Config
{
    /// <summary>The SPACE addresses that may use the bridge, as a browser sends them in Origin. Empty: any website, as SALTO's bridge.</summary>
    public List<string> Origins { get; set; } = [];
    public bool Bluetooth { get; set; }
    public bool Serial { get; set; } = true;

    /// <summary>Whether the service fetches and installs new releases on its own.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Settings that let no page in: what the service runs on when the file is there but cannot be read.</summary>
    public static Config Nobody => new() { Origins = ["(settings unreadable)"] };

    /// <summary>The settings in the file, the defaults when there is none, or null when it cannot be read.</summary>
    public static Config? Read(string? file = null)
    {
        file ??= Paths.ConfigFile;
        var config = new Config();
        if (!File.Exists(file)) return config;
        try
        {
            var saved = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if (saved["origins"] is JsonArray origins) config.Origins = [.. origins.Select(o => o!.GetValue<string>())];
            config.Bluetooth = saved["bluetooth"]?.GetValue<bool>() ?? config.Bluetooth;
            config.Serial = saved["serial"]?.GetValue<bool>() ?? config.Serial;
            config.AutoUpdate = saved["autoUpdate"]?.GetValue<bool>() ?? config.AutoUpdate;
            return config;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException or IOException)
        {
            return null;
        }
    }

    /// <summary>For the command: settings that cannot be read are set aside, with a word about it, and the defaults take over.
    /// The service never does this: see Server.Settings.</summary>
    public static Config Load()
    {
        if (Read() is { } config) return config;
        var aside = Paths.ConfigFile + ".unreadable";
        File.Move(Paths.ConfigFile, aside, true);
        Console.Error.WriteLine($"Settings could not be read; they are set aside as {Paths.Short(aside)} and the defaults apply: any website may use the bridge until you `spacebridge allow` your SPACE again.");
        return new Config();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Folder);
        var saved = new JsonObject
        {
            ["origins"] = new JsonArray([.. Origins.Select(o => (JsonNode)o)]),
            ["bluetooth"] = Bluetooth,
            ["serial"] = Serial,
            ["autoUpdate"] = AutoUpdate,
        };
        // Written beside and renamed over: the running service never sees half a file.
        var fresh = Paths.ConfigFile + ".new";
        File.WriteAllText(fresh, saved.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(fresh, Paths.ConfigFile, true);
    }

    public bool Allows(string? origin) => origin is null || Origins.Count == 0 || Origins.Contains(origin, StringComparer.OrdinalIgnoreCase);

    /// <summary>"https://Space.example:8100/ProAccessSpace/" -> "https://space.example:8100", the form of an Origin header.</summary>
    public static string Origin(string address)
    {
        // No guessing between http and https: the wrong one would be a different origin.
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            throw new ArgumentException($"'{address}' is not a SPACE address: write it as in the browser, like https://space.example:8100");
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}

/// <summary>SALTO's key for signing pipe tokens. It is not part of this program: `spacebridge key import` takes it from SALTO's own installer.</summary>
public static class SignKey
{
    // SHA-256 and length of the key that the Windows bridges 1.5.0 and 2.1.0 carry.
    const string KnownHash = "5e079a8faf8d3fbbf88c5000c39c1d8258cabfcdb57b3aeb45fd48bc578d7b63";
    const int KnownLength = 128;

    public static byte[]? Override;

    public static bool Present => Override is not null || File.Exists(Paths.KeyFile);

    public static byte[] Data => Override ?? (File.Exists(Paths.KeyFile)
        ? Encoding.UTF8.GetBytes(File.ReadAllText(Paths.KeyFile).Trim())
        : throw new RestError(500, 10, "The signing key is missing: run `spacebridge key import <SPACE address>`"));

    /// <summary>Finds the key in SALTO's Setup_SaltoLocalIOBridge.exe (a file, or the address of a SPACE that serves it) and stores it.</summary>
    public static async Task Import(string source)
    {
        var work = Directory.CreateTempSubdirectory("spacebridge-key-").FullName;
        try
        {
            var installer = source;
            if (!File.Exists(source))
            {
                var url = Config.Origin(source) + "/ProAccessSpace/Setup_SaltoLocalIOBridge.exe";
                Console.WriteLine($"Downloading {url}");
                // SPACE often runs on a self-signed certificate; what is downloaded only counts if it holds the known key.
                using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromMinutes(5) };
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                HttpResponseMessage response;
                try
                {
                    response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, patience.Token);
                }
                catch (OperationCanceledException)
                {
                    var other = url.StartsWith("https") ? "http" + Config.Origin(source)[5..] : "https" + Config.Origin(source)[4..];
                    throw new Failure($"No answer from {Config.Origin(source)}. Is it http instead of https, or the other way round?", $"spacebridge key import {other}");
                }
                catch (HttpRequestException e)
                {
                    // An address copied from the README (your-space) ends here, as a name nobody knows.
                    throw new Failure($"{Config.Origin(source)} cannot be reached ({e.Message}). Give the address of your own SPACE, the one you open in the browser.", "spacebridge key import https://space.example:8100");
                }
                using (response)
                {
                    if (!response.IsSuccessStatusCode) throw new Failure($"{Config.Origin(source)} answered {(int)response.StatusCode} instead of SALTO's bridge installer: is that a ProAccess SPACE? "
                        + "The installer downloaded from SPACE works as well.", "spacebridge key import ~/Downloads/Setup_SaltoLocalIOBridge.exe");
                    installer = Path.Combine(work, "setup.exe");
                    await File.WriteAllBytesAsync(installer, await response.Content.ReadAsByteArrayAsync());
                }
            }
            var key = Search(installer, work, 0) ?? throw new Failure(
                "There is no signing key this version knows in that installer; it knows the key of the Windows bridges 1.5.0 and 2.1.0.", "spacebridge update");
            Directory.CreateDirectory(Paths.Folder);
            File.WriteAllText(Paths.KeyFile, key);
            File.SetUnixFileMode(Paths.KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    // The installer is a WiX bundle: cabinets holding an MSI, holding a cabinet, holding SaltoLocalIOBridge.exe,
    // where the key is a .NET string (UTF-16).
    static string? Search(string file, string work, int depth)
    {
        var data = File.ReadAllBytes(file);
        if (data.Length < 8 || depth > 4) return null;
        if (data.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            foreach (var stream in CompoundFileStreams(data).Where(s => s.AsSpan().StartsWith("MSCF"u8)))
                if (SearchCabinet(stream, work, depth) is { } found) return found;
            return null;
        }
        if (data[0] == 'M' && data[1] == 'Z' && data.Length < 4 << 20 && KeyIn(data) is { } key) return key;
        // Cabinets appended to (or inside) the file.
        for (var at = 0; (at = data.AsSpan().IndexOfFrom("MSCF\0\0\0\0"u8, at)) >= 0; at += 4)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 8));
            if (size <= 0 || size > data.Length - at) continue;
            if (SearchCabinet(data[at..(at + size)], work, depth) is { } found) return found;
            at += size - 4;
        }
        return null;
    }

    static string? SearchCabinet(byte[] cabinet, string work, int depth)
    {
        var folder = Directory.CreateDirectory(Path.Combine(work, Guid.NewGuid().ToString("N"))).FullName;
        var file = folder + ".cab";
        File.WriteAllBytes(file, cabinet);
        // macOS's tar (libarchive) reads cabinets.
        if (Shell.Run("/usr/bin/tar", ["-xf", file, "-C", folder]).Code != 0) return null;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .OrderByDescending(f => Path.GetFileName(f).StartsWith("SaltoLocalIOBridge"))
            .Select(f => Search(f, work, depth + 1)).FirstOrDefault(k => k is not null);
    }

    static string? KeyIn(byte[] data)
    {
        var hash = Convert.FromHexString(KnownHash);
        Span<byte> text = stackalloc byte[KnownLength], digest = stackalloc byte[32];
        for (var at = 0; at + KnownLength * 2 <= data.Length; at++)
        {
            // Hex digits in UTF-16: only then is the hash worth computing.
            if (!IsHex(data[at]) || data[at + 1] != 0 || !IsHex(data[at + 2]) || data[at + 3] != 0) continue;
            var ok = true;
            for (var i = 0; i < KnownLength && ok; i++)
            {
                text[i] = data[at + i * 2];
                ok = data[at + i * 2 + 1] == 0 && IsHex(text[i]);
            }
            if (ok && SHA256.TryHashData(text, digest, out _) && digest.SequenceEqual(hash)) return Encoding.ASCII.GetString(text);
        }
        return null;

        static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
    }

    /// <summary>The streams of an OLE compound file (an MSI) that live in whole sectors.</summary>
    static IEnumerable<byte[]> CompoundFileStreams(byte[] d)
    {
        var sector = 1 << BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(0x1E));
        uint U32(int at) => at >= 0 && at + 4 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at)) : 0xFFFFFFFE;
        int At(uint s) => (int)Math.Min((s + 1) * (long)sector, int.MaxValue - sector);

        // The table of FAT sectors: 109 entries in the header, the rest in a chain of DIFAT sectors.
        var fatSectors = Enumerable.Range(0, 109).Select(i => U32(0x4C + i * 4)).ToList();
        for (var (s, n) = (U32(0x44), 0); s < 0xFFFFFFFA && n < 4096; s = U32(At(s) + sector - 4), n++)
            fatSectors.AddRange(Enumerable.Range(0, sector / 4 - 1).Select(i => U32(At(s) + i * 4)));
        var fat = fatSectors.Where(s => s < 0xFFFFFFFA).SelectMany(s => Enumerable.Range(0, sector / 4).Select(i => U32(At(s) + i * 4))).ToArray();

        byte[] Chain(uint start, long size)
        {
            using var stream = new MemoryStream();
            for (var (s, n) = (start, 0); s < 0xFFFFFFFA && s < fat.Length && n <= fat.Length && At(s) + sector <= d.Length; s = fat[s], n++)
                stream.Write(d, At(s), sector);
            var all = stream.ToArray();
            return size >= 0 && size < all.Length ? all[..(int)size] : all;
        }

        var directory = Chain(U32(0x30), -1);
        for (var e = 0; e + 128 <= directory.Length; e += 128)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(e + 0x78));
            // Type 2 is a stream; smaller ones sit in the mini stream, where no cabinet fits.
            if (directory[e + 0x42] == 2 && size >= 4096) yield return Chain(BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(e + 0x74)), size);
        }
    }

    static int IndexOfFrom(this Span<byte> data, ReadOnlySpan<byte> value, int from)
    {
        if (from >= data.Length) return -1;
        var at = data[from..].IndexOf(value);
        return at < 0 ? -1 : at + from;
    }
}

public static class Shell
{
    /// <summary>Runs a program and returns its exit code and output.</summary>
    public static (int Code, string Output) Run(string program, string[] arguments, bool show = false)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = !show, RedirectStandardError = !show };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = show ? "" : process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim());
    }
}
