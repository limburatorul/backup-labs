# Backup Labs

Backups for Windows that you set up once and stop thinking about. Pick any folders (or whole drives),
pick where the backups go, and Backup Labs keeps them, on a schedule, when files change, or when you
plug in the backup drive.

**[Download the installer](https://github.com/limburatorul/backup-labs/releases/latest)** — Windows 10/11, x64, no admin rights needed. The app updates itself.
Screenshots, a comparison with other backup tools and the full guide: **[protagonistlabs.app/backuplabs](https://protagonistlabs.app/backuplabs/?utm_source=github&utm_medium=readme&utm_campaign=backuplabs)**.

## What it does

- **Incremental backups you can browse.** Every backup is a normal folder named by its date. Files that
  didn't change are NTFS hard links to the previous backup, so they take no extra space, and any backup can
  be deleted without breaking the others.
- **Or archives:** one `.bkl` file per backup with four compression levels, or **encrypted**
  (AES-256-GCM, key from your password with PBKDF2-SHA256, 600,000 iterations). A plain `.bkl` is an
  ordinary zip inside: 7-Zip and other archivers open it as it is, and renamed to `.zip` it opens in
  Explorer, so your files stay reachable without Backup Labs. Encrypted archives open only in Backup Labs,
  and only with the password. Backups made before 1.3.0 as `.zip` are still listed, restored and pruned.
- **Double-click a `.bkl`** and it opens in Backup Labs, on the Restore panel for that file, wherever it
  is: restore it whole into a folder, or find one file in it. An encrypted one asks for its password. A
  backup in one of your jobs' destinations can also go back to its original places.
- **Several jobs**, each with its own folders, destination, format, schedule and retention.
- **When:** manually, hourly, every 6 hours, daily or weekly; a minute after files change; and when the
  backup drive is plugged in (found again even if it comes back under another letter).
- **Smart retention:** everything from the last day, one a day for a month, one a week for a year. Or simply the last N.
- **Exclusions:** `node_modules; *.tmp; C:\Users\me\AppData`.
- **Open files** (Outlook `.pst`, databases) through a Volume Shadow Copy. Windows asks for administrator
  permission at each such backup.
- **Restore** a whole backup to the original places or to another folder, or **find one file** and bring
  back any of its versions.
- **Clone a drive:** an SD card, a USB stick, a hard disk or SSD, sector by sector, to a raw `.img` (Etcher,
  Win32 Disk Imager and `dd` read it) or straight onto another disk, and an image back onto a disk. Every
  sector, or only the space NTFS uses. The disk Windows runs from can be copied (through a shadow copy),
  never written to. Onto a bigger disk, the GPT backup table moves to the new end, a recovery partition at
  the end moves along, and the partition before it grows, file system included; onto a smaller one, as long
  as the partitions fit. Verified by reading everything back. Needs administrator permission.
- Lives in the tray, starts with Windows if you want, progress bar and cancel for everything.
- **Works with [SpaceScan](https://protagonistlabs.app/spacescan/?utm_source=github&utm_medium=readme&utm_campaign=backuplabs):**
  it names your backup folders and the folders you back up, backs a folder up from its row menu, and opens
  on a backup drive that's full.

Nothing leaves your computer: no account, no cloud, no telemetry. The only network call is the update check
against this repository's releases.

## Build

Needs the .NET 10 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
.\build.ps1            # engine tests, self-contained publish, dist\BackupLabs-<version>-setup.exe
dotnet run --project tests   # engine tests alone
```

The version lives only in `<Version>` in `Backup.csproj`. A release is
`gh release create v<version> dist\BackupLabs-<version>-setup.exe`; the updater picks up the latest release
whose asset ends in `setup.exe` and checks it against the SHA-256 GitHub publishes.

## Layout

| File | What |
|---|---|
| `Engine.cs` | backup, retention, restore, file versions, shadow copies |
| `Crypto.cs` | the encrypted archive stream, the saved password (DPAPI) |
| `Program.cs` | entry point, jobs and settings, USB drive tracking, the elevated run for open files |
| `MainWindow.xaml(.cs)` | the window, tray, scheduling, real-time watching |
| `Updater.cs`, `UpdateDialog.xaml` | self-update from GitHub releases |
| `installer/` | Inno Setup script and the icon generator |
| `tests/` | engine checks (`--vss <file>` runs the shadow-copy check, needs admin) |

Settings and the log are in `%APPDATA%\BackupLabs`.

Archives are `<date>.bkl`. Plain or encrypted is told by the first 8 bytes (`BKLENC1\0` = encrypted, see
`Crypto.cs`), never by the extension. The installer registers `.bkl` for the current user
(`HKCU\Software\Classes\.bkl` → `BackupLabs.Backup`, "Backup Labs backup", the app's icon) and removes it on
uninstall; opening one runs `BackupLabs.exe --open "<file>"`, which a running copy takes over.

For UI checks only: `BACKUPLABS_TEST_ROOT=<folder>` runs a separate copy whose settings, log and
ProtagonistLabs folder live in that folder, with its own single-instance names and no autostart writes, so it
never touches or hands its arguments to your own Backup Labs.

## More from Protagonist Labs

- [SpaceScan](https://protagonistlabs.app/spacescan/?utm_source=github&utm_medium=readme&utm_campaign=backuplabs): see what is filling up a drive, free.
- [File Labs](https://protagonistlabs.app/filelabs/?utm_source=github&utm_medium=readme&utm_campaign=backuplabs): a free dual-pane file manager.
- [All apps](https://protagonistlabs.app/?utm_source=github&utm_medium=readme&utm_campaign=backuplabs): Windows apps that each do one job properly.
