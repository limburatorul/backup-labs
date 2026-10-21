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
