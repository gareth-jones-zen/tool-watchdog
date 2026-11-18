using System.Globalization;
using System.Text.RegularExpressions;
using PyCompat;
using ReviewQueue;
using Tomlyn;
using Tomlyn.Model;

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
public sealed record TasksRule(IReadOnlyList<string> Names, int Days, string? RegisterHint);

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
        "# [tasks]                        # scheduled tasks: there, enabled, run lately, last run not failed\n" +
        "# names = [\"my-tool watch\"]\n" +
        "# days = 14\n" +
        "\n" +
        "# [schedule]                     # the hours watched: logs are judged, and `run` acts, only inside them\n" +
        "# days = \"Mon-Fri\"\n" +
        "# hours = \"08:00-19:00\"\n" +
        "# every_minutes = 15             # autostart: how often `run` runs\n" +
        "# after_unlock_minutes = 1       # and this long after each unlock (catches up after sleep)\n";

    public static readonly string[] KnownKeys =
        ["root", "output", "log", "tasks", "schedule", "notify", "state_file", "log_file", "review"];

    public required string Path { get; init; }
    public required string Root { get; init; }
    public IReadOnlyList<OutputRule> Outputs { get; init; } = [];
    public IReadOnlyList<LogRule> Logs { get; init; } = [];
    public TasksRule? Tasks { get; init; }
    public Window Window { get; init; } = Window.Default;
    public int EveryMinutes { get; init; } = 15;
    public int? AfterUnlockMinutes { get; init; } = 1;
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

        TasksRule? tasks = null;
        if (raw.TryGetValue("tasks", out var t))
        {
            if (t is not TomlTable table)
                throw new UsageException($"{full}: [tasks] should be a table.");
            Strict(table, "[tasks]", "names", "days", "register_hint");
            var names = table.TryGetValue("names", out var list)
                ? list is TomlArray a && a.All(x => x is string { Length: > 0 }) ? a.Cast<string>().ToList() : throw Bad("[tasks]", "names should be a list of task names")
                : [];
            var days = Number(table, "days", "[tasks]")?.Value ?? 14;
            tasks = new TasksRule(names, (int)days, table.TryGetValue("register_hint", out _) ? Text(table, "register_hint", "[tasks]") : null);
        }

        var window = Window.Default;
        var every = 15;
        int? unlock = 1;
        if (raw.TryGetValue("schedule", out var s))
        {
            if (s is not TomlTable table)
                throw new UsageException($"{full}: [schedule] should be a table.");
            Strict(table, "[schedule]", "days", "hours", "every_minutes", "after_unlock_minutes");
            var daysText = table.TryGetValue("days", out _) ? Text(table, "days", "[schedule]") : "Mon-Fri";
            var hoursText = table.TryGetValue("hours", out _) ? Text(table, "hours", "[schedule]") : "08:00-19:00";
            var m = HoursPattern().Match(hoursText);
            if (!m.Success)
                throw Bad("[schedule]", $"hours should be like \"08:00-19:00\", not '{hoursText}'");
            TimeOnly Clock(string h, string mm) => int.Parse(h, CultureInfo.InvariantCulture) == 24
                ? TimeOnly.MaxValue : new TimeOnly(int.Parse(h, CultureInfo.InvariantCulture), int.Parse(mm, CultureInfo.InvariantCulture));
            var (start, end) = (Clock(m.Groups[1].Value, m.Groups[2].Value), Clock(m.Groups[3].Value, m.Groups[4].Value));
            if (end <= start)
                throw Bad("[schedule]", "hours should end after they start");
            window = new Window(ParseDays(daysText) ?? throw Bad("[schedule]", $"days should be like \"Mon-Fri\" or \"Mon, Wed, Fri\", not '{daysText}'"),
                start, end, $"{daysText} {hoursText}");
            if (Number(table, "every_minutes", "[schedule]") is { } e)
                every = e.Value >= 1 && e.Value == Math.Floor(e.Value) ? (int)e.Value : throw Bad("[schedule]", "every_minutes should be a whole number, 1 or more");
            if (Number(table, "after_unlock_minutes", "[schedule]") is { } u)
                unlock = u.Value == Math.Floor(u.Value) ? (int)u.Value : throw Bad("[schedule]", "after_unlock_minutes should be a whole number");
        }

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
            Tasks = tasks,
            Window = window,
            EveryMinutes = every,
            AfterUnlockMinutes = unlock,
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
