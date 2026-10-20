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
        if (args.Length == 2 && args[0] == "--elevated-clone") return Elevated.RunClone(args[1]);

        var instance = @"Local\limburatorul.BackupLabs" + (Ecosystem.TestRoot == null ? "" : ".Test");
        using var mutex = new Mutex(true, instance, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Show");
        // folders and files to back up, from File Labs' "Back up with Backup Labs" or any command line
        var paths = args.Where(a => !a.StartsWith("--")).ToList();
        // a whole job from a sister app (Shelf's game saves): read now, the app that wrote the file may delete it
        var at = Array.IndexOf(args, "--job-from");
        if (at >= 0 && at + 1 < args.Length)
        {
            paths.Remove(args[at + 1]);
            Directory.CreateDirectory(Store.DataDir); // for the log, on a PC where Backup Labs never ran
            try
            {
                if (Ecosystem.ReadSpec(File.ReadAllText(args[at + 1])) is JobSpec spec) paths.Add(Ecosystem.Line(spec));
                else Store.Log("--job-from: nothing to back up in " + args[at + 1]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Store.Log("--job-from: " + e.Message); }
        }
        // a sister app asking for one job on screen (SpaceScan's "Show in Backup Labs")
        var shown = Array.IndexOf(args, "--show-job");
        if (shown >= 0 && shown + 1 < args.Length)
        {
            paths.Remove(args[shown + 1]);
            if (Ecosystem.IsJobId(args[shown + 1])) paths.Add("job:" + args[shown + 1]);
        }
        // a backup double-clicked in Explorer (the installer's .bkl association): the Restore panel on that file
        var open = Array.IndexOf(args, "--open");
        if (open >= 0 && open + 1 < args.Length)
        {
            paths.Remove(args[open + 1]);
            if (Engine.OpenProblem(args[open + 1]) is string why) { Directory.CreateDirectory(Store.DataDir); Store.Log($"--open: ignored {args[open + 1]}: {why}"); }
            else paths.Add("open:" + Path.GetFullPath(args[open + 1]));
        }
        if (!first) { Store.Leave(paths.ToArray()); show.Set(); return 0; } // the running instance comes forward and takes them

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
}

static class Store
{
    public static readonly string DataDir = Path.Combine(Ecosystem.AppData, "BackupLabs");
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
    public sealed record Outcome(RunResult? Result, string? Error, bool Cancelled, CloneResult? Cloned = null);

    public static string FileFor(string id, string kind) => Path.Combine(Store.DataDir, $"elevated-{id}.{kind}");

    /// What the elevated copy says for a full disk; Windows' own message is in the user's language.
    public const string DriveFull = "The backup drive is full.";

    public static int Run(string jobId) => Serve(jobId, "Backup with open files", (progress, ct) =>
    {
        var job = Store.Load().Jobs.FirstOrDefault(j => j.Id == jobId) ?? throw new InvalidOperationException("The job no longer exists.");
        return new Outcome(Engine.Run(job.Options(), Store.Log, progress, ct), null, false);
    });

    /// A disk clone: reading and writing whole disks needs administrator rights. The request comes as a file.
    public static int RunClone(string id) => Serve(id, "Clone", (progress, ct) =>
    {
        var request = JsonSerializer.Deserialize<CloneRequest>(File.ReadAllText(FileFor(id, "request"))) ?? throw new InvalidOperationException("The clone request is unreadable.");
        return new Outcome(null, null, false, Clone.Run(request, Store.Log, progress, ct));
    });

    static int Serve(string id, string what, Func<Action<double, string>, CancellationToken, Outcome> work)
    {
        using var cts = new CancellationTokenSource();
        using var watch = new Timer(_ => { if (File.Exists(FileFor(id, "cancel"))) cts.Cancel(); }, null, 500, 500);
        Outcome outcome;
        try
        {
            outcome = work((f, text) =>
            {
                try { File.WriteAllText(FileFor(id, "progress"), f.ToString(CultureInfo.InvariantCulture) + "\n" + text); }
                catch (IOException) { } // the app is reading it right now; the next report comes in 150 ms
            }, cts.Token);
        }
        catch (OperationCanceledException) { outcome = new Outcome(null, null, true); }
        catch (Exception e) { outcome = new Outcome(null, Engine.DiskFull(e) ? DriveFull : e.Message, false); Store.Log($"{what} failed: {e}"); }
        File.WriteAllText(FileFor(id, "result"), JsonSerializer.Serialize(outcome));
        return 0;
    }
}
