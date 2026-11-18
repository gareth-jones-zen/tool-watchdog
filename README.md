# tool-watchdog

Are the tools that run by themselves still producing what they should?

A line in a log saying "alive" proves a watcher is running, not that anything comes out of it. tool-watchdog checks
the products themselves (the newest report filed, the latest prep written, the queue a triage run stamps), and how
fresh each should be; that each watcher has logged lately; and that each scheduled task is there, enabled, run lately
and not failing. On a schedule, it raises each new problem once, as a notification and an item in a review queue, so
you hear when something stops rather than whenever you next look.

```
tool-watchdog check --problems
STALE output Preps written: 3 working day(s) old (expected within 2)  [meeting-preps]
STALE log    mail-triage: last logged Wed 18 Nov 09:12
STALE task   team-leave sync: last run failed (0x1)
```

Windows only (Task Scheduler, notifications). Built in .NET 11 with Native AOT: no runtime to install.

## Install

1. Download `tool-watchdog-<version>-win-x64.zip` from [Releases](https://github.com/gareth-jones-zen/tool-watchdog/releases).
2. Unzip it to a folder that will stay put, such as `%LOCALAPPDATA%\Programs\tool-watchdog`.
3. Run `tool-watchdog config`, write your checks into the file it names, and try `tool-watchdog check`.
4. `tool-watchdog autostart on` runs it by itself (registering the task needs you at the console).

It holds two programs: `tool-watchdog.exe`, for a console, and `tool-watchdog-background.exe`, the same program with
no window, which the scheduled task runs.

## Commands

```
tool-watchdog check              every check, fresh or not (exit 1 if anything is stale)
    --problems                   only what's wrong
    --json                       as JSON
tool-watchdog run                the scheduled pass (below)
    --now TIME                   check and run: as though it were TIME (yyyy-mm-ddThh:mm)
tool-watchdog autostart on       run every few minutes, at logon and after unlocking: off, restart, status
tool-watchdog config             create the settings file (if missing) and say where it is
```

**Exit codes:** 0 done (for `check`, nothing stale); 1 something stale, or `run` failed; 2 settings or arguments.

## The scheduled pass: `run`

- **Only inside the watched hours** (`[schedule]`, Monday to Friday 08:00-19:00 by default). Outside them it checks
  nothing and exits at once, so the task can run every 15 minutes, all the time, at almost no cost, and you change
  the hours in the settings rather than re-registering the task.
- **A new problem is raised once:** one notification (several at once share it), and, with `[review]`, an item in a
  review queue (type `stale`, with the check's name, kind, tool and when it was first seen). While it lasts it isn't
  raised again. **When it clears, its item gets a `cleared` time**, so whoever reads the queue can say it recovered.
- **What it has raised is kept in `state_file`**, and each pass logs one line to `log_file`.
- **A pass that fails** (it can't write its state, say) notifies once, until a pass succeeds again.
- **Why a scheduled job rather than a watcher:** a watcher that stops or hangs goes quiet, and noticing that is this
  tool's job. A job starts afresh every time, Task Scheduler records each run, and the unlock trigger catches up after
  sleep (waking always ends in an unlock, and a repetition trigger may skip runs missed while asleep).

`check` runs every check whatever the time; only logs are judged by the watched hours (a log quiet overnight isn't a
problem).

## Settings

`%APPDATA%\tool-watchdog\config.toml` by default (`--config` or `TOOL_WATCHDOG_CONFIG` for another). Paths are relative
to the settings file, with `~` and `%VARIABLES%`.

```toml
base = "base-config.toml"         # optional: settings shared with other tools (see below)
root = ".."                       # files under it are shown relative to it (default: this folder)
notify = true                     # run: a notification for each new problem
state_file = "..."                # default %LOCALAPPDATA%\tool-watchdog\state.json
log_file = "..."                  # default %LOCALAPPDATA%\tool-watchdog\tool-watchdog.log

[[output]]                        # a product a tool should keep producing
name = "Preps written"
tool = "meeting-preps"            # who produces it, so a stale line says where to look
files = ["../meetings/preps/20*.md"]   # one pattern or a list (* and ? within a folder; not recursive)
date = "name"                     # name: the newest YYYY-MM-DD (or YYYY-MM) in a file name
                                  # modified: the newest file's time; json:<field>: an ISO time in a JSON file
working_days = 2                  # or days, or hours (default: 7 days); working days are Monday to Friday
from = "2026-11-23"               # optional: only check from this date, or until one
until = "2027-01-01"

[[log]]                           # a watcher's log, with a timestamped line ("2026-11-18 09:00:00 ...")
name = "mail-triage"
file = "%LOCALAPPDATA%/my-tools/mail-triage.log"
hours = 2                         # how long it may be quiet within the watched hours (default 2)

[tasks]                           # scheduled tasks: there, enabled, run within `days`, last run not failed
names = ["mail-triage watch", "team-leave sync"]
days = 14
register_hint = "register-tasks.ps1"   # optional: said after "not registered"

[schedule]
days = "Mon-Fri"                  # or "Mon, Wed, Fri", "Sat-Sun", "every day"
hours = "08:00-19:00"
every_minutes = 15                # autostart: how often run runs
after_unlock_minutes = 1          # and this long after each unlock

[review]                          # optional: where run puts its items (background-tools-lib's ReviewQueue)
file = "%LOCALAPPDATA%/my-tools/review.json"
source = "watchdog"
```

The file is read strictly: an unknown setting, or one of the wrong type, is an error that names it. A last task result
of 0x41301 (running), 0x41303 (never run) or 0x41306 (stopped by a restart) isn't a failure.

**`base`:** several tools built on the shared library can keep their common settings in one file. Its `[paths]` are
variables for this file's paths (`%EA_LOCAL%`), and a table such as `[review]` applies only if this file has that
section too, its own keys winning. The loader is background-tools-lib's `PyCompat.BaseSettings`.

## Building

You need the .NET 11 SDK (`global.json` pins the feature band). Native AOT publishing also needs the Visual Studio C++
build tools. The shared libraries are a sibling checkout of
[background-tools-lib](https://github.com/gareth-jones-zen/background-tools-lib):

```
dotnet test -c Release
dotnet publish src/ToolWatchdog.Console -c Release -r win-x64 -o publish
dotnet publish src/ToolWatchdog.Background -c Release -r win-x64 -o publish
```

A tag `v*` builds, tests and publishes a release on GitHub Actions, with SHA-256 checksums and a provenance
attestation.
