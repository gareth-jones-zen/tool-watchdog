# tool-watchdog

Are the tools that run by themselves still producing what they should?

A line in a log saying "alive" proves a watcher is running, not that anything comes out of it. tool-watchdog checks
the products themselves (the newest report filed, the latest prep written, the queue a triage run stamps), and how
fresh each should be; that each watcher has logged lately; and that each scheduled task is there, enabled, run lately
and not failing; and that files meant to stay small (instruction files an AI agent loads, say) are within their
budgets. On a schedule, it raises each new problem once, as a notification and an item in a review queue, so
you hear when something stops rather than whenever you next look.

```
tool-watchdog check --problems
STALE output Preps written: 3 working days old (expected within 2 working days)  [meeting-preps]
STALE log    mail-triage: last logged Wed 18 Nov 09:12
STALE task   team-leave sync: last run failed (0x1)
STALE size   Instructions: 212 lines, 21,400 characters in 2 files (budget 200 lines, 20,000): over by 12 lines and 1,400 characters
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
    --brief                      a line for a session briefing: all fine, or the problems (one per line)
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
- **A problem is raised only once it's been seen on `confirm_runs` passes in a row** (default 2; 0.3.0). On waking,
  every tool starts at once and the watchdog can look before the others have caught up, so one stale pass is a blip:
  if it clears on the next pass, it was never raised. A real problem reaches you one pass later.
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

[[size]]                          # files that must stay within a budget (0.4.0; see below)
name = "Instructions"
files = ["CLAUDE.md", "rules/**/*.md"] # one pattern or a list, relative to this file; ** is any depth of folders
total_lines = 200                 # all the matched files together; each budget is optional, but give one
total_chars = 20000
each_chars = 15000                # any one file
required = false                  # true: no file matching is a problem (default false)

[tasks]                           # scheduled tasks: there, enabled, run within `days`, last run not failed
names = ["mail-triage watch", "team-leave sync"]
days = 14
register_hint = "register-tasks.ps1"   # optional: said after "not registered"

[schedule]
days = "Mon-Fri"                  # or "Mon, Wed, Fri", "Sat-Sun", "every day"
hours = "08:00-19:00"
every_minutes = 15                # autostart: how often run runs
after_unlock_minutes = 1          # and this long after each unlock
confirm_runs = 2                  # raise a problem once it's seen on this many passes in a row (1: at once)

[review]                          # optional: where run puts its items (background-tools-lib's ReviewQueue)
file = "%LOCALAPPDATA%/my-tools/review.json"
source = "watchdog"
```

The file is read strictly: an unknown setting, or one of the wrong type, is an error that names it. A last task result
of 0x41301 (running), 0x41303 (never run) or 0x41306 (stopped by a restart) isn't a failure.

**`base`:** several tools built on the shared library can keep their common settings in one file. Its `[paths]` are
variables for this file's paths (`%EA_LOCAL%`), and a table such as `[review]` applies only if this file has that
section too, its own keys winning. The loader is background-tools-lib's `PyCompat.BaseSettings`.

### Size budgets: `[[size]]`

Some files only work while they stay small: the instructions an AI agent loads at the start of every session, for
example, where each line costs attention and context. A `[[size]]` gives a set of files a budget, and a crossed budget
is a problem like any other: `check` shows it, and `run` raises it once (after `confirm_runs` passes), with a review
item that's marked cleared when the files are back within it.

```
ok    size   Always loaded: 182 lines, 19,400 characters in 2 files (budget 200 lines, 20,000)
STALE size   Rules: 41 lines, 31,200 characters in 3 files (budget 15,000 characters each): over 15,000 each: rules\api.md (16,100)
```

- **`total_lines` and `total_chars`** are for all the matched files together; **`each_chars`** is for any one of
  them, and a crossed one names the files over it. Give any of the three, but at least one.
- **`files`** are patterns relative to the settings file. **`**` matches any depth of folders** (including none),
  skipping hidden folders below it; it doesn't follow junctions or links. A file
  matched by two patterns counts once.
- **Counting:** a line ending (CRLF, LF or CR) is one character, so a checkout's line endings don't change the count,
  and a byte-order mark isn't counted. A last line without a newline still counts as a line.
- **No file matching is fine** unless `required = true`: a budget can be set for files that don't exist yet.
- `--json` adds `lines`, `chars` and `files` (as shown, relative to `root`) to a size check.

### Working files' ages: `[[age]]` (0.5.0)

Tools that download or save things to work on (receipts, emails handed to a handler, screenshots) should tidy them away
themselves once they're done with them, since only the tool knows when that is. An `[[age]]` is the safety net: it
**warns** when anything there is older than it should be, and **never deletes**.

```toml
[[age]]
name = "Claim documents"
tool = "people-first-approvals"          # optional: named in the warning
files = "%LOCALAPPDATA%/my-tools/documents/*"   # one pattern or a list; ** is any depth of folders
days = 30
```

```
ok    age    Claim documents: 2 there, the oldest 3d (limit 30 days)
STALE age    Claim documents: 1 of 3 older than 30 days: documents\abc (41d): is people-first-approvals tidying up?
```

- **A match may be a file or a folder; a folder is as old as the newest file in it** (its own time if empty), so a
  folder still being worked in isn't old.
- **Nothing there is fine.** A crossed limit is a problem like any other: `check` shows it, and `run` raises it once.

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
