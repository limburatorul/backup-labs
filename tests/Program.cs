using System.IO.Compression;
using Backup;

// Open files through a shadow copy. Needs admin, so it runs on its own: Tests.exe --vss <result file>
if (args.Length == 2 && args[0] == "--vss")
{
    var vroot = Path.Combine(Path.GetTempPath(), "backup-vss-" + DateTime.Now.Ticks);
    var vsrc = Path.Combine(vroot, "src");
    Directory.CreateDirectory(vsrc);
    File.WriteAllText(Path.Combine(vsrc, "locked.pst"), "mail database");
    string outcome;
    using (var held = new FileStream(Path.Combine(vsrc, "locked.pst"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try
        {
            var plain = Engine.Run(new RunOptions(new[] { vsrc }, Path.Combine(vroot, "plain")), _ => { }, null, default);
            var shadow = Engine.Run(new RunOptions(new[] { vsrc }, Path.Combine(vroot, "shadow")) { OpenFiles = true }, _ => { }, null, default);
            var copied = Path.Combine(shadow.Snapshot, Engine.RelPath(vsrc), "locked.pst");
            outcome = plain.Failed == 1 && shadow.Failed == 0 && File.ReadAllText(copied) == "mail database"
                ? "ok   a file held open by another program is copied through a shadow copy"
                : $"FAIL plain failed={plain.Failed}, shadow failed={shadow.Failed}";
        }
        catch (Exception e) { outcome = "FAIL " + e; }
    }
    File.WriteAllText(args[1], outcome);
    return outcome.StartsWith("ok") ? 0 : 1;
}

var root = Path.Combine(Path.GetTempPath(), "backup-test-" + DateTime.Now.Ticks);
var src = Path.Combine(root, "src");
var dst = Path.Combine(root, "dst");
Directory.CreateDirectory(Path.Combine(src, "sub"));
File.WriteAllText(Path.Combine(src, "a.txt"), "one");
File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "two");
File.SetAttributes(Path.Combine(src, "sub", "b.txt"), FileAttributes.ReadOnly);
var rel = Engine.RelPath(src);
var log = new List<string>();
string Read(RunResult r, string f) => File.ReadAllText(Path.Combine(r.Snapshot, rel, f));
RunResult Backup(RunOptions o) { Thread.Sleep(1100); return Engine.Run(o, log.Add, null, default); }
RunResult Run(int keep, params string[] sources) => Backup(new RunOptions(sources, dst) { Keep = keep });
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   " : "FAIL ") + what); if (!ok) failures++; }

// ---- incremental folders ----
var r1 = Run(2, src);
Check(r1.Copied == 2 && r1.Linked == 0, "first backup copies everything");

File.WriteAllText(Path.Combine(src, "a.txt"), "changed");
var r2 = Run(2, src);
Check(r2.Copied == 1 && r2.Linked == 1, "second backup copies only the changed file");
Check(Read(r1, "a.txt") == "one" && Read(r2, "a.txt") == "changed", "each backup keeps its own version");
Check(Read(r2, @"sub\b.txt") == "two", "unchanged file is present in the new backup");

var versions = Engine.Find(Engine.Snapshots(dst), "a.tx", null, default);
Check(versions.Count == 2 && versions.All(v => v.Name == "a.txt"), "find lists each distinct version once");
var restoredOld = Engine.RestoreFile(versions.OrderBy(v => v.Modified).First(), Path.Combine(root, "one-file"), null);
Check(File.ReadAllText(restoredOld) == "one", "one old version restored on its own");
Check(Engine.Find(Engine.Snapshots(dst), "b.txt", null, default).Count == 1, "an unchanged file is one version, not one per backup");

File.Delete(Path.Combine(src, "a.txt"));
var r3 = Run(2, src);
Check(!File.Exists(Path.Combine(r3.Snapshot, rel, "a.txt")), "deleted file is absent from the new backup");
Check(!Directory.Exists(r1.Snapshot) && Engine.Snapshots(dst).Count == 2, "retention removes the oldest (read-only file included)");
Check(Read(r2, @"sub\b.txt") == "two" && Read(r3, @"sub\b.txt") == "two", "pruning leaves linked files intact");

var r4 = Run(1, src, Path.Combine(root, "missing"));
Check(r4.Failed == 1 && Engine.Snapshots(dst).Count == 3, "a missing folder blocks pruning");

// ---- exclusions ----
Directory.CreateDirectory(Path.Combine(src, "node_modules", "x"));
File.WriteAllText(Path.Combine(src, "node_modules", "x", "big.js"), "junk");
File.WriteAllText(Path.Combine(src, "sub", "scratch.tmp"), "junk");
Directory.CreateDirectory(Path.Combine(src, "cache"));
File.WriteAllText(Path.Combine(src, "cache", "c.bin"), "junk");
var exclude = Engine.ParsePatterns($"node_modules; *.tmp ;{Path.Combine(src, "cache")}\\");
var ex = Backup(new RunOptions(new[] { src }, Path.Combine(root, "dst-ex")) { Exclude = exclude });
Check(!Directory.Exists(Path.Combine(ex.Snapshot, rel, "node_modules")) && !File.Exists(Path.Combine(ex.Snapshot, rel, "sub", "scratch.tmp"))
      && !Directory.Exists(Path.Combine(ex.Snapshot, rel, "cache")) && File.Exists(Path.Combine(ex.Snapshot, rel, "sub", "b.txt")),
      "exclusions skip names, wildcards and full paths");
Check(Engine.ExcludedPath(exclude, Path.Combine(src, "node_modules", "x", "big.js")) && !Engine.ExcludedPath(exclude, Path.Combine(src, "sub", "b.txt")),
      "the watcher sees the same exclusions");

// ---- archives ----
Directory.CreateDirectory(Path.Combine(src, "empty"));
var z = Backup(new RunOptions(new[] { src }, dst) { Zip = CompressionLevel.SmallestSize, Keep = 3, Exclude = exclude });
using (var a = ZipFile.OpenRead(z.Snapshot))
{
    var prefix = rel.Replace('\\', '/') + "/";
    Check(z.Snapshot.EndsWith(".zip") && new StreamReader(a.GetEntry(prefix + "sub/b.txt")!.Open()).ReadToEnd() == "two", "archive holds the files");
    Check(a.GetEntry(prefix + "empty/") != null, "archive keeps empty folders");
}
Check(Engine.Snapshots(dst).Count == 3 && Engine.Snapshots(dst)[^1] == z.Snapshot, "archives count toward retention");

// ---- encrypted archives ----
var encDst = Path.Combine(root, "dst-enc");
var enc = Backup(new RunOptions(new[] { src }, encDst) { Zip = CompressionLevel.Fastest, Password = "correct horse", Exclude = exclude });
Check(enc.Snapshot.EndsWith(".bkl") && !File.ReadAllText(enc.Snapshot).Contains("b.txt"), "encrypted archive hides even the file names");
var fromEnc = Path.Combine(root, "from-enc");
Engine.Restore(enc.Snapshot, fromEnc, "correct horse", log.Add, null, default);
Check(File.ReadAllText(Path.Combine(fromEnc, rel, "sub", "b.txt")) == "two", "encrypted archive restores with the password");
Check(Engine.Find(new[] { enc.Snapshot }, "b.txt", "correct horse", default).Count == 1, "find looks inside encrypted archives");
try { Engine.Restore(enc.Snapshot, Path.Combine(root, "x"), "wrong", log.Add, null, default); Check(false, "wrong password refused"); }
catch (InvalidOperationException e) { Check(e.Message.Contains("Wrong password"), "wrong password is refused"); }
var tampered = Path.Combine(root, "tampered");
Directory.CreateDirectory(tampered);
var bytes = File.ReadAllBytes(enc.Snapshot);
bytes[^20] ^= 1;
File.WriteAllBytes(Path.Combine(tampered, Path.GetFileName(enc.Snapshot)), bytes);
try { Engine.Restore(Path.Combine(tampered, Path.GetFileName(enc.Snapshot)), Path.Combine(root, "y"), "correct horse", log.Add, null, default); Check(false, "tampering detected"); }
catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { Check(true, "an altered encrypted archive is refused"); }
{   // several chunks, the last one partial, read back through random seeks
    var data = new byte[3 * (1 << 20) + 12345];
    new Random(1).NextBytes(data);
    var file = Path.Combine(root, "chunks.bin");
    using (var w = new EncryptStream(File.Create(file), "pw", 100_000)) w.Write(data);
    using var r = new DecryptStream(File.OpenRead(file), "pw");
    r.Position = (1 << 20) - 5;
    var part = new byte[20];
    r.ReadExactly(part);
    Check(r.Length == data.Length && part.SequenceEqual(data.Skip((1 << 20) - 5).Take(20)), "encryption spans chunks and seeks");
    var cut = Path.Combine(root, "cut.bin");
    File.WriteAllBytes(cut, File.ReadAllBytes(file).Take(Crypto.HeaderSize + 2 * ((1 << 20) + 16)).ToArray());
    try { using var c = new DecryptStream(File.OpenRead(cut), "pw"); c.Position = (1 << 20) + 1; c.ReadByte(); Check(false, "truncation detected"); }
    catch (InvalidDataException) { Check(true, "an archive cut at a chunk boundary is detected"); }
}
var saved = Crypto.Protect("s3cret");
Check(saved != "s3cret" && Crypto.Unprotect(saved) == "s3cret", "saved password round-trips through DPAPI");

// ---- smart retention ----
var now = new DateTime(2026, 9, 28, 12, 0, 0);
string S(DateTime d) => Path.Combine(root, d.ToString("yyyy-MM-dd_HHmmss"));
var times = new[]
{
    now.AddHours(-1), now.AddHours(-5),                         // last 24 h: both stay
    now.AddDays(-2).AddHours(-1), now.AddDays(-2).AddHours(-3), // same day: the newer stays
    now.AddDays(-100), now.AddDays(-101),                       // same ISO week, 100 days ago: the newer stays
    now.AddDays(-400),                                          // older than a year: goes
};
var fake = times.OrderBy(d => d).Select(S).ToList();
var gone = Engine.Expired(fake, true, 0, now).Select(Engine.Date).ToHashSet();
Check(gone.SetEquals(new[] { now.AddDays(-2).AddHours(-3), now.AddDays(-101), now.AddDays(-400) }),
      "smart retention: all of the last day, daily for a month, weekly for a year");
Check(Engine.Expired(fake.Take(1).ToList(), true, 0, now).Count == 0, "the newest backup always stays");

// ---- restore ----
var b = Path.Combine(src, "sub", "b.txt");
var same = Engine.Restore(r3.Snapshot, null, null, log.Add, null, default);
Check(same.Copied == 0 && same.Linked == 1, "restore skips files that are already identical");
File.SetAttributes(b, FileAttributes.Normal); File.WriteAllText(b, "broken!"); File.SetAttributes(b, FileAttributes.ReadOnly);
var back = Engine.Restore(r3.Snapshot, null, null, log.Add, null, default);
Check(back.Copied == 1 && File.ReadAllText(b) == "two", "restore to original location overwrites a changed read-only file");
var fromZip = Path.Combine(root, "fromzip");
Engine.Restore(z.Snapshot, fromZip, null, log.Add, null, default);
Check(File.ReadAllText(Path.Combine(fromZip, rel, "sub", "b.txt")) == "two" && Directory.Exists(Path.Combine(fromZip, rel, "empty")), "restore from zip into another folder");
Engine.Restore(r3.Snapshot, Path.Combine(root, "fromdir"), null, log.Add, null, default);
Check(File.ReadAllText(Path.Combine(root, "fromdir", rel, "sub", "b.txt")) == "two", "restore from folders into another folder");
Directory.CreateDirectory(Path.Combine(root, "z"));
var evil = Path.Combine(root, "z", "evil.zip");
using (var a = ZipFile.Open(evil, ZipArchiveMode.Create))
    using (var w = new StreamWriter(a.CreateEntry("../escape.txt").Open())) w.Write("x");
var ev = Engine.Restore(evil, Path.Combine(root, "evilout"), null, log.Add, null, default);
Check(ev.Failed == 1 && !File.Exists(Path.Combine(root, "escape.txt")), "zip entries climbing out of the target are refused");
try { Engine.Restore(r3.Snapshot, Path.Combine(dst, "x"), null, log.Add, null, default); Check(false, "restore into backup folder refused"); }
catch (InvalidOperationException) { Check(true, "restore into the backup folder is refused"); }

try { Engine.Run(new RunOptions(new[] { src }, Path.Combine(src, "bk")), log.Add, null, default); Check(false, "overlap refused"); }
catch (InvalidOperationException) { Check(true, "destination inside a source is refused"); }

Check(Engine.RelPath(@"D:\") == "D" && Engine.RelPath(@"\\nas\share\x") == @"UNC\nas\share\x", "path mapping");

// ---- a single file as a source ----
var lone = Path.Combine(root, "lone", "notes.txt");
Directory.CreateDirectory(Path.GetDirectoryName(lone)!);
File.WriteAllText(lone, "just this");
File.WriteAllText(Path.Combine(root, "lone", "neighbour.txt"), "not asked for");
var loneDst = Path.Combine(root, "dst-file");
var f1 = Backup(new RunOptions(new[] { lone }, loneDst));
Check(f1.Copied == 1 && f1.Failed == 0 && File.ReadAllText(Path.Combine(f1.Snapshot, Engine.RelPath(lone))) == "just this", "a single file is backed up");
Check(Directory.GetFiles(Path.GetDirectoryName(Path.Combine(f1.Snapshot, Engine.RelPath(lone)))!).Length == 1, "and nothing else from its folder");
var f2 = Backup(new RunOptions(new[] { lone }, loneDst));
Check(f2.Copied == 0 && f2.Linked == 1, "unchanged, the file is linked to the previous backup");
var f3 = Backup(new RunOptions(new[] { lone, Path.Combine(src, "sub") }, Path.Combine(root, "dst-file-zip")) { Zip = CompressionLevel.Fastest, Exclude = Engine.ParsePatterns("*.txt") });
using (var both = ZipFile.OpenRead(f3.Snapshot))
    Check(both.GetEntry(Engine.RelPath(lone).Replace('\\', '/')) != null && both.Entries.Count(e => e.Name.EndsWith(".txt")) == 1,
        "in a zip beside a folder, and kept although the folder's exclusions name it");
Engine.Restore(f1.Snapshot, Path.Combine(root, "restored-file"), null, log.Add, null, default);
Check(File.ReadAllText(Path.Combine(root, "restored-file", Engine.RelPath(lone))) == "just this", "and restored");

foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
Directory.Delete(root, true);
Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
return failures;
