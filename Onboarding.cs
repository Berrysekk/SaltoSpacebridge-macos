namespace SaltoBridge;

// `spacebridge setup` in a terminal, and the first `spacebridge`: the setup as a few questions instead of an argument.
// Esc goes back a step; on the first one it leaves without changing anything.
public static partial class Cli
{
    static string Ready => Green("✓ ") + "Set up. Log in to SPACE again. The first time, the browser asks whether the SPACE page may reach devices on this computer: "
        + "allow it and repeat the action (SPACE gives up after two seconds, so the first try fails while the question is open).";

    const int Steps = 4;
    static string Step(int n) => $"step {n} of {Steps}";

    /// <summary>Asks for the SPACE (and takes SALTO's signing key from it), who may use the bridge and which devices, then installs the service.
    /// False when the user leaves it before the service is installed.</summary>
    static async Task<bool> Onboarding(bool welcome = true)
    {
        var config = Config.Load();
        var address = config.Origins.FirstOrDefault() ?? "";
        var origin = "";
        var only = true;
        bool serial = config.Serial, bluetooth = config.Bluetooth;
        var first = welcome ? 0 : 1;
        // Addresses allowed before, which "only this SPACE" keeps.
        int Others() => config.Origins.Count(o => o != origin);

        for (var step = first; ;)
        {
            if (step < first) return false;
            switch (step)
            {
                case 0:
                    step += Choose("Welcome to Spacebridge", $"Spacebridge {Version}",
                    [
                        .. Para("Spacebridge lets ProAccess SPACE in the browser use the encoder, PPD or nCoder plugged into this Mac."),
                        "",
                        .. Para("Setting it up takes a minute: the address of your SPACE, who may use the bridge, which devices, and the background service."),
                    ], [("Set it up", ""), ("Not now", Dim("spacebridge setup, whenever you are ready"))]) == 0 ? 1 : -1;
                    break;

                case 1:
                    if (await AskSpace(address) is not { } space)
                    {
                        step--;
                        break;
                    }
                    (address, origin) = (space, Config.Origin(space));
                    step++;
                    break;

                case 2:
                    var access = Choose("Who may use the bridge", Step(2),
                    [
                        .. Para("SALTO's own bridge answers any website that asks, so a page you happen to visit could reach your encoder. "
                            + "Spacebridge can answer your SPACE only."),
                    ], [($"Only {origin}", Dim(Others() > 0 ? $"and the {Others()} already allowed" : "recommended")), ("Any website", Dim("as SALTO's own bridge"))], only ? 0 : 1);
                    if (access >= 0) only = access == 0;
                    step += access >= 0 ? 1 : -1;
                    break;

                case 3:
                    if (DevicesStep(serial, bluetooth) is { } chosen)
                    {
                        (serial, bluetooth) = chosen;
                        step++;
                    }
                    else step--;
                    break;

                case 4:
                    var answer = Choose("Ready to install", Step(4),
                    [
                        $"{"SPACE",-14}{origin}",
                        $"{"Allowed",-14}{(only ? "only this SPACE" + (Others() > 0 ? " and the addresses already allowed" : "") : Yellow("any website"))}",
                        $"{"Serial ports",-14}{(serial ? "on" : "off")}",
                        $"{"Bluetooth",-14}{(bluetooth ? "on" : "off")}",
                        "",
                        .. Para("The background service starts now and at every login. macOS asks for your password once, to trust the bridge's own certificate for https://localhost; it renews itself after that."
                            + (bluetooth ? " It also asks once whether Spacebridge may use Bluetooth." : "")),
                    ], [("Install", ""), ("Back", "")]);
                    if (answer != 0)
                    {
                        step--;
                        break;
                    }
                    config = Config.Load();
                    if (!only) config.Origins.Clear();
                    else if (!config.Origins.Contains(origin)) config.Origins.Add(origin);
                    (config.Serial, config.Bluetooth) = (serial, bluetooth);
                    config.Save();
                    if (await Show("Installing", () =>
                    {
                        Console.WriteLine(Dim("Starting the service…"));
                        Install();
                        return Task.FromResult(0);
                    }, "continue")) return true;
                    // What went wrong is on the screen it leaves; the summary comes back, to try again or go back.
                    break;
            }
        }
    }

    /// <summary>Asks for the address of the SPACE until SALTO's signing key could be taken from it, or from a downloaded installer.
    /// The address, or null to go back.</summary>
    static async Task<string?> AskSpace(string address)
    {
        var problem = "";
        while (true)
        {
            if (Ask("Your SPACE", problem + SpacePrompt, address, Step(1)) is not { } answer) return null;
            address = answer;
            string origin;
            try
            {
                origin = Config.Origin(address);
            }
            catch (ArgumentException e)
            {
                problem = Problem(e.Message);
                continue;
            }
            problem = "";
            if (SignKey.Present) return address;

            Busy($"Fetching SALTO's signing key from {origin}…");
            try
            {
                await Quietly(() => SignKey.Import(origin));
                return address;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var way = Choose("No signing key", Step(1),
                [
                    .. Report(e).Split('\n').SelectMany(Para),
                    "",
                    .. Para("The key is read out of SALTO's bridge installer, which every SPACE serves. Check the address, "
                        + "or use the installer downloaded from SPACE (Setup_SaltoLocalIOBridge.exe)."),
                ], [("Type the address again", ""), ("Use a downloaded installer…", "")]);
                if (way == 1 && await AskInstaller()) return address;
            }
        }
    }

    /// <summary>Takes the signing key from Setup_SaltoLocalIOBridge.exe on disk. False when the user goes back.</summary>
    static async Task<bool> AskInstaller()
    {
        var problem = "";
        while (Ask("SALTO's installer", problem + "The path to Setup_SaltoLocalIOBridge.exe.\nDrag the file into this window to type its path.", "", Step(1)) is { } answer)
        {
            var path = FilePath(answer);
            if (!File.Exists(path))
            {
                problem = Problem($"No file at {path}.");
                continue;
            }
            Busy("Reading the signing key out of the installer…");
            try
            {
                await Quietly(() => SignKey.Import(path));
                return true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                problem = Problem(e.Message);
            }
        }
        return false;
    }

    /// <summary>Serial ports and Bluetooth, switched with Enter. Null to go back.</summary>
    static (bool Serial, bool Bluetooth)? DevicesStep(bool serial, bool bluetooth)
    {
        Func<Task<string?>> none = () => Task.FromResult<string?>(null);
        for (var at = 0; ;)
        {
            var picked = Pick(Box("Devices", Step(3),
            [
                .. Para("USB encoders, PPDs and nCoders always work. Serial ports and Bluetooth can be switched here, and later in the menu."),
            ]),
            [
                new("Serial ports", none, (serial ? Green("on") : Dim("off")) + Dim("  serial encoders on /dev/cu.*")),
                new("Bluetooth LE", none, (bluetooth ? Green("on") : Dim("off")) + Dim("  not yet tried with a SALTO device")),
                new("Continue", none),
            ], ref at, false);
            switch (picked)
            {
                case 0: serial = !serial; break;
                case 1: bluetooth = !bluetooth; break;
                case 2: return (serial, bluetooth);
                default: return null;
            }
        }
    }

    /// <summary>A screen with a question and its answers: the index of the one picked, or -1 for Esc.</summary>
    static int Choose(string title, string right, List<string> rows, (string Label, string Note)[] answers, int at = 0)
    {
        Func<Task<string?>> none = () => Task.FromResult<string?>(null);
        return Pick(Box(title, right, rows), [.. answers.Select(a => new Item(a.Label, none, a.Note))], ref at, false);
    }

    /// <summary>A paragraph broken into the lines of a frame.</summary>
    static List<string> Para(string text) => Wrap(text, Wide() - 4);

    /// <summary>What went wrong, above the question that is asked again.</summary>
    static string Problem(string message) => string.Join("\n", Para(Red("✗ ") + message)) + "\n\n";

    static void Busy(string what) => Console.Write("\r\e[2K  " + Dim(what));

    /// <summary>Runs something that reports on the console without it showing on the screen.</summary>
    static async Task Quietly(Func<Task> work)
    {
        var terminal = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            await work();
        }
        finally
        {
            Console.SetOut(terminal);
        }
    }

    /// <summary>A path as typed or dropped into the terminal: in quotes, with escaped spaces, or from ~.</summary>
    static string FilePath(string typed)
    {
        var path = typed.Trim();
        if (path.Length > 1 && (path[0] is '\'' or '"') && path[^1] == path[0]) path = path[1..^1];
        path = path.Replace("\\ ", " ");
        if (path == "~" || path.StartsWith("~/")) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Path.GetFullPath(path);
    }
}
