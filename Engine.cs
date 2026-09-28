using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Backup;

public sealed record RunOptions(IReadOnlyList<string> Sources, string Destination)
{
    public CompressionLevel? Zip { get; init; }                      // null: incremental folders
    public string? Password { get; init; }                           // with Zip: an encrypted .bkl archive
    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();
    public bool SmartKeep { get; init; }                             // else: the last Keep backups
    public int Keep { get; init; } = 30;
    public bool OpenFiles { get; init; }                             // read from a VSS shadow copy (needs admin)
}

public sealed record RunResult(string Snapshot, int Copied, int Linked, int Failed, long BytesCopied);

public sealed record FileVersion(string Snapshot, string Rel, DateTime Modified, long Size)
{
    public string Name => Path.GetFileName(Rel);
    public string OriginalPath => Engine.Original(Rel);
}

// Every backup is a complete folder tree named by its date. Files unchanged since the previous
// backup (same size + modified time) are NTFS hard links to it, so they cost no space, and any
// backup can be deleted without breaking the others. Archive mode writes one full .zip (or an
// encrypted .bkl) per run instead.
public static class Engine
{
    const string Stamp = "yyyy-MM-dd_HHmmss";
    const string Partial = ".partial";

    sealed class Stats
    {
        public int Copied, Linked, Failed;
        public long Bytes, Done, Total;
        public bool LinkWarned;
        public IReadOnlyList<string> Exclude = Array.Empty<string>();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
    }

    public static string? Problem(IReadOnlyList<string> sources, string dest)
    {
        if (sources.Count == 0) return "Add at least one folder to back up.";
        if (string.IsNullOrWhiteSpace(dest)) return "Choose where the backups go.";
        if (!Path.IsPathFullyQualified(dest)) return "The destination must be a full path, like E:\\Backups.";
        foreach (var src in sources)
            if (Inside(src, dest) || Inside(dest, src)) return $"The destination overlaps {src}.";
        return null;
    }

    public static RunResult Run(RunOptions o, Action<string> log, Action<double, string>? progress, CancellationToken ct)
    {
        if (Problem(o.Sources, o.Destination) is string p) throw new InvalidOperationException(p);
        var dest = Path.GetFullPath(o.Destination);
        var sources = o.Sources.Select(Path.GetFullPath).ToList();
        var present = sources.Where(Directory.Exists).ToList();
        if (present.Count == 0) throw new InvalidOperationException("None of the folders exist right now (drive disconnected?).");

        Directory.CreateDirectory(dest);
        foreach (var d in Directory.GetDirectories(dest, "*" + Partial)) DeleteTree(d); // left by an interrupted run
        foreach (var f in Directory.GetFiles(dest, "*" + Partial)) File.Delete(f);

        var previous = Snapshots(dest);
        previous.Reverse();
        var ext = o.Zip == null ? "" : o.Password != null ? ".bkl" : ".zip";
        var final = Path.Combine(dest, DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture)) + ext;
        if (Path.Exists(final)) throw new InvalidOperationException("A backup was made less than a second ago.");
        var work = final + Partial;
        var st = new Stats { Exclude = o.Exclude };

        var shadows = new List<Shadow>();
        try
        {
            // where each source is read from: itself, or the same path inside a shadow copy of its volume
            var read = present.ToDictionary(s => s, s => s, StringComparer.OrdinalIgnoreCase);
            if (o.OpenFiles)
                foreach (var volume in present.Where(s => !s.StartsWith(@"\\")).GroupBy(s => Path.GetPathRoot(s)!, StringComparer.OrdinalIgnoreCase))
                {
                    var shadow = Shadow.Create(volume.Key);
                    shadows.Add(shadow);
                    log($"Shadow copy of {volume.Key} created");
                    foreach (var s in volume) read[s] = shadow.Device + @"\" + s[volume.Key.Length..];
                }

            st.Total = present.Sum(s => Scan(new DirectoryInfo(read[s]), s, st, ct));

            Stream? output = o.Zip == null ? null : File.Create(work);
            if (output != null && o.Password != null) output = new EncryptStream(output, o.Password);
            using (var archive = output == null ? null : new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var src in sources)
                {
                    if (!present.Contains(src)) { st.Failed++; log($"Missing folder, skipped: {src}"); continue; }
                    var rel = RelPath(src);
                    if (archive != null)
                        Copy(new DirectoryInfo(read[src]), src, rel.Replace('\\', '/'), null, archive, o.Zip!.Value, st, log, progress, ct);
                    else
                    {
                        var baseDir = previous.Select(s => Path.Combine(s, rel)).FirstOrDefault(Directory.Exists);
                        Copy(new DirectoryInfo(read[src]), src, Path.Combine(work, rel), baseDir, null, default, st, log, progress, ct);
                    }
                }
            }
        }
        finally
        {
            foreach (var s in shadows) s.Dispose();
        }
        if (o.Zip == null) Directory.Move(work, final); else File.Move(work, final);

        // A missing folder (unplugged drive) makes an incomplete backup; pruning after it could
        // eventually delete every good copy of that folder.
        if (present.Count == sources.Count) Prune(dest, o, log);
        else log("Old backups kept because a folder was missing.");
        return new RunResult(final, st.Copied, st.Linked, st.Failed, st.Bytes);
    }

    // dst is a folder path, or the entry prefix inside `zip` when archiving. `orig` is src's real path
    // (src may be inside a shadow copy): exclusions, messages and the log speak in real paths.
    static void Copy(DirectoryInfo src, string orig, string dst, string? baseDir, ZipArchive? zip, CompressionLevel level, Stats st,
        Action<string> log, Action<double, string>? progress, CancellationToken ct)
    {
        if (zip == null) Directory.CreateDirectory(dst);
        FileSystemInfo[] entries;
        try { entries = src.GetFileSystemInfos(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            st.Failed++; log($"Cannot read {orig}: {e.Message}"); return;
        }
        if (zip != null && entries.Length == 0) zip.CreateEntry(dst + "/"); // keep empty folders

        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var real = Path.Combine(orig, e.Name);
            if (Excluded(st.Exclude, e.Name, real)) continue;
            var to = zip == null ? Path.Combine(dst, e.Name) : dst + "/" + e.Name;
            var from = baseDir == null ? null : Path.Combine(baseDir, e.Name);
            if (e is DirectoryInfo d)
            {
                if (d.LinkTarget != null) continue; // junctions/symlinks: loops or duplicates (OneDrive placeholders are not links)
                Copy(d, real, to, from, zip, level, st, log, progress, ct);
                continue;
            }
            var f = (FileInfo)e;
            try
            {
                if (zip != null) { zip.CreateEntryFromFile(f.FullName, to, level); st.Copied++; st.Bytes += f.Length; }
                else if (from != null && Same(f, from) && Link(to, from, st, log)) st.Linked++;
                else { f.CopyTo(to); st.Copied++; st.Bytes += f.Length; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                st.Failed++; log($"Skipped {real}: {ex.Message}");
            }
            Advance(st, f.Length, real, progress);
        }
    }

    // Name patterns (node_modules, *.tmp) match any file or folder name; patterns with a backslash
    // (C:\Users\*\AppData) match whole paths.
    static bool Excluded(IReadOnlyList<string> patterns, string name, string path)
    {
        foreach (var p in patterns)
            if (p.Contains('\\')
                    ? FileSystemName.MatchesSimpleExpression(p.Replace(@"\", @"\\"), path) // '\' is the expression's escape character
                    : FileSystemName.MatchesSimpleExpression(p, name))
                return true;
        return false;
    }

    /// Whether a changed path falls under an exclusion (used by the real-time watcher).
    public static bool ExcludedPath(IReadOnlyList<string> patterns, string path)
    {
        var parts = path.Split('\\');
        for (int i = 1; i <= parts.Length; i++)
            if (Excluded(patterns, parts[i - 1], string.Join('\\', parts, 0, i))) return true;
        return false;
    }

    public static List<string> ParsePatterns(string text) =>
        text.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.TrimEnd('\\')).Where(p => p != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static bool Same(FileInfo f, string other)
    {
        var o = new FileInfo(other);
        return o.Exists && o.Length == f.Length && o.LastWriteTimeUtc == f.LastWriteTimeUtc;
    }

    // Progress weighs bytes plus a fixed cost per file, so folders of many small files still move the bar.
    const long PerFile = 64 * 1024;

    static void Advance(Stats st, long bytes, string path, Action<double, string>? progress)
    {
        st.Done += bytes + PerFile;
        if (progress == null || st.Clock.ElapsedMilliseconds < 150) return;
        st.Clock.Restart();
        progress(st.Total == 0 ? 0 : Math.Min(1, (double)st.Done / st.Total), $"{st.Copied + st.Linked:N0} files · {path}");
    }

    static long Scan(DirectoryInfo dir, string orig, Stats st, CancellationToken ct)
    {
        long total = 0;
        try
        {
            foreach (var e in dir.GetFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                var real = Path.Combine(orig, e.Name);
                if (Excluded(st.Exclude, e.Name, real)) continue;
                if (e is FileInfo f) total += f.Length + PerFile;
                else if (e is DirectoryInfo d && d.LinkTarget == null) total += Scan(d, real, st, ct);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the copy pass reports it
        return total;
    }

    // ---- restore ----

    public static bool IsArchive(string snapshot) =>
        snapshot.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || snapshot.EndsWith(".bkl", StringComparison.OrdinalIgnoreCase);

    static ZipArchive OpenArchive(string snapshot, string? password)
    {
        if (!snapshot.EndsWith(".bkl", StringComparison.OrdinalIgnoreCase)) return ZipFile.OpenRead(snapshot);
        if (password == null) throw new InvalidOperationException("This backup is encrypted: set the job's password first.");
        return new ZipArchive(new DecryptStream(File.OpenRead(snapshot), password), ZipArchiveMode.Read);
    }

    static void CheckRestoreTarget(string snapshot, string? into)
    {
        if (into == null) return;
        if (!Path.IsPathFullyQualified(into)) throw new InvalidOperationException("Choose a full folder path to restore into.");
        // writing into the backup folder could overwrite hard-linked files shared by other backups
        if (Inside(into, Path.GetDirectoryName(snapshot)!)) throw new InvalidOperationException("Restore somewhere outside the backup folder.");
    }

    // into == null: back to the original paths. Otherwise the backup's layout (C\Users\...) is recreated
    // inside `into`. Files already identical are skipped; files not in the backup are left alone.
    public static RunResult Restore(string snapshot, string? into, string? password, Action<string> log, Action<double, string>? progress, CancellationToken ct)
    {
        CheckRestoreTarget(snapshot, into);
        var st = new Stats();
        if (IsArchive(snapshot))
        {
            using var zip = OpenArchive(snapshot, password);
            st.Total = zip.Entries.Sum(e => e.Length + PerFile);
            foreach (var e in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var to = Target(e.FullName, into);
                if (to == null) { st.Failed++; log($"Skipped unexpected entry {e.FullName}"); continue; }
                try
                {
                    if (e.FullName.EndsWith('/')) Directory.CreateDirectory(to);
                    else { Prepare(to); e.ExtractToFile(to, true); st.Copied++; st.Bytes += e.Length; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    st.Failed++; log($"Could not restore {to}: {ex.Message}");
                }
                Advance(st, e.Length, to, progress);
            }
        }
        else
        {
            st.Total = Scan(new DirectoryInfo(snapshot), snapshot, st, ct);
            RestoreDir(new DirectoryInfo(snapshot), "", into, st, log, progress, ct);
        }
        return new RunResult(snapshot, st.Copied, st.Linked, st.Failed, st.Bytes);
    }

    static void RestoreDir(DirectoryInfo dir, string rel, string? into, Stats st,
        Action<string> log, Action<double, string>? progress, CancellationToken ct)
    {
        FileSystemInfo[] entries;
        try { entries = dir.GetFileSystemInfos(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            st.Failed++; log($"Cannot read {dir.FullName}: {e.Message}"); return;
        }
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var r = rel == "" ? e.Name : rel + "\\" + e.Name;
            if (e is DirectoryInfo d) { RestoreDir(d, r, into, st, log, progress, ct); continue; }
            var f = (FileInfo)e;
            var to = Target(r, into);
            if (to == null) { st.Failed++; log($"Skipped unexpected file {f.FullName}"); continue; }
            try
            {
                if (Same(f, to)) st.Linked++;
                else { Prepare(to); f.CopyTo(to, true); st.Copied++; st.Bytes += f.Length; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                st.Failed++; log($"Could not restore {to}: {ex.Message}");
            }
            Advance(st, f.Length, to, progress);
        }
    }

    /// Every distinct version of files whose name contains `query`, across the given backups.
    public static List<FileVersion> Find(IEnumerable<string> snapshots, string query, string? password, CancellationToken ct)
    {
        var found = new List<FileVersion>();
        foreach (var s in snapshots)
        {
            ct.ThrowIfCancellationRequested();
            if (IsArchive(s))
            {
                using var zip = OpenArchive(s, password);
                foreach (var e in zip.Entries)
                    if (!e.FullName.EndsWith('/') && e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        found.Add(new FileVersion(s, e.FullName.Replace('/', '\\'), e.LastWriteTime.DateTime, e.Length));
            }
            else
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
                foreach (var f in new DirectoryInfo(s).EnumerateFiles("*", options))
                    if (f.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                        found.Add(new FileVersion(s, Path.GetRelativePath(s, f.FullName), f.LastWriteTime, f.Length));
            }
        }
        // One file unchanged across many backups is one version: keep the newest backup holding it.
        // Zip times are 2-second precise, hence the rounding.
        return found
            .OrderByDescending(v => v.Snapshot, StringComparer.Ordinal)
            .GroupBy(v => (v.Rel.ToLowerInvariant(), v.Size, v.Modified.Ticks / (2 * TimeSpan.TicksPerSecond)))
            .Select(g => g.First())
            .OrderBy(v => v.Rel, StringComparer.OrdinalIgnoreCase).ThenByDescending(v => v.Modified)
            .ToList();
    }

    /// Restores one version; returns where it went.
    public static string RestoreFile(FileVersion v, string? into, string? password)
    {
        CheckRestoreTarget(v.Snapshot, into);
        var to = Target(v.Rel, into) ?? throw new InvalidOperationException("Unexpected path in the backup.");
        Prepare(to);
        if (IsArchive(v.Snapshot))
        {
            using var zip = OpenArchive(v.Snapshot, password);
            var entry = zip.GetEntry(v.Rel.Replace('\\', '/')) ?? throw new FileNotFoundException("Not found in the backup.", v.Rel);
            entry.ExtractToFile(to, true);
        }
        else File.Copy(Path.Combine(v.Snapshot, v.Rel), to, true);
        return to;
    }

    static void Prepare(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var cur = new FileInfo(file);
        if (cur.Exists && cur.IsReadOnly) cur.IsReadOnly = false; // else the overwrite is refused
    }

    // Inverse of RelPath. Null for anything that isn't drive\... or UNC\server\share\..., or that tries to
    // climb out with ".." (a tampered zip must not write outside its target).
    static string? Target(string rel, string? into)
    {
        var parts = rel.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains(':'))) return null;
        if (into != null) return Path.Combine(into, Path.Combine(parts));
        if (parts[0] == "UNC" && parts.Length >= 3) return @"\\" + string.Join('\\', parts[1..]);
        if (parts[0].Length == 1 && char.IsAsciiLetter(parts[0][0])) return parts[0] + @":\" + string.Join('\\', parts[1..]);
        return null;
    }

    /// Where a file in a backup came from, for display.
    public static string Original(string rel) => Target(rel, null) ?? rel;

    public static DateTime Date(string snapshot) =>
        DateTime.ParseExact(Path.GetFileNameWithoutExtension(snapshot), Stamp, CultureInfo.InvariantCulture);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    // Fails on FAT/exFAT, some network shares, or past NTFS's 1023 links per file: the caller then copies.
    static bool Link(string to, string from, Stats st, Action<string> log)
    {
        if (CreateHardLink(Long(to), Long(from), IntPtr.Zero)) return true;
        if (!st.LinkWarned) { st.LinkWarned = true; log($"Hard links not possible here (error {Marshal.GetLastWin32Error()}), copying unchanged files instead."); }
        return false;
    }

    static string Long(string p) => p.StartsWith(@"\\?\") ? p : p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;

    // C:\Users\me\Docs -> C\Users\me\Docs, \\nas\share -> UNC\nas\share: unique and stable across runs.
    public static string RelPath(string full)
    {
        full = full.TrimEnd('\\');
        return full.StartsWith(@"\\") ? Path.Combine("UNC", full[2..]) : full.Replace(":", "");
    }

    static bool Inside(string child, string parent) =>
        (Path.GetFullPath(child).TrimEnd('\\') + '\\').StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    // ---- retention ----

    // Backup folders, .zip and encrypted .bkl archives, oldest first.
    public static List<string> Snapshots(string dest) => !Directory.Exists(dest) ? new() :
        Directory.GetDirectories(dest).Where(d => IsStamp(Path.GetFileName(d)))
            .Concat(Directory.GetFiles(dest).Where(f => IsArchive(f) && IsStamp(Path.GetFileNameWithoutExtension(f))))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();

    static bool IsStamp(string name) => DateTime.TryParseExact(name, Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// Which backups the policy lets go. Smart: everything from the last 24 hours, the newest of each
    /// day for 30 days, the newest of each week for a year. The newest backup always stays.
    public static List<string> Expired(List<string> oldestFirst, bool smart, int keep, DateTime now)
    {
        if (!smart) return oldestFirst.Take(oldestFirst.Count - Math.Max(1, keep)).ToList();
        var kept = new HashSet<string>();
        var days = new HashSet<DateTime>();
        var weeks = new HashSet<(int, int)>();
        foreach (var s in Enumerable.Reverse(oldestFirst))
        {
            var d = Date(s);
            var age = now - d;
            if (age <= TimeSpan.FromHours(24)) kept.Add(s);
            else if (age <= TimeSpan.FromDays(30)) { if (days.Add(d.Date)) kept.Add(s); }
            else if (age <= TimeSpan.FromDays(365)) { if (weeks.Add((ISOWeek.GetYear(d), ISOWeek.GetWeekOfYear(d)))) kept.Add(s); }
        }
        if (oldestFirst.Count > 0) kept.Add(oldestFirst[^1]);
        return oldestFirst.Where(s => !kept.Contains(s)).ToList();
    }

    static void Prune(string dest, RunOptions o, Action<string> log)
    {
        foreach (var s in Expired(Snapshots(dest), o.SmartKeep, o.Keep, DateTime.Now))
        {
            try { DeleteTree(s); log($"Removed old backup {Path.GetFileName(s)}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"Could not remove {s}: {e.Message}"); }
        }
    }

    static void DeleteTree(string dir)
    {
        if (File.Exists(dir)) { File.Delete(dir); return; } // an archive
        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            if (File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    // ---- open files: a Volume Shadow Copy of each source volume, through WMI (needs admin) ----

    sealed class Shadow : IDisposable
    {
        dynamic? copy;
        public string Device = "";

        public static Shadow Create(string volume)
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!)!;
            dynamic wmi = locator.ConnectServer(".", @"root\cimv2");
            dynamic cls = wmi.Get("Win32_ShadowCopy");
            dynamic input = cls.Methods_.Item("Create").InParameters.SpawnInstance_();
            input.Properties_.Item("Volume").Value = volume;
            input.Properties_.Item("Context").Value = "ClientAccessible";
            dynamic output = cls.ExecMethod_("Create", input);
            int code = output.Properties_.Item("ReturnValue").Value;
            if (code != 0) throw new InvalidOperationException($"Could not make a shadow copy of {volume} (WMI code {code}). Open files need administrator rights.");
            string id = output.Properties_.Item("ShadowID").Value;
            dynamic copy = wmi.Get($"Win32_ShadowCopy.ID='{id}'");
            return new Shadow { copy = copy, Device = (string)copy.DeviceObject };
        }

        public void Dispose()
        {
            copy?.Delete_();
            copy = null;
        }
    }
}
