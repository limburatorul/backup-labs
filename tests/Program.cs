using System.IO.Compression;
using Backup;

// Before anything else: the app's code under test resolves %APPDATA% through BACKUPLABS_TEST_ROOT (Ecosystem.TestRoot),
// and reading Store's settings folder moves %APPDATA%\BackupLabs into the shared folder. Never the user's own.
Environment.SetEnvironmentVariable("BACKUPLABS_TEST_ROOT", Path.Combine(Path.GetTempPath(), "backup-test-appdata-" + DateTime.Now.Ticks));
if (!Ecosystem.AppData.StartsWith(Path.GetTempPath())) { Console.WriteLine("FAIL the tests would reach the real %APPDATA%"); return 1; }

// Disk cloning on virtual disks. Needs admin: Tests.exe --clone <result file>
if (args.Length >= 2 && args[0] == "--clone") return CloneTests.Run(args[1], args.Length > 2 ? args[2] : null);

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
            Thread.Sleep(1100);
            // a second shadow copy in the same process: it used to be made and then fail, staying on the disk
            var second = Engine.Run(new RunOptions(new[] { vsrc }, Path.Combine(vroot, "shadow")) { OpenFiles = true }, _ => { }, null, default);
            outcome = plain.Failed == 1 && shadow.Failed == 0 && second.Failed == 0 && File.ReadAllText(copied) == "mail database"
                ? "ok   a file held open by another program is copied through a shadow copy, twice in a row"
                : $"FAIL plain failed={plain.Failed}, shadow failed={shadow.Failed}, second failed={second.Failed}";
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
    Check(z.Snapshot.EndsWith(".bkl") && new StreamReader(a.GetEntry(prefix + "sub/b.txt")!.Open()).ReadToEnd() == "two", "archive holds the files");
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
// ---- 1.3.0: every archive is .bkl, plain or encrypted told apart by the first bytes; the old names still work ----
{
    var nroot = Path.Combine(root, "naming");
    var nsrc = Path.Combine(nroot, "src");
    Directory.CreateDirectory(nsrc);
    var blob = new byte[300_000];
    new Random(7).NextBytes(blob);
    File.WriteAllBytes(Path.Combine(nsrc, "data.bin"), blob);
    var nrel = Engine.RelPath(nsrc);
    var ndst = Path.Combine(nroot, "dst");
    bool Identical(string into) => File.Exists(Path.Combine(into, nrel, "data.bin")) && File.ReadAllBytes(Path.Combine(into, nrel, "data.bin")).SequenceEqual(blob);
    string Into(string name) => Path.Combine(nroot, name);
    bool Refused(Action a, Func<Exception, bool> right) { try { a(); return false; } catch (Exception e) { return right(e); } }

    var plain = Backup(new RunOptions(new[] { nsrc }, ndst) { Zip = CompressionLevel.Optimal });
    Check(plain.Snapshot.EndsWith(".bkl") && !Engine.Encrypted(plain.Snapshot), "a plain archive is written as <stamp>.bkl");
    using (var a = ZipFile.OpenRead(plain.Snapshot)) Check(a.GetEntry(nrel.Replace('\\', '/') + "/data.bin") != null, "and is an ordinary zip inside, readable without Backup Labs");
    Engine.Restore(plain.Snapshot, Into("r-plain"), null, log.Add, null, default);
    Check(Identical(Into("r-plain")), "a plain .bkl restores byte for byte");
    var sealedArc = Backup(new RunOptions(new[] { nsrc }, ndst) { Zip = CompressionLevel.Fastest, Password = "pw 1.3.0" });
    Engine.Restore(sealedArc.Snapshot, Into("r-enc"), "pw 1.3.0", log.Add, null, default);
    Check(sealedArc.Snapshot.EndsWith(".bkl") && Engine.Encrypted(sealedArc.Snapshot) && Identical(Into("r-enc")), "an encrypted .bkl still restores byte for byte");

    // what 1.2 wrote: <stamp>.zip for plain archives, <stamp>.bkl for encrypted ones (same bytes as today's)
    var odst = Path.Combine(nroot, "old");
    Directory.CreateDirectory(odst);
    var oldZip = Path.Combine(odst, "2026-01-01_120000.zip");
    var oldBkl = Path.Combine(odst, "2026-01-02_120000.bkl");
    File.Copy(plain.Snapshot, oldZip);
    File.Copy(sealedArc.Snapshot, oldBkl);
    Check(Engine.Snapshots(odst).SequenceEqual(new[] { oldZip, oldBkl }), "an old .zip and an old encrypted .bkl are both listed");
    Engine.Restore(oldZip, Into("r-oldzip"), null, log.Add, null, default);
    Engine.Restore(oldBkl, Into("r-oldbkl"), "pw 1.3.0", log.Add, null, default);
    Check(Identical(Into("r-oldzip")) && Identical(Into("r-oldbkl")), "and both restore");
    Check(Engine.Find(Engine.Snapshots(odst), "data", "pw 1.3.0", default).Count == 1, "find reads both (one version, unchanged across them)");
    var newer = Backup(new RunOptions(new[] { nsrc }, odst) { Zip = CompressionLevel.Fastest, Keep = 1 });
    Check(Engine.Snapshots(odst).SequenceEqual(new[] { newer.Snapshot }) && !File.Exists(oldZip) && !File.Exists(oldBkl), "and retention removes them like any other backup");

    // the header decides, never the name
    var odd = Path.Combine(nroot, "odd");
    Directory.CreateDirectory(odd);
    var zipAsBkl = Path.Combine(odd, "made-by-another-tool.bkl");
    ZipFile.CreateFromDirectory(nsrc, zipAsBkl); // any zip, named .bkl
    Engine.Restore(zipAsBkl, Into("r-zipasbkl"), null, log.Add, null, default);
    Check(!Engine.Encrypted(zipAsBkl) && File.ReadAllBytes(Path.Combine(Into("r-zipasbkl"), "data.bin")).SequenceEqual(blob), "a plain zip named .bkl is read as a zip");
    var bklAsZip = Path.Combine(odd, "renamed.zip");
    File.Copy(sealedArc.Snapshot, bklAsZip);
    Check(Engine.Encrypted(bklAsZip) && Refused(() => Engine.Restore(bklAsZip, Into("r-x"), null, log.Add, null, default), e => e is InvalidOperationException && e.Message.Contains("encrypted")),
          "an encrypted archive named .zip asks for its password instead of failing as a broken zip");
    Engine.Restore(bklAsZip, Into("r-bklaszip"), "pw 1.3.0", log.Add, null, default);
    Check(Identical(Into("r-bklaszip")), "and restores with it");
    var junk = Path.Combine(odd, "junk.bkl");
    var noise = new byte[50_000];
    new Random(3).NextBytes(noise);
    File.WriteAllBytes(junk, noise);
    var tiny = Path.Combine(odd, "tiny.bkl");
    File.WriteAllBytes(tiny, new byte[] { 0x42, 0x4B });
    bool NotABackup(Exception e) => e is InvalidDataException && e.Message.Contains("not a Backup Labs backup");
    Check(Refused(() => Engine.Restore(junk, Into("r-junk"), null, log.Add, null, default), NotABackup)
          && Refused(() => Engine.Find(new[] { junk }, "x", null, default), NotABackup)
          && Refused(() => Engine.Probe(junk, null), NotABackup) && Refused(() => Engine.Probe(tiny, null), NotABackup)
          && !Directory.Exists(Into("r-junk")), "random bytes (or two bytes) named .bkl are refused clearly, nothing written");
    Check(Engine.OpenProblem(junk) == null && Engine.OpenProblem(bklAsZip) == null && Engine.OpenProblem("relative.bkl") != null
          && Engine.OpenProblem(Path.Combine(odd, "missing.bkl")) != null && Engine.OpenProblem(Path.Combine(nsrc, "data.bin")) != null,
          "--open takes only an existing .bkl or .zip given by its full path");
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

// ---- jobs from sister apps (--job-from) and Shelf's save folders ----
string J(object o) => System.Text.Json.JsonSerializer.Serialize(o);
var missing = Path.Combine(root, "no-such-save");
var spec = Ecosystem.ReadSpec(J(new { id = "shelf-saves", name = "Game saves (Shelf)", sources = new[] { src, lone, missing, "relative\\x", src } }));
Check(spec is { Id: "shelf-saves", Name: "Game saves (Shelf)" } && spec.Sources.SequenceEqual(new[] { src, lone }), "a job request keeps only full paths that exist, each once");
Check(Ecosystem.ReadSpec(Ecosystem.Line(spec!)) is { Id: "shelf-saves" } again && again.Sources.Count == 2, "and survives the one-line handoff to a running instance");
Check(Ecosystem.ReadSpec(J(new { name = "x", sources = new[] { missing } })) == null, "a request naming nothing that exists is ignored");
Check(Ecosystem.ReadSpec(J(new { id = "..\\evil", sources = new[] { src } })) == null && Ecosystem.ReadSpec("not json") == null && Ecosystem.ReadSpec("[1]") == null,
    "an odd id or a file that is not a job is ignored");
var shelf = J(new { schema = 1, updated = "2026-10-15T09:00:00Z", games = new object[] {
    new { id = "a", name = "A", saves = new[] { src, missing } }, new { id = "b", name = "B", saves = new[] { lone, src } }, new { id = "c", name = "C" }, 5 } });
Check(Ecosystem.ShelfSaves(shelf) is { } saves && saves.SequenceEqual(new[] { src, lone }), "Shelf's save folders: every game's existing saves, once");
Check(Ecosystem.ShelfSaves(J(new { schema = 2, games = new[] { new { saves = new[] { src } } } })) == null && Ecosystem.ShelfSaves("{") == null
    && Ecosystem.ShelfSaves(J(new { schema = 1, games = "x" })) == null && Ecosystem.ShelfSaves(J(new { schema = 1, games = new[] { new { saves = new[] { missing } } } })) == null,
    "a Shelf file with another schema, garbage or no existing saves changes nothing");

// ---- the job Stash made follows Stash's backup folder (data\stash.items.json), only while Stash is installed ----
var eco = Path.Combine(root, "eco");
Directory.CreateDirectory(Path.Combine(eco, "apps"));
Directory.CreateDirectory(Path.Combine(eco, "data"));
var stashExe = Path.Combine(root, "Stash.exe");
File.WriteAllText(stashExe, "");
var stashDest = Path.Combine(root, "stash-moved");
Directory.CreateDirectory(stashDest);
void Card(object o) => File.WriteAllText(Path.Combine(eco, "apps", "stash.json"), J(o));
void Items(string text) => File.WriteAllText(Path.Combine(eco, "data", "stash.items.json"), text);
List<string> Src() => new() { src };
bool Kept(string id) { var s = Src(); return !Ecosystem.FollowStash(id, s, eco) && s.SequenceEqual(new[] { src }); }
Card(new { schema = 1, id = "stash", exe = stashExe });
Items("\uFEFF" + J(new { schema = 1, updated = "2026-10-20T09:00:00Z", folder = stashDest, items = new[] { new { name = "x.zip" } } })); // PowerShell writes a BOM
var sj = Src();
Check(Ecosystem.FollowStash(Ecosystem.StashJobId, sj, eco) && sj.SequenceEqual(new[] { stashDest }) && !Ecosystem.FollowStash(Ecosystem.StashJobId, sj, eco),
    "Stash's job: a moved backup folder replaces its sources (file with a BOM), and only once");
Check(Kept("other") && Kept(Ecosystem.ShelfJobId), "a job with another id is left alone");
Card(new { schema = 1, id = "stash", exe = Path.Combine(root, "gone.exe") });
Check(Kept(Ecosystem.StashJobId), "Stash's card naming an exe that is gone changes nothing");
Card(new { schema = 1, id = "stash", exe = @"\\localhost\c$\Windows\notepad.exe" });
Check(Kept(Ecosystem.StashJobId), "nor a card whose exe is on a network share");
Card(new { schema = 2, id = "stash", exe = stashExe });
Check(Kept(Ecosystem.StashJobId), "nor a schema 2 card");
File.Delete(Path.Combine(eco, "apps", "stash.json"));
Check(Kept(Ecosystem.StashJobId), "nor no card at all");
Card(new { schema = 1, id = "stash", exe = stashExe });
foreach (var (bad, why) in new[] {
    (J(new { schema = 2, folder = stashDest }), "schema 2"), (J(new { schema = "1", folder = stashDest }), "schema as text"), (J(new { schema = 1.5, folder = stashDest }), "schema 1.5"),
    (J(new { schema = 1, folder = 5 }), "folder as a number"), (J(new { schema = 1 }), "no folder"),
    (J(new { schema = 1, folder = @"\\server\share\Stash" }), "a UNC folder"), (J(new { schema = 1, folder = @"\\?\C:\Windows" }), "a device path"),
    (J(new { schema = 1, folder = "relative\\Stash" }), "a relative folder"), (J(new { schema = 1, folder = Path.Combine(root, "no-such-folder") }), "a missing folder"),
    (J(new { schema = 1, folder = lone }), "a file, not a folder"), ("{\"schema\":1,\"folder\":", "cut-off JSON"), ("[1,2]", "an array"), ("", "an empty file") })
{
    Items(bad);
    Check(Kept(Ecosystem.StashJobId), $"Stash's file with {why} changes nothing");
}
Items(J(new { schema = 1, folder = stashDest, pad = new string('x', 1 << 20) }));
Check(Kept(Ecosystem.StashJobId), "nor one over 1 MB");
File.Delete(Path.Combine(eco, "data", "stash.items.json"));
Check(Kept(Ecosystem.StashJobId), "nor no file at all");

// ---- 1.5.0: when a job runs by itself (Schedule.Next) ----
{
    DateTime T(string s) => DateTime.ParseExact(s, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    DateTime? N(int hours, string at, DayOfWeek day, string? last, string since) =>
        Schedule.Next(hours, (int)TimeSpan.Parse(at).TotalMinutes, day, last == null ? null : T(last), T(since));
    const DayOfWeek Mon = DayOfWeek.Monday;
    Check(N(0, "12:00", Mon, "2026-10-20 12:00:00", "2026-10-01 00:00:00") == null, "schedule: manual never comes due");
    Check(N(1, "12:00", Mon, "2026-10-21 10:00:00", "2026-10-01 00:00:00") == T("2026-10-21 11:00:00")
          && N(6, "12:00", Mon, "2026-10-21 10:00:00", "2026-10-01 00:00:00") == T("2026-10-21 16:00:00"), "hourly and every 6 h: that long after the last run, as before");
    // the 1.4.0 bug: a job that never ran was due at once, so it started as soon as it had folders and a destination
    Check(N(1, "12:00", Mon, null, "2026-10-21 09:30:00") == T("2026-10-21 10:30:00"), "a new hourly job waits an hour from its setup");
    Check(N(24, "12:00", Mon, null, "2026-10-21 13:00:00") == T("2026-10-22 12:00:00"), "a new daily job set up after its time waits for tomorrow");
    Check(N(24, "12:00", Mon, null, "2026-10-21 11:00:00") == T("2026-10-21 12:00:00"), "and set up before it runs today at that time");
    Check(N(168, "09:00", DayOfWeek.Wednesday, null, "2026-10-21 08:00:00") == T("2026-10-21 09:00:00")
          && N(168, "09:00", DayOfWeek.Wednesday, null, "2026-10-21 10:00:00") == T("2026-10-28 09:00:00")
          && N(168, "09:00", Mon, null, "2026-10-21 10:00:00") == T("2026-10-26 09:00:00"), "weekly: the next such day at that time, today if still ahead");
    Check(N(24, "12:00", Mon, "2026-10-20 12:00:20", "2026-10-01 00:00:00") == T("2026-10-21 12:00:00"), "daily: the day after the last run, at its time");
    Check(N(24, "12:00", Mon, "2026-10-21 11:00:00", "2026-10-01 00:00:00") == T("2026-10-21 12:00:00"), "a run by hand before the time doesn't move it");
    Check(N(24, "12:00", Mon, "2026-10-20 12:00:20", "2026-10-21 09:00:00") == T("2026-10-21 12:00:00")
          && N(24, "08:00", Mon, "2026-10-20 08:00:20", "2026-10-21 09:00:00") == T("2026-10-22 08:00:00"),
          "a time changed today: still today if ahead, else tomorrow, never at once");
    // the PC off or asleep through the time: due at the next check, once
    var missed = N(24, "12:00", Mon, "2026-10-17 12:00:30", "2026-10-01 00:00:00")!.Value;
    var woke = T("2026-10-21 08:00:00");
    Check(missed == T("2026-10-18 12:00:00") && woke >= missed && N(24, "12:00", Mon, "2026-10-21 08:00:05", "2026-10-01 00:00:00") == T("2026-10-21 12:00:00"),
          "PC asleep for days: one catch-up run on waking, then the usual time");
    var weekMissed = N(168, "20:00", DayOfWeek.Friday, "2026-10-02 20:00:10", "2026-10-01 00:00:00")!.Value;
    Check(weekMissed == T("2026-10-09 20:00:00") && N(168, "20:00", DayOfWeek.Friday, "2026-10-21 08:00:05", "2026-10-01 00:00:00") == T("2026-10-23 20:00:00"),
          "weekly missed twice: one catch-up, then the next Friday");
    Check(N(24, "23:30", Mon, "2026-10-31 23:30:05", "2026-10-01 00:00:00") == T("2026-11-01 23:30:00")
          && N(24, "00:15", Mon, "2026-02-28 23:59:00", "2026-01-01 00:00:00") == T("2026-03-01 00:15:00")
          && N(168, "09:00", Mon, "2026-02-27 09:00:00", "2026-01-01 00:00:00") == T("2026-03-02 09:00:00")
          && N(168, "09:00", DayOfWeek.Thursday, "2026-12-31 10:00:00", "2026-01-01 00:00:00") == T("2027-01-07 09:00:00"),
          "end of month, end of February, end of year");
    // DST in Romania (GTB Standard Time): 28 Mar 2027 03:00 -> 04:00; 25 Oct 2026 04:00 -> 03:00
    var ro = TimeZoneInfo.FindSystemTimeZoneById("GTB Standard Time");
    var spring = N(24, "03:30", Mon, "2027-03-27 03:30:10", "2027-01-01 00:00:00")!.Value;
    Check(spring == T("2027-03-28 03:30:00") && ro.IsInvalidTime(spring) && T("2027-03-28 04:00:00") >= spring,
          "DST spring: a time that doesn't exist that day runs when the clock jumps past it");
    var autumn = N(24, "03:30", Mon, "2026-10-25 03:30:10", "2026-01-01 00:00:00")!.Value;
    Check(ro.IsAmbiguousTime(T("2026-10-25 03:30:00")) && autumn == T("2026-10-26 03:30:00"),
          "DST autumn: a time that comes twice runs once (the second 03:30 is before the next time)");
    Check(N(24, "12:00", Mon, "2026-03-28 12:00:10", "2026-01-01 00:00:00") == T("2026-03-29 12:00:00"), "a DST day is still one run a day at its time");
}
// ---- 1.5.0: settings from 1.4.0 ----
{
    var upgraded = new DateTime(2026, 10, 21, 8, 0, 0);
    var old = """
    {
      "Jobs": [
        { "Id": "a", "Name": "Daily", "Sources": ["C:\\x"], "Destination": "E:\\B", "IntervalHours": 24, "LastRun": "2026-10-19T21:15:42.123+03:00", "LastResult": "ok" },
        { "Id": "b", "Name": "Weekly", "Sources": ["C:\\x"], "Destination": "E:\\B", "IntervalHours": 168, "LastRun": "2026-10-14T08:00:05" },
        { "Id": "c", "Name": "Hourly", "Sources": ["C:\\x"], "Destination": "E:\\B", "IntervalHours": 1, "LastRun": "2026-10-21T07:20:00" },
        { "Id": "d", "Name": "Never ran", "Sources": ["C:\\x"], "Destination": "E:\\B", "IntervalHours": 24, "LastRun": null }
      ],
      "LastSeenVersion": "1.4.0"
    }
    """;
    var s = Store.Parse(old, upgraded)!;
    var (a, wk, h, d) = (s.Jobs[0], s.Jobs[1], s.Jobs[2], s.Jobs[3]);
    DateTime? Next(Job j) => Schedule.Next(j.IntervalHours, j.At, j.Day, j.LastRun, j.Since);
    var lastA = a.LastRun!.Value; // read in local time, as 1.4.0 saved and compared it
    Check(s.TimedSchedules && a.At == lastA.Hour * 60 + lastA.Minute && a.Day == lastA.DayOfWeek
          && Next(a) == lastA.Date.AddDays(1).AddMinutes(a.At), "1.4.0 daily job: its last run's time, so it comes due when 1.4.0 would have run it");
    Check(wk.Day == DayOfWeek.Wednesday && wk.At == 8 * 60 && Next(wk) == new DateTime(2026, 10, 21, 8, 0, 0) && upgraded >= Next(wk),
          "1.4.0 weekly job a week old: due at once, as in 1.4.0 (catch-up kept)");
    Check(Next(h) == new DateTime(2026, 10, 21, 8, 20, 0), "1.4.0 hourly job: an hour after its last run, unchanged");
    Check(d.Since == upgraded && d.At == 12 * 60 && Next(d) == new DateTime(2026, 10, 21, 12, 0, 0) && !(upgraded >= Next(d)),
          "1.4.0 job that never ran: waits for its time (12:00), no run on upgrade");
    var reread = Store.Parse(System.Text.Json.JsonSerializer.Serialize(s), upgraded.AddDays(3))!;
    Check(reread.Jobs[0].At == a.At && reread.Jobs[3].Since == upgraded, "a 1.5.0 file is read as saved, not migrated again");
    var odd = Store.Parse("""{ "TimedSchedules": true, "Jobs": [ { "At": 5000, "Day": 9, "Since": "2099-01-01T00:00:00" } ] }""", upgraded)!.Jobs[0];
    Check(odd.At == 12 * 60 && odd.Day == DayOfWeek.Monday && odd.Since == upgraded, "a hand-edited time, day or future date falls back to sane values");
    Check(Store.Parse("null", upgraded) == null, "an empty settings file is no settings");
}
// ---- 1.6.0: every app's data in %APPDATA%\ProtagonistLabs; the apps job backs up that folder alone ----
{
    var pr = Path.Combine(root, "preset");
    var roaming = Path.Combine(pr, "profile", "AppData", "Roaming");
    var programData = Path.Combine(pr, "programdata");
    var plRoot = Path.Combine(roaming, "ProtagonistLabs");
    var (fresh, freshExcl) = (new List<string> { plRoot }, Ecosystem.AppsExclude(plRoot));
    Check(fresh.SequenceEqual(new[] { plRoot }) && freshExcl.Count == 13, "the button's job: the shared folder alone, 13 cache patterns left out");

    // the job as 1.5.0 made it on the user's PC (23.10.2026), plus a folder and an exclusion the user added by hand
    var gb = Path.Combine(roaming, "game-browser");
    var mine = @"D:\My notes";
    var sources = new List<string> { plRoot }
        .Concat(new[] { "game-browser", "reel", "SteamRadar", "Lume", "Frostpane", "FileLabs", "ThunderbirdBackup", "SpaceScan", "AutoClicker", "Glaze" }.Select(f => Path.Combine(roaming, f)))
        .Append(Path.Combine(programData, "Sysoptimizer")).Append(Path.Combine(roaming, "BackupLabs")).Append(mine).ToList();
    var excl = new List<string> { Path.Combine(gb, "covers"), Path.Combine(gb, "Cache"), Path.Combine(roaming, "Lume", "icons"), "*.tmp" };
    Check(Ecosystem.MoveAppsJob(sources, excl, plRoot, roaming, programData) && sources.SequenceEqual(new[] { plRoot, mine }),
          "the apps job: old per-app folders out, the shared folder first, a folder the user added stays");
    Check(excl[0] == "*.tmp" && excl.Skip(1).SequenceEqual(Ecosystem.AppsExclude(plRoot)), "its old caches' exclusions give way to the new ones; the user's own stays");
    Check(!Ecosystem.MoveAppsJob(sources, excl, plRoot, roaming, programData) && sources.Count == 2 && excl.Count == 14, "a second start changes nothing");
    var edited = new List<string> { mine };
    Check(!Ecosystem.MoveAppsJob(edited, new List<string>(), plRoot, roaming, programData) && edited.SequenceEqual(new[] { mine }),
          "a job the user later trimmed to their own folder is left alone");
    var inside = new List<string> { Path.Combine(roaming, "reel"), Path.Combine(plRoot, "Shelf") + "\\", Path.Combine(roaming, "game-browser-old") };
    Check(Ecosystem.MoveAppsJob(inside, new List<string>(), plRoot, roaming, programData) && inside.SequenceEqual(new[] { plRoot, Path.Combine(roaming, "game-browser-old") }),
          "a folder inside the shared one would be backed up twice: out; a sibling whose name starts like an old folder stays");

    // the patterns do what they say, on a synthetic shared folder after every app moved in
    void Fill(string dir, int kb) { Directory.CreateDirectory(dir); File.WriteAllBytes(Path.Combine(dir, "f.bin"), new byte[kb * 1024]); }
    var shelfDir = Path.Combine(plRoot, "Shelf");
    Fill(shelfDir, 64); Fill(Path.Combine(shelfDir, "save-backups"), 512);
    Fill(Path.Combine(shelfDir, "covers"), 3000); Fill(Path.Combine(shelfDir, "trailers"), 9000); Fill(Path.Combine(shelfDir, "Cache"), 1500); Fill(Path.Combine(shelfDir, "GPUCache"), 100);
    Fill(Path.Combine(plRoot, "Reel", "Code Cache"), 700); Fill(Path.Combine(plRoot, "Reel", "posters"), 50);
    Fill(Path.Combine(plRoot, "Lume", "icons"), 300); Fill(Path.Combine(plRoot, "Backup Labs"), 10); Fill(Path.Combine(plRoot, "apps"), 1);
    var with = Engine.Run(new RunOptions(fresh, Path.Combine(pr, "dst-with")) { Exclude = freshExcl }, log.Add, null, default);
    var without = Engine.Run(new RunOptions(fresh, Path.Combine(pr, "dst-without")), log.Add, null, default);
    Console.WriteLine($"     synthetic shared folder: {Engine.Size(without.BytesCopied)} in full, {Engine.Size(with.BytesCopied)} with the caches left out ({with.Copied} files)");
    string In(string p) => Path.Combine(with.Snapshot, Engine.RelPath(p), "f.bin");
    Check(with.Failed == 0 && File.Exists(In(Path.Combine(shelfDir, "save-backups"))) && File.Exists(In(Path.Combine(plRoot, "Reel", "posters"))) && File.Exists(In(Path.Combine(plRoot, "Backup Labs")))
          && !File.Exists(In(Path.Combine(shelfDir, "trailers"))) && !File.Exists(In(Path.Combine(plRoot, "Reel", "Code Cache"))) && !File.Exists(In(Path.Combine(plRoot, "Lume", "icons")))
          && with.BytesCopied < without.BytesCopied / 10,
          "the job backs up every app's settings and saves, not the caches (under a tenth of the bytes here)");

    // Backup Labs' own folder, moved once by a rename
    var oldData = Path.Combine(pr, "old", "BackupLabs");
    var newData = Path.Combine(pr, "new", "ProtagonistLabs", "Backup Labs");
    Fill(oldData, 1);
    Check(Ecosystem.DataFolder(oldData, newData) == newData && !Directory.Exists(oldData) && File.Exists(Path.Combine(newData, "f.bin")), "data folder: moved to the shared folder");
    Check(Ecosystem.DataFolder(oldData, newData) == newData && File.Exists(Path.Combine(newData, "f.bin")), "and a second start finds nothing to move");
    Fill(oldData, 2);
    Check(Ecosystem.DataFolder(oldData, newData) == newData && File.Exists(Path.Combine(oldData, "f.bin")) && new FileInfo(Path.Combine(newData, "f.bin")).Length == 1024
          && File.ReadAllText(Path.Combine(newData, "backup.log")).Contains("was left where it is"), "both holding data: nothing mixed, the old one stays, the log says so");
    Directory.Delete(newData, true);
    Directory.CreateDirectory(newData);
    Check(Ecosystem.DataFolder(oldData, newData) == newData && !Directory.Exists(oldData) && new FileInfo(Path.Combine(newData, "f.bin")).Length == 2048, "an empty new folder is taken over");
    Fill(oldData, 3);
    Directory.Delete(newData, true);
    using (new FileStream(Path.Combine(oldData, "f.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        Check(Ecosystem.DataFolder(oldData, newData) == oldData && File.Exists(Path.Combine(oldData, "f.bin")), "a file held open: this session uses the old folder, nothing half moved");
    Check(Ecosystem.DataFolder(oldData, newData) == newData && !Directory.Exists(oldData), "and the next start moves it");

    Check(Ecosystem.Uninstalled("{00000000-0000-0000-0000-000000000000}_is1") == null, "an app with no uninstall entry anywhere is not installed");
}

// ---- What's new: the notes built into the exe ----
{
    var heads = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "whatsnew.txt")).Where(l => l.StartsWith("## ")).Select(l => l[3..].Split(" | ")).ToList();
    Check(heads.Count > 10 && heads.All(h => h.Length == 3 && Version.TryParse(h[0], out _) && DateTime.TryParseExact(h[1], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _) && h[2].Trim() != ""),
          "What's new: every release reads as version | date | title");
    var csproj = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Backup.csproj"));
    Check(csproj.Contains($"<Version>{heads[0][0]}</Version>"), "What's new: the newest release is the version being built");
}

// ---- a failed update check is a failure, never "nothing newer" ----
bool CheckFails(string feed)
{
    Updater.Feed = feed;
    try { Updater.Check(manual: true).GetAwaiter().GetResult(); return false; }
    catch (Exception e) when (Updater.Failed(e)) { return true; }
}
static string Serve(int status, string body)
{
    var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    probe.Start(); int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
    var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    _ = Task.Run(() =>
    {
        var c = listener.GetContext();
        c.Response.StatusCode = status;
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        c.Response.ContentLength64 = bytes.Length;
        c.Response.OutputStream.Write(bytes);
        c.Response.Close(); // the listener is left to the process: stopping it here can cut the answer off
    });
    return $"http://localhost:{port}/";
}
Check(CheckFails("http://127.0.0.1:9/"), "update check: offline is a failure");
Check(CheckFails(Serve(403, "{\"message\":\"API rate limit exceeded\"}")), "update check: 403 (rate limit) is a failure");
Check(CheckFails(Serve(502, "")), "update check: 5xx is a failure");
Check(CheckFails(Serve(200, "<html>")), "update check: an unreadable answer is a failure");
Updater.Feed = Serve(200, "{\"tag_name\":\"v0.0.1\",\"body\":\"\",\"assets\":[{\"name\":\"BackupLabs-0.0.1-setup.exe\",\"browser_download_url\":\"x\",\"size\":1}]}");
Check(Updater.Check(manual: true).GetAwaiter().GetResult() == null, "update check: nothing newer is not a failure");

// ---- what SpaceScan reads: data\backuplabs.jobs.json ----
{
    var json = Ecosystem.JobsJson(new[] { new SharedJob("a1", "Photos", new() { @"D:\Photos" }, @"E:\Backups", "folders", "2026-10-17T08:00:00Z", "ok") }, new DateTime(2026, 10, 17, 9, 0, 0));
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var r = doc.RootElement;
    var j = r.GetProperty("jobs")[0];
    Check(r.GetProperty("schema").GetInt32() == 1 && r.GetProperty("updated").GetString() == "2026-10-17T09:00:00Z"
          && j.GetProperty("id").GetString() == "a1" && j.GetProperty("sources")[0].GetString() == @"D:\Photos"
          && j.GetProperty("destination").GetString() == @"E:\Backups" && j.GetProperty("lastRun").GetString() == "2026-10-17T08:00:00Z"
          && j.GetProperty("lastResult").GetString() == "ok", "the shared jobs file has the contract's shape (schema 1, camelCase, UTC times)");
    Check(Ecosystem.IsJobId("a1b2-c_3") && !Ecosystem.IsJobId("..\\x") && !Ecosystem.IsJobId("a\" --x"), "--show-job takes plain ids only");
}

// ---- disk cloning: the parts that need no administrator ----
Check(Clone.Crc32("123456789"u8) == 0xCBF43926, "CRC-32 matches the standard check value");
{
    var disk = CloneTests.MakeGpt(4096, (new Guid("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), 2048, 2559), (new Guid("de94bba4-06d1-4d40-a16a-bfd50179d6ac"), 2560, 3071));
    var gpt = Clone.Gpt.Parse(disk[..(1 << 20 > disk.Length ? disk.Length : 1 << 20)], 512)!;
    Check(gpt != null && gpt.Parts().Count() == 2 && gpt.LastUsed == 3071, "a GPT table is read: two partitions, the last ending at sector 3071");
    Check(Clone.Needed(disk, 512, disk.Length) == (3071 + 1 + 32 + 1) * 512L, "the space a GPT disk needs ends with its backup table");
    var bigger = new byte[8192 * 512];
    disk.AsSpan(0, 34 * 512).CopyTo(bigger);
    gpt!.Move(1, 8192 - 33 - 512 - 1 - 7); // the recovery partition, to near the new end
    foreach (var (lba, data) in gpt.Relocated(8192, disk[..512])) data.CopyTo(bigger, lba * 512);
    Check(CloneTests.ValidGpt(bigger, 512, 8192, out var why), "fitted to a bigger disk, both GPT headers and tables check out" + (why == "" ? "" : $" ({why})"));
    var moved = Clone.Gpt.Parse(bigger[..(34 * 512)], 512)!.Parts().ToList();
    Check(moved[1].First == 8192 - 33 - 512 - 1 - 7 && moved[1].Last - moved[1].First == 511, "the moved partition keeps its length at its new place");
    var mbr = new byte[512];
    mbr[510] = 0x55; mbr[511] = 0xAA; mbr[446 + 4] = 0x0C;
    BitConverter.GetBytes(2048u).CopyTo(mbr, 446 + 8); BitConverter.GetBytes(10000u).CopyTo(mbr, 446 + 12);
    Check(Clone.Needed(mbr, 512, 1L << 30) == (2048 + 10000) * 512L, "an MBR card needs up to the end of its last partition");
}
var cloneDisks = Clone.Disks();
var system = cloneDisks.FirstOrDefault(d => d.System);
Check(system != null && system.Parts.Count > 0, "the disk list finds the disk Windows runs from, with its partitions");
if (system != null)
{
    var other = cloneDisks.FirstOrDefault(d => !d.System && !d.Boot);
    Check(Clone.Problem(new CloneRequest("ImageToDisk", -1, Path.Combine(root, "x.img"), system.Number, null, false, true, false, "", system.Id), cloneDisks) != null,
          "nothing is ever written to the disk Windows runs from");
    File.WriteAllBytes(Path.Combine(root, "x.img"), new byte[1 << 20]);
    Check(Clone.Problem(new CloneRequest("ImageToDisk", -1, Path.Combine(root, "x.img"), system.Number, null, false, true, false, "", system.Id), cloneDisks)!.Contains("Windows runs from"),
          "an image can't be written onto the system disk");
    Check(Clone.Problem(new CloneRequest("DiskToImage", system.Number, null, -1, Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "x.img"), false, true, false, system.Id, ""), cloneDisks) != null,
          "an image isn't saved on the disk it copies");
    if (other != null)
        Check(Clone.Problem(new CloneRequest("DiskToDisk", other.Number, null, other.Number, null, false, true, false, other.Id, other.Id), cloneDisks) != null,
              "a disk isn't cloned onto itself");
}

foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
Directory.Delete(root, true);
Console.WriteLine(failures == 0 ? "all passed" : $"{failures} failed");
return failures;

// Updater.cs reports a wrong download size through the window's formatter; the tests have no window.
namespace Backup { static class MainWindow { public static string Size(long bytes) => $"{bytes} B"; } }
