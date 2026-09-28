# Backup Labs

Backups for Windows that you set up once and stop thinking about. Pick any folders (or whole drives),
pick where the backups go, and Backup Labs keeps them, on a schedule, when files change, or when you
plug in the backup drive.

**[Download the installer](https://github.com/limburatorul/backup-labs/releases/latest)** — Windows 10/11, x64, no admin rights needed. The app updates itself.

## What it does

- **Incremental backups you can browse.** Every backup is a normal folder named by its date. Files that
  didn't change are NTFS hard links to the previous backup, so they take no extra space, and any backup can
  be deleted without breaking the others.
- **Or archives:** one `.zip` per backup with four compression levels, or an **encrypted** archive
  (AES-256-GCM, key from your password with PBKDF2-SHA256, 600,000 iterations). Encrypted archives open
  only in Backup Labs, and only with the password.
- **Several jobs**, each with its own folders, destination, format, schedule and retention.
- **When:** manually, hourly, every 6 hours, daily or weekly; a minute after files change; and when the
  backup drive is plugged in (found again even if it comes back under another letter).
- **Smart retention:** everything from the last day, one a day for a month, one a week for a year. Or simply the last N.
- **Exclusions:** `node_modules; *.tmp; C:\Users\me\AppData`.
- **Open files** (Outlook `.pst`, databases) through a Volume Shadow Copy. Windows asks for administrator
  permission at each such backup.
- **Restore** a whole backup to the original places or to another folder, or **find one file** and bring
  back any of its versions.
- Lives in the tray, starts with Windows if you want, progress bar and cancel for everything.

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
