using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Backup;

static class CloneTests
{
    // An independent CRC-32 (bit by bit), so the GPT checks don't grade the engine with its own table.
    static uint Crc(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) { c ^= b; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1)); }
        return ~c;
    }

    /// A blank disk of `sectors` 512-byte sectors with a protective MBR and a valid GPT holding `parts`.
    public static byte[] MakeGpt(long sectors, params (Guid Type, long First, long Last)[] parts)
    {
        var disk = new byte[sectors * 512];
        disk[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 12), (uint)(sectors - 1));
        disk[510] = 0x55; disk[511] = 0xAA;
        var entries = new byte[128 * 128];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i].Type.TryWriteBytes(entries.AsSpan(i * 128));
            Guid.NewGuid().TryWriteBytes(entries.AsSpan(i * 128 + 16));
            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(i * 128 + 32), parts[i].First);
            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(i * 128 + 40), parts[i].Last);
        }
        var last = sectors - 1;
        byte[] Header(long my, long alt, long at)
        {
            var h = new byte[512];
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), my);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), alt);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), 34);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), last - 33);
            Guid.NewGuid().TryWriteBytes(h.AsSpan(56));
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), at);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(80), 128);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(84), 128);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), Crc(entries));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc(h.AsSpan(0, 92)));
            return h;
        }
        Header(1, last, 2).CopyTo(disk, 512);
        entries.CopyTo(disk, 2 * 512);
        entries.CopyTo(disk, (last - 32) * 512);
        Header(last, 1, last - 32).CopyTo(disk, last * 512);
        return disk;
    }

    /// Whether a disk image carries a consistent GPT for its size: both headers, both tables, all CRCs.
    public static bool ValidGpt(byte[] disk, int sector, long sectors, out string why)
    {
        var last = sectors - 1;
        bool Header(long lba, long my, long alt, out long entriesAt, out string w)
        {
            var h = disk.AsSpan((int)(lba * sector), sector);
            entriesAt = BinaryPrimitives.ReadInt64LittleEndian(h[72..]);
            w = "";
            if (!h[..8].SequenceEqual("EFI PART"u8)) { w = $"no header at {lba}"; return false; }
            var copy = h[..92].ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(16), 0);
            if (Crc(copy) != BinaryPrimitives.ReadUInt32LittleEndian(h[16..])) { w = $"header CRC at {lba}"; return false; }
            if (BinaryPrimitives.ReadInt64LittleEndian(h[24..]) != my || BinaryPrimitives.ReadInt64LittleEndian(h[32..]) != alt) { w = $"my/alternate at {lba}"; return false; }
            if (BinaryPrimitives.ReadInt64LittleEndian(h[48..]) != last - 33) { w = $"last usable at {lba}"; return false; }
            var entries = disk.AsSpan((int)(entriesAt * sector), 128 * 128);
            if (Crc(entries) != BinaryPrimitives.ReadUInt32LittleEndian(h[88..])) { w = $"entries CRC for {lba}"; return false; }
            return true;
        }
        if (!Header(1, 1, last, out var primary, out why)) return false;
        if (!Header(last, last, 1, out var backup, out why)) return false;
        if (backup != last - 32) { why = "backup entries not just before the backup header"; return false; }
        if (!disk.AsSpan((int)(primary * sector), 128 * 128).SequenceEqual(disk.AsSpan((int)(backup * sector), 128 * 128))) { why = "the two tables differ"; return false; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(disk.AsSpan(446 + 12)) != (uint)last) { why = "protective MBR size"; return false; }
        return true;
    }

    // ---- with administrator rights, on virtual disks made for the test: Tests.exe --clone <result file> ----

    static readonly List<string> lines = new();
    static int fails;
    static void Check(bool ok, string what) { lines.Add((ok ? "ok   " : "FAIL ") + what); if (!ok) fails++; }

    static string work = "";
    static void Diskpart(string script)
    {
        var file = Path.Combine(work, "diskpart.txt");
        File.WriteAllText(file, script);
        var p = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{file}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("diskpart failed:\n" + script + "\n" + output);
    }

    static string Vhd(string name) => Path.Combine(work, name + ".vhdx");
    static void Create(string name, int mb) => Diskpart($"create vdisk file=\"{Vhd(name)}\" maximum={mb} type=expandable\nselect vdisk file=\"{Vhd(name)}\"\nattach vdisk\n");
    static void Detach(string name) { try { Diskpart($"select vdisk file=\"{Vhd(name)}\"\ndetach vdisk\n"); } catch (Exception) { } }

    static DiskInfo Disk(string name)
    {
        for (int i = 0; i < 40; i++)
        {
            var d = Clone.Disks().FirstOrDefault(x => x.Location.Equals(Vhd(name), StringComparison.OrdinalIgnoreCase));
            if (d != null) return d;
            Thread.Sleep(250);
        }
        throw new Exception($"{name}: not in the disk list. Disks: " + string.Join("; ", Clone.Disks().Select(x => $"{x.Number} {x.Model} {x.Location}")));
    }

    /// The drive letter of the copy's data volume, once Windows has mounted it.
    static string Mounted(string name)
    {
        Diskpart($"select vdisk file=\"{Vhd(name)}\"\nonline disk noerr\n");
        for (int i = 0; i < 40; i++)
        {
            var part = Disk(name).Parts.FirstOrDefault(p => p.Letter != '\0');
            if (part != null && Directory.Exists(part.Letter + @":\files")) return part.Letter + @":\";
            Thread.Sleep(500);
        }
        var d = Disk(name);
        throw new Exception($"{name}: no mounted volume with the test files. Parts: " + string.Join("; ", d.Parts.Select(p => $"{p.Offset}+{p.Size} {p.Type} [{p.Letter}]")) + " | dirs: " + string.Join(", ", d.Parts.Where(p => p.Letter != '\0').Select(p => $"{p.Letter}: " + string.Join(",", Directory.Exists(p.Letter + @":\") ? Directory.GetFileSystemEntries(p.Letter + @":\").Select(Path.GetFileName) : new[] { "(not ready)" }))));
    }

    static Dictionary<string, string> Hashes(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "files")).ToDictionary(Path.GetFileName!, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    public static int Run(string resultFile, string? appExe)
    {
        work = Path.Combine(Path.GetTempPath(), "bl-clone-" + DateTime.Now.Ticks);
        Directory.CreateDirectory(work);
        var log = new List<string>();
        CloneRequest Req(string mode, DiskInfo? s, string? si, DiskInfo? t, string? ti, bool used, bool grow) =>
            new(mode, s?.Number ?? -1, si, t?.Number ?? -1, ti, used, true, grow, s?.Id ?? "", t?.Id ?? "");
        try
        {
            // the original: a 96 MB GPT disk, a 48 MB NTFS data partition with files, a 16 MB recovery partition, free space after
            Create("src", 1024);
            Diskpart($"select vdisk file=\"{Vhd("src")}\"\nconvert gpt\ncreate partition primary size=700\nformat fs=ntfs quick label=BLSRC\nassign\n" +
                     "create partition primary size=100\nformat fs=ntfs quick label=BLREC\nset id=de94bba4-06d1-4d40-a16a-bfd50179d6ac\n");
            var src = Disk("src");
            var letter = src.Parts.First(p => p.Letter != '\0').Letter + @":\";
            Directory.CreateDirectory(letter + "files");
            var rng = new Random(5);
            for (int i = 0; i < 24; i++) { var b = new byte[rng.Next(1, 1 << 20)]; rng.NextBytes(b); File.WriteAllBytes($@"{letter}files\f{i}.bin", b); }
            var original = Hashes(letter);
            lines.Add($"     original: Disk {src.Number}, {src.Parts.Count} partitions, {original.Count} files");

            var img1 = Path.Combine(work, "full.img");
            var full = Clone.Run(Req("DiskToImage", src, null, null, img1, false, false), log.Add, null, default);
            Check(new FileInfo(img1).Length == src.Size && full.Verified, "disk → image, every sector: the image is the disk's size and verified");
            var img2 = Path.Combine(work, "used.img");
            var used = Clone.Run(Req("DiskToImage", src, null, null, img2, true, false), log.Add, null, default);
            Check(used.Read < full.Read && used.Verified, $"disk → image, used space only: read {Engine.Size(used.Read)} instead of {Engine.Size(full.Read)}");
            Check(log.Count(l => l.Contains("read through a shadow copy")) >= 2, "the data volume was read through a shadow copy each time");

            if (appExe != null)
            {   // the app's own way: a request file, BackupLabs.exe --elevated-clone, progress and result files
                var id = "clone-test" + DateTime.Now.Ticks;
                var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BackupLabs");
                Directory.CreateDirectory(dataDir);
                string F(string kind) => Path.Combine(dataDir, $"elevated-{id}.{kind}");
                var img3 = Path.Combine(work, "app.img");
                File.WriteAllText(F("request"), System.Text.Json.JsonSerializer.Serialize(Req("DiskToImage", src, null, null, img3, true, false)));
                var app = Process.Start(new ProcessStartInfo(appExe, $"--elevated-clone {id}") { UseShellExecute = false })!;
                double seen = 0;
                while (!app.HasExited)
                {
                    Thread.Sleep(100);
                    try { seen = Math.Max(seen, double.Parse(File.ReadAllText(F("progress")).Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture)); }
                    catch (Exception) { } // not written yet, or mid-write
                }
                using var outcome = System.Text.Json.JsonDocument.Parse(File.ReadAllText(F("result")));
                var cloned = outcome.RootElement.GetProperty("Cloned");
                Check(cloned.ValueKind == System.Text.Json.JsonValueKind.Object && cloned.GetProperty("Verified").GetBoolean() && File.Exists(img3) && seen > 0,
                      $"the app's elevated clone: request in, progress (up to {seen:P0}) and a verified result out");
                foreach (var kind in new[] { "request", "progress", "result", "cancel" }) File.Delete(F(kind));
            }

            Create("same", 1024);
            var same = Disk("same");
            var d2d = Clone.Run(Req("DiskToDisk", src, null, same, null, true, false), log.Add, null, default);
            Check(d2d.Verified && log.Count(l => l.Contains("read through a shadow copy")) >= 3, "disk → disk, used space only, through a shadow copy, verified");
            Detach("src"); // the copies are identical to it: Windows keeps them offline while it is attached
            Check(Same(original, Hashes(Mounted("same"))), "the disk-to-disk copy holds the same files");
            Detach("same");

            Create("big", 1400);
            var big = Disk("big");
            var grown = Clone.Run(Req("ImageToDisk", null, img2, big, null, false, true), log.Add, null, default);
            Check(Same(original, Hashes(Mounted("big"))), "image → a bigger disk holds the same files");
            var parts = Disk("big").Parts.OrderBy(p => p.Offset).ToList();
            var data = parts.First(p => p.Letter != '\0');
            var rec = parts.Last();
            Check(rec.Type == "de94bba4-06d1-4d40-a16a-bfd50179d6ac" && rec.Offset + rec.Size > 1390L << 20, "the recovery partition moved to the end of the bigger disk");
            Check(data.Size > 1200L << 20 && new DriveInfo(data.Letter.ToString()).TotalSize > 1200L << 20,
                  $"the data partition and its file system took the new space ({Engine.Size(data.Size)}; {string.Join(" / ", grown.Notes)})");
            Detach("big");

            Create("small", 900);
            var small = Disk("small");
            Clone.Run(Req("ImageToDisk", null, img1, small, null, false, false), log.Add, null, default);
            Check(Same(original, Hashes(Mounted("small"))), "image → a smaller disk the partitions fit on holds the same files");
            Detach("small");

            Create("tiny", 600);
            var tiny = Disk("tiny");
            Check(Clone.Problem(Req("ImageToDisk", null, img1, tiny, null, false, false), Clone.Disks()) is string p && p.Contains("too small"), "a disk too small for the partitions is refused");
        }
        catch (Exception e) { Check(false, "clone test stopped: " + e); }
        finally
        {
            foreach (var n in new[] { "src", "same", "big", "small", "tiny" }) Detach(n);
            lines.Add("     log: " + string.Join(" | ", log));
            File.WriteAllLines(resultFile, lines);
            try { Directory.Delete(work, true); } catch (Exception) { }
        }
        return fails;
    }
}
