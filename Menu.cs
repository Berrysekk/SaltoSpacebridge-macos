using System.Text;
using System.Text.RegularExpressions;

namespace SaltoBridge;

// `spacebridge` without a command, in a terminal: everything the commands do, behind a menu.
// Arrows move, Enter picks, Esc goes back.
public static partial class Cli
{
    /// <summary>One line of a menu. What it does returns a note for the top of the menu, or null to leave the menu.</summary>
    sealed record Item(string Label, Func<Task<string?>> Do, string Value = "", string Section = "");

    const string SpacePrompt = "The address of your SPACE as you type it in the browser,\nlike https://space.example:8100";
    const int Refresh = -2;

    /// <summary>What `uninstall` said: the program it ran from may be gone, so the menu ends with it.</summary>
    static string? farewell;

    static string Accent(string s) => Paint("38;5;75", s);
    static string Grey(string s) => Paint("38;5;240", s);
    static string Selected(string s) => Paint("1;38;5;231;48;5;25", s);

    /// <summary>Runs screens on the alternate screen, without a cursor; the terminal comes back as it was, also after Ctrl+C.</summary>
    static async Task<T> FullScreen<T>(Func<Task<T>> screens)
    {
        static void Leave() => Console.Write("\e[?25h\e[?1049l");
        ConsoleCancelEventHandler restore = (_, _) => Leave();
        Console.Write("\e[?1049h\e[?25l");
        Console.CancelKeyPress += restore;
        try
        {
            return await screens();
        }
        finally
        {
            Console.CancelKeyPress -= restore;
            Leave();
        }
    }

    static bool IsSetUp => File.Exists(Paths.Plist) && SignKey.Present;

    static async Task<int> Menu()
    {
        await FullScreen(async () =>
        {
            // The first run starts with the guided setup; leaving it leads to the menu.
            var note = !IsSetUp && await Onboarding() ? Ready : "";
            // Read once a round, by the items; the panel above them shows the same moment.
            var config = Config.Load();
            ServiceState? service = null;
            await Level(
                () =>
                {
                    var rows = ServiceRows(service, hints: false).Select(r => $"{r.Name,-13}{r.Value}").ToList();
                    var ports = Discovery.All(config.Serial);
                    if (ports.Count == 0) rows.Add($"{"Devices",-13}{Dim("no encoder, PPD or nCoder attached")}");
                    rows.AddRange(ports.Take(4).Select((p, i) => $"{(i == 0 ? "Devices" : ""),-13}{Bold(p.Name)}  {Kind(p)}  {Dim(p.Info)}"));
                    if (ports.Count > 4) rows.Add($"{"",-13}{Dim($"and {ports.Count - 4} more")}");
                    return ($"Spacebridge {Version}", $"SALTO Local IO Bridge {Server.BridgeVersion}", rows);
                },
                () =>
                {
                    config = Config.Load();
                    service = Service();
                    var running = service is { Answers: true };
                    var refused = Refused().Except(config.Origins).Count();
                    var items = new List<Item>();
                    if (!IsSetUp)
                        items.Add(new("Set up…", async () => await Onboarding(welcome: false) ? Ready : "", Dim("your SPACE, who may use the bridge, devices, the service"), "Set up"));
                    items.AddRange(
                    [
                        new("Allowed addresses", OriginsMenu, (config.Origins.Count == 0 ? Yellow("any website") : $"{config.Origins.Count} address{(config.Origins.Count == 1 ? "" : "es")}")
                            + (refused > 0 ? Yellow($" · {refused} refused") : ""), "Access"),
                        Asked("Signing key", source => Key(["import", source]), "The address of a SPACE, or the path to Setup_SaltoLocalIOBridge.exe",
                            SignKey.Present ? Green("imported") : Red("missing") + Dim(" · devices cannot be opened"), "Access"),
                        Toggle("Bluetooth", "bluetooth", config.Bluetooth, config.Bluetooth ? LastLogged(" Bluetooth: ") : null, "Devices"),
                        Toggle("Serial ports", "serial", config.Serial, null, "Devices"),
                        Shown("Bluetooth scan", () => Devices(true), Dim("listens 15 s for SALTO devices"), "Devices"),
                        running ? Quiet("Stop", () => { Silent(Stop); return "Service stopped until the next login."; }, "", "Service")
                            : Quiet("Start", () => { Start(); return "Service started."; }, "", "Service"),
                        Quiet("Restart", () => { Restart(); return "Service restarted."; }, "", "Service"),
                        Shown("Update", () => UpdateNow(false), service?.Update is { } update && Update.IsNewer(update, Version)
                            ? Yellow($"version {update} is available") : Dim("looks for a new release now"), "Service"),
                        Toggle("Automatic updates", "autoupdate", config.AutoUpdate, null, "Service"),
                        new("Uninstall…", UninstallMenu, "", "Service"),
                        new("Live log", LiveLog, Dim(Paths.Short(Paths.Log)), "Diagnostics"),
                        Shown("Doctor", () => Doctor(true), Dim("finds what is wrong and repairs it"), "Diagnostics"),
                    ]);
                    return items;
                }, root: true, note: note);
            return 0;
        });
        if (farewell is not null) Console.Write(farewell);
        return 0;
    }

    static Task<string?> OriginsMenu() => Level(
        () => ("Allowed addresses", "", Config.Load().Origins.Count == 0
            ? ["Any website may use the bridge, as with SALTO's own.", "Add your SPACE and only it gets an answer."]
            : ["Only the pages listed here get an answer from the bridge,", "and a pipe may only lead back to the page's own server."]),
        () =>
        {
            var config = Config.Load();
            var items = new List<Item>
            {
                new("Add an address…", () => Ask("Add an address", SpacePrompt) is { } address
                    ? Now(() => { Silent(() => Allow([address])); return $"{Config.Origin(address)} may use the bridge."; })
                    : Task.FromResult<string?>("")),
            };
            items.AddRange(config.Origins.Select(o => Confirmed(o, config.Origins.Count == 1
                ? "It is the only address on the list: without it any website may use the bridge." : "Pages from this address will no longer get an answer.", "Remove it", () =>
            {
                Silent(() => Deny([o]));
                return $"{o} removed." + (config.Origins.Count == 1 ? " The list is empty now: any website may use the bridge." : "");
            }, Dim("⏎ removes it…"), "Allowed")));
            items.AddRange(Refused().Except(config.Origins).TakeLast(5).Select(r => Quiet(r, () => { Silent(() => Allow([r])); return $"{r} may use the bridge."; },
                Yellow("was refused") + Dim(" · ⏎ allows it"), "Refused")));
            if (config.Origins.Count > 0)
                items.Add(Confirmed("Allow any website", "Empties the list: any page you open may then reach your encoder, as with SALTO's own bridge.", "Allow any website",
                    () => { Silent(() => Allow(["--any"])); return "Any website may use the bridge."; }, Dim("as SALTO's own bridge does…")));
            return items;
        });

    static Task<string?> UninstallMenu() => Level(
        () => ("Uninstall", "", ["Removes the background service and takes its certificate out of your keychain."]),
        () =>
        [
            new("Cancel", () => Task.FromResult<string?>(null)),
            Farewell("Remove the service", false, Dim("settings and signing key stay")),
            Farewell("Remove everything", true, Dim("also settings, signing key and the spacebridge command")),
        ]);

    static Item Farewell(string label, bool all, string value) => new(label, () =>
    {
        Working();
        farewell = Captured(() => Remove(all));
        return Task.FromResult<string?>(null);
    }, value);

    /// <summary>The service's log as it grows, until Esc.</summary>
    static Task<string?> LiveLog()
    {
        if (!File.Exists(Paths.Log)) throw new Failure($"There is no log yet at {Paths.Short(Paths.Log)}: the service has not run.", "spacebridge start");
        using var reader = new StreamReader(new FileStream(Paths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var lines = new List<string>();
        for (bool all = false, changed = true; ;)
        {
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
                changed = true;
            }
            if (changed)
            {
                // The web server's own lines (every TLS connection) only on request.
                var shown = lines.Where(l => all || !(l.StartsWith("dbug:") || l.StartsWith("fail:") || l.StartsWith("warn:") || l.StartsWith(' ')));
                var screen = Box("Live log", Paths.Short(Paths.Log), []);
                screen.RemoveAt(screen.Count - 1);
                screen.AddRange(shown.TakeLast(Math.Max(5, Console.WindowHeight - 4)).Select(l => " " + Tint(l)));
                Draw([.. screen, "", Hints(("a", all ? "requests only" : "all lines"), ("esc ←", "back"))]);
                changed = false;
            }
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(120);
                continue;
            }
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.Escape or ConsoleKey.LeftArrow or ConsoleKey.Enter or ConsoleKey.Q: return Task.FromResult<string?>("");
                case ConsoleKey.A: (all, changed) = (!all, true); break;
            }
        }
    }

    static string Tint(string line)
    {
        if (line.Length < 20 || !char.IsDigit(line[0])) return Dim(line);
        var rest = line[20..];
        var status = rest.IndexOf(" -> ", StringComparison.Ordinal) + 4;
        if (rest.Contains("refused origin")) rest = Yellow(rest);
        else if (rest.Contains(": http://localhost:")) rest = Bold(rest);
        else if (status > 4 && status + 3 <= rest.Length)
            rest = rest[..status] + Paint(rest[status] switch { '2' or '3' => "32", '4' => "33", _ => "31" }, rest[status..(status + 3)]) + rest[(status + 3)..];
        return Dim(line[..20]) + rest;
    }

    // ---- the pieces every screen is made of ----

    /// <summary>A menu that stays until Esc; what an item reports shows above it on the next round.</summary>
    static async Task<string?> Level(Func<(string Title, string Right, List<string> Rows)> head, Func<List<Item>> items, bool root = false, string note = "")
    {
        for (var at = 0; farewell is null;)
        {
            var list = items();
            var (title, right, rows) = head();
            var top = Box(title, right, rows);
            if (note.Length > 0) top.AddRange(["", .. note.Split('\n').SelectMany(l => Wrap(l, Wide() - 2)).Select(l => "  " + l)]);
            var picked = Pick(top, list, ref at, root);
            if (picked == Refresh) continue;
            if (picked < 0) break;
            try
            {
                if (await list[picked].Do() is not { } result) break;
                note = result;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                note = Report(e);
            }
        }
        return "";
    }

    /// <summary>Draws the menu and waits for a choice: its index, -1 for Esc, or Refresh when the main screen is due for a fresh look.</summary>
    static int Pick(List<string> top, List<Item> items, ref int at, bool root)
    {
        at = Math.Clamp(at, 0, items.Count - 1);
        var sections = items.Any(i => i.Section.Length > 0) ? 13 : 0;
        var labels = Math.Max(22, items.Max(i => i.Label.Length)) + 2;
        var due = Environment.TickCount64 + 3000;
        while (true)
        {
            var lines = new List<string>(top) { "" };
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var section = Accent((i == 0 || items[i - 1].Section != item.Section ? item.Section.ToUpperInvariant() : "").PadRight(sections));
                lines.Add(i == at
                    ? $" {section}{Selected(Pad($" ❯ {item.Label.PadRight(labels)}{Plain(item.Value)}", Wide() - sections))}"
                    : $" {section}   {item.Label.PadRight(labels)}{item.Value}");
            }
            Draw([.. lines, "", Hints(("↑↓", "move"), ("⏎ →", "select"), root ? ("esc", "quit") : ("esc ←", "back"))]);
            while (!Console.KeyAvailable)
            {
                if (root && Environment.TickCount64 > due) return Refresh;
                Thread.Sleep(40);
            }
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.UpArrow: at = (at + items.Count - 1) % items.Count; break;
                case ConsoleKey.DownArrow: at = (at + 1) % items.Count; break;
                case ConsoleKey.Enter or ConsoleKey.RightArrow: return at;
                case ConsoleKey.Escape or ConsoleKey.Q: return -1;
                // On the main screen ← does nothing: one press too many on the way back should not quit.
                case ConsoleKey.LeftArrow when !root: return -1;
            }
        }
    }

    /// <summary>A line of text from the user, or null when they press Esc or leave it empty.</summary>
    static string? Ask(string title, string prompt, string initial = "", string right = "")
    {
        Draw([.. Box(title, right, [.. prompt.Split('\n')]), "", Hints(("⏎", "confirm"), ("esc", "cancel")), "", "  " + Accent("›") + " " + initial]);
        Console.Write("\e[?25h");
        var text = new StringBuilder(initial);
        try
        {
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape) return null;
                if (key.Key == ConsoleKey.Enter) return text.ToString().Trim() is { Length: > 0 } answer ? answer : null;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (text.Length == 0) continue;
                    text.Length--;
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    text.Append(key.KeyChar);
                    Console.Write(key.KeyChar);
                }
            }
        }
        finally
        {
            Console.Write("\e[?25l");
        }
    }

    /// <summary>Runs a command under its title and leaves its output on screen until Enter or Esc.</summary>
    static async Task<bool> Show(string title, Func<Task<int>> command, string done = "back")
    {
        Console.Write($"\e[H\e[J {Bold(Accent(title))}\n {Grey(new string('─', Wide()))}\n\n");
        var worked = false;
        try
        {
            worked = await command() == 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine(Report(e));
        }
        Console.Write("\n" + Hints(("⏎ esc ←", done)));
        while (Console.ReadKey(true).Key is not (ConsoleKey.Enter or ConsoleKey.Escape or ConsoleKey.LeftArrow or ConsoleKey.Q)) { }
        return worked;
    }

    /// <summary>What a command prints, as text.</summary>
    static string Captured(Func<int> command)
    {
        var terminal = Console.Out;
        var text = new StringWriter();
        Console.SetOut(text);
        try
        {
            command();
        }
        finally
        {
            Console.SetOut(terminal);
        }
        return text.ToString();
    }

    static void Silent(Func<int> command) => Captured(command);

    static void Working() => Console.Write("\r\e[2K" + Dim("  Working…"));

    static Task<string?> Now(Func<string> action)
    {
        Working();
        return Task.FromResult<string?>(Green("✓ ") + action());
    }

    static Item Quiet(string label, Func<string> action, string value = "", string section = "") => new(label, () => Now(action), value, section);

    /// <summary>Asks before it acts; Esc and the first answer both mean no.</summary>
    static Item Confirmed(string label, string question, string yes, Func<string> action, string value = "", string section = "") => new(label, () =>
    {
        var at = 0;
        Func<Task<string?>> nothing = () => Task.FromResult<string?>("");
        return Pick(Box(label, "", [question]), [new("No, leave it", nothing), new(yes, nothing)], ref at, false) == 1 ? Now(action) : nothing();
    }, value, section);

    static Item Shown(string label, Func<Task<int>> command, string value = "", string section = "") =>
        new(label, async () => { await Show(label, command); return ""; }, value, section);

    static Item Asked(string label, Func<string, Task<int>> command, string prompt, string value, string section) =>
        new(label, async () => { if (Ask(label.TrimEnd('…'), prompt) is { } answer) await Show(label.TrimEnd('…'), () => command(answer)); return ""; }, value, section);

    static Item Toggle(string label, string setting, bool on, string? state, string section) => Quiet(label, () =>
    {
        Silent(() => Configure([setting, on ? "off" : "on"]));
        return $"{label} {(on ? "off" : "on")}." + (setting == "bluetooth" && !on ? " macOS asks once whether Spacebridge may use Bluetooth." : "");
    }, on ? Green("on") + (state is null ? "" : Dim(" · " + state)) : Dim("off"), section);

    // ---- drawing ----

    static readonly Regex Ansi = new("\e\\[[0-9;?]*[A-Za-z]");

    static string Plain(string s) => Ansi.Replace(s, "");

    /// <summary>The width the screens are laid out for.</summary>
    static int Wide() => Math.Clamp((Console.WindowWidth > 0 ? Console.WindowWidth : 80) - 2, 40, 86);

    /// <summary>Cuts a line, colours and all, to a number of visible characters.</summary>
    static string Fit(string s, int width)
    {
        if (Plain(s).Length <= width) return s;
        var cut = new StringBuilder();
        for (int i = 0, visible = 0; i < s.Length && visible < width - 1;)
        {
            var code = Ansi.Match(s, i);
            if (code.Success && code.Index == i)
            {
                cut.Append(code.Value);
                i += code.Length;
            }
            else
            {
                cut.Append(s[i++]);
                visible++;
            }
        }
        return cut.Append('…').Append(color ? "\e[0m" : "").ToString();
    }

    /// <summary>Breaks a sentence at spaces into lines of a width.</summary>
    static List<string> Wrap(string text, int width)
    {
        var lines = new List<string> { "" };
        foreach (var word in text.Split(' '))
            if (lines[^1].Length > 0 && Plain(lines[^1]).Length + 1 + Plain(word).Length > width) lines.Add(word);
            else lines[^1] += (lines[^1].Length > 0 ? " " : "") + word;
        return lines;
    }

    static string Pad(string s, int width) => Fit(s, width) + new string(' ', Math.Max(0, width - Plain(s).Length));

    static string Hints(params (string Key, string What)[] keys) => "  " + string.Join("   ", keys.Select(k => Bold(k.Key) + " " + Dim(k.What)));

    /// <summary>A frame with its title in the top edge.</summary>
    static List<string> Box(string title, string right, List<string> rows)
    {
        var width = Wide();
        var rule = Math.Max(1, width - 5 - title.Length - (right.Length > 0 ? right.Length + 3 : 0));
        var lines = new List<string> { $" {Grey("╭─")} {Bold(Accent(title))} {Grey(new string('─', rule))}{(right.Length > 0 ? $" {Dim(right)} {Grey("─")}" : "")}{Grey("╮")}" };
        lines.AddRange(rows.Select(row => $" {Grey("│")} {Pad(row, width - 4)} {Grey("│")}"));
        lines.Add($" {Grey("╰" + new string('─', width - 2) + "╯")}");
        return lines;
    }

    /// <summary>Paints a whole screen over the last one, without a flash of empty screen in between.</summary>
    // shortcut: a window with fewer rows than the screen has lines scrolls; add a scrolling menu if a screen ever outgrows 24 rows.
    static void Draw(List<string> lines)
    {
        var width = (Console.WindowWidth > 0 ? Console.WindowWidth : 80) - 1;
        Console.Write("\e[H" + string.Join("\e[K\n", lines.Select(l => Fit(l, width))) + "\e[K\e[J");
    }
}
