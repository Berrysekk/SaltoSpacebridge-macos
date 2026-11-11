using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SaltoBridge;

/// <summary>New releases from GitHub: the service looks for one every few hours and installs it, `spacebridge update` does so on request.</summary>
public static class Update
{
    const string Releases = "https://api.github.com/repos/Berrysekk/SaltoSpacebridge-macos/releases/latest";

    public sealed record Release(string Version, string Url, string? Sha256);

    /// <summary>Homebrew owns its copy: `brew upgrade` updates that one.</summary>
    public static bool Homebrew => new FileInfo("/opt/homebrew/bin/spacebridge").LinkTarget?.Contains("Cellar/") == true;

    /// <summary>The latest release when it is newer than this program, else null.</summary>
    public static async Task<Release?> Newer()
    {
        // SPACEBRIDGE_RELEASES: another address to ask, for testing an update without publishing one.
        var address = Environment.GetEnvironmentVariable("SPACEBRIDGE_RELEASES") ?? Releases;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("spacebridge");
            return Parse(await http.GetStringAsync(address)) is { } latest && IsNewer(latest.Version, Cli.Version) ? latest : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new Failure($"GitHub could not be asked for the latest release ({e.Message}). Check the internet connection.", "spacebridge update");
        }
    }

    /// <summary>GitHub's description of a release: its version and the archive for this kind of Mac.</summary>
    public static Release? Parse(string json)
    {
        var release = JsonNode.Parse(json);
        var asset = (release?["assets"] as JsonArray)?.FirstOrDefault(a => a?["name"]?.GetValue<string>().EndsWith("macos-arm64.tar.gz") == true);
        if (release?["tag_name"]?.GetValue<string>() is not { } tag || asset?["browser_download_url"]?.GetValue<string>() is not { } url) return null;
        var digest = asset["digest"]?.GetValue<string>();
        return new(tag.TrimStart('v'), url, digest?.StartsWith("sha256:") == true ? digest[7..] : null);
    }

    public static bool IsNewer(string version, string than) => Version.TryParse(version, out var a) && Version.TryParse(than, out var b) && a > b;

    /// <summary>Downloads a release, checks it and puts it in the installed app's place. The service runs it from its next start.</summary>
    public static async Task Install(Release release)
    {
        if (Homebrew) throw new Failure("This copy came from Homebrew, which updates it.", "brew upgrade spacebridge && spacebridge install");
        if (!Directory.Exists(Paths.App)) throw new Failure("Spacebridge is not installed yet.", "spacebridge setup");
        // Next to the app, so that swapping them is a rename on the same disk.
        var work = Path.Combine(Paths.Folder, "update");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, "spacebridge.tar.gz");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                await File.WriteAllBytesAsync(archive, await http.GetByteArrayAsync(release.Url));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new Failure($"Downloading {release.Url} failed ({e.Message}). Nothing was changed.", "spacebridge update");
            }
            if (release.Sha256 is { } expected && !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new Failure("The download does not match the checksum GitHub gives for the release. Nothing was changed.", "spacebridge update");

            var fresh = Path.Combine(work, "Spacebridge.app");
            var program = Path.Combine(fresh, "Contents/MacOS/spacebridge");
            // It has to be whole and has to run on this Mac before it takes the place of the one that works.
            if (Shell.Run("/usr/bin/tar", ["-xzf", archive, "-C", work]).Code != 0 || !File.Exists(program)
                || Shell.Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", fresh]).Code != 0
                || !Shell.Run(program, ["version"]).Output.Contains(release.Version))
                throw new Failure($"Release {release.Version} is damaged or does not run on this Mac. The installed version stays.");

            var old = Path.Combine(work, "old.app");
            Directory.Move(Paths.App, old);
            try
            {
                Directory.Move(fresh, Paths.App);
            }
            catch
            {
                Directory.Move(old, Paths.App);
                throw;
            }
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    /// <summary>The service's own look for a release. Never while SPACE is using a device; whatever fails waits for the next round.</summary>
    public static async void Automatic(bool install)
    {
        try
        {
            if (Homebrew || await Newer() is not { } release) return;
            if (!install)
            {
                // `spacebridge status` and the menu show it.
                var state = JsonNode.Parse(File.ReadAllText(Paths.StateFile))!;
                if (state["update"]?.GetValue<string>() == release.Version) return;
                state["update"] = release.Version;
                File.WriteAllText(Paths.StateFile, state.ToJsonString());
                Log.Info($"Version {release.Version} is available: `spacebridge update` installs it.");
                return;
            }
            if (Pipe.Any) return;
            Log.Info($"Updating to {release.Version}.");
            await Install(release);
            Log.Info($"Updated to {release.Version}; restarting.");
            // launchd starts the service again, from the new program.
            Environment.Exit(0);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Info("No update this time: " + e.Message);
        }
    }
}
