# Backup Labs 1.3.0: archives are .bkl files that open in Backup Labs

## What changed

- **One extension for every archive.** Archive mode now writes `<stamp>.bkl` for plain and encrypted archives
  alike (1.2 wrote plain ones as `<stamp>.zip`). A plain `.bkl` is still an ordinary zip: 7-Zip and other
  archivers that recognise zips by their signature open it as it is, and renamed to `.zip` it opens in Explorer.
- **Encryption is told by the header, not the name.** `Engine.Encrypted` reads the first 8 bytes and compares
  them with `BKLENC1\0` (`Crypto.IsHeader`). `OpenArchive` uses it, so a plain zip named `.bkl` opens as a zip
  and an encrypted archive renamed `.zip` asks for its password instead of failing as a broken zip. A file that
  is neither is refused with "… is not a Backup Labs backup: neither a zip nor an encrypted archive."
- **Old backups unchanged.** `.zip` backups from 1.2 and encrypted `.bkl` ones are listed, restored, searched
  and pruned as before (`IsArchive` and `Snapshots` already accepted both extensions).
- **Double-click a `.bkl`.** The installer registers `.bkl` per user (`HKCU\Software\Classes\.bkl` →
  `BackupLabs.Backup`, "Backup Labs backup", the exe's icon, `ChangesAssociations=yes`) and removes it on
  uninstall. Opening one runs `BackupLabs.exe --open "<file>"`. `--open` and not a bare path, because a bare path
  already means "back this up" (File Labs' handoff) and a `.zip` handed over that way must keep being backed up.
- **`--open` in the app.** `Program.Main` accepts it only for a full path to an existing `.bkl` or `.zip`
  (`Engine.OpenProblem`); anything else is logged (`--open: ignored <arg>: <reason>`) and ignored. Like
  `--show-job`, it travels to an already running copy through `incoming.txt` and the Show event.
- **The Restore panel on that file.** It shows the file alone, wherever it is. Whole backup to a folder and
  Find a file work on it. "Original locations" is shown only when the file is one of a job's backups (it sits in
  that job's destination); otherwise "Another folder" is chosen and the other choice is hidden. An encrypted
  file gets a "Password of this backup" field; when it is left empty, the owning job's saved password is tried.
  Before a whole restore starts, the archive is opened once with that password (`Engine.Probe`), so a missing
  or wrong password, or a file that is not a backup, is reported in the panel, where it can be corrected,
  instead of after the panel has closed.
- **Renamed files.** A backup whose name is not a date shows its file name where the date would be.
- **Test-only switch.** `BACKUPLABS_TEST_ROOT=<folder>` moves settings, log and the ProtagonistLabs folder
  into that folder, suffixes the single-instance mutex and event names with `.Test`, and skips writes to the
  Run key. Windows' known-folder lookup ignores the `APPDATA` variable, so redirecting that was not enough.
  Without the variable nothing changes.

## Files

`Engine.cs`, `Crypto.cs`, `Program.cs`, `Ecosystem.cs`, `MainWindow.xaml(.cs)`, `installer/BackupLabs.iss`,
`tests/Program.cs`, `README.md`, `Backup.csproj` (1.3.0).

## How it was tested

**Engine tests** (`dotnet run --project tests`, also run by `build.ps1`): 76 checks, all passed. 13 of them are
new for 1.3.0, on synthetic data in %TEMP%:

- a plain archive is written as `<stamp>.bkl`, is an ordinary zip inside (`ZipFile.OpenRead`) and restores byte for byte (300 KB of random bytes);
- an encrypted `.bkl` still restores byte for byte;
- 1.2 naming: a `<stamp>.zip` and an encrypted `<stamp>.bkl` side by side are both listed, both restore, Find
  reads both, and a new backup with Keep = 1 prunes both;
- header, not name: a zip made by `ZipFile.CreateFromDirectory` and named `.bkl` restores as a zip; an encrypted
  archive named `.zip` is refused without a password ("…encrypted…") and restores with it;
- 50 KB of random bytes named `.bkl`, and a 2-byte `.bkl`, are refused by Restore, Find and Probe with the
  "not a Backup Labs backup" message, and nothing is written;
- `--open` validation: an existing `.bkl`/`.zip` by full path passes; a relative path, a missing file and a `.bin` don't.

**Build:** `build.ps1` ran the tests, published (172 MB self-contained) and compiled
`dist\BackupLabs-1.3.0-setup.exe` (50.3 MB) with the new `[Registry]` section.

**UI check on an isolated copy** (the published `dist\app\BackupLabs.exe` with `BACKUPLABS_TEST_ROOT` in the
scratchpad, synthetic files and archives made by the engine, all actions through UI Automation patterns, no
injected keys or clicks, screenshots of the test window only via `PrintWindow`): 17 checks, all passed.

| # | Check | Result |
|---|---|---|
| 1 | `--open "Photos backup.bkl"` (plain, outside every job) opens the Restore panel on that file | ok |
| 2 | "Original locations" not shown, "Another folder" chosen | ok |
| 3 | Whole backup to a folder | 6 of 6 files byte-identical |
| 4 | A second launch with `--open` exits (code 0) and the running test copy shows the file | ok |
| 5 | Find a file "holiday-3" in it | "1 version of 1 file", restored, identical |
| 6 | Encrypted `.bkl` shows the password field | ok |
| 7 | Restore with no password / a wrong one | message in the panel, panel stays open |
| 8 | Restore with the right password | 6 of 6 files identical |
| 9 | 20 KB of random bytes named `.bkl` | refused in the panel, Restore disabled |
| 10 | A `.bkl` in a job's destination | "a backup of Synthetic job", original locations offered |
| 11 | `--open notes.txt` and `--open relative.bkl` | logged as ignored, screen unchanged |
| 12 | The job's own "Restore…" afterwards | unchanged: its destination's list, original locations chosen |

The user's own `%APPDATA%\BackupLabs` and `%APPDATA%\ProtagonistLabs` files kept their timestamps from
17.10, `HKCU\Software\Classes\.bkl` does not exist, and only the processes the script started were stopped.

**7-Zip** (26.04, installed on this PC): `7z x "Photos backup.bkl"` extracted 6 files, all identical to the
source (`diff -r`). The encrypted `.bkl` is not an archive to 7-Zip, as intended.

Screenshots: `img/1.3.0/1-open-plain-bkl.png`, `2-find-a-file.png`, `3-encrypted-password.png`,
`4-wrong-password.png`, `5-not-a-backup.png`, `6-job-backup.png`, `7-job-restore-unchanged.png`.

## Not tested

- **The file association itself.** The installer was compiled but not run: installing would register `.bkl`
  in this PC's real HKCU. So an actual double-click in Explorer, the icon and type name Explorer shows, and the
  removal on uninstall were not seen. The command the association runs (`--open "<file>"`) was tested as above.
- Restoring an opened backup to its original locations (it would overwrite files; the option's presence was checked).
- Opening a 1.2 `.zip` by double-click (not associated; `--open` accepts it and the engine tests cover reading it).
- Explorer opening a plain `.bkl` renamed to `.zip` (it is a standard zip; .NET's zip reader and 7-Zip read it).
