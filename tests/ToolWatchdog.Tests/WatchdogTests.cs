using System.Text.Json.Nodes;
using WinBackground;

namespace ToolWatchdog.Tests;

/// <summary>tool-watchdog on a temporary folder of products, logs and settings, with fake tasks, notifications and clock.</summary>
public sealed class WatchdogTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tool-watchdog-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Title, List<string> Lines)> notified = [];
    private readonly FakeTask task = new();
    private DateTime now = new(2026, 11, 18, 10, 0, 0);   // a Wednesday, inside the default hours
    private Dictionary<string, TaskInfo> tasks = new()
    {
        ["a watch"] = new("Running", new DateTime(2026, 11, 18, 8, 0, 0), 0x41301),
        ["b sync"] = new("Ready", new DateTime(2026, 11, 18, 9, 45, 0), 0),
    };

    private const string Settings = """
        root = "repo"
        state_file = "state.json"
        log_file = "watchdog.log"

        [[output]]
        name = "Reports"
        tool = "reporter"
        files = ["repo/reports/20*.md", "repo/more/*/20*.md"]
        date = "name"
        working_days = 2

        [[output]]
        name = "Queue"
        tool = "queuer"
        files = "data/queue.json"
        date = "json:checked"
        hours = 1

        [[output]]
        name = "Later"
        files = "nothing/*.md"
        days = 3
        from = "2026-12-01"

        [[log]]
        name = "watcher"
        file = "data/watcher.log"

        [tasks]
        names = ["a watch", "b sync"]
        days = 14
        register_hint = "register.ps1"

        [review]
        file = "data/review.json"
        source = "watchdog"
        """;

    public WatchdogTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "repo", "reports"));
        Directory.CreateDirectory(Path.Combine(dir, "repo", "more", "x"));
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings);
        File.WriteAllText(Path.Combine(dir, "repo", "reports", "2026-11-16-a.md"), "");
        File.WriteAllText(Path.Combine(dir, "repo", "more", "x", "2026-11-17.md"), "");
        File.WriteAllText(Path.Combine(dir, "repo", "reports", "2026-13-40-bad.md"), "");   // not a date: ignored
        File.WriteAllText(Path.Combine(dir, "data", "queue.json"), """{"checked": "2026-11-18T09:30:00+00:00", "review": []}""");
        File.WriteAllText(Path.Combine(dir, "data", "watcher.log"), "2026-11-18 09:00:00  alive\npartial line\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private CliHost Host(TextWriter? output = null) => new()
    {
        Out = output ?? TextWriter.Null,
        Err = output ?? TextWriter.Null,
        Env = name => name == "LOCALAPPDATA" ? dir : null,
        Now = () => now,
        TaskInfo = _ => tasks,
        Notify = (title, lines) => notified.Add((title, lines.ToList())),
        Task = () => task,
        ProgramDir = dir,
    };

    private Config Config => ToolWatchdog.Config.Load(Path.Combine(dir, "config.toml"));

    private (int Code, string Out) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), .. args], Host(output));
        return (code, output.ToString());
    }

    private Check One(string name) => Checks.Run(Config, now, _ => tasks).Single(c => c.Name == name);

    [Fact]
    public void Everything_fresh_is_ok_and_check_exits_0()
    {
        var (code, output) = Run("check");
        Assert.Equal(0, code);
        Assert.Equal(
            "ok    output Reports: 1 working day old (expected within 2 working days)\n" +
            "ok    output Queue: 30m old (expected within 1 hour)\n" +
            "-     output Later: not checked now (from 2026-12-01 until -)\n" +
            "ok    log    watcher: last logged Wed 18 Nov 09:00\n" +
            "ok    task   a watch: running, last run Wed 18 Nov 08:00\n" +
            "ok    task   b sync: ready, last run Wed 18 Nov 09:45\n", output);
        Assert.Equal("Everything is producing.\n", Run("check", "--problems").Out);
        Assert.Equal("all 5 checks fine (outputs, watchers' logs, tasks)\n", Run("check", "--brief").Out);
    }

    [Fact]
    public void Products_take_the_newest_date_across_patterns_and_show_files_relative_to_root()
    {
        var reports = One("Reports");
        Assert.Equal((new DateTime(2026, 11, 17), Path.Combine("more", "x", "2026-11-17.md")), (reports.Newest!.Value, reports.File));
        now = new DateTime(2026, 11, 20, 10, 0, 0);
        Assert.True(One("Reports").Stale);   // three working days after the 17th
        now = new DateTime(2026, 11, 22, 10, 0, 0);
        Assert.Equal("3 working days old (expected within 2 working days)", One("Reports").Detail);   // the weekend doesn't count
    }

    [Fact]
    public void A_json_time_is_read_as_its_clock_says_and_nothing_found_is_stale()
    {
        now = new DateTime(2026, 11, 18, 11, 0, 0);
        Assert.Equal(("1h 30m old (expected within 1 hour)", true), (One("Queue").Detail, One("Queue").Stale));
        File.WriteAllText(Path.Combine(dir, "data", "queue.json"), "not json");
        Assert.Equal(("nothing found", true), (One("Queue").Detail, One("Queue").Stale));
        now = new DateTime(2026, 12, 1, 10, 0, 0);
        Assert.Equal(("nothing found", true, false), (One("Later").Detail, One("Later").Stale, One("Later").Skipped));
    }

    [Fact]
    public void A_log_is_judged_only_in_the_watched_hours_but_one_never_written_always_is()
    {
        now = new DateTime(2026, 11, 18, 11, 30, 0);
        Assert.True(One("watcher").Stale);   // 2.5 hours, in working hours
        now = new DateTime(2026, 11, 18, 19, 30, 0);
        Assert.False(One("watcher").Stale);  // evening
        now = new DateTime(2026, 11, 21, 11, 30, 0);
        Assert.False(One("watcher").Stale);  // Saturday
        File.Delete(Path.Combine(dir, "data", "watcher.log"));
        Assert.Equal(("never logged", true), (One("watcher").Detail, One("watcher").Stale));
    }

    [Fact]
    public void Tasks_must_be_there_enabled_not_failing_and_run_lately()
    {
        tasks.Remove("a watch");
        tasks["b sync"] = new("Ready", new DateTime(2026, 11, 1, 9, 0, 0), 0x80070002);
        Assert.Equal("not registered (register.ps1)", One("a watch").Detail);
        Assert.Equal("last run failed (0x80070002); not run since 01 Nov", One("b sync").Detail);
        tasks["b sync"] = new("Disabled", null, 0);
        Assert.Equal("disabled", One("b sync").Detail);
        tasks["b sync"] = new("Ready", null, 0x41303);
        Assert.Equal("never run", One("b sync").Detail);
        var broken = Checks.Tasks(Config.Tasks!, now, _ => throw new InvalidOperationException("no COM"));
        Assert.Equal("couldn't read Task Scheduler: InvalidOperationException: no COM", Assert.Single(broken).Detail);
        Assert.Equal(1, Run("check").Code);
        tasks["b sync"] = new("Disabled", null, 0);
        Assert.Equal("2 problem(s); `tool-watchdog check` for every check\n" +
                     "   STALE task   a watch: not registered (register.ps1)\n" +
                     "   STALE task   b sync: disabled\n", Run("check", "--brief").Out);
    }

    [Fact]
    public void Json_has_the_python_originals_fields()
    {
        var json = JsonNode.Parse(Run("check", "--json").Out)!.AsArray();
        Assert.Equal(6, json.Count);
        Assert.Equal(["kind", "name", "tool", "stale", "skipped", "newest", "file", "detail"], json[0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal("2026-11-17T00:00", (string?)json[0]!["newest"]);
        Assert.Equal(["kind", "name", "tool", "stale", "skipped", "detail"], json[2]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void Run_raises_a_new_problem_once_and_marks_it_cleared()
    {
        now = new DateTime(2026, 11, 18, 11, 15, 0);   // the queue and the log have gone stale
        Assert.Equal(0, Run("run").Code);
        Assert.Empty(notified);                        // seen once: not yet (0.3.0, confirm_runs = 2)
        Assert.Contains("(2 not yet confirmed)", File.ReadAllText(Path.Combine(dir, "watchdog.log")));
        now = new DateTime(2026, 11, 18, 11, 30, 0);
        Assert.Equal(0, Run("run").Code);
        var (title, lines) = Assert.Single(notified);
        Assert.Equal("tool-watchdog: 2 new problems", title);
        Assert.Equal(["Queue: 2h 00m old (expected within 1 hour)", "watcher: last logged Wed 18 Nov 09:00"], lines);
        var items = Config.Review!.Open().Items("watchdog");
        Assert.Equal(["Queue: 2h 00m old (expected within 1 hour) [queuer]", "watcher: last logged Wed 18 Nov 09:00"],
            items.Select(i => (string?)i["title"]));

        now = now.AddMinutes(15);
        Run("run");
        Assert.Single(notified);   // still stale, not raised again
        Assert.Equal(2, Config.Review!.Open().Items("watchdog").Count);

        File.AppendAllText(Path.Combine(dir, "data", "watcher.log"), "2026-11-18 11:40:00  alive\n");
        now = now.AddMinutes(15);
        Run("run");
        var after = Config.Review!.Open().Items("watchdog");
        Assert.Equal("2026-11-18T12:00:00", (string?)after.Single(i => (string?)i["name"] == "watcher")["cleared"]);
        Assert.Null(after.Single(i => (string?)i["name"] == "Queue")["cleared"]);
        Assert.Contains("cleared: watcher", File.ReadAllText(Path.Combine(dir, "watchdog.log")));
    }

    [Fact]
    public void A_blip_that_clears_on_the_next_run_is_never_raised()
    {
        now = new DateTime(2026, 11, 18, 11, 15, 0);   // stale for one run, as on waking before the others catch up
        Run("run");
        File.WriteAllText(Path.Combine(dir, "data", "queue.json"), "{\"checked\": \"2026-11-18T11:25:00\"}");
        File.AppendAllText(Path.Combine(dir, "data", "watcher.log"), "2026-11-18 11:25:00  alive\n");
        now = new DateTime(2026, 11, 18, 11, 30, 0);
        Run("run");
        Assert.Empty(notified);
        Assert.Empty(Config.Review!.Open().Items("watchdog"));

        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + "\n[schedule]\nconfirm_runs = 1\n");
        now = new DateTime(2026, 11, 18, 13, 0, 0);
        Run("run");
        Assert.Single(notified);                        // confirm_runs = 1: the first sighting, as before 0.3.0
    }

    [Fact]
    public void Run_does_nothing_outside_the_watched_hours()
    {
        now = new DateTime(2026, 11, 18, 21, 0, 0);
        File.WriteAllText(Path.Combine(dir, "data", "queue.json"), "{}");
        var (code, output) = Run("run");
        Assert.Equal((0, "Outside the watched hours (Mon-Fri 08:00-19:00): nothing checked.\n"), (code, output));
        Assert.Empty(notified);
        Assert.False(File.Exists(Path.Combine(dir, "state.json")));
    }

    [Fact]
    public void Run_with_notify_off_still_keeps_review_items()
    {
        File.AppendAllText(Path.Combine(dir, "config.toml"), "\n");
        var text = File.ReadAllText(Path.Combine(dir, "config.toml")).Replace("log_file = \"watchdog.log\"", "log_file = \"watchdog.log\"\nnotify = false");
        File.WriteAllText(Path.Combine(dir, "config.toml"), text);
        now = new DateTime(2026, 11, 18, 11, 15, 0);
        Run("run");
        now = new DateTime(2026, 11, 18, 11, 30, 0);
        Run("run");
        Assert.Empty(notified);
        Assert.Equal(2, Config.Review!.Open().Items("watchdog").Count);
    }

    [Fact]
    public void A_run_that_fails_says_so_but_bad_settings_are_a_usage_error()
    {
        Directory.CreateDirectory(Path.Combine(dir, "state.json"));   // the state can't be written
        Assert.Equal(1, Run("run").Code);
        Assert.Equal("tool-watchdog couldn't run", Assert.Single(notified).Title);
        Assert.Contains("run failed", File.ReadAllText(Path.Combine(dir, "watchdog.log")));

        notified.Clear();
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings.Replace("[review]", "[review]\nbogus = \"1\""));
        Assert.Equal(2, Run("run").Code);   // settings: not a run failure, nothing notified
        Assert.Empty(notified);
    }

    [Theory]
    [InlineData("Mon-Fri", new[] { 1, 2, 3, 4, 5 })]
    [InlineData("Sat-Sun", new[] { 0, 6 })]
    [InlineData("Fri-Mon", new[] { 0, 1, 5, 6 })]
    [InlineData("mon, wednesday, Fri", new[] { 1, 3, 5 })]
    [InlineData("every day", new[] { 0, 1, 2, 3, 4, 5, 6 })]
    public void Watched_days_are_written_plainly(string text, int[] expected) =>
        Assert.Equal(expected, ToolWatchdog.Config.ParseDays(text)!.Select(d => (int)d).Order());

    [Fact]
    public void The_schedule_sets_the_window_and_the_task()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + "\n[schedule]\ndays = \"every day\"\nhours = \"07:30-22:00\"\nevery_minutes = 10\nafter_unlock_minutes = 2\n");
        Assert.True(Config.Window.Contains(new DateTime(2026, 11, 21, 21, 59, 0)));
        Assert.False(Config.Window.Contains(new DateTime(2026, 11, 21, 22, 0, 0)));
        var (code, output) = Run("autostart", "on");
        Assert.Equal(0, code);
        Assert.Contains("every 10 minutes, at logon and 2 minute(s) after each unlock", output);
        var (exe, args, schedule) = Assert.Single(task.Enabled);
        Assert.EndsWith("tool-watchdog.exe", exe);
        Assert.Equal(["--config", Path.Combine(dir, "config.toml"), "run"], args);
        Assert.Equal((TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), false), (schedule.Every, schedule.AfterUnlock, schedule.KeepRunning));
    }

    [Fact]
    public void After_unlock_minutes_0_registers_no_unlock_trigger()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + "\n[schedule]\nevery_minutes = 15\nafter_unlock_minutes = 0\n");
        Assert.Null(Config.AfterUnlockMinutes);
        var (code, output) = Run("autostart", "on");
        Assert.Equal(0, code);
        Assert.Contains("every 15 minutes, at logon,", output);
        Assert.DoesNotContain("unlock", output);
        var (_, _, schedule) = Assert.Single(task.Enabled);
        Assert.True(schedule.AtLogon);
        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Every);
        Assert.Null(schedule.AfterUnlock);
    }

    [Fact]
    public void After_unlock_minutes_defaults_to_1()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + "\n[schedule]\nevery_minutes = 15\n");
        Assert.Equal(1, Config.AfterUnlockMinutes);
    }

    [Theory]
    [InlineData("[schedule]\nhours = \"19:00-08:00\"", "hours should end after they start")]
    [InlineData("[schedule]\ndays = \"Funday\"", "days should be like")]
    [InlineData("[schedule]\nafter_unlock_minutes = -1", "after_unlock_minutes should be a number, 0 or more")]
    [InlineData("[[output]]\nname = \"x\"\nfiles = \"a\"\ndays = 1\nhours = 2", "one of hours, working_days or days")]
    [InlineData("[[output]]\nname = \"x\"\nfiles = \"a\"\ndate = \"when\"", "date should be name, modified or json:<field>")]
    [InlineData("[[log]]\nname = \"x\"\nfile = \"a\"\nwhen = 1", "unknown setting 'when'")]
    [InlineData("colour = \"red\"", "unknown setting 'colour'")]
    public void Settings_are_read_strictly(string extra, string message)
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), extra + "\n");
        var output = new StringWriter();
        Assert.Equal(2, Cli.Run(["--config", Path.Combine(dir, "config.toml"), "check"], Host(output)));
        Assert.Contains(message, output.ToString());
    }

    [Fact]
    public void Glob_keeps_dotdot_as_written_and_skips_hidden_names()
    {
        File.WriteAllText(Path.Combine(dir, "repo", "reports", ".2026-11-30.md"), "");
        var found = Checks.Glob(Path.Combine(dir, "data", "..", "repo", "reports", "20*.md")).ToList();
        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Contains(@"\data\..\repo\reports\", f));
        Assert.Single(Checks.Glob(Path.Combine(dir, "repo", "*", "x", "2026-11-17.MD")));   // case-insensitive
        Assert.Empty(Checks.Glob(Path.Combine(dir, "repo", "reports", "missing.md")));
    }

    // ---- sizes (0.4.0) ----------------------------------------------------------------------------------------------

    private const string SizeSettings = """

        [[size]]
        name = "Always"
        files = ["repo/docs/*.md", "repo/docs/A.md"]
        total_lines = 3
        total_chars = 10

        [[size]]
        name = "Rules"
        files = "repo/rules/**/*.md"
        each_chars = 5

        [[size]]
        name = "Not yet"
        files = "repo/none/**/*.md"
        total_chars = 100
        """;

    private void WithSizes(string extra = "")
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + SizeSettings + "\n" + extra + "\n");
        Directory.CreateDirectory(Path.Combine(dir, "repo", "docs"));
        Directory.CreateDirectory(Path.Combine(dir, "repo", "rules", "x", "y"));
        Directory.CreateDirectory(Path.Combine(dir, "repo", "rules", ".hidden"));
        File.WriteAllText(Path.Combine(dir, "repo", "docs", "A.md"), "﻿one\r\ntwo\r\n", new System.Text.UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "repo", "rules", "top.md"), "12345\n");
        File.WriteAllText(Path.Combine(dir, "repo", "rules", "x", "y", "r.md"), "abc");
        File.WriteAllText(Path.Combine(dir, "repo", "rules", ".hidden", "h.md"), "a long hidden file, not counted");
    }

    [Fact]
    public void A_size_within_budget_is_ok_counting_each_file_once_and_line_endings_as_one()
    {
        WithSizes();
        var always = One("Always");
        Assert.Equal(("2 lines, 8 characters (budget 3 lines, 10)", false), (always.Detail, always.Stale));
        Assert.Equal([Path.Combine("docs", "A.md")], always.Files);
        Assert.Equal("ok    size   Always: 2 lines, 8 characters (budget 3 lines, 10)", always.Line());
    }

    [Fact]
    public void Doublestar_reaches_any_depth_skipping_hidden_folders_and_each_chars_names_the_files_over()
    {
        WithSizes();
        var rules = One("Rules");
        Assert.Equal([Path.Combine("rules", "top.md"), Path.Combine("rules", "x", "y", "r.md")], rules.Files);
        Assert.True(rules.Stale);
        Assert.Equal($"2 lines, 9 characters in 2 files (budget 5 characters each): over 5 each: {Path.Combine("rules", "top.md")} (6)", rules.Detail);
        Assert.Empty(Checks.Glob(Path.Combine(dir, "repo", "rules", "**", "r.md")));   // not recursive: ** is *
    }

    [Fact]
    public void A_size_over_its_totals_says_by_how_much()
    {
        WithSizes();
        File.WriteAllText(Path.Combine(dir, "repo", "docs", "B.md"), "1\n2\n3\n");
        var always = One("Always");
        Assert.True(always.Stale);
        Assert.Equal("5 lines, 14 characters in 2 files (budget 3 lines, 10): over by 2 lines and 4 characters", always.Detail);
        Assert.Equal((5L, 14L), (always.Lines!.Value, always.Chars!.Value));
    }

    [Fact]
    public void No_files_is_fine_unless_required()
    {
        WithSizes();
        Assert.Equal(("no files yet (budget 100 characters)", false), (One("Not yet").Detail, One("Not yet").Stale));
        WithSizes("required = true");   // appended to the last [[size]], Not yet
        Assert.Equal(("nothing found", true), (One("Not yet").Detail, One("Not yet").Stale));
    }

    [Fact]
    public void Sizes_are_in_check_json_and_brief()
    {
        WithSizes();
        File.WriteAllText(Path.Combine(dir, "repo", "rules", "top.md"), "1234\n");
        Assert.Equal("all 8 checks fine (outputs, watchers' logs, tasks, sizes)\n", Run("check", "--brief").Out);
        var json = JsonNode.Parse(Run("check", "--json").Out)!.AsArray();
        var always = json.Single(j => (string?)j!["name"] == "Always")!.AsObject();
        Assert.Equal(["kind", "name", "tool", "stale", "skipped", "lines", "chars", "files", "detail"], always.Select(kv => kv.Key));
        Assert.Equal((2L, 8L), ((long)always["lines"]!, (long)always["chars"]!));
        File.WriteAllText(Path.Combine(dir, "repo", "rules", "top.md"), "123456\n");
        var (code, output) = Run("check", "--problems");
        Assert.Equal(1, code);
        Assert.StartsWith("STALE size   Rules: ", output);
    }

    [Fact]
    public void Run_raises_a_size_problem_once_confirmed_and_clears_it()
    {
        WithSizes();   // Rules is over its each_chars
        Run("run");
        Assert.Empty(notified);
        now = now.AddMinutes(15);
        Run("run");
        var (title, lines) = Assert.Single(notified);
        Assert.Equal("tool-watchdog: 1 new problem", title);
        Assert.StartsWith("Rules: 2 lines, 9 characters", Assert.Single(lines));
        var item = Assert.Single(Config.Review!.Open().Items("watchdog"));
        Assert.Equal(("size", "Rules"), ((string?)item["kind"], (string?)item["name"]));

        File.WriteAllText(Path.Combine(dir, "repo", "rules", "top.md"), "1234\n");
        now = now.AddMinutes(15);
        Run("run");
        Assert.NotNull(Assert.Single(Config.Review!.Open().Items("watchdog"))["cleared"]);
        Assert.Single(notified);
    }

    [Theory]
    [InlineData("[[size]]\nname = \"x\"\nfiles = \"a\"", "needs a budget")]
    [InlineData("[[size]]\nname = \"x\"\ntotal_chars = 10", "needs files")]
    [InlineData("[[size]]\nname = \"x\"\nfiles = \"a\"\ntotal_lines = 1.5", "total_lines should be a whole number")]
    [InlineData("[[size]]\nname = \"x\"\nfiles = \"a\"\neach_chars = 0", "each_chars should be a whole number, 1 or more")]
    [InlineData("[[size]]\nname = \"x\"\nfiles = \"a\"\neach_chars = 5\nrequired = \"yes\"", "required should be true or false")]
    [InlineData("[[size]]\nname = \"x\"\nfiles = \"a\"\neach_chars = 5\nhours = 1", "unknown setting 'hours'")]
    [InlineData("[[size]]\nfiles = \"a\"\neach_chars = 5", "needs name")]
    [InlineData("[size]\nname = \"x\"", "size should be [[size]] tables")]
    public void Size_settings_are_read_strictly(string extra, string message)
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), extra + "\n");
        var output = new StringWriter();
        Assert.Equal(2, Cli.Run(["--config", Path.Combine(dir, "config.toml"), "check"], Host(output)));
        Assert.Contains(message, output.ToString());
    }

    [Fact]
    public void Measure_counts_lines_and_characters()
    {
        Assert.Equal((0L, 0L), Checks.Measure(""));
        Assert.Equal((1L, 3L), Checks.Measure("abc"));
        Assert.Equal((2L, 4L), Checks.Measure("a\r\nb\n"));
        Assert.Equal((2L, 3L), Checks.Measure("﻿a\rb"));
    }

    [Fact]
    public void Working_days_count_weekdays_after_the_start()
    {
        Assert.Equal(0, Checks.WorkingDaysBetween(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 22)));   // Fri to Sun
        Assert.Equal(1, Checks.WorkingDaysBetween(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23)));   // Fri to Mon
        Assert.Equal(5, Checks.WorkingDaysBetween(new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23)));
    }

    // ---- ages (0.5.0) ---------------------------------------------------------------------------------------------

    private const string AgeSettings = """

        [[age]]
        name = "Documents"
        tool = "claims"
        files = "work/documents/*"
        days = 30

        [[age]]
        name = "Shots"
        files = ["work/shots/*.png", "work/none/*"]
        days = 7
        """;

    private void WithAges()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + AgeSettings + Environment.NewLine);
        var docs = Path.Combine(dir, "work", "documents");
        var old = Directory.CreateDirectory(Path.Combine(docs, "old")).FullName;
        File.WriteAllText(Path.Combine(old, "a.pdf"), "x");
        File.SetLastWriteTime(Path.Combine(old, "a.pdf"), now.AddDays(-40));
        var busy = Directory.CreateDirectory(Path.Combine(docs, "busy")).FullName;
        File.WriteAllText(Path.Combine(busy, "a.pdf"), "x");
        File.SetLastWriteTime(Path.Combine(busy, "a.pdf"), now.AddDays(-40));
        File.WriteAllText(Path.Combine(busy, "reply.md"), "x");
        File.SetLastWriteTime(Path.Combine(busy, "reply.md"), now.AddDays(-2));
        var shots = Directory.CreateDirectory(Path.Combine(dir, "work", "shots")).FullName;
        File.WriteAllText(Path.Combine(shots, "s.png"), "x");
        File.SetLastWriteTime(Path.Combine(shots, "s.png"), now.AddDays(-3));
    }

    [Fact]
    public void An_age_check_warns_of_old_files_and_a_folder_is_as_old_as_its_newest_file()
    {
        WithAges();
        var documents = One("Documents");
        Assert.True(documents.Stale);
        Assert.StartsWith("1 of 2 older than 30 days: ", documents.Detail);
        Assert.EndsWith($"{Path.Combine("work", "documents", "old")} (40d): is claims tidying up?", documents.Detail);
        var shots = One("Shots");
        Assert.False(shots.Stale);
        Assert.Equal("1 there, the oldest 3d (limit 7 days)", shots.Detail);
        Assert.True(Directory.Exists(Path.Combine(dir, "work", "documents", "old")));   // warns only: nothing deleted
    }

    [Fact]
    public void Nothing_there_is_fine_and_ages_show_in_the_brief()
    {
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + AgeSettings + Environment.NewLine);
        Assert.Equal(("nothing there (limit 30 days)", false), (One("Documents").Detail, One("Documents").Stale));
        Assert.Contains("tasks, working files' ages)", Run("check", "--brief").Out);
    }

    [Theory]
    [InlineData("name = 'x'|files = 'a'", "needs days")]
    [InlineData("name = 'x'|days = 3", "needs files")]
    [InlineData("name = 'x'|files = 'a'|days = 0", "days should be a whole number, 1 or more")]
    [InlineData("name = 'x'|files = 'a'|days = 3|hours = 1", "unknown setting 'hours'")]
    public void Age_settings_are_read_strictly(string lines, string message)
    {
        var toml = string.Join(Environment.NewLine, ["", "[[age]]", .. lines.Split('|'), ""]);
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings + toml);
        Assert.Contains(message, Assert.Throws<UsageException>(() => Config).Message);
    }

    private sealed class FakeTask : ITask
    {
        public List<(string Exe, IReadOnlyList<string> Args, TaskSchedule Schedule)> Enabled { get; } = [];
        public string Name => Cli.TaskName;
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => Enable(exe, args, workingDir, TaskSchedule.Watcher);
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Enabled.Add((exe, args, schedule));
            return exe + " " + string.Join(" ", args);
        }
        public bool Disable() => true;
        public bool Stop() => true;
        public IReadOnlyDictionary<string, string>? Status() => null;
    }
}
