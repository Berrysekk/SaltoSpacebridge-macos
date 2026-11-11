using System.Text;

namespace SaltoBridge;

// `spacebridge` without a command, in a terminal: the same commands behind a menu.
// Arrows move, Enter picks, Esc goes back.
public static partial class Cli
{
    /// <summary>One line of a menu. What it does returns a note for the top of the menu, or null to leave the menu.</summary>
    sealed record Item(string Label, Func<Task<string?>> Do);

    const string SpacePrompt = "Address of your SPACE as you type it in the browser, e.g. https://space.example:8100";

    static async Task<int> Menu()
    {
        // The alternate screen, without a cursor; the terminal comes back as it was, also after Ctrl+C.
        void Leave() => Console.Write("\e[?25h\e[?1049l");
        Console.Write("\e[?1049h\e[?25l");
        Console.CancelKeyPress += (_, _) => Leave();
        try
        {
            await Level(() => Captured(Status), () =>
            {
                var items = new List<Item>();
                if (!File.Exists(Paths.Plist) || !SignKey.Present) items.Add(Asked("Set up for your SPACE…", SpacePrompt, address => Setup([address])));
                items.AddRange(
                [
                    new("Refresh", () => Task.FromResult<string?>("")),
                    Shown("Bluetooth devices (listens 15 s)", () => Devices(true)),
                    new("Allowed addresses", OriginsMenu),
                    new("Settings", SettingsMenu),
                    new("Service", ServiceMenu),
                    Asked("Import signing key…", "Address of a SPACE, or the path to Setup_SaltoLocalIOBridge.exe", source => Key(["import", source])),
                    Shown("Logs", () => Task.FromResult(Logs(["-n", $"{Math.Max(10, Console.WindowHeight - 4)}"]))),
                    Shown("Self-test", SelfTest.Run),
                    new("Quit", () => Task.FromResult<string?>(null)),
                ]);
                return items;
            });
            return 0;
        }
        finally
        {
            Leave();
        }
    }

    static Task<string?> OriginsMenu() => Level(
        () => Bold("Allowed addresses") + "\n\n  " + (Config.Load().Origins.Count == 0
            ? "Any website may use the bridge, as with SALTO's own."
            : "Only these pages get an answer, and a pipe may only lead back to the page's own server."),
        () =>
        {
            var config = Config.Load();
            var items = config.Origins.Select(o => Quiet($"Remove  {o}", () => Deny([o]))).ToList();
            items.AddRange(Refused().Except(config.Origins).TakeLast(3).Select(r => Quiet($"Allow   {r}  (was refused)", () => Allow([r]))));
            items.Add(Asked("Add an address…", SpacePrompt, address => Task.FromResult(Allow([address]))));
            if (config.Origins.Count > 0) items.Add(Quiet("Allow any website", () => Allow(["--any"])));
            return items;
        });

    static Task<string?> SettingsMenu() => Level(
        () => Bold("Settings") + "\n\n  Enter switches a setting; the service restarts with it.",
        () =>
        {
            var config = Config.Load();
            return
            [
                Quiet($"Bluetooth     {(config.Bluetooth ? "on" : "off")}", () => Configure(["bluetooth", config.Bluetooth ? "off" : "on"])),
                Quiet($"Serial ports  {(config.Serial ? "on" : "off")}", () => Configure(["serial", config.Serial ? "off" : "on"])),
            ];
        });

    static Task<string?> ServiceMenu() => Level(
        () => Bold("Service") + "\n\n  " + (Service() is { Answers: true } service ? $"{Green("●")} running {Dim($"(pid {service.Pid})")}"
            : File.Exists(Paths.Plist) ? $"{Red("●")} not running" : $"{Dim("○")} not installed"),
        () =>
        [
            Quiet("Start", () => { Start(); return 0; }),
            Quiet("Stop", Stop),
            Quiet("Restart", () => { Restart(); return 0; }),
            Shown("Install or update", () => { Install(); return Task.FromResult(0); }),
            Confirmed("Uninstall", () => Uninstall(false)),
            Confirmed("Uninstall and delete settings and signing key", () => Uninstall(true)),
        ]);

    /// <summary>A menu that stays until Esc; what an item printed shows above it on the next round.</summary>
    static async Task<string?> Level(Func<string> header, Func<List<Item>> items)
    {
        var note = "";
        for (var at = 0; ;)
        {
            var list = items();
            var picked = Pick(header().TrimEnd('\n') + (note.Trim().Length > 0 ? "\n\n  " + note.Trim('\n').Replace("\n", "\n  ") : ""), list.Select(i => i.Label).ToList(), ref at);
            if (picked < 0 || await list[picked].Do() is not { } result) return "";
            note = result;
        }
    }

    /// <summary>Draws the menu and waits for a choice: its index, or -1 for Esc.</summary>
    static int Pick(string header, List<string> items, ref int at)
    {
        at = Math.Clamp(at, 0, items.Count - 1);
        while (true)
        {
            var screen = new StringBuilder("\e[H\e[J").Append(header).Append("\n\n");
            for (var i = 0; i < items.Count; i++) screen.Append(i == at ? $"  \e[7m {items[i]} \e[0m\n" : $"   {items[i]}\n");
            Console.Write(screen.Append('\n').Append(Dim("  ↑↓ move · Enter select · Esc back")));
            switch (Console.ReadKey(true).Key)
            {
                case ConsoleKey.UpArrow: at = (at + items.Count - 1) % items.Count; break;
                case ConsoleKey.DownArrow: at = (at + 1) % items.Count; break;
                case ConsoleKey.Enter: return at;
                case ConsoleKey.Escape or ConsoleKey.Q: return -1;
            }
        }
    }

    /// <summary>A line of text from the user, or null when they press Esc or leave it empty.</summary>
    static string? Ask(string prompt)
    {
        Console.Write($"\e[H\e[J{Bold(prompt)}\n\n  {Dim("Enter confirm · Esc cancel")}\n\n  > \e[?25h");
        var text = new StringBuilder();
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

    /// <summary>Runs a command on a cleared screen and leaves its output there until Enter or Esc.</summary>
    static async Task Show(Func<Task<int>> command)
    {
        Console.Write("\e[H\e[J");
        try
        {
            await command();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine(Red("error: ") + e.Message);
        }
        Console.Write(Dim("\n  Enter or Esc: back"));
        while (Console.ReadKey(true).Key is not (ConsoleKey.Enter or ConsoleKey.Escape)) { }
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
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            text.WriteLine(Red("error: ") + e.Message);
        }
        finally
        {
            Console.SetOut(terminal);
        }
        return text.ToString();
    }

    static Item Quiet(string label, Func<int> command) => new(label, () =>
    {
        Console.Write("\r\e[2K" + Dim("  Working…"));
        return Task.FromResult<string?>(Captured(command));
    });

    static Item Shown(string label, Func<Task<int>> command) => new(label, async () => { await Show(command); return ""; });

    static Item Asked(string label, string prompt, Func<string, Task<int>> command) =>
        new(label, async () => { if (Ask(prompt) is { } answer) await Show(() => command(answer)); return ""; });

    static Item Confirmed(string label, Func<int> command) => new(label, () =>
    {
        var at = 0;
        return Task.FromResult<string?>(Pick(Bold(label + "?"), ["No, keep it", "Yes"], ref at) == 1 ? Captured(command) : "");
    });
}
