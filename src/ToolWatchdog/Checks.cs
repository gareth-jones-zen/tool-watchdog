using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinBackground;

namespace ToolWatchdog;

/// <summary>One check's result. Key orders follow the Python original's dictionaries, so --json reads the same.</summary>
public sealed class Check
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string Tool { get; init; } = "";
    public bool Stale { get; set; }
    public bool Skipped { get; set; }
    public bool HasNewest { get; set; }
    public DateTime? Newest { get; set; }
    public string? File { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>A size check's measure (0.4.0): lines and characters across its files, and the files, as shown.</summary>
    public long? Lines { get; set; }
    public long? Chars { get; set; }
    public IReadOnlyList<string>? Files { get; set; }

    /// <summary>The key a problem is remembered by between runs.</summary>
    public string Key => $"{Kind}|{Name}";

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["kind"] = Kind, ["name"] = Name, ["tool"] = Tool, ["stale"] = Stale, ["skipped"] = Skipped };
        if (HasNewest)
            json["newest"] = Newest is { } n ? n.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : null;
        if (File is not null)
            json["file"] = File;
        if (Kind == "size")
        {
            json["lines"] = Lines;
            json["chars"] = Chars;
            json["files"] = new JsonArray([.. (Files ?? []).Select(f => (JsonNode?)f)]);
        }
        json["detail"] = Detail;
        return json;
    }

    /// <summary>An age as hours and minutes (0.3.0; "1.1 hours" read oddly): "1h 03m", "45m", "26h 10m".</summary>
    internal static string Age(TimeSpan age)
    {
        var minutes = Math.Max(0, (int)Math.Round(age.TotalMinutes));
        return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
    }

    /// <summary>"1 hour", "2 hours", "1.5 hours": the settings' number as written, with its unit.</summary>
    internal static string Plural(string number, string unit) => number == "1" ? $"1 {unit}" : $"{number} {unit}s";

    /// <summary>One line: "STALE output Preps written: 3 working day(s) old (expected within 2)  [meeting-preps]".</summary>
    public string Line()
    {
        var mark = Skipped ? "-" : Stale ? "STALE" : "ok";
        var where = Stale && Tool.Length > 0 && Kind == "output" ? $"  [{Tool}]" : "";
        return $"{mark,-6}{Kind,-7}{Name}: {Detail}{where}";
    }
}

/// <summary>A task's state as Task Scheduler reports it: Ready, Running, Disabled..., when it last ran, and the result.</summary>
public sealed record TaskInfo(string State, DateTime? Last, long Result);

public static partial class Checks
{
    /// <summary>Task Scheduler results that aren't failures: success, running, never run yet, stopped by a restart.</summary>
    public static readonly IReadOnlySet<long> TaskOk = new HashSet<long> { 0, 0x41301, 0x41303, 0x41306 };

    [GeneratedRegex(@"^(\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex Stamp();   // doc-filer writes 2026/11/10

    [GeneratedRegex(@"(20\d{2})-(\d{2})(?:-(\d{2}))?", RegexOptions.CultureInvariant)]
    private static partial Regex NameDate();

    /// <summary>Every check, in the settings' order: outputs, logs, sizes, ages, then tasks.</summary>
    public static List<Check> Run(Config config, DateTime now, Func<IReadOnlyList<string>, IReadOnlyDictionary<string, TaskInfo>> taskInfo)
    {
        var checks = config.Outputs.Select(r => Output(config, r, now)).ToList();
        checks.AddRange(config.Logs.Select(r => Log(config, r, now)));
        checks.AddRange(config.Sizes.Select(r => Size(config, r)));
        checks.AddRange(config.Ages.Select(r => Age(config, r, now)));
        if (config.Tasks is { Names.Count: > 0 } tasks)
            checks.AddRange(Tasks(tasks, now, taskInfo));
        return checks;
    }

    /// <summary>Weekdays after start, up to and including end.</summary>
    public static int WorkingDaysBetween(DateOnly start, DateOnly end)
    {
        var days = 0;
        for (var day = start; day < end;)
        {
            day = day.AddDays(1);
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                days++;
        }
        return days;
    }

    // ---- outputs --------------------------------------------------------------------------------------------------

    public static Check Output(Config config, OutputRule rule, DateTime now)
    {
        var check = new Check { Kind = "output", Name = rule.Name, Tool = rule.Tool };
        var today = DateOnly.FromDateTime(now);
        if ((rule.From is { } from && today < DateOnly.Parse(from, CultureInfo.InvariantCulture)) ||
            (rule.Until is { } until && today >= DateOnly.Parse(until, CultureInfo.InvariantCulture)))
        {
            check.Skipped = true;
            check.Detail = $"not checked now (from {rule.From ?? "-"} until {rule.Until ?? "-"})";
            return check;
        }
        var (when, path) = Newest(rule);
        if (when is not { } newest)
        {
            check.Stale = true;
            check.Detail = "nothing found";
            return check;
        }
        check.HasNewest = true;
        check.Newest = newest;
        check.File = Shown(config, path!);
        switch (rule.Limit.Unit)
        {
            case "hours":
                var hours = (now - newest).TotalHours;
                check.Stale = hours > rule.Limit.Value;
                check.Detail = $"{Check.Age(now - newest)} old (expected within {Check.Plural(rule.Limit.Text, "hour")})";
                break;
            case "working_days":
                var working = WorkingDaysBetween(DateOnly.FromDateTime(newest), today);
                check.Stale = working > rule.Limit.Value;
                check.Detail = $"{Check.Plural(working.ToString(CultureInfo.InvariantCulture), "working day")} old (expected within {Check.Plural(rule.Limit.Text, "working day")})";
                break;
            default:
                var days = today.DayNumber - DateOnly.FromDateTime(newest).DayNumber;
                check.Stale = days > rule.Limit.Value;
                check.Detail = $"{Check.Plural(days.ToString(CultureInfo.InvariantCulture), "day")} old (expected within {Check.Plural(rule.Limit.Text, "day")})";
                break;
        }
        return check;
    }

    /// <summary>A file as a check shows it: relative to root when it's under it, otherwise in full.</summary>
    internal static string Shown(Config config, string path) =>
        path.StartsWith(config.Root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(config.Root, path) : path;

    /// <summary>The newest product's time and file, across the rule's patterns (the first found wins a tie).</summary>
    public static (DateTime? When, string? Path) Newest(OutputRule rule)
    {
        (DateTime When, string Path)? best = null;
        foreach (var file in rule.Files.SelectMany(p => Glob(p)))
        {
            DateTime? when = null;
            if (rule.Date == "name")
            {
                var m = NameDate().Match(Path.GetFileName(file));
                if (m.Success && DateOnly.TryParseExact($"{m.Groups[1].Value}-{m.Groups[2].Value}-{(m.Groups[3].Success ? m.Groups[3].Value : "01")}",
                        "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    when = d.ToDateTime(TimeOnly.MinValue);
            }
            else if (rule.Date == "modified")
            {
                try
                {
                    when = System.IO.File.GetLastWriteTime(file);
                }
                catch (IOException)
                {
                }
            }
            else
                when = JsonTime(file, rule.Date[5..]);
            if (when is { } w && (best is null || w > best.Value.When))
                best = (w, file);
        }
        return best is { } b ? (b.When, b.Path) : (null, null);
    }

    /// <summary>An ISO time in a JSON file's top-level field, as its clock reads (any offset is dropped, not
    /// converted, as the Python did); null if the file, the field or the time can't be read.</summary>
    internal static DateTime? JsonTime(string file, string field)
    {
        try
        {
            var node = JsonNode.Parse(System.IO.File.ReadAllText(file, Encoding.UTF8));
            if (node is not JsonObject obj || obj[field] is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length == 0)
                return null;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at) ? at.DateTime : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // ---- sizes ----------------------------------------------------------------------------------------------------

    /// <summary>A [[size]] (0.4.0): the files' lines and characters against the budgets. Line endings are counted as
    /// one character whatever the file uses (so a checkout's CRLF doesn't change the count), and a BOM isn't counted.
    /// A file matched by two patterns counts once.</summary>
    public static Check Size(Config config, SizeRule rule)
    {
        var check = new Check { Kind = "size", Name = rule.Name };
        var files = rule.Files.SelectMany(p => Glob(p, recursive: true)).Where(System.IO.File.Exists)
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => (Full: f, Shown: Shown(config, f))).OrderBy(f => f.Shown, StringComparer.OrdinalIgnoreCase).ToList();
        var budget = Budget(rule);
        check.Files = files.Select(f => f.Shown).ToList();
        if (files.Count == 0)
        {
            check.Stale = rule.Required;
            check.Detail = rule.Required ? "nothing found" : $"no files yet ({budget})";
            return check;
        }
        var measured = new List<(string Shown, long Lines, long Chars)>();
        foreach (var (full, shown) in files)
        {
            try
            {
                var (lines, chars) = Measure(System.IO.File.ReadAllText(full, Encoding.UTF8));
                measured.Add((shown, lines, chars));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                check.Stale = true;
                check.Detail = $"couldn't read {shown}: {error.Message}";
                return check;
            }
        }
        var (totalLines, totalChars) = (measured.Sum(m => m.Lines), measured.Sum(m => m.Chars));
        check.Lines = totalLines;
        check.Chars = totalChars;
        var detail = $"{Check.Plural(N(totalLines), "line")}, {Check.Plural(N(totalChars), "character")}" +
                     (files.Count > 1 ? $" in {files.Count} files" : "") + $" ({budget})";
        var over = new List<string>();
        if (rule.TotalLines is { } maxLines && totalLines > maxLines)
            over.Add(Check.Plural(N(totalLines - maxLines), "line"));
        if (rule.TotalChars is { } maxChars && totalChars > maxChars)
            over.Add(Check.Plural(N(totalChars - maxChars), "character"));
        var problems = new List<string>();
        if (over.Count > 0)
            problems.Add("over by " + string.Join(" and ", over));
        if (rule.EachChars is { } maxEach && measured.Where(m => m.Chars > maxEach).ToList() is { Count: > 0 } big)
            problems.Add($"over {N(maxEach)} each: " + string.Join(", ", big.Select(m => $"{m.Shown} ({N(m.Chars)})")));
        check.Stale = problems.Count > 0;
        check.Detail = problems.Count > 0 ? $"{detail}: {string.Join("; ", problems)}" : detail;
        return check;
    }

    // ---- ages -----------------------------------------------------------------------------------------------------

    /// <summary>An [[age]] (0.5.0): every file or folder the patterns match, against the oldest allowed. A folder's age is
    /// its newest file's (its own time if it's empty), so one still being written to isn't old. Warns only.</summary>
    public static Check Age(Config config, AgeRule rule, DateTime now)
    {
        var check = new Check { Kind = "age", Name = rule.Name, Tool = rule.Tool };
        var limit = $"limit {Check.Plural(rule.Days.ToString(CultureInfo.InvariantCulture), "day")}";
        var entries = rule.Files.SelectMany(p => Glob(p, recursive: true)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (entries.Count == 0)
        {
            check.Detail = $"nothing there ({limit})";
            return check;
        }
        var aged = new List<(string Shown, DateTime When)>();
        foreach (var entry in entries)
        {
            try
            {
                DateTime when;
                if (Directory.Exists(entry))
                {
                    var inside = Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories).Select(System.IO.File.GetLastWriteTime).ToList();
                    when = inside.Count > 0 ? inside.Max() : Directory.GetLastWriteTime(entry);
                }
                else
                    when = System.IO.File.GetLastWriteTime(entry);
                aged.Add((Shown(config, entry), when));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Gone or locked since it was listed: the next pass sees it.
            }
        }
        var old = aged.Where(a => a.When < now.AddDays(-rule.Days)).OrderBy(a => a.When).ToList();
        string Days(DateTime when) => $"{(int)(now - when).TotalDays}d";
        if (old.Count > 0)
        {
            check.Stale = true;
            check.Detail = $"{old.Count} of {aged.Count} older than {Check.Plural(rule.Days.ToString(CultureInfo.InvariantCulture), "day")}: " +
                           string.Join(", ", old.Take(5).Select(a => $"{a.Shown} ({Days(a.When)})")) + (old.Count > 5 ? ", ..." : "") +
                           (rule.Tool.Length > 0 ? $": is {rule.Tool} tidying up?" : "");
        }
        else
            check.Detail = $"{aged.Count} there, the oldest {(aged.Count > 0 ? Days(aged.Min(a => a.When)) : "0d")} ({limit})";
        return check;
    }

    /// <summary>"budget 200 lines, 20,000", "budget 20,000 characters", "budget 200 lines, 15,000 each".</summary>
    internal static string Budget(SizeRule rule)
    {
        var parts = new List<string>();
        if (rule.TotalLines is { } lines)
            parts.Add(Check.Plural(N(lines), "line"));
        if (rule.TotalChars is { } chars)
            parts.Add(parts.Count > 0 ? N(chars) : Check.Plural(N(chars), "character"));
        if (rule.EachChars is { } each)
            parts.Add(parts.Count > 0 ? $"{N(each)} each" : $"{Check.Plural(N(each), "character")} each");
        return "budget " + string.Join(", ", parts);
    }

    /// <summary>A text's lines (a last line without a newline counts; an empty text has none) and characters, with
    /// CRLF and CR as one, and without a BOM.</summary>
    internal static (long Lines, long Chars) Measure(string text)
    {
        if (text.StartsWith('﻿'))
            text = text[1..];
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
        return (lines, text.Length);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    // ---- glob -----------------------------------------------------------------------------------------------------

    /// <summary>Files matching a pattern, as Python's glob.glob: * and ? within one folder level, matching ignoring
    /// case; a wildcard skips names starting with "." unless it starts with one; folders in the middle of a pattern
    /// must be folders; the order is the file system's. The parts without wildcards are kept as written, so "..\x"
    /// stays in the result. With recursive (as recursive=True), a ** part is this folder and every folder below it,
    /// skipping hidden ones; without it, ** is the same as *.</summary>
    public static IEnumerable<string> Glob(string pattern, bool recursive = false)
    {
        var root = Path.GetPathRoot(pattern) ?? "";
        var parts = pattern[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Any(HasMagic))
            return System.IO.File.Exists(pattern) || Directory.Exists(pattern) ? [pattern] : [];
        IEnumerable<string> found = [root.Length == 0 ? "" : root];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var last = i == parts.Length - 1;
            if (recursive && part == "**")
            {
                found = found.SelectMany(folder => (IEnumerable<string>)[folder, .. Below(folder, last)]).ToList();
                continue;
            }
            if (!HasMagic(part))
            {
                found = found.Select(f => Join(f, part)).Where(p => last ? System.IO.File.Exists(p) || Directory.Exists(p) : Directory.Exists(p)).ToList();
                continue;
            }
            var match = new Regex("^" + Regex.Escape(part).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            var hidden = part.StartsWith('.');
            found = found.SelectMany(folder =>
            {
                var at = folder.Length == 0 ? "." : folder;
                if (!Directory.Exists(at))
                    return [];
                try
                {
                    var entries = last ? Directory.EnumerateFileSystemEntries(at) : Directory.EnumerateDirectories(at);
                    return entries.Select(Path.GetFileName)
                        .Where(n => n is not null && (hidden || !n.StartsWith('.')) && match.IsMatch(n))
                        .Select(n => Join(folder, n!)).ToList();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return [];
                }
            }).ToList();
        }
        return found;
    }

    /// <summary>Every folder below a folder (and with files, every file too), skipping hidden names, as ** finds them.</summary>
    private static List<string> Below(string folder, bool files)
    {
        var at = folder.Length == 0 ? "." : folder;
        var result = new List<string>();
        if (!Directory.Exists(at))
            return result;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(at))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.'))
                    continue;
                var path = Join(folder, name);
                if (Directory.Exists(entry))
                {
                    result.Add(path);
                    // not into a junction or link: one pointing back up would never end
                    if (!System.IO.File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                        result.AddRange(Below(path, files));
                }
                else if (files)
                    result.Add(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return result;
    }

    private static bool HasMagic(string part) => part.IndexOfAny(['*', '?', '[']) >= 0;

    private static string Join(string folder, string name) =>
        folder.Length == 0 ? name : folder.EndsWith('\\') || folder.EndsWith('/') ? folder + name : folder + "\\" + name;

    // ---- logs -----------------------------------------------------------------------------------------------------

    /// <summary>The last timestamped line in the last 8 KB of a log; null if it has none or can't be read.</summary>
    public static DateTime? LastLogged(string path)
    {
        string[] tail;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 8192), SeekOrigin.Begin);
            var bytes = new byte[stream.Length - stream.Position];
            stream.ReadExactly(bytes);
            tail = Encoding.UTF8.GetString(bytes).Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        for (var i = tail.Length - 1; i >= 0; i--)
            if (Stamp().Match(tail[i]) is { Success: true } m &&
                DateTime.TryParseExact(m.Groups[1].Value.Replace('/', '-').Replace(' ', 'T'), "yyyy-MM-ddTHH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                return when;
        return null;
    }

    public static Check Log(Config config, LogRule rule, DateTime now)
    {
        var when = LastLogged(rule.File);
        var stale = when is null || (config.Window.Contains(now) && (now - when.Value).TotalHours > rule.Hours);
        return new Check
        {
            Kind = "log", Name = rule.Name, Tool = rule.Name, Stale = stale, HasNewest = true, Newest = when,
            Detail = when is { } w ? $"last logged {w.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)}" : "never logged",
        };
    }

    // ---- tasks ----------------------------------------------------------------------------------------------------

    /// <summary>Each task's state, last run and last result, in one PowerShell call through Task Scheduler's COM
    /// object (the ScheduledTasks module took 14 seconds for a dozen tasks; COM isn't available under Native AOT).</summary>
    public static IReadOnlyDictionary<string, TaskInfo> TaskInfoFromWindows(IReadOnlyList<string> names)
    {
        var quoted = string.Join(",", names.Select(n => "'" + n.Replace("'", "''", StringComparison.Ordinal) + "'"));
        var script = "$s = New-Object -ComObject Schedule.Service; $s.Connect(); $root = $s.GetFolder('\\'); $out = @{}; " +
                     "$states = @{ 0 = 'Unknown'; 1 = 'Disabled'; 2 = 'Queued'; 3 = 'Ready'; 4 = 'Running' }; " +
                     $"foreach ($n in @({quoted})) {{ try {{ $t = $root.GetTask($n) }} catch {{ continue }}; " +
                     "$out[$n] = @{ state = $states[[int]$t.State]; " +
                     "last = if ($t.LastRunTime.Year -gt 2000) { $t.LastRunTime.ToString('s') } else { $null }; " +
                     "result = [int64]$t.LastTaskResult } }; $out | ConvertTo-Json -Compress";
        var done = Shell.PowerShell(script, 90);
        if (done.ExitCode != 0)
            throw new InvalidOperationException((done.StandardError.Length > 0 ? done.StandardError : done.StandardOutput).Trim() is var t && t.Length > 300 ? t[..300] : t);
        return ParseTaskInfo(done.StandardOutput);
    }

    internal static IReadOnlyDictionary<string, TaskInfo> ParseTaskInfo(string json)
    {
        var result = new Dictionary<string, TaskInfo>();
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject all)
            return result;
        foreach (var (name, node) in all)
        {
            if (node is not JsonObject task)
                continue;
            DateTime? last = task["last"] is JsonValue lv && lv.TryGetValue<string>(out var text)
                && DateTime.TryParseExact(text, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
            var code = task["result"] is JsonValue rv && rv.TryGetValue<long>(out var r) ? r : 0;
            result[name] = new TaskInfo(task["state"]?.GetValue<string>() ?? "Unknown", last, code);
        }
        return result;
    }

    public static List<Check> Tasks(TasksRule rule, DateTime now, Func<IReadOnlyList<string>, IReadOnlyDictionary<string, TaskInfo>> taskInfo)
    {
        IReadOnlyDictionary<string, TaskInfo> info;
        try
        {
            info = taskInfo(rule.Names);
        }
        catch (Exception error)
        {
            return [new Check { Kind = "task", Name = "scheduled tasks", Stale = true, Detail = $"couldn't read Task Scheduler: {error.GetType().Name}: {error.Message}" }];
        }
        var checks = new List<Check>();
        foreach (var name in rule.Names)
        {
            var check = new Check { Kind = "task", Name = name, Tool = name.Split(' ')[0] };
            if (!info.TryGetValue(name, out var task))
            {
                check.Stale = true;
                check.Detail = rule.RegisterHint is { } hint ? $"not registered ({hint})" : "not registered";
            }
            else if (task.State == "Disabled")
            {
                check.Stale = true;
                check.Detail = "disabled";
            }
            else
            {
                var problems = new List<string>();
                var result = task.Result & 0xFFFFFFFF;
                if (!TaskOk.Contains(result))
                    problems.Add($"last run failed (0x{result:X})");
                if (task.Last is not { } last)
                    problems.Add("never run");
                else if (Math.Floor((now - last).TotalDays) > rule.Days)
                    problems.Add($"not run since {last.ToString("dd MMM", CultureInfo.InvariantCulture)}");
                check.Stale = problems.Count > 0;
                check.HasNewest = true;
                check.Newest = task.Last;
                check.Detail = problems.Count > 0 ? string.Join("; ", problems)
                    : $"{task.State.ToLowerInvariant()}, last run {task.Last!.Value.ToString("ddd dd MMM HH:mm", CultureInfo.InvariantCulture)}";
            }
            checks.Add(check);
        }
        return checks;
    }
}
