using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;

namespace Backup;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--elevated-job") return Elevated.Run(args[1]);

        using var mutex = new Mutex(true, @"Local\limburatorul.BackupLabs", out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\limburatorul.BackupLabs.Show");
        // folders and files to back up, from File Labs' "Back up with Backup Labs" or any command line
        var paths = args.Where(a => !a.StartsWith("--")).ToArray();
        if (!first) { Store.Leave(paths); show.Set(); return 0; } // the running instance comes forward and takes them

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = (ResourceDictionary)Application.LoadComponent(new Uri("/BackupLabs;component/Theme.xaml", UriKind.Relative));
        var window = new MainWindow(show);
        if (!args.Contains("--tray")) window.Show();
        window.NewJobFor(paths.Concat(Store.Take()));
        app.Run();
        return 0;
    }
}

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
}

static class Store
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BackupLabs");
    static readonly string SettingsPath = Path.Combine(DataDir, "settings.json");
    public static readonly string LogPath = Path.Combine(DataDir, "backup.log");
    static readonly object LogLock = new();

    public static AppSettings Load()
    {
        Directory.CreateDirectory(DataDir);
        AppSettings? s = null;
        if (File.Exists(SettingsPath))
            try { s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)); }
            catch (JsonException e) { Log("Settings unreadable, starting fresh: " + e.Message); }
        s ??= new AppSettings();
        if (s.Jobs.Count == 0) s.Jobs.Add(new Job());
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

// A USB disk can come back under another letter; its volume id stays.
static class Drives
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int size);

    public static string Volume(string path)
    {
        var root = Path.IsPathFullyQualified(path) ? Path.GetPathRoot(path) : null;
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return "";
        var name = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPoint(root, name, name.Capacity) ? name.ToString() : "";
    }

    /// Whether the job's backup drive is connected; follows it to a new letter if it moved.
    public static bool Present(Job j)
    {
        if (j.DestVolume == "") return Directory.Exists(Path.GetPathRoot(j.Destination) ?? "");
        if (Volume(j.Destination) == j.DestVolume) return true;
        foreach (var d in DriveInfo.GetDrives())
            if (Volume(d.Name) == j.DestVolume)
            {
                var rel = Path.GetRelativePath(Path.GetPathRoot(j.Destination)!, j.Destination);
                j.Destination = rel == "." ? d.Name : Path.Combine(d.Name, rel);
                Store.Log($"{j.Name}: the backup drive is now {d.Name}");
                return true;
            }
        return false;
    }
}

// Open files need a shadow copy, and that needs administrator rights, so such a job runs in a second,
// elevated copy of the app (one UAC prompt per backup). The two talk through small files next to the
// settings: progress and cancel while it runs, the result at the end.
static class Elevated
{
    public sealed record Outcome(RunResult? Result, string? Error, bool Cancelled);

    public static string FileFor(string jobId, string kind) => Path.Combine(Store.DataDir, $"elevated-{jobId}.{kind}");

    public static int Run(string jobId)
    {
        using var cts = new CancellationTokenSource();
        using var watch = new Timer(_ => { if (File.Exists(FileFor(jobId, "cancel"))) cts.Cancel(); }, null, 500, 500);
        Outcome outcome;
        try
        {
            var job = Store.Load().Jobs.FirstOrDefault(j => j.Id == jobId) ?? throw new InvalidOperationException("The job no longer exists.");
            var result = Engine.Run(job.Options(), Store.Log, (f, text) =>
            {
                try { File.WriteAllText(FileFor(jobId, "progress"), f.ToString(CultureInfo.InvariantCulture) + "\n" + text); }
                catch (IOException) { } // the app is reading it right now; the next report comes in 150 ms
            }, cts.Token);
            outcome = new Outcome(result, null, false);
        }
        catch (OperationCanceledException) { outcome = new Outcome(null, null, true); }
        catch (Exception e) { outcome = new Outcome(null, e.Message, false); Store.Log("Backup with open files failed: " + e); }
        File.WriteAllText(FileFor(jobId, "result"), JsonSerializer.Serialize(outcome));
        return 0;
    }
}
