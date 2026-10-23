using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Backup;

/// A job another app asks for: `--job-from <file.json>` with { id?, name, sources }.
public sealed record JobSpec(string? Id, string Name, List<string> Sources);

/// A job as the sister apps see it in data\backuplabs.jobs.json. LastRun is ISO 8601 UTC; LastResult is
/// "ok", "failed" or "cancelled", both null before the first run.
public sealed record SharedJob(string Id, string Name, List<string> Sources, string Destination, string Format, string? LastRun, string? LastResult);

// The Protagonist Labs folder, %APPDATA%\ProtagonistLabs: every app leaves its card in apps\<id>.json and
// what it shares in data\<id>.<kind>.json. No server, nothing sent anywhere. Files written by other apps
// are not trusted: missing, unreadable or odd means absent, never an error on screen.
static class Ecosystem
{
    /// Test only: BACKUPLABS_TEST_ROOT stands in for %APPDATA% (settings, log, this folder), gives the copy its own
    /// single-instance names and keeps it off the Run key, so a UI check never reaches the user's own Backup Labs.
    /// (Windows' known-folder lookup ignores the APPDATA variable, hence a variable of our own.)
    public static readonly string? TestRoot = Environment.GetEnvironmentVariable("BACKUPLABS_TEST_ROOT") is { Length: > 0 } t ? t : null;
    public static readonly string AppData = TestRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static readonly string Root = Path.Combine(AppData, "ProtagonistLabs");
    public const string ShelfJobId = "shelf-saves"; // Shelf's "Back up all saves with Backup Labs"
    public const string StashJobId = "stash-backups"; // Stash's "Backup Labs" line in its Works with

    /// The card, at every start, off the UI thread: the folder is a bonus and must never slow the start.
    public static void WriteCard(string version) => Task.Run(() =>
    {
        try
        {
            var file = Path.Combine(Root, "apps", "backuplabs.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["schema"] = 1,
                ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["id"] = "backuplabs",
                ["name"] = "Backup Labs",
                ["version"] = version,
                ["exe"] = Environment.ProcessPath!,
                ["edition"] = "free",
                ["paid"] = false,
                ["page"] = "https://protagonistlabs.app/backuplabs/",
                ["commands"] = new Dictionary<string, string[]>
                {
                    ["job-from"] = new[] { "--job-from", "{file}" },
                    ["back-up"] = new[] { "{path}" },           // a new job with that folder or file, waiting for a destination
                    ["show-job"] = new[] { "--show-job", "{id}" }, // the window, on that job (ids from data\backuplabs.jobs.json)
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    });

    /// The jobs, for the sister apps (SpaceScan names backup folders and backed-up folders with them):
    /// data\backuplabs.jobs.json. Written whenever they change and after every run; small, so on the spot.
    public static void WriteJobs(IEnumerable<SharedJob> jobs)
    {
        try
        {
            var file = Path.Combine(Root, "data", "backuplabs.jobs.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JobsJson(jobs, DateTime.UtcNow));
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // a courtesy to the sisters, never a failure here
    }

    public static string JobsJson(IEnumerable<SharedJob> jobs, DateTime now) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["schema"] = 1,
        ["updated"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["jobs"] = jobs.ToList(),
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    /// A sister's exe from its card, or null when it isn't installed (no card, a broken one, or the exe is gone).
    public static string? Exe(string id) => Exe(id, Root);

    static string? Exe(string id, string root)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "apps", id + ".json")));
            var c = doc.RootElement;
            return c.ValueKind == JsonValueKind.Object && c.TryGetProperty("schema", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1
                && c.TryGetProperty("exe", out var exe) && exe.ValueKind == JsonValueKind.String && Local(exe.GetString()) && File.Exists(exe.GetString()) ? exe.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { return null; }
    }

    /// A path on a drive of this PC, judged by its text alone: a \\server\share or \\?\ path is refused before
    /// File.Exists could reach for the network (an SMB lookup hands out the user's credentials) or a device.
    static bool Local(string? path) => path != null && Regex.IsMatch(path, @"^[A-Za-z]:\\") && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    /// A job id another app may name (--show-job): the same plain characters as in a spec.
    public static bool IsJobId(string id) => Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$");

    /// The paths that can be backed up: full paths that exist, each once.
    public static List<string> Existing(IEnumerable<string> paths) =>
        paths.Where(p => Path.IsPathFullyQualified(p) && Path.Exists(p))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new();

    /// A job request, from the file another app wrote or from the line it left for the running instance.
    /// Null when it is not one, or names nothing that exists.
    public static JobSpec? ReadSpec(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var id = r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            // the id ends up in file names (Elevated.FileFor), so only plain characters
            if (id != null && !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$")) return null;
            var name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            if (name.Length > 100) name = name[..100];
            var sources = Existing(Strings(r, "sources"));
            return sources.Count == 0 ? null : new JobSpec(id, name, sources);
        }
        catch (JsonException) { return null; }
    }

    /// One line, so it can go through incoming.txt with the paths a second launch leaves.
    public static string Line(JobSpec s) => JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// Every save folder or file Shelf knows of (data\shelf.games.json), or null when there is no usable list.
    public static List<string>? ShelfSaves()
    {
        try { return ShelfSaves(File.ReadAllText(Path.Combine(Root, "data", "shelf.games.json"))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static List<string>? ShelfSaves(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1
                || !r.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array) return null;
            var saves = Existing(games.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object).SelectMany(g => Strings(g, "saves")));
            return saves.Count == 0 ? null : saves;
        }
        catch (Exception e) when (e is JsonException or FormatException) { return null; }
    }

    /// The job Stash made follows Stash's backup folder (data\stash.items.json), so a folder moved in Stash is
    /// backed up from where it is now. True when the job's sources changed; with Stash gone or its file unusable
    /// the job keeps what it has. The root is a test's; the app uses its own.
    public static bool FollowStash(string jobId, List<string> sources, string? root = null)
    {
        if (jobId != StashJobId || StashFolderUnder(root ?? Root) is not string folder
            || sources.Count == 1 && string.Equals(sources[0], folder, StringComparison.OrdinalIgnoreCase)) return false;
        sources.Clear();
        sources.Add(folder);
        return true;
    }

    static string? StashFolderUnder(string root)
    {
        if (Exe("stash", root) == null) return null; // only while Stash is here: its card exists and its exe too
        try
        {
            var file = new FileInfo(Path.Combine(root, "data", "stash.items.json"));
            return file.Length > 1 << 20 ? null : StashFolder(File.ReadAllText(file.FullName)); // 20 items are ~10 KB
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// Stash's backup folder from its file (ReadAllText drops a BOM), or null unless it is a local folder that exists.
    public static string? StashFolder(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1
                || !r.TryGetProperty("folder", out var f) || f.ValueKind != JsonValueKind.String) return null;
            var folder = f.GetString()!;
            return folder.Length <= 1024 && Local(folder) && Directory.Exists(folder) ? Path.GetFullPath(folder) : null;
        }
        catch (Exception e) when (e is JsonException or FormatException) { return null; }
    }

    // ---- "Back up our apps' data": one job for the settings and data of every studio app here ----

    public const string AppsJobId = "protagonist-apps";

    // Electron's own caches, rebuilt on the next start
    static readonly string[] Chromium = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Shared Dictionary", "Crashpad" };

    // Where each app kept its data before every app moved into the shared folder (1.5.0's job listed them one by one).
    // All under Roaming AppData; Sysoptimizer's under ProgramData.
    static readonly string[] OldFolders = { "game-browser", "reel", "Reminder", "SteamRadar", "Lume", "Frostpane", "FileLabs", "ThunderbirdBackup", "SpaceScan", "AutoClicker", "Glaze", "BackupLabs" };

    public static (List<string> Sources, List<string> Exclude) AppsJob() => (new() { Root }, AppsExclude(Root));

    /// Every studio app keeps its settings and data in the shared folder, %APPDATA%\ProtagonistLabs\<App>, so the job
    /// is that folder alone. Left out: what the apps download or rebuild by themselves, as path patterns, so an app
    /// that moves its data in later is covered too. Chromium's caches sit at the top of each Electron app's folder;
    /// Shelf's art and trailers come back from the stores, Lume's icons from the apps.
    public static List<string> AppsExclude(string root) =>
        Chromium.Select(n => Path.Combine(root, "*", n))
            .Concat(new[] { "covers", "screenshots", "trailers", "thumbs" }.Select(n => Path.Combine(root, "Shelf", n)))
            .Append(Path.Combine(root, "Lume", "icons")).ToList();

    public static bool MoveAppsJob(List<string> sources, List<string> exclude) => MoveAppsJob(sources, exclude, Root, AppData,
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    /// 1.5.0's job, brought to the shared folder: the apps' old folders (and anything inside the shared folder, which
    /// would be backed up twice) leave the sources, the shared folder comes first, the old caches' exclusions give way
    /// to the new ones. A folder or file the user added that is none of these stays, and so do their own exclusions.
    /// Only while an old folder is still listed, so it runs once and a later edit by the user is never undone.
    /// True when the job changed. The paths are a test's; the app passes the real ones.
    public static bool MoveAppsJob(List<string> sources, List<string> exclude, string root, string roaming, string programData)
    {
        var old = OldFolders.Select(f => Path.Combine(roaming, f)).Append(Path.Combine(programData, "Sysoptimizer")).ToList();
        bool Old(string p) => old.Any(o => Under(p, o));
        if (!sources.Any(Old)) return false;
        sources.RemoveAll(p => Old(p) || Under(p, root));
        sources.Insert(0, root);
        exclude.RemoveAll(Old);
        exclude.AddRange(AppsExclude(root).Where(x => !exclude.Contains(x, StringComparer.OrdinalIgnoreCase)));
        return true;
    }

    static bool Under(string path, string folder) =>
        path.TrimEnd('\\').Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + @"\", StringComparison.OrdinalIgnoreCase);

    // ---- where Backup Labs keeps its own settings and log ----

    /// %APPDATA%\ProtagonistLabs\Backup Labs, moved there from %APPDATA%\BackupLabs once, before anything in it is opened
    /// (the installer does the same; whichever runs first does it). A rename, never a copy: instant, and nothing is ever
    /// half moved. When the new folder already holds something, nothing is mixed: the old one stays and the log says so.
    /// When the rename fails (a file held open), this session uses the old folder and the next start tries again.
    public static string DataFolder(string old, string now)
    {
        if (!Directory.Exists(old)) return now;
        try
        {
            if (Directory.Exists(now))
            {
                if (Directory.EnumerateFileSystemEntries(now).Any())
                {
                    File.AppendAllText(Path.Combine(now, "backup.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {old} was left where it is: {now} already holds data{Environment.NewLine}");
                    return now;
                }
                Directory.Delete(now);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(now)!);
            Directory.Move(old, now);
            return now;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return old; }
    }

    /// An installed app's exe from its uninstall entry (DisplayIcon), looked for per machine first, in both registry
    /// views, then for this user: the studio's apps install per machine now, their older versions per user.
    public static string? Uninstalled(string key)
    {
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var k = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + key);
                if (k?.GetValue("DisplayIcon") is string icon && icon.Split(',')[0].Trim('"') is var exe && Local(exe) && File.Exists(exe)) return exe;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    /// Sister apps installed on this PC (their card exists and its exe is still there), and whether the
    /// user bought one of the paid ones.
    public static (HashSet<string> Installed, bool Bought) Sisters()
    {
        var installed = new HashSet<string>();
        var bought = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "apps"), "*.json"))
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var c = doc.RootElement;
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("schema", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1) continue;
                    if (!c.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    if (!c.TryGetProperty("exe", out var exe) || exe.ValueKind != JsonValueKind.String || !Local(exe.GetString()) || !File.Exists(exe.GetString())) continue;
                    installed.Add(id.GetString()!);
                    if (c.TryGetProperty("paid", out var p) && p.ValueKind == JsonValueKind.True
                        && c.TryGetProperty("edition", out var ed) && ed.ValueKind == JsonValueKind.String && ed.GetString() == "full") bought = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // no folder yet
        return (installed, bought);
    }
}
