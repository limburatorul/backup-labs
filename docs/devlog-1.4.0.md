# Backup Labs 1.4.0: works with Stash

## What changed

- **Stash's job follows Stash's backup folder.** Stash 2.2.0 hands its backup folder over with
  `--job-from` (`{id: "stash-backups", name: "Thunderbird backups (Stash)", sources: [<folder>]}`), which
  1.2's `--job-from` already creates or updates by id. New: before each run of the job whose id is
  `stash-backups`, `Ecosystem.FollowStash` reads `%APPDATA%\ProtagonistLabs\data\stash.items.json` and, when
  its `folder` differs, makes that folder the job's only source and logs
  `<job>: Stash's backup folder is now <new> (was <old>)`. Same place and shape as the Shelf job's sync in
  `RunAsync`. Why: a user who moves Stash's backup folder would otherwise keep backing up the old, now stale
  one without being told.
- **Only while Stash is installed, only with a sane file.** The job keeps its sources unless all of these
  hold: `apps\stash.json` is schema 1 and its `exe` is a local path (`X:\…`) to a file that exists;
  `stash.items.json` is at most 1 MB (20 items are about 10 KB), parses (a BOM is fine, PowerShell writes
  one), has `schema` exactly the number 1 and `folder` a string of at most 1024 characters that is a local
  path to an existing folder. Anything else (no card, exe gone, schema 2, wrong types, cut-off JSON,
  relative, UNC or `\\?\` paths, a missing folder, a file instead of a folder) changes nothing.
- **Network and device paths refused by their text.** `Ecosystem.Local` accepts only `X:\…` paths and is
  checked before `File.Exists`/`Directory.Exists`, so a hostile card or data file can't make Backup Labs
  open an SMB connection (which hands out the user's NTLM credentials) or touch a device. It now guards every
  card exe read (`Exe`, used for SpaceScan too, and `Sisters`), not only Stash's. A mapped network drive
  letter still counts as local; the user chose that mapping.
- **Works with.** One line, in the existing style: installed, "Stash · its Thunderbird and Firefox backups
  get versions here, on another disk or NAS"; not installed, the name links to
  `https://protagonistlabs.app/stash/?utm_source=backuplabs&utm_medium=app&utm_campaign=works-with` and the
  line says "would get versions here".

## Files

`Ecosystem.cs` (`StashJobId`, `FollowStash`, `StashFolder`, `Local`, `Exe(id, root)`), `MainWindow.xaml.cs`
(`RunAsync`, `ShowWorksWith`), `tests/Program.cs`, `README.md`, `Backup.csproj` (1.4.0).

## How it was tested

**Engine tests** (`dotnet run --project tests`, also run by `build.ps1`): 97 checks, all passed; 21 are new,
on synthetic cards and files under a temporary root (`FollowStash(id, sources, root)`):

- folder moved → the job's sources become exactly that folder (file written with a BOM); a second call changes nothing;
- a job with another id, and Shelf's job, are left alone;
- card: exe gone, exe on `\\localhost\c$\…`, schema 2, no card → unchanged;
- file: schema 2, schema `"1"`, schema 1.5, folder a number, no folder, a UNC folder, a `\\?\` path, a
  relative folder, a missing folder, a file instead of a folder, cut-off JSON, an array, an empty file,
  a file over 1 MB, no file → unchanged.

**Build:** `build.ps1` ran the tests, published (172 MB self-contained) and compiled
`dist\BackupLabs-1.4.0-setup.exe` (50.3 MB).

**End to end on an isolated copy** (published `dist\app\BackupLabs.exe`, `BACKUPLABS_TEST_ROOT` in the
scratchpad, a synthetic Stash card with BOM pointing at an empty dummy `Stash.exe`; the user's Backup Labs was
not running):

| # | Step | Result |
|---|---|---|
| 1 | `--job-from` a Stash-style spec (sources: `stash-old`) | job `stash-backups` "Thunderbird backups (Stash)", source `stash-old`, the only job; window moved off-screen 740 ms after it appeared, then closed |
| 2 | destination set in the test settings; `stash.items.json` (BOM) now says `folder: stash-new` | |
| 3 | started with `--tray` (no window); the never-run job is due on the first 20 s tick | ran after 21 s; log: "Stash's backup folder is now …\stash-new (was …\stash-old)", "2 copied (21 B)" |
| 4 | backup contents | `stash-new\firefox-new.zip` and `thunderbird-new.zip`, identical text; nothing from `stash-old`; saved sources = `stash-new` |
| 5 | Works with, read through UI Automation | installed: "Stash · its Thunderbird and Firefox backups get versions here, on another disk or NAS", no link; card exe deleted: "… would get versions here …" with a "Stash" hyperlink |

The user's `%APPDATA%\BackupLabs\settings.json` and `%APPDATA%\ProtagonistLabs\apps\backuplabs.json` kept
their timestamps; no test process was left running and no dialog appeared.

## Not tested

- With the real Stash 2.2.0 (built, not released): its writer was not run here; the file shape was taken
  from Stash's `Write-SharedFile` call and devlog, and reproduced with BOM in the tests.
- No screenshot: an off-screen WPF window renders blank through `PrintWindow`, and putting the test window on
  the user's screen was not acceptable. The Works with text was checked through UI Automation instead.
- The link's target URL was not clicked; it is built by the same `Page()` as the other Works with links.
