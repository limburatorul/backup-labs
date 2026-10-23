using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backup;

public sealed class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My backup";
    public List<string> Sources { get; set; } = new();
    public string Destination { get; set; } = "";
    public string DestVolume { get; set; } = "";      // volume id, to find a USB drive again after its letter changes
    public string Format { get; set; } = "Folders";   // Folders | Zip | Encrypted
    public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;
    public string Password { get; set; } = "";        // DPAPI-protected, this user on this PC
    public List<string> Exclude { get; set; } = new();
    public int IntervalHours { get; set; } = 24;      // 0 = manual only
    public int At { get; set; } = 12 * 60;            // Daily and Weekly: minutes after midnight
    public DayOfWeek Day { get; set; } = DayOfWeek.Monday; // Weekly
    public DateTime Since { get; set; } = DateTime.Now; // set up or rescheduled: the schedule counts from here (see Schedule)
    public bool RealTime { get; set; }
    public bool OnDriveConnected { get; set; }
    public bool OpenFiles { get; set; }
    public bool SmartKeep { get; set; } = true;
    public int Keep { get; set; } = 30;
    public DateTime? LastRun { get; set; }
    public string LastResult { get; set; } = "";

    // runtime only
    [JsonIgnore] public DateTime RetryAfter;
    [JsonIgnore] public bool Requested;               // run as soon as nothing else runs
    [JsonIgnore] public DateTime ChangedAt;           // last change the real-time watcher saw
    [JsonIgnore] public DateTime StartedAt;
    [JsonIgnore] public string Status { get; set; } = "";

    public string? PlainPassword() => Password == "" ? null : Crypto.Unprotect(Password);

    public SharedJob Shared() => new(Id, Name, Sources, Destination, Format.ToLowerInvariant(),
        LastRun?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
        LastRun == null && LastResult == "" ? null : LastResult.StartsWith("Failed") ? "failed" : LastResult.StartsWith("Cancelled") ? "cancelled" : "ok");

    public RunOptions Options() => new(Sources, Destination)
    {
        Zip = Format == "Folders" ? null : Compression,
        Password = Format == "Encrypted" ? PlainPassword() ?? throw new InvalidOperationException("Set a password for this encrypted job.") : null,
        Exclude = Exclude,
        SmartKeep = SmartKeep,
        Keep = Keep,
        OpenFiles = OpenFiles,
    };
}

public sealed class AppSettings
{
    public List<Job> Jobs { get; set; } = new();
    public string LastSeenVersion { get; set; } = ""; // for "What's new" after an update
    public bool TimedSchedules { get; set; }          // false in a file from 1.4.0 or older: see Store.Parse
}

static class Store
{
    // with the other studio apps' data since 1.6.0; moved from %APPDATA%\BackupLabs on the first start (see DataFolder)
    public static readonly string DataDir = Ecosystem.DataFolder(Path.Combine(Ecosystem.AppData, "BackupLabs"), Path.Combine(Ecosystem.Root, "Backup Labs"));
    static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    public static readonly string LogPath = Path.Combine(DataDir, "backup.log");
    static readonly object LogLock = new();

    public static AppSettings Load()
    {
        Directory.CreateDirectory(DataDir);
        AppSettings? s = null;
        if (File.Exists(SettingsPath))
            try { s = Parse(File.ReadAllText(SettingsPath), DateTime.Now); }
            catch (JsonException e) { Log("Settings unreadable, starting fresh: " + e.Message); }
        s ??= new AppSettings { TimedSchedules = true };
        if (s.Jobs.Count == 0) s.Jobs.Add(new Job());
        if (s.Jobs.FirstOrDefault(j => j.Id == Ecosystem.AppsJobId) is { } apps && Ecosystem.MoveAppsJob(apps.Sources, apps.Exclude))
        {
            Log($"{apps.Name}: now backs up {string.Join("; ", apps.Sources)}, where every studio app keeps its data");
            Save(s);
        }
        return s;
    }

    /// The settings file, brought up to date. 1.4.0 ran Daily and Weekly jobs 24 h or 7 days after their last run;
    /// 1.5.0 runs them at a time of day (and a day). A job that already ran gets the time and day of its last run, and
    /// counts from that run, so it comes due exactly when 1.4.0 would have run it, catch-up included. A job that never
    /// ran counts from now (Since's default): it waits for its first time instead of starting at once.
    public static AppSettings? Parse(string json, DateTime now)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json);
        if (s == null) return null;
        foreach (var j in s.Jobs)
        {
            if (!s.TimedSchedules && j.LastRun is DateTime last)
            {
                j.At = last.Hour * 60 + last.Minute;
                j.Day = last.DayOfWeek;
                j.Since = default;
            }
            if (j.At is < 0 or >= 24 * 60) j.At = 12 * 60; // a hand-edited file
            if (!Enum.IsDefined(j.Day)) j.Day = DayOfWeek.Monday;
            if (j.Since > now) j.Since = now;              // the clock was set back: don't wait for a date that far
        }
        s.TimedSchedules = true;
        return s;
    }

    public static void Save(AppSettings s)
    {
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, SettingsPath, true);
    }

    // What a second launch was started with, one path a line, left where the running instance looks
    // when it is told to come forward. The elevated run talks to the window through files here too.
    static readonly string IncomingPath = Path.Combine(DataDir, "incoming.txt");

    public static void Leave(string[] paths)
    {
        if (paths.Length == 0) return;
        Directory.CreateDirectory(DataDir);
        File.AppendAllLines(IncomingPath, paths);
    }

    public static string[] Take()
    {
        try
        {
            var paths = File.ReadAllLines(IncomingPath);
            File.Delete(IncomingPath);
            return paths;
        }
        catch (IOException) { return Array.Empty<string>(); } // nothing was left, the usual case
    }

    public static void Log(string message)
    {
        lock (LogLock) File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
    }
}

/// When a job runs by itself. Daily and Weekly run at their time (and day); Hourly and Every 6 h run that long after
/// the last run. Every schedule counts from the later of the last run and `since`, the moment the job was set up or
/// its schedule changed: a job just made, or given its folders and destination, waits for its first time. (1.4.0
/// counted a never-run job as due, so it started on the next 20-second check, the moment it could.) A time missed
/// while the PC was off or asleep is in the past at the next check, so the job runs then, once: its new last run
/// moves the next time past now.
public static class Schedule
{
    public static DateTime? Next(int intervalHours, int at, DayOfWeek day, DateTime? lastRun, DateTime since)
    {
        if (intervalHours <= 0) return null;
        var from = lastRun > since ? lastRun.Value : since;
        if (intervalHours is not (24 or 168)) return from.AddHours(intervalHours);
        // wall-clock arithmetic on purpose: 02:30 stays 02:30 across a DST change; on the spring day it doesn't exist and
        // the job runs at 03:00, when the clock has passed it; on the autumn day the run after the first 02:30 moves the
        // next time to the following day, so it doesn't run twice
        var slot = from.Date.AddMinutes(at);
        if (intervalHours == 168) slot = slot.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7);
        return slot > from ? slot : slot.AddDays(intervalHours / 24);
    }
}
