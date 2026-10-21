# Backup Labs 1.5.0: jobs wait for their time, daily and weekly at a set time, one click for our apps' data

## What changed

- **A new job no longer starts by itself while it is set up.** The owner's report: "as soon as I choose the
  folder, the app already starts and makes a backup, without me clicking anything". Cause, in `Due`:
  `IntervalHours > 0 && (LastRun is not DateTime || …)`. A job that never ran counted as due, and every new
  job is Daily (24 h) by default, so the 20-second timer started it on the first tick after it had both a
  folder and a destination. (The 1.4.0 devlog even used this: "the never-run job is due on the first 20 s
  tick".) Now every schedule counts from the later of the last run and `Job.Since`, the moment the job was
  set up or rescheduled: adding or removing folders, setting the destination, or a job handed over by a sister
  app (`--job-from`, File Labs, SpaceScan) set `Since` to now while the job has never run. Such a job runs
  when you press "Back up now" or at its first scheduled time, never on setup.
  Three smaller ways to the same symptom are closed too: "Back up all jobs now" in the tray left a pending
  request on unfinished jobs, which then ran the moment they were finished (now only jobs that can run are
  asked); plugging in the backup drive did the same (now only finished jobs are asked); changes the "When
  files change" watcher saw while the job was being set up counted (now only changes after `Since`).
  Jobs that already ran behave as before.
- **Daily at a time, Weekly on a day at a time.** Under the schedule segment, Daily shows "at [21:30]" and
  Weekly adds a Mon–Sun segment. The time box takes 24-hour times (`21:30`, `9:05`, `21`); anything else is
  put back to the saved time, like the Keep box. Radio buttons in the app's segment style (arrow keys move
  between days), and screen-reader names "Monday" … "Sunday" and "Time of day, 24-hour". The header's
  "Next …" line follows it (weekly shows the weekday). Changing the schedule, time or day counts from that
  moment: a time already past today waits for tomorrow instead of starting at once. If the PC was off or
  asleep at the time, the job runs at the next 20-second check after it is on again, once (its new last run
  moves the next time past now). Hourly and Every 6 h are unchanged: that long after the last run (a new job:
  after its setup).
  Rule, `Schedule.Next(interval, at, day, lastRun, since)` in `Jobs.cs`: from = later of lastRun and since;
  Daily = the first `at` after from; Weekly = the first `day` at `at` after from. Wall-clock arithmetic: on the
  spring DST day a 03:30 that doesn't exist runs when the clock jumps to 04:00; on the autumn day the run at
  the first 03:30 moves the next time to the following day, so the repeated 03:30 doesn't run it again.
- **Migration from 1.4.0 (`AppSettings.TimedSchedules`, false in older files).** 1.4.0 ran Daily/Weekly jobs
  24 h / 7 days after their last run. The least surprising translation is the one that keeps every job's
  timing: a job that already ran gets the time of day (and weekday) of its last run, and counts from that
  run. It then comes due at the same minute 1.4.0 would have run it, and an overdue job still catches up on
  the first check after the update (as 1.4.0 would). The user sees that time in the box and can change it.
  A job that never ran gets the default, 12:00 (Monday for weekly), and counts from the update: it waits for
  12:00 instead of starting. Keeping "every 24 h since last" alongside the new rule would have meant two
  schedulers and a box showing no time; deriving the time gives the same behaviour with one rule.
  Hand-edited values out of range (time ≥ 24 h, day 9, a `Since` in the future) fall back to sane ones.
- **"Back up our apps' data"** in the sidebar, under New job, shown when another Protagonist Labs app is
  installed (its card and exe exist) or the job already exists. It makes the job "Protagonist Labs apps"
  (id `protagonist-apps`), Daily, and puts the cursor in the destination box, as a new job does; a second
  click opens that job instead of making another. Sources, only those that exist:
  `%APPDATA%\ProtagonistLabs`, `%APPDATA%\BackupLabs`, and for each installed app its data folder, read from
  each app's source: Shelf `game-browser`, Reel `reel`, Reminder `Reminder`, SteamRadar `SteamRadar`, Lume
  `Lume`, Frostpane `Frostpane`, File Labs `FileLabs`, Stash `ThunderbirdBackup`, SpaceScan `SpaceScan`, Auto
  Clicker `AutoClicker`, Glaze `Glaze` (all under Roaming AppData), Sysoptimizer `%ProgramData%\Sysoptimizer`.
  Not included: Planner (its plan sits next to its exe; AppData only holds the browser profile it opens),
  Cursor Selector (its library is next to its exe), HotkeyScan (keeps nothing but its card).
  An app's folder is used only while the app is installed, because several names are generic (`FlowLauncher`
  on this PC belongs to the open-source Flow Launcher, not to Lume, and `Reminder` or `AutoClicker` could be
  anyone's). Exclusions, as full paths and only those that exist (so a `Cache` folder that is some app's own
  data elsewhere is kept): Shelf's `covers`, `screenshots`, `trailers`, `thumbs`; Lume's `icons`; and
  Chromium's `Cache`, `Code Cache`, `GPUCache`, `DawnCache`, `DawnGraphiteCache`, `DawnWebGPUCache`,
  `Shared Dictionary`, `Crashpad` in the Electron apps' folders (Shelf, Reel, Reminder, SteamRadar, Lume).
  A card may name its data folder in a new optional field `"data"` (no card has it today); it is used instead
  of the known folder only if, judged by its text before the disk is touched, it is a local `X:\…` path
  (no `\\server`, `\\?\` or `\\.\` path, no `:stream`), at most 260 characters, and strictly inside the user's
  profile once `..` is resolved. Otherwise the known folder is used. A broken card, a BOM, `data` of the wrong
  type, a card whose exe is gone: that app is treated as not installed or as having no `data`.
- **Code moved, not changed:** `Job`, `AppSettings` and `Store` went from `Program.cs` to `Jobs.cs`, so the
  tests can read settings files from 1.4.0.

## Files

`Jobs.cs` (new: `Job.At`, `Job.Day`, `Job.Since`, `AppSettings.TimedSchedules`, `Store.Parse`, `Schedule`),
`Program.cs` (the moved classes removed), `MainWindow.xaml` (time/day row, "Back up our apps' data"),
`MainWindow.xaml.cs` (`Due`, `Refresh`, `Rescheduled`, `Edited`, `TimeBox_*`, `AppsJob_Click`, tray and drive
triggers), `Ecosystem.cs` (`AppsJobId`, `AppsJob`, `CardFolder`), `tests/Program.cs`, `tests/Tests.csproj`,
`README.md`, `Backup.csproj` (1.5.0).

## How it was tested

**Engine tests** (`dotnet run --project tests`, also run by `build.ps1`): 135 checks, all passed; 38 new.

- Schedule (15): manual never; hourly / 6 h after the last run; a new hourly job an hour after setup; a new
  daily job set up after its time waits for tomorrow, before it runs today; weekly today if ahead, else next
  week, other days; daily after a run; a run by hand before the time doesn't move it; a time changed today
  never runs at once; PC asleep three days: one catch-up then the usual time; weekly missed twice: one
  catch-up; 31 Oct → 1 Nov, 28 Feb → 1 Mar, a Friday-to-Monday weekly across February's end, 31 Dec → 7 Jan;
  DST in Romania: 28 Mar 2027 03:30 is invalid there and is passed when the clock reads 04:00; 25 Oct 2026
  03:30 is ambiguous and the job runs once.
- Settings from 1.4.0 (7): a daily job gets its last run's time and comes due exactly 24 h after it (minute
  precision); a weekly job a week old is due at once (catch-up kept); hourly unchanged; a never-run job waits
  for 12:00; a 1.5.0 file is not migrated again; out-of-range values; `null`.
- Apps' data (16): synthetic profile, ProgramData and cards: the shared folder, Shelf, Reminder (card `data`
  a UNC path → the known folder), Lume (card `data` inside the profile → used), Glaze (card `data`
  `C:\Windows` → the known folder), Sysoptimizer (card with a BOM, `data` a number), Backup Labs; left out:
  Reel (exe gone), Auto Clicker (no folder), Frostpane (broken card). Exclusions exactly the caches present,
  by path. A backup of the synthetic set: **17.6 MB in full, 637 KB with the caches left out** (sizes made
  up in proportion: 64 KB settings, 512 KB saves, 3 MB covers, 2 MB screenshots, 9 MB trailers, 800 KB
  thumbs, 2.3 MB Chromium caches, 300 KB icons), with a `library\Cache` that is app data still copied. 12
  refused card paths: none, UNC, `\\?\`, `\\.\PhysicalDrive0`, relative, `..` out of the profile, the profile
  itself, a sibling folder whose name starts like the profile, an alternate data stream, over 260
  characters, `C:\Windows\System32`, `C:`; one accepted.

**Build:** `build.ps1`: tests, publish (172 MB self-contained), `dist\BackupLabs-1.5.0-setup.exe` (50.3 MB).

**End to end, isolated** (Debug builds of 1.4.0 and 1.5.0 in the scratchpad, `BACKUPLABS_TEST_ROOT` per case,
started with `--tray`, synthetic source and destination; the user's Backup Labs 1.4.0 kept running untouched):

| Case | 1.4.0 | 1.5.0 |
|---|---|---|
| a job just given a folder and a destination (never ran, Daily) | **backed up after 21 s** | no backup in 45 s |
| 1.4.0 settings, daily job last run 2 days ago | (due) | backed up after 21 s, once |
| daily, time set to 2 minutes ahead (08:08), never ran | | backed up at 08:08:10, once in 200 s |
| daily at 06:00, last run 3 days ago (PC off) | | one catch-up after 21 s, no second in 70 s |

**UI, isolated, window off-screen** (UI Automation; the window was brought up by the app's own
`--show-job`, moved to −32000 within 278 ms and kept there; foreground given back to the user's window
after each step that activated it; no keys sent): a weekly job (Friday 21:30) shows 7 day buttons named
Monday…Sunday with Friday selected, the time box "21:30" (keyboard-focusable), header "Next Fri 23 Oct,
21:30"; Daily hides the days and shows "Next 21 Oct, 21:30"; Hourly hides both and shows the time an hour
from the change. With a synthetic Shelf card and folder, "Back up our apps' data" clicked twice: one job
"Protagonist Labs apps", Daily, no destination, sources ProtagonistLabs, game-browser, BackupLabs,
exclusions covers, trailers, Cache (the three present). No backup was made during the whole UI run (25 s
past the last action). No test process was left running and no dialog appeared.

## Not tested

- Typing into the time box through the UI (it would have taken keyboard focus from the user's session); its
  parsing is `TimeSpan.TryParseExact` with four formats, read but not run here.
- Real sleep/resume and a real DST change: covered by the wall-clock rule and the timer comparing against
  `DateTime.Now` on every tick, tested with fixed dates, not with the PC asleep.
- The apps' data job on the real `%APPDATA%`: only folder names were listed (all twelve Roaming folders above
  exist on the owner's PC; `%ProgramData%\Sysoptimizer` was not looked at); their contents and sizes were not read.
  Reminder has no card on that PC yet, so its folder would be left out there until Reminder writes one.
- No screenshot: an off-screen WPF window renders blank, and the test window was not allowed on screen.
