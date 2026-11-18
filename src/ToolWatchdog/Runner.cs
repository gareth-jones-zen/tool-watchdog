using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ToolWatchdog;

/// <summary>The scheduled pass (`run`). Inside the watched hours it runs every check, and a problem it hasn't seen
/// before is raised once: a notification, and an item in the review queue for the next session. While it lasts it
/// isn't raised again; when it clears, its item is marked cleared. Outside the watched hours it does nothing.</summary>
public static class Runner
{
    /// <summary>What a run did, for the log and the tests.</summary>
    public sealed record Outcome(bool Watched, IReadOnlyList<Check> Checks, IReadOnlyList<Check> New, IReadOnlyList<string> Cleared);

    public static Outcome Run(Config config, CliHost host, Action<string> log)
    {
        var now = host.Now();
        if (!config.Window.Contains(now))
            return new Outcome(false, [], [], []);
        var checks = Checks.Run(config, now, host.TaskInfo);
        var problems = checks.Where(c => c.Stale).ToList();
        var state = LoadState(config.StateFile);
        var known = state["problems"]!.AsObject();
        var fresh = problems.Where(p => !known.ContainsKey(p.Key)).ToList();
        var cleared = known.Select(kv => kv.Key).Where(k => problems.All(p => p.Key != k)).ToList();
        var stamp = now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var queue = config.Review?.Open(() => host.Now());
        foreach (var check in fresh)
        {
            var entry = new JsonObject { ["first_seen"] = stamp };
            if (queue is not null)
            {
                var fields = new JsonObject
                {
                    ["title"] = $"{check.Name}: {check.Detail}" + (check.Kind == "output" && check.Tool.Length > 0 ? $" [{check.Tool}]" : ""),
                    ["kind"] = check.Kind,
                    ["name"] = check.Name,
                    ["tool"] = check.Tool,
                    ["first_seen"] = stamp,
                };
                entry["review_id"] = queue.Add(config.Review!.Source, "stale", fields);
            }
            known[check.Key] = entry;
        }
        foreach (var key in cleared)
        {
            if (queue is not null && known[key] is JsonObject old && old["first_seen"]?.GetValue<string>() is { } first)
            {
                var (kind, name) = (key.Split('|', 2)[0], key.Split('|', 2)[1]);
                queue.Update(config.Review!.Source, new JsonObject { ["kind"] = kind, ["name"] = name, ["first_seen"] = first },
                    new JsonObject { ["cleared"] = stamp });
            }
            known.Remove(key);
        }
        state["checked"] = stamp;
        SaveState(config.StateFile, state);

        if (fresh.Count > 0 && config.Notify)
            host.Notify($"tool-watchdog: {fresh.Count} new problem{(fresh.Count == 1 ? "" : "s")}",
                fresh.Take(4).Select(c => $"{c.Name}: {c.Detail}").Concat(fresh.Count > 4 ? [$"and {fresh.Count - 4} more"] : []));
        log($"run: {checks.Count(c => !c.Skipped)} checks, {problems.Count} problem(s)" +
            (fresh.Count > 0 ? $"; new: {string.Join("; ", fresh.Select(c => c.Line().Trim()))}" : "") +
            (cleared.Count > 0 ? $"; cleared: {string.Join(", ", cleared.Select(k => k.Split('|', 2)[1]))}" : ""));
        return new Outcome(true, checks, fresh, cleared);
    }

    internal static JsonObject LoadState(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is JsonObject state && state["problems"] is JsonObject)
                return state;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        return new JsonObject { ["problems"] = new JsonObject() };
    }

    internal static void SaveState(string path, JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
            state.WriteTo(writer);
        File.Move(temp, path, true);
    }
}
