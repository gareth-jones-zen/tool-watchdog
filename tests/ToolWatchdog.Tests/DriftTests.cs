using WinBackground;

namespace ToolWatchdog.Tests;

/// <summary>0.7.0: a task compared with its tool's [schedule] ([tasks.settings]), and the watchdog's own [schedule] read
/// through background-tools-lib's shared parser.</summary>
public sealed class DriftTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tool-watchdog-drift-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime now = new(2026, 11, 18, 10, 0, 0);

    public DriftTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private string Tool(string schedule)
    {
        var path = Path.Combine(dir, "my-tool.toml");
        File.WriteAllText(path, "enabled = true\n" + schedule);
        return path;
    }

    private static string Described(string fingerprint) => ScheduleDrift.WithFingerprint("Does things.", fingerprint);

    private List<Check> Run(string file, TaskInfo task, TaskKind kind = TaskKind.Job)
    {
        var rule = new TasksRule(["my-tool run"], 14, null)
        {
            Settings = new Dictionary<string, TaskLink> { ["my-tool run"] = new(file, kind) },
        };
        return Checks.Tasks(rule, now, _ => new Dictionary<string, TaskInfo> { ["my-tool run"] = task });
    }

    [Fact]
    public void A_task_matching_its_settings_is_fine()
    {
        var file = Tool("[schedule]\nevery_minutes = 15\nat_logon = true\nafter_unlock_minutes = 0\n");
        var check = Assert.Single(Run(file, new TaskInfo("Ready", now.AddMinutes(-5), 0, Described("v1 job every=15 daily=- logon=yes unlock=0"))));
        Assert.False(check.Stale);
        Assert.DoesNotContain("settings changed", check.Detail);
    }

    [Fact]
    public void A_task_registered_before_its_settings_changed_is_a_problem()
    {
        var file = Tool("[schedule]\nevery_minutes = 60\nafter_unlock_minutes = 0\n");
        var check = Assert.Single(Run(file, new TaskInfo("Ready", now.AddMinutes(-5), 0, Described("v1 job every=15 daily=- logon=yes unlock=0"))));
        Assert.True(check.Stale);
        Assert.Contains("settings changed since registration: run `my-tool autostart on`", check.Detail);
    }

    [Fact]
    public void A_task_registered_before_fingerprints_is_a_note_not_a_problem()
    {
        var file = Tool("[schedule]\nevery_minutes = 15\n");
        var check = Assert.Single(Run(file, new TaskInfo("Ready", now.AddMinutes(-5), 0, "Does things.")));
        Assert.False(check.Stale);
        Assert.Contains("registered before its schedule was recorded", check.Detail);
    }

    [Fact]
    public void A_watcher_compares_as_a_watcher_and_no_schedule_is_a_note()
    {
        var watcher = Tool("[schedule]\nevery_minutes = 5\nhours = \"00:00-24:00\"\n");
        Assert.False(Assert.Single(Run(watcher, new TaskInfo("Running", now.AddMinutes(-5), 0x41301, Described("v1 watcher logon=yes")), TaskKind.Watcher)).Stale);
        var none = Tool("");
        var check = Assert.Single(Run(none, new TaskInfo("Ready", now.AddMinutes(-5), 0, "Does things.")));
        Assert.False(check.Stale);
        Assert.Contains("has no [schedule]", check.Detail);
    }

    [Fact]
    public void A_broken_schedule_in_the_tool_settings_is_a_problem()
    {
        var file = Tool("[schedule]\nevery_minutes = \"often\"\n");
        var check = Assert.Single(Run(file, new TaskInfo("Ready", now.AddMinutes(-5), 0, Described("v1 job every=15 daily=- logon=yes unlock=0"))));
        Assert.True(check.Stale);
        Assert.Contains("couldn't read its [schedule]", check.Detail);
    }

    [Fact]
    public void Tasks_settings_are_read_relative_to_the_watchdog_settings()
    {
        var path = Path.Combine(dir, "tool-watchdog.toml");
        File.WriteAllText(path, "[tasks]\nnames = [\"my-tool run\", \"other watch\"]\n[tasks.settings]\n\"my-tool run\" = \"my-tool.toml\"\n\"other watch\" = { file = \"other.toml\", kind = \"watcher\" }\n");
        var config = Config.Load(path, _ => null);
        Assert.Equal(Path.Combine(dir, "my-tool.toml"), config.Tasks!.Settings["my-tool run"].File);
        Assert.Equal(TaskKind.Job, config.Tasks.Settings["my-tool run"].Kind);
        Assert.Equal(TaskKind.Watcher, config.Tasks.Settings["other watch"].Kind);
    }

    [Fact]
    public void Own_schedule_through_the_shared_parser_keeps_the_window_and_defaults()
    {
        var path = Path.Combine(dir, "tool-watchdog.toml");
        File.WriteAllText(path, "[schedule]\ndays = \"Mon-Fri\"\nhours = \"08:00-19:00\"\nevery_minutes = 15\nafter_unlock_minutes = 0\nconfirm_runs = 3\n");
        var config = Config.Load(path, _ => null);
        Assert.Equal("v1 job every=15 daily=- logon=yes unlock=0", config.Schedule.Fingerprint(TaskKind.Job));
        Assert.Equal("Mon-Fri 08:00-19:00", config.Window.Text);
        Assert.True(config.Window.Contains(now));
        Assert.False(config.Window.Contains(new DateTime(2026, 11, 21, 10, 0, 0)));
        Assert.Equal(3, config.ConfirmRuns);

        File.WriteAllText(path, "confirm_runs = 4\n");
        var defaults = Config.Load(path, _ => null);
        Assert.Equal("v1 job every=15 daily=- logon=yes unlock=1", defaults.Schedule.Fingerprint(TaskKind.Job));
        Assert.Equal(4, defaults.ConfirmRuns);

        File.WriteAllText(path, "[schedule]\ndays = \"every day\"\n");
        Assert.True(Config.Load(path, _ => null).Window.Contains(new DateTime(2026, 11, 21, 10, 0, 0)));
    }
}
