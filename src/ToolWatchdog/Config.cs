using System.Globalization;
using System.Text.RegularExpressions;
using PyCompat;
using ReviewQueue;
using Tomlyn;
using Tomlyn.Model;
using WinBackground;

namespace ToolWatchdog;

/// <summary>Settings that can't be used, or a request that can't be met: the command line reports the message and
/// exits 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>How old a product may be: within so many hours, working days (Monday to Friday) or days. Text is the
/// number as the settings file wrote it, for the check's detail.</summary>
public sealed record Limit(string Unit, double Value, string Text);

/// <summary>An [[output]]: a product some tool should keep producing, and how fresh it should be.</summary>
public sealed record OutputRule(string Name, string Tool, IReadOnlyList<string> Files, string Date, Limit Limit,
    string? From, string? Until);

/// <summary>A [[log]]: a watcher's log, which should have a timestamped line within so many hours.</summary>
public sealed record LogRule(string Name, string File, double Hours);

/// <summary>[tasks]: scheduled tasks that must exist, be enabled, and have run within so many days without failing.</summary>
public sealed record TasksRule(IReadOnlyList<string> Names, int Days, string? RegisterHint)
{
    /// <summary>[tasks.settings] (0.7.0): the settings file each task's [schedule] is in, to spot a task registered before its
    /// settings changed.</summary>
    public IReadOnlyDictionary<string, TaskLink> Settings { get; init; } = new Dictionary<string, TaskLink>();
}

/// <summary>A task's tool settings file, and whether the task is a watcher or a job (how its schedule is fingerprinted).</summary>
public sealed record TaskLink(string File, TaskKind Kind);

/// <summary>A [[size]] (0.4.0): files that must stay within a budget, in lines and characters across them all, and in
/// characters for any one of them. Each budget is optional, but at least one is given. Files are globs, and ** matches
/// any depth of folders. No file matching is a problem only when Required.</summary>
public sealed record SizeRule(string Name, IReadOnlyList<string> Files, long? TotalLines, long? TotalChars, long? EachChars,
    bool Required);

/// <summary>An [[age]] (0.5.0): working files a tool should tidy away itself (documents downloaded, emails saved for a
/// handler, screenshots), none of which should be older than Days. Files are globs (** any depth); a match may be a file or
/// a folder, and a folder's age is that of the newest file in it. Nothing matching is fine: it only warns, never deletes.</summary>
public sealed record AgeRule(string Name, string Tool, IReadOnlyList<string> Files, int Days);

/// <summary>[schedule]: the hours and days that count as watched. A log is judged only inside them, and `run` (the
/// scheduled pass) does nothing outside them.</summary>
public sealed record Window(IReadOnlySet<DayOfWeek> Days, TimeOnly Start, TimeOnly End, string Text)
{
    public static Window Default { get; } = new(
        new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        new TimeOnly(8, 0), new TimeOnly(19, 0), "Mon-Fri 08:00-19:00");

    public bool Contains(DateTime when)
    {
        var time = TimeOnly.FromDateTime(when);
        return Days.Contains(when.DayOfWeek) && time >= Start && time < End;
    }
}

/// <summary>The settings file. See the README, and Template.
/// The file is read strictly: an unknown key or a value of the wrong type is an error that names it.</summary>
public sealed partial class Config
{
    public const string EnvConfig = "TOOL_WATCHDOG_CONFIG";

    // Explicit "\n" (a raw literal would take the .cs file's line endings), written with CRLF.
    public const string Template =
        "# tool-watchdog settings. See the README.\n" +
        "# base = \"base-config.toml\"     # settings shared with other tools\n" +
        "# root = \"..\"                   # files under it are shown relative to it (default: this folder)\n" +
        "\n" +
        "# [[output]]                     # a product a tool should keep producing\n" +
        "# name = \"Reports filed\"\n" +
        "# tool = \"my-tool\"               # who produces it, so a stale line says where to look\n" +
        "# files = \"reports/20*.md\"       # one pattern or a list; relative to this file\n" +
        "# date = \"name\"                  # name (a date in the file name), modified, or json:<field>\n" +
        "# working_days = 2               # or days, or hours\n" +
        "\n" +
        "# [[log]]                        # a watcher's log, with a timestamped line at least hourly\n" +
        "# name = \"my-watcher\"\n" +
        "# file = \"%LOCALAPPDATA%/my-tool/my-tool.log\"\n" +
        "\n" +
        "# [[size]]                       # files that must stay within a budget\n" +
        "# name = \"Instructions\"\n" +
        "# files = [\"docs/*.md\", \"rules/**/*.md\"]   # one pattern or a list; ** is any depth of folders\n" +
        "# total_lines = 200              # all the files together (each budget is optional)\n" +
        "# total_chars = 20000\n" +
        "# each_chars = 15000             # any one file\n" +
        "\n" +
        "# [[age]]                        # working files a tool should tidy away itself (warns; never deletes)\n" +
        "# name = \"Downloaded documents\"\n" +
        "# tool = \"my-tool\"\n" +
        "# files = \"%LOCALAPPDATA%/my-tool/documents/*\"   # files or folders; a folder's age is its newest file's\n" +
        "# days = 30\n" +
        "\n" +
        "# [tasks]                        # scheduled tasks: there, enabled, run lately, last run not failed\n" +
        "# names = [\"my-tool watch\"]\n" +
        "# days = 14\n" +
        "\n" +
        "# [schedule]                     # the hours watched: logs are judged, and `run` acts, only inside them\n" +
        "# days = \"Mon-Fri\"\n" +
        "# hours = \"08:00-19:00\"\n" +
        "# every_minutes = 15             # autostart: how often `run` runs\n" +
        "# after_unlock_minutes = 1       # and this long after each unlock (catches up after sleep); 0: no unlock trigger\n" +
        "# confirm_runs = 2               # `run` raises a problem only once it's been seen on this many runs in a row\n";

    public static readonly string[] KnownKeys =
        ["root", "output", "log", "size", "age", "tasks", "schedule", "confirm_runs", "notify", "state_file", "log_file", "review"];

    /// <summary>The watchdog's own [schedule] when the settings say nothing (0.7.0, as before): every 15 minutes, at logon and
    /// a minute after each unlock, checking Mon-Fri 08:00-19:00.</summary>
    public static readonly ScheduleSettings DefaultSchedule = new()
    {
        EveryMinutes = 15, AtLogon = true, AfterUnlockMinutes = 1,
        Days = new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        Hours = (TimeSpan.FromHours(8), TimeSpan.FromHours(19)),
    };

    /// <summary>The task's schedule (background-tools-lib's shared [schedule]).</summary>
    public ScheduleSettings Schedule { get; init; } = DefaultSchedule;

    public required string Path { get; init; }
    public required string Root { get; init; }
    public IReadOnlyList<OutputRule> Outputs { get; init; } = [];
    public IReadOnlyList<LogRule> Logs { get; init; } = [];
    public IReadOnlyList<SizeRule> Sizes { get; init; } = [];
    public IReadOnlyList<AgeRule> Ages { get; init; } = [];
    public TasksRule? Tasks { get; init; }
    public Window Window { get; init; } = Window.Default;
    public int EveryMinutes { get; init; } = 15;
    public int? AfterUnlockMinutes { get; init; } = 1;

    /// <summary>`run` raises a problem only once it's been seen on this many runs in a row (0.3.0): a blip on waking,
    /// when every tool starts at once and the watchdog can look before the others have caught up, doesn't notify.</summary>
    public int ConfirmRuns { get; init; } = 2;
    public bool Notify { get; init; } = true;
    public required string StateFile { get; init; }
    public required string LogFile { get; init; }
    public ReviewSettings? Review { get; init; }

    /// <summary>A path setting as a full path: ~ and %VARIABLES% (the base's [paths] first) expanded, relative to the
    /// settings file's folder. Not normalised, so ".." stays as written, as Python's glob kept it.</summary>
    public required Func<string, string> Resolve { get; init; }

    public static string Local(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var local = env("LOCALAPPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(local) ? PyPath.Home(env) : local, "tool-watchdog"));
    }

    public static string DefaultPath(Func<string, string?>? env = null)
    {
        env ??= Environment;
        var appData = env("APPDATA");
        return PyPath.Pure(System.IO.Path.Combine(string.IsNullOrEmpty(appData) ? PyPath.Home(env) : appData, "tool-watchdog", "config.toml"));
    }

    public static string? Environment(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('\0') ? null : System.Environment.GetEnvironmentVariable(name);

    [GeneratedRegex(@"^([01]\d|2[0-4]):([0-5]\d)-([01]\d|2[0-4]):([0-5]\d)\z", RegexOptions.CultureInvariant)]
    private static partial Regex HoursPattern();

    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static Config Load(string? path = null, Func<string, string?>? env = null)
    {
        env ??= Environment;
        var given = !string.IsNullOrEmpty(path) ? path : env(EnvConfig) is { Length: > 0 } fromEnv ? fromEnv : DefaultPath(env);
        var full = PyPath.Resolve(given);
        if (!File.Exists(full))
            throw new UsageException($"No settings file at {full}. Run `tool-watchdog config` to create one.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                text = text[1..];
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message.TrimEnd());
        }
        catch (Exception error) when (error is not (IOException or UnauthorizedAccessException or UsageException))
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        foreach (var key in raw.Keys)
            if (!KnownKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", KnownKeys)}).");

        var dir = System.IO.Path.GetDirectoryName(full)!;
        var expandWith = env;
        string Expand(string value) => PyPath.ExpandVars(PyPath.ExpandUser(value, expandWith), expandWith);
        string Resolve(string value)
        {
            var expanded = Expand(value).Replace('/', '\\');
            return PyPath.IsAbsolute(expanded) ? expanded : System.IO.Path.Join(dir, expanded);
        }
        string Full(string value) => PyPath.Absolute(Resolve(value));

        UsageException Bad(string where, string what) => new($"{full}: {where} {what}.");
        void Strict(TomlTable table, string where, params string[] keys)
        {
            foreach (var key in table.Keys)
                if (!keys.Contains(key))
                    throw new UsageException($"{full}: {where} has an unknown setting '{key}' (known: {string.Join(", ", keys)}).");
        }
        string Text(TomlTable table, string key, string where, bool required = false, string? fallback = null)
        {
            if (!table.TryGetValue(key, out var value))
                return required ? throw Bad(where, $"needs {key}") : fallback!;
            return value is string { Length: > 0 } s ? s : throw Bad(where, $"{key} should be text, and not empty");
        }
        (double Value, string Text)? Number(TomlTable table, string key, string where)
        {
            if (!table.TryGetValue(key, out var value))
                return null;
            return value switch
            {
                long n when n >= 0 => (n, n.ToString(CultureInfo.InvariantCulture)),
                double d when d >= 0 => (d, PyRepr(d)),
                _ => throw Bad(where, $"{key} should be a number, 0 or more"),
            };
        }
        string? Day(TomlTable table, string key, string where)
        {
            if (!table.TryGetValue(key, out var value))
                return null;
            var text = value switch
            {
                string s => s,
                TomlDateTime d => d.ToString(),
                _ => throw Bad(where, $"{key} should be a date (yyyy-mm-dd)"),
            };
            return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? text.Trim() : throw Bad(where, $"{key} should be a date (yyyy-mm-dd), not '{text}'");
        }
        IEnumerable<TomlTable> Tables(string key)
        {
            if (!raw.TryGetValue(key, out var value))
                return [];
            return value is TomlTableArray array ? array : throw new UsageException($"{full}: {key} should be [[{key}]] tables.");
        }

        var outputs = new List<OutputRule>();
        foreach (var table in Tables("output"))
        {
            var where = $"[[output]] {(table.TryGetValue("name", out var n) ? n : "(no name)")}";
            Strict(table, where, "name", "tool", "files", "date", "days", "working_days", "hours", "from", "until");
            var files = table.TryGetValue("files", out var f)
                ? f switch
                {
                    string one => [one],
                    TomlArray many when many.Count > 0 && many.All(x => x is string { Length: > 0 }) => many.Cast<string>().ToList(),
                    _ => throw Bad(where, "files should be a pattern or a list of them"),
                }
                : throw Bad(where, "needs files");
            var date = Text(table, "date", where, fallback: "modified");
            if (date is not ("name" or "modified") && !(date.StartsWith("json:", StringComparison.Ordinal) && date.Length > 5))
                throw Bad(where, $"date should be name, modified or json:<field>, not '{date}'");
            var limits = new[] { "hours", "working_days", "days" }.Select(k => (k, Number(table, k, where))).Where(x => x.Item2 is not null).ToList();
            if (limits.Count > 1)
                throw Bad(where, "should give one of hours, working_days or days");
            var limit = limits.Count == 1
                ? new Limit(limits[0].k, limits[0].Item2!.Value.Value, limits[0].Item2!.Value.Text)
                : new Limit("days", 7, "7");
            if (limit.Unit != "hours" && limit.Value != Math.Floor(limit.Value))
                throw Bad(where, $"{limit.Unit} should be a whole number");
            outputs.Add(new OutputRule(Text(table, "name", where, required: true), Text(table, "tool", where, fallback: ""),
                files.Select(Resolve).ToList(), date, limit, Day(table, "from", where), Day(table, "until", where)));
        }

        var logs = new List<LogRule>();
        foreach (var table in Tables("log"))
        {
            var where = $"[[log]] {(table.TryGetValue("name", out var n) ? n : "(no name)")}";
            Strict(table, where, "name", "file", "hours");
            logs.Add(new LogRule(Text(table, "name", where, required: true), Full(Text(table, "file", where, required: true)),
                Number(table, "hours", where)?.Value ?? 2));
        }

        var sizes = new List<SizeRule>();
        foreach (var table in Tables("size"))
        {
            var where = $"[[size]] {(table.TryGetValue("name", out var n) ? n : "(no name)")}";
            Strict(table, where, "name", "files", "total_lines", "total_chars", "each_chars", "required");
            var files = table.TryGetValue("files", out var f)
                ? f switch
                {
                    string { Length: > 0 } one => [one],
                    TomlArray many when many.Count > 0 && many.All(x => x is string { Length: > 0 }) => many.Cast<string>().ToList(),
                    _ => throw Bad(where, "files should be a pattern or a list of them"),
                }
                : throw Bad(where, "needs files");
            long? Budget(string key) => Number(table, key, where) is { } b
                ? b.Value >= 1 && b.Value == Math.Floor(b.Value) ? (long)b.Value : throw Bad(where, $"{key} should be a whole number, 1 or more")
                : null;
            var (lines, chars, each) = (Budget("total_lines"), Budget("total_chars"), Budget("each_chars"));
            if (lines is null && chars is null && each is null)
                throw Bad(where, "needs a budget: total_lines, total_chars or each_chars");
            var required = table.TryGetValue("required", out var req)
                ? req is bool rb ? rb : throw Bad(where, "required should be true or false")
                : false;
            sizes.Add(new SizeRule(Text(table, "name", where, required: true), files.Select(Resolve).ToList(), lines, chars, each, required));
        }

        var ages = new List<AgeRule>();
        foreach (var table in Tables("age"))
        {
            var where = $"[[age]] {(table.TryGetValue("name", out var n) ? n : "(no name)")}";
            Strict(table, where, "name", "tool", "files", "days");
            var files = table.TryGetValue("files", out var f)
                ? f switch
                {
                    string { Length: > 0 } one => [one],
                    TomlArray many when many.Count > 0 && many.All(x => x is string { Length: > 0 }) => many.Cast<string>().ToList(),
                    _ => throw Bad(where, "files should be a pattern or a list of them"),
                }
                : throw Bad(where, "needs files");
            var days = Number(table, "days", where) is { } d
                ? d.Value >= 1 && d.Value == Math.Floor(d.Value) ? (int)d.Value : throw Bad(where, "days should be a whole number, 1 or more")
                : throw Bad(where, "needs days, the oldest a file there may be");
            ages.Add(new AgeRule(Text(table, "name", where, required: true), Text(table, "tool", where, fallback: ""), files.Select(Resolve).ToList(), days));
        }

        TasksRule? tasks = null;
        if (raw.TryGetValue("tasks", out var t))
        {
            if (t is not TomlTable table)
                throw new UsageException($"{full}: [tasks] should be a table.");
            Strict(table, "[tasks]", "names", "days", "register_hint", "settings");
            var names = table.TryGetValue("names", out var list)
                ? list is TomlArray a && a.All(x => x is string { Length: > 0 }) ? a.Cast<string>().ToList() : throw Bad("[tasks]", "names should be a list of task names")
                : [];
            var days = Number(table, "days", "[tasks]")?.Value ?? 14;
            // [tasks.settings] (0.7.0): a task's tool settings file, so its [schedule] can be compared with the task.
            var links = new Dictionary<string, TaskLink>(StringComparer.Ordinal);
            if (table.TryGetValue("settings", out var sv))
            {
                if (sv is not TomlTable settingsTable)
                    throw Bad("[tasks.settings]", "should be a table of task names and settings files");
                foreach (var (task, value) in settingsTable)
                {
                    if (!names.Contains(task))
                        throw Bad("[tasks.settings]", $"names '{task}', which isn't in [tasks] names");
                    links[task] = value switch
                    {
                        string { Length: > 0 } file => new TaskLink(Full(file), TaskKind.Job),
                        TomlTable inline when inline.Keys.All(k => k is "file" or "kind")
                            && inline.TryGetValue("file", out var f) && f is string { Length: > 0 } fs => new TaskLink(Full(fs),
                                !inline.TryGetValue("kind", out var k) ? TaskKind.Job
                                : k is "job" ? TaskKind.Job : k is "watcher" ? TaskKind.Watcher
                                : throw Bad("[tasks.settings]", $"'{task}': kind should be \"job\" or \"watcher\"")),
                        _ => throw Bad("[tasks.settings]", $"'{task}' should be a settings file, or {{ file = \"x.toml\", kind = \"watcher\" }}"),
                    };
                }
            }
            tasks = new TasksRule(names, (int)days, table.TryGetValue("register_hint", out _) ? Text(table, "register_hint", "[tasks]") : null)
            {
                Settings = links,
            };
        }

        // [schedule]: background-tools-lib's shared keys (0.7.0). confirm_runs is the watchdog's own, so it's a top-level
        // setting now; [schedule] confirm_runs is the old place, still read, but not with the top-level one.
        var schedule = DefaultSchedule;
        var window = Window.Default;
        int? confirmOld = null;
        if (raw.TryGetValue("schedule", out var s))
        {
            if (s is not TomlTable table)
                throw new UsageException($"{full}: [schedule] should be a table.");
            var shared = new TomlTable();
            foreach (var (key, value) in table)
                if (key != "confirm_runs" && !(key == "days" && value is string everyDay && everyDay.Trim().Equals("every day", StringComparison.OrdinalIgnoreCase)))
                    shared[key] = value;
            var allDays = table.TryGetValue("days", out var dv) && dv is string dt && dt.Trim().Equals("every day", StringComparison.OrdinalIgnoreCase);
            try
            {
                schedule = ScheduleSettings.Parse(shared, DefaultSchedule, TaskKind.Job, $"{full}: [schedule]");
            }
            catch (FormatException error)
            {
                throw new UsageException(error.Message.TrimEnd());
            }
            if (allDays)
                schedule = schedule with { Days = null };
            if (schedule.Hours is { } h && h.To <= h.From)
                throw Bad("[schedule]", "hours should end after they start (the watchdog's window can't run past midnight)");
            if (Number(table, "confirm_runs", "[schedule]") is { } c)
                confirmOld = c.Value >= 1 && c.Value == Math.Floor(c.Value) ? (int)c.Value : throw Bad("[schedule]", "confirm_runs should be a whole number, 1 or more");
            var daysText = table.ContainsKey("days") ? (table["days"] is string ds ? ds : string.Join(", ", ((TomlArray)table["days"]).Cast<string>())) : "Mon-Fri";
            var hoursText = table.TryGetValue("hours", out var hv) && hv is string hs ? hs : "08:00-19:00";
            var (from, to) = schedule.Hours ?? (TimeSpan.FromHours(8), TimeSpan.FromHours(19));
            window = new Window(schedule.Days is { } sd ? new HashSet<DayOfWeek>(sd) : [.. Enum.GetValues<DayOfWeek>()],
                TimeOnly.FromTimeSpan(from), to >= TimeSpan.FromHours(24) ? TimeOnly.MaxValue : TimeOnly.FromTimeSpan(to), $"{daysText} {hoursText}");
        }
        var confirm = 2;
        if (raw.ContainsKey("confirm_runs"))
        {
            if (confirmOld is not null)
                throw new UsageException($"{full}: confirm_runs is set both at the top and in [schedule]: keep the top-level one ([schedule] is the old place).");
            confirm = Number(raw, "confirm_runs", "confirm_runs") is { } top && top.Value >= 1 && top.Value == Math.Floor(top.Value)
                ? (int)top.Value : throw new UsageException($"{full}: confirm_runs should be a whole number, 1 or more.");
        }
        else if (confirmOld is { } old)
            confirm = old;
        var every = schedule.EveryMinutes ?? 15;
        int? unlock = schedule.AfterUnlockMinutes > 0 ? schedule.AfterUnlockMinutes : null;

        ReviewSettings? review = null;
        if (raw.TryGetValue("review", out var r))
        {
            try
            {
                review = ReviewSettings.Read(r, full, Full);
            }
            catch (FormatException error)
            {
                throw new UsageException(error.Message);
            }
        }
        var notify = !raw.TryGetValue("notify", out var nv) || (nv is bool b ? b : throw new UsageException($"{full}: notify should be true or false."));
        string? Top(string key) => raw.TryGetValue(key, out var value)
            ? value is string { Length: > 0 } text ? text : throw new UsageException($"{full}: {key} should be text, and not empty.")
            : null;

        return new Config
        {
            Path = full,
            Root = Top("root") is { } root ? Full(root) : dir,
            Outputs = outputs,
            Logs = logs,
            Sizes = sizes,
            Ages = ages,
            Tasks = tasks,
            Window = window,
            Schedule = schedule,
            EveryMinutes = every,
            AfterUnlockMinutes = unlock,
            ConfirmRuns = confirm,
            Notify = notify,
            StateFile = Top("state_file") is { } st ? Full(st) : System.IO.Path.Combine(Local(env), "state.json"),
            LogFile = Top("log_file") is { } log ? Full(log) : System.IO.Path.Combine(Local(env), "tool-watchdog.log"),
            Review = review,
            Resolve = Resolve,
        };
    }

    /// <summary>"Mon-Fri", "Mon, Wed, Fri", "Sat-Sun" or "every day" as days of the week; null if it isn't one.</summary>
    internal static HashSet<DayOfWeek>? ParseDays(string text)
    {
        var days = new HashSet<DayOfWeek>();
        if (text.Trim().Equals("every day", StringComparison.OrdinalIgnoreCase))
            return [.. Enum.GetValues<DayOfWeek>()];
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-', StringSplitOptions.TrimEntries);
            int Index(string name) => Array.FindIndex(DayNames, d => name.Length >= 3 && d.Equals(name[..3], StringComparison.OrdinalIgnoreCase));
            if (ends.Length is < 1 or > 2 || ends.Any(e => Index(e) < 0))
                return null;
            var (from, to) = (Index(ends[0]), Index(ends[^1]));
            for (var d = from; ; d = (d + 1) % 7)
            {
                days.Add((DayOfWeek)d);
                if (d == to)
                    break;
            }
        }
        return days.Count > 0 ? days : null;
    }

    /// <summary>A float as Python's repr writes it ("2.0", "1.5"), for a detail that quotes the setting.</summary>
    internal static string PyRepr(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e16 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);

    public static string CreateDefault(string? path, Func<string, string?>? env = null)
    {
        var full = PyPath.Resolve(!string.IsNullOrEmpty(path) ? path : DefaultPath(env));
        if (!File.Exists(full))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Template.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(false));
        }
        return full;
    }
}
