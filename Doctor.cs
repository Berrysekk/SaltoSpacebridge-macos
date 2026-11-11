using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SaltoBridge;

/// <summary>Something went wrong that the user can put right: what happened, and the command that fixes it.</summary>
public sealed class Failure(string message, string? fix = null) : Exception(message)
{
    public string? Fix => fix;
}

// Errors as the user reads them, and `spacebridge doctor`.
public static partial class Cli
{
    const string Installer = "curl -fsSL https://raw.githubusercontent.com/Berrysekk/SaltoSpacebridge-macos/main/install.sh | sh";

    static readonly string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>What went wrong and, where there is one, the command that fixes it.</summary>
    static (string Message, string? Fix) Explain(Exception e) => e switch
    {
        Failure f => (f.Message, f.Fix),
        // "Access to the path '/…' is denied."
        UnauthorizedAccessException when e.Message.Split('\'') is [_, var path, ..] => ($"No permission to change {Paths.Short(path)}.",
            $"sudo chown -R \"$(id -un)\" \"{(path.StartsWith(Paths.Folder) ? Paths.Folder : Path.GetDirectoryName(path))}\""),
        _ => (e.Message, null),
    };

    static string Report(Exception e)
    {
        var (message, fix) = Explain(e);
        return Red("✗ ") + message + (fix is null ? "" : $"\n  {Dim("Fix:")} {Bold(fix)}");
    }

    static bool Attempt(Func<bool> repair)
    {
        try
        {
            return repair();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine("    " + Dim(Explain(e).Message));
            return false;
        }
    }

    /// <summary>Every `spacebridge` on the PATH, also a link whose program is gone.</summary>
    static List<string> Commands() => [.. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Distinct()
        .Select(folder => Path.Combine(folder, "spacebridge")).Where(p => File.Exists(p) || new FileInfo(p).LinkTarget is not null)];

    /// <summary>Puts the command into the first folder of the PATH that the user may write to, as the installer does.</summary>
    static bool LinkCommand()
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':');
        foreach (var folder in new[] { home + "/.local/bin", home + "/bin", "/opt/homebrew/bin", "/usr/local/bin" }.Where(path.Contains))
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.CreateSymbolicLink(Path.Combine(folder, "spacebridge"), Paths.Binary);
                return true;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        }
        return false;
    }

    /// <summary>`spacebridge doctor`: what keeps the bridge from working, put right where that takes no decision of the user's.</summary>
    static async Task<int> Doctor(bool repair)
    {
        var open = 0;
        void Check(bool ok, string good, string bad, string fix, Func<bool>? mend = null)
        {
            if (ok) Console.WriteLine($"  {Green("✓")} {good}");
            else if (repair && mend is not null && Attempt(mend)) Console.WriteLine($"  {Green("✓")} {good} {Yellow("· repaired")}");
            else
            {
                open++;
                Console.WriteLine($"  {Red("✗")} {bad}\n    {Dim("Fix:")} {Bold(fix)}");
            }
        }
        bool Runs(string program, params string[] arguments) => Shell.Run(program, arguments).Code == 0;

        Console.WriteLine($"{Bold("Spacebridge")} {Version} {Dim(repair ? "· checking and repairing" : "· checking")}\n");

        var writable = Attempt(() =>
        {
            Directory.CreateDirectory(Paths.Folder);
            File.WriteAllText(Path.Combine(Paths.Folder, ".probe"), "");
            File.Delete(Path.Combine(Paths.Folder, ".probe"));
            return true;
        });
        Check(writable, "Its folder can be written", $"No permission to write {Paths.Short(Paths.Folder)}", $"sudo chown -R \"$(id -un)\" \"{Paths.Folder}\"");

        // Settings that cannot be read are set aside by Load itself.
        var config = Config.Load();
        Check(SignKey.Present, "SALTO's signing key is imported", "SALTO's signing key is missing: devices cannot be opened",
            $"spacebridge key import {config.Origins.FirstOrDefault() ?? "<SPACE address>"}");

        if (Directory.Exists(Paths.App))
        {
            Check(!Runs("/usr/bin/xattr", "-p", "com.apple.quarantine", Paths.App), "macOS lets the app run", "macOS holds the app in quarantine",
                $"xattr -dr com.apple.quarantine \"{Paths.App}\"", () => Runs("/usr/bin/xattr", "-dr", "com.apple.quarantine", Paths.App));
            Check(Runs("/usr/bin/codesign", "--verify", "--deep", "--strict", Paths.App), "The app's signature is whole", "The app's signature is broken (macOS ties the Bluetooth permission to it)",
                $"codesign --force --deep --sign - \"{Paths.App}\"", () => Runs("/usr/bin/codesign", "--force", "--deep", "--sign", "-", Paths.App));
        }

        var update = Path.Combine(Paths.Folder, "update");
        Check(!Directory.Exists(Paths.OldFolder) && !File.Exists(Paths.OldPlist) && !Directory.Exists(update), "Nothing is left of older versions", "Parts of an older version are left", "spacebridge install", () =>
        {
            RetireOld();
            if (Directory.Exists(update)) Directory.Delete(update, true);
            return true;
        });

        var service = Service();
        var installed = false;
        Check(service is { Answers: true } && service.Version == Version,
            $"The background service runs{(service is null ? "" : $" on http://localhost:{service.Http}")}",
            !File.Exists(Paths.Plist) ? "The background service is not installed"
                : service is { Answers: true } ? $"The service is version {service.Version}, this command is {Version}" : "The background service does not run",
            !SignKey.Present ? "spacebridge setup" : File.Exists(Paths.Plist) && service is not { Answers: true } ? "spacebridge install && spacebridge logs" : "spacebridge install",
            // Without the signing key there is a setup to go through, not a repair.
            !SignKey.Present ? null : () =>
            {
                installed = true;
                Install();
                return Service() is { Answers: true };
            });

        if (File.Exists(Paths.CertificateFile))
            Check(CertificateTrusted(), "https://localhost is trusted, and its certificate renews itself", "The bridge's certificate is not trusted: a SPACE on https cannot reach the bridge",
                "spacebridge install", installed ? null : TrustCertificate);

        var commands = Commands();
        var gone = commands.Where(p => !File.Exists(p)).ToList();
        Check(commands.Count > 0 && gone.Count == 0, $"The spacebridge command is on your PATH{(commands.Count > 0 ? $" ({Paths.Short(commands[0])})" : "")}",
            gone.Count > 0 ? $"{Paths.Short(gone[0])} points at a program that is gone" : "The spacebridge command is not on your PATH",
            gone.Count > 0 ? $"sudo ln -sf \"{Paths.Binary}\" \"{gone[0]}\"" : $"sudo mkdir -p /usr/local/bin && sudo ln -sf \"{Paths.Binary}\" /usr/local/bin/spacebridge", () =>
            {
                if (!File.Exists(Paths.Binary)) return false;
                foreach (var link in gone)
                {
                    File.Delete(link);
                    File.CreateSymbolicLink(link, Paths.Binary);
                }
                return gone.Count > 0 || LinkCommand();
            });

        // An alias from an older installer's advice hides the command, also when its program has moved.
        foreach (var file in new[] { (Environment.GetEnvironmentVariable("ZDOTDIR") ?? home) + "/.zshrc", home + "/.bash_profile", home + "/.bashrc", home + "/.profile" }.Where(File.Exists))
            foreach (var alias in File.ReadLines(file).Where(l => l.TrimStart().StartsWith("alias spacebridge=")))
                if (alias.Split('=', 2)[1].Trim('\'', '"', ' ') is var target && !File.Exists(target))
                    Check(false, "", $"An alias in {Paths.Short(file)} points at {Paths.Short(target)}, which is gone", $"sed -i '' '/alias spacebridge=/d' \"{file}\" && exec \"$SHELL\"");

        try
        {
            if (await SaltoBridge.Update.Newer() is not { } release) Console.WriteLine($"  {Green("✓")} This is the latest version");
            else if (SaltoBridge.Update.Homebrew) Console.WriteLine($"  {Yellow("●")} Version {release.Version} is available\n    {Dim("Fix:")} {Bold("brew upgrade spacebridge && spacebridge install")}");
            else Console.WriteLine($"  {Yellow("●")} Version {release.Version} is available{(config.AutoUpdate ? Dim(" · it installs itself within hours") : "")}\n    {Dim("Now:")} {Bold("spacebridge update")}");
        }
        catch (Failure e)
        {
            Console.WriteLine($"  {Dim("– " + e.Message)}");
        }

        Console.WriteLine(open == 0 ? $"\n{Green("All good.")}" : $"\n{Red(open == 1 ? "One problem is left" : $"{open} problems are left")}: run the command after Fix, then `spacebridge doctor` again.");
        return open == 0 ? 0 : 1;
    }
}

/// <summary>macOS's own judgement of a certificate: what Safari and Chrome go by.</summary>
static class Keychain
{
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const string Security = "/System/Library/Frameworks/Security.framework/Security";

    [DllImport(CoreFoundation)] static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] static extern nint CFStringCreateWithCString(nint allocator, string text, uint encoding);
    [DllImport(CoreFoundation)] static extern void CFRelease(nint it);
    [DllImport(Security)] static extern nint SecCertificateCreateWithData(nint allocator, nint data);
    [DllImport(Security)] static extern nint SecPolicyCreateSSL(byte server, nint host);
    [DllImport(Security)] static extern int SecTrustCreateWithCertificates(nint certificates, nint policies, out nint trust);
    [DllImport(Security)] static extern byte SecTrustEvaluateWithError(nint trust, nint error);

    /// <summary>Whether a web server showing this certificate would be accepted as the host.</summary>
    public static bool Trusts(X509Certificate2 certificate, string host)
    {
        const uint utf8 = 0x08000100;
        nint data = CFDataCreate(0, certificate.RawData, certificate.RawData.Length), name = CFStringCreateWithCString(0, host, utf8);
        nint item = SecCertificateCreateWithData(0, data), policy = SecPolicyCreateSSL(1, name), trust = 0;
        try
        {
            return item != 0 && SecTrustCreateWithCertificates(item, policy, out trust) == 0 && SecTrustEvaluateWithError(trust, 0) != 0;
        }
        finally
        {
            foreach (var it in new[] { trust, policy, item, name, data }) if (it != 0) CFRelease(it);
        }
    }
}
