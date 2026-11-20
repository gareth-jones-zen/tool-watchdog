using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinBackground;

namespace ToolWatchdog;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public Func<string, string?> Env { get; init; } = Config.Environment;
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
    public Func<IReadOnlyList<string>, IReadOnlyDictionary<string, TaskInfo>> TaskInfo { get; init; } = Checks.TaskInfoFromWindows;
    public Action<string, IEnumerable<string>> Notify { get; init; } = Toast.Notify;
    public Func<ITask> Task { get; init; } = () => new ScheduledTask(Cli.TaskName,
        "Checks that the tools are still producing what they should (tool-watchdog run).");
    public string ProgramDir { get; init; } = AppContext.BaseDirectory;
}

/// <summary>tool-watchdog: are the tools that run by themselves still producing? Exit codes: 0 done (check: nothing
/// stale); 1 something stale, or the run failed; 2 settings or arguments.</summary>
public static class Cli
{
    public const string Prog = "tool-watchdog";
    public const string TaskName = "tool-watchdog run";
    public const string BackgroundExe = "tool-watchdog-background.exe";

    private const string Usage = "usage: tool-watchdog [-h] [--version] [--config CONFIG] {check,run,autostart,config} ...";

    // Explicit "\n" throughout (a raw literal would take the .cs file's line endings).
    public const string Help =
        Usage + "\n\n" +
        "tool-watchdog: are the tools that run by themselves still producing what they should? It checks their products\n" +
        "(the newest file, and how fresh), their logs, and their scheduled tasks; and that files stay within a size budget.\n\n" +
        "    tool-watchdog check              every check, fresh or not (exit 1 if anything is stale)\n" +
        "        --problems                   only what's wrong\n" +
        "        --json                       as JSON\n" +
        "        --brief                      a line for a session briefing: all fine, or the problems\n" +
        "    tool-watchdog run                the scheduled pass: inside the watched hours, raise each new problem once\n" +
        "                                     (a notification, and a review item), and mark cleared ones\n" +
        "        --now TIME                   check and run: as though it were TIME (yyyy-mm-ddThh:mm)\n" +
        "    tool-watchdog autostart on       run every few minutes, at logon and after unlocking (off, restart, status)\n" +
        "    tool-watchdog config             create the settings file (if missing) and say where it is\n\n" +
        "options:\n" +
        "  -h, --help       show this help message and exit\n" +
        "  --version        show program's version number and exit\n" +
        "  --config CONFIG  settings file (default %APPDATA%\\tool-watchdog\\config.toml)\n";

    private static readonly string[] Commands = ["check", "run", "autostart", "config"];

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal sealed class Options
    {
        public string? Config;
        public string Command = "";
        public List<string> Args = [];
        public bool Json, Problems, Brief;
        public DateTime? Now;
    }

    private sealed class ArgumentsException(string message) : Exception(message);

    public static int Run(IReadOnlyList<string> argv, CliHost? host = null)
    {
        host ??= new CliHost();
        Options options;
        try
        {
            var parsed = Parse(argv, host);
            if (parsed is null)
                return 0;
            options = parsed;
        }
        catch (ArgumentsException error)
        {
            host.Err.WriteLine(Usage);
            host.Err.WriteLine($"{Prog}: error: {error.Message}");
            return 2;
        }
        if (options.Now is { } fixedNow)
            host = new CliHost
            {
                Out = host.Out, Err = host.Err, Env = host.Env, Now = () => fixedNow, TaskInfo = host.TaskInfo,
                Notify = host.Notify, Task = host.Task, ProgramDir = host.ProgramDir,
            };

        Config? config = null;
        try
        {
            if (options.Command == "config")
            {
                host.Out.WriteLine(ToolWatchdog.Config.CreateDefault(options.Config ?? host.Env(ToolWatchdog.Config.EnvConfig), host.Env));
                return 0;
            }
            config = ToolWatchdog.Config.Load(options.Config, host.Env);
            return options.Command switch
            {
                "check" => CheckCommand(config, options, host),
                "run" => RunCommand(config, host),
                _ => Autostart(config, options.Args[0], host),
            };
        }
        catch (UsageException error)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            host.Err.WriteLine($"{error.GetType().Name}: {error.Message}");
            if (options.Command == "run" && config is not null)
                Failed(config, host, error);
            return 1;
        }
    }

    private static int CheckCommand(Config config, Options options, CliHost host)
    {
        var checks = Checks.Run(config, host.Now(), host.TaskInfo);
        var shown = options.Problems ? checks.Where(c => c.Stale).ToList() : checks;
        if (options.Brief)
            host.Out.Write(Brief(checks));
        else if (options.Json)
            host.Out.WriteLine(ToJson(shown));
        else
        {
            foreach (var check in shown)
                host.Out.WriteLine(check.Line());
            if (options.Problems && shown.Count == 0)
                host.Out.WriteLine("Everything is producing.");
        }
        return checks.Any(c => c.Stale) ? 1 : 0;
    }

    /// <summary>For a session briefing (0.2.0): "all 30 checks fine (outputs, watchers' logs, tasks)", with ", sizes"
    /// when there are size checks (0.4.0), or how many problems and a line for each.</summary>
    internal static string Brief(IReadOnlyList<Check> checks)
    {
        var stale = checks.Where(c => c.Stale).ToList();
        if (stale.Count == 0)
            return $"all {checks.Count(c => !c.Skipped)} checks fine (outputs, watchers' logs, tasks" +
                   (checks.Any(c => c.Kind == "size") ? ", sizes" : "") +
                   (checks.Any(c => c.Kind == "age") ? ", working files' ages" : "") + ")\n";
        var text = new StringBuilder($"{stale.Count} problem(s); `tool-watchdog check` for every check\n");
        foreach (var check in stale)
            text.Append("   ").Append(check.Line()).Append('\n');
        return text.ToString();
    }

    internal static string ToJson(IEnumerable<Check> checks)
    {
        var array = new JsonArray();
        foreach (var check in checks)
            array.Add((JsonNode)check.ToJson());
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            array.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static int RunCommand(Config config, CliHost host)
    {
        var log = Logger.Create(config.LogFile, host.Out, stampEcho: false, clock: host.Now);
        var outcome = Runner.Run(config, host, log);
        if (!outcome.Watched)
            host.Out.WriteLine($"Outside the watched hours ({config.Window.Text}): nothing checked.");
        ForgetFailure(config);
        return 0;
    }

    /// <summary>A run that failed: logged, and notified once until a run succeeds again.</summary>
    private static void Failed(Config config, CliHost host, Exception error)
    {
        try
        {
            Logger.Create(config.LogFile, null, clock: host.Now)($"run failed: {error.GetType().Name}: {error.Message}");
            var state = Runner.LoadState(config.StateFile);
            if (state["failing"] is null)
            {
                if (config.Notify)
                    host.Notify("tool-watchdog couldn't run", [$"{error.GetType().Name}: {error.Message}", $"Log: {config.LogFile}"]);
                state["failing"] = host.Now().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                Runner.SaveState(config.StateFile, state);
            }
        }
        catch (Exception)
        {
            // nothing more can be done: the exit code says it failed
        }
    }

    private static void ForgetFailure(Config config)
    {
        var state = Runner.LoadState(config.StateFile);
        if (state.Remove("failing"))
            Runner.SaveState(config.StateFile, state);
    }

    private static int Autostart(Config config, string action, CliHost host)
    {
        var task = host.Task();
        switch (action)
        {
            case "on":
                var background = Path.Combine(host.ProgramDir, BackgroundExe);
                var exe = File.Exists(background) ? background : Path.Combine(host.ProgramDir, "tool-watchdog.exe");
                var schedule = new TaskSchedule
                {
                    AtLogon = true,
                    Every = TimeSpan.FromMinutes(config.EveryMinutes),
                    AfterUnlock = config.AfterUnlockMinutes is { } u ? TimeSpan.FromMinutes(u) : null,
                    KeepRunning = false,
                    TimeLimit = TimeSpan.FromMinutes(5),
                };
                var line = task.Enable(exe, ["--config", config.Path, "run"], Path.GetDirectoryName(config.Path), schedule);
                host.Out.WriteLine($"Autostart is on: '{TaskName}' runs every {config.EveryMinutes} minutes, at logon" +
                                   (config.AfterUnlockMinutes is { } m ? $" and {m} minute(s) after each unlock" : "") +
                                   $", and has run now. It checks only within {config.Window.Text}.\n  {line}");
                return 0;
            case "off":
                host.Out.WriteLine(task.Disable() ? "Autostart is off." : "Autostart was already off.");
                return 0;
            case "restart":
                try
                {
                    host.Out.WriteLine(task.Restart() ? $"Started '{TaskName}' now." : "Autostart is off, so there's nothing to restart.");
                }
                catch (InvalidOperationException error)
                {
                    host.Err.WriteLine(error.Message);
                    return 1;
                }
                return 0;
            default:
                var info = task.Status();
                if (info is null)
                    host.Out.WriteLine("Autostart is off. `tool-watchdog autostart on` turns it on.");
                else
                {
                    host.Out.WriteLine($"Autostart is on ('{TaskName}').");
                    foreach (var name in new[] { "state", "last_run", "last_result", "next_run", "command" })
                        host.Out.WriteLine($"  {name.Replace('_', ' '),-12} {info.GetValueOrDefault(name, "")}");
                }
                return 0;
        }
    }

    private static Options? Parse(IReadOnlyList<string> argv, CliHost host)
    {
        var options = new Options();
        var i = 0;
        for (; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            var arg = argv[i];
            if (arg is "-h" or "--help")
            {
                host.Out.Write(Help);
                return null;
            }
            if (arg == "--version")
            {
                host.Out.WriteLine(Version);
                return null;
            }
            if (arg == "--config")
            {
                if (++i >= argv.Count)
                    throw new ArgumentsException("argument --config: expected one argument");
                options.Config = argv[i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
                options.Config = arg["--config=".Length..];
            else
                throw new ArgumentsException($"unrecognized arguments: {arg}");
        }
        if (i >= argv.Count)
            throw new ArgumentsException("the following arguments are required: command");
        options.Command = argv[i++];
        if (!Commands.Contains(options.Command))
            throw new ArgumentsException($"argument command: invalid choice: '{options.Command}' (choose from {string.Join(", ", Commands.Select(c => $"'{c}'"))})");

        for (; i < argv.Count; i++)
        {
            var arg = argv[i];
            var name = arg.Split('=', 2)[0];
            switch (options.Command, name)
            {
                case (_, "-h" or "--help"):
                    host.Out.Write(Help);
                    return null;
                case ("check", "--json"):
                    options.Json = true;
                    break;
                case ("check", "--problems"):
                    options.Problems = true;
                    break;
                case ("check", "--brief"):
                    options.Brief = true;
                    break;
                case ("check" or "run", "--now"):
                    string text;
                    if (arg.StartsWith("--now=", StringComparison.Ordinal))
                        text = arg["--now=".Length..];
                    else if (++i < argv.Count)
                        text = argv[i];
                    else
                        throw new ArgumentsException("argument --now: expected one argument");
                    options.Now = DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var at) ? at : throw new ArgumentsException($"argument --now: expected yyyy-mm-ddThh:mm, not '{text}'");
                    break;
                default:
                    if (arg.StartsWith('-') && arg != "-")
                        throw new ArgumentsException($"unrecognized arguments: {arg}");
                    options.Args.Add(arg);
                    break;
            }
        }
        var (min, max, what) = options.Command switch
        {
            "autostart" => (1, 1, "action"),
            _ => (0, 0, ""),
        };
        if (options.Args.Count < min)
            throw new ArgumentsException($"the following arguments are required: {what}");
        if (options.Args.Count > max)
            throw new ArgumentsException($"unrecognized arguments: {string.Join(" ", options.Args.Skip(max))}");
        if (options.Command == "autostart" && options.Args[0] is not ("on" or "off" or "restart" or "status"))
            throw new ArgumentsException($"argument action: invalid choice: '{options.Args[0]}' (choose from 'on', 'off', 'restart', 'status')");
        return options;
    }
}
