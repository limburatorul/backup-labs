using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Backup;

public sealed record PartInfo(long Offset, long Size, string Type, char Letter);

public sealed record DiskInfo(int Number, string Model, long Size, int SectorSize, string Bus, bool System, bool Boot,
    string Style, string Id, string Location, List<PartInfo> Parts)
{
    public string Letters => string.Join(" ", Parts.Where(p => p.Letter != '\0').Select(p => p.Letter + ":"));
    /// Where the partitions end: a smaller disk is enough when everything up to here fits.
    public long Needed => Style == "GPT" && Parts.Count > 0 ? Parts.Max(p => p.Offset + p.Size) + 34L * SectorSize
                        : Style == "MBR" && Parts.Count > 0 ? Parts.Max(p => p.Offset + p.Size) : Size;
}

/// Mode: DiskToImage, ImageToDisk or DiskToDisk. The ids are the disks as the user saw them when choosing;
/// they are checked again right before anything is written, so a card swapped meanwhile is never erased.
public sealed record CloneRequest(string Mode, int SourceDisk, string? SourceImage, int TargetDisk, string? TargetImage,
    bool UsedOnly, bool Verify, bool Grow, string SourceId, string TargetId);

public sealed record CloneResult(string Target, long Read, long Written, bool Verified, List<string> Notes);

// Copies a whole disk, sector for sector, to an image file or another disk, or an image onto a disk.
//   • Volumes in use are read through a shadow copy, so even the disk Windows runs from comes out consistent.
//   • "Used space only" reads just the clusters NTFS has allocated (its volume bitmap); everything else on
//     the disk (partition tables, EFI and other partitions, gaps) is copied whole.
//   • A disk is written from the end towards the start, its first megabyte (the partition table) last, so
//     a write cut short never leaves a table that points at half-written partitions.
//   • A GPT disk of another size gets its backup table moved to its new end; with Grow, a recovery
//     partition at the end moves along, and the data partition before it takes the new space.
public static class Clone
{
    const int Chunk = 4 << 20;
    const int Head = 1 << 20;
    static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), Recovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    // ---- the disks, as Windows' storage management sees them (works without admin rights) ----

    static dynamic Storage()
    {
        dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!)!;
        return locator.ConnectServer(".", @"root\Microsoft\Windows\Storage");
    }

    static IEnumerable<dynamic> Each(object set)
    {
        dynamic s = set;
        int n = s.Count;
        for (int i = 0; i < n; i++) yield return s.ItemIndex(i);
    }

    static object? P(dynamic o, string name) => o.Properties_.Item(name).Value;
    static long L(object? v) => v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);

    static string Bus(int t) => t switch
    {
        1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        15 => "Virtual", 17 => "NVMe", _ => "Disk",
    };

    public static List<DiskInfo> Disks()
    {
        var wmi = Storage();
        var parts = new List<(int Disk, PartInfo Part)>();
        foreach (var p in Each(wmi.ExecQuery("SELECT DiskNumber, Offset, Size, DriveLetter, GptType, MbrType FROM MSFT_Partition")))
        {
            var letter = (int)L(P(p, "DriveLetter"));
            var type = P(p, "GptType") is string g && g != "" ? g.Trim('{', '}').ToLowerInvariant()
                     : P(p, "MbrType") is object m ? "mbr:" + L(m).ToString("x2") : "";
            parts.Add(((int)L(P(p, "DiskNumber")), new PartInfo(L(P(p, "Offset")), L(P(p, "Size")), type, letter is >= 'A' and <= 'Z' ? (char)letter : '\0')));
        }
        var disks = new List<DiskInfo>();
        foreach (var d in Each(wmi.ExecQuery("SELECT Number, FriendlyName, Size, LogicalSectorSize, BusType, IsSystem, IsBoot, PartitionStyle, UniqueId, Location FROM MSFT_Disk")))
        {
            if (P(d, "Number") == null) continue;
            var n = (int)L(P(d, "Number"));
            var size = L(P(d, "Size"));
            disks.Add(new DiskInfo(n, ((P(d, "FriendlyName") as string) ?? "Disk").Trim(), size, Math.Max(512, (int)L(P(d, "LogicalSectorSize"))),
                Bus((int)L(P(d, "BusType"))), Equals(P(d, "IsSystem"), true), Equals(P(d, "IsBoot"), true),
                L(P(d, "PartitionStyle")) switch { 1 => "MBR", 2 => "GPT", _ => "RAW" }, $"{P(d, "UniqueId")}|{size}", (P(d, "Location") as string) ?? "",
                parts.Where(x => x.Disk == n).Select(x => x.Part).OrderBy(x => x.Offset).ToList()));
        }
        return disks.OrderBy(d => d.Number).ToList();
    }

    /// What stands in the way of this request, in words for the user; null when it can go ahead.
    public static string? Problem(CloneRequest r, IReadOnlyList<DiskInfo> disks)
    {
        DiskInfo? Find(int n) => disks.FirstOrDefault(d => d.Number == n);
        var src = r.Mode == "ImageToDisk" ? null : Find(r.SourceDisk);
        var dst = r.Mode == "DiskToImage" ? null : Find(r.TargetDisk);
        if (r.Mode != "ImageToDisk" && src == null) return "Choose the disk to copy.";
        if (r.Mode == "ImageToDisk" && (r.SourceImage is not string img || !Path.IsPathFullyQualified(img) || !File.Exists(img))) return "Choose the image file to write.";
        if (r.Mode == "DiskToImage")
        {
            if (string.IsNullOrWhiteSpace(r.TargetImage) || !Path.IsPathFullyQualified(r.TargetImage)) return "Choose where the image file goes.";
            if (DiskOf(r.TargetImage) == src!.Number) return "The image can't be saved on the disk it is a copy of.";
            return null;
        }
        if (dst == null) return "Choose the disk to write to.";
        if (dst.System || dst.Boot) return $"Disk {dst.Number} is the disk Windows runs from: it can only be copied, never written to.";
        if (src != null && src.Number == dst.Number) return "The copy and the original are the same disk.";
        if (r.Mode == "ImageToDisk" && DiskOf(r.SourceImage!) == dst.Number) return "The image file is on the disk it would overwrite.";
        if (src != null && src.SectorSize != dst.SectorSize) return $"The disks use different sector sizes ({src.SectorSize} and {dst.SectorSize} bytes), so a sector copy wouldn't be readable.";
        var head = src == null ? ReadHead(r.SourceImage!) : null;
        if (head != null && Gpt.Parse(head, dst.SectorSize == 512 ? 4096 : 512) != null && Gpt.Parse(head, dst.SectorSize) == null)
            return $"The image was made for disks with {(dst.SectorSize == 512 ? 4096 : 512)}-byte sectors; Disk {dst.Number} uses {dst.SectorSize}.";
        var needed = src?.Needed ?? Needed(head!, dst.SectorSize, new FileInfo(r.SourceImage!).Length);
        if (needed > dst.Size) return $"Disk {dst.Number} is too small: the partitions need {Engine.Size(needed)}, it has {Engine.Size(dst.Size)}.";
        return null;
    }

    static byte[] ReadHead(string image)
    {
        using var f = File.OpenRead(image);
        var head = new byte[Math.Min(Head, f.Length)];
        f.ReadExactly(head);
        return head;
    }

    /// Bytes from the start of a disk (or image) that hold everything its partition table describes.
    public static long Needed(byte[] head, int sector, long fullLength)
    {
        if (Gpt.Parse(head, sector) is { } g) return Math.Min(fullLength, (g.LastUsed + 1 + g.EntrySectors + 1) * sector);
        if (head.Length >= 512 && head[510] == 0x55 && head[511] == 0xAA)
        {
            long end = 0;
            for (int i = 0; i < 4; i++)
            {
                var e = head.AsSpan(446 + 16 * i, 16);
                if (e[4] == 0) continue;
                end = Math.Max(end, ((long)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) + BinaryPrimitives.ReadUInt32LittleEndian(e[12..])) * 512);
            }
            if (end > 0) return Math.Min(fullLength, end);
        }
        return fullLength;
    }

    // ---- raw access ----

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, SHARE_RW = 3, OPEN_EXISTING = 3;
    const uint NO_BUFFERING = 0x20000000, WRITE_THROUGH = 0x80000000;
    const uint IOCTL_DISK_GET_LENGTH_INFO = 0x7405C, IOCTL_DISK_UPDATE_PROPERTIES = 0x70140, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;
    const uint FSCTL_LOCK_VOLUME = 0x90018, FSCTL_DISMOUNT_VOLUME = 0x90020, FSCTL_GET_NTFS_VOLUME_DATA = 0x90064;
    const uint FSCTL_GET_VOLUME_BITMAP = 0x9006F, FSCTL_SET_SPARSE = 0x900C4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstVolume(StringBuilder name, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool FindNextVolume(IntPtr find, StringBuilder name, int size);
    [DllImport("kernel32.dll")] static extern bool FindVolumeClose(IntPtr find);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string root, StringBuilder? label, int labelSize, out uint serial, out uint maxName, out uint flags, StringBuilder fs, int fsSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int size);

    static SafeFileHandle Open(string path, bool write, bool raw = true)
    {
        var h = CreateFile(path, GENERIC_READ | (write ? GENERIC_WRITE : 0), SHARE_RW, IntPtr.Zero, OPEN_EXISTING,
                           raw ? NO_BUFFERING | (write ? WRITE_THROUGH : 0) : 0, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"Cannot open {path}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return h;
    }

    static bool Ioctl(SafeFileHandle h, uint code, byte[]? output = null) => DeviceIoControl(h, code, null, 0, output, output?.Length ?? 0, out _, IntPtr.Zero);

    static long LengthOf(SafeFileHandle h)
    {
        var o = new byte[8];
        if (!Ioctl(h, IOCTL_DISK_GET_LENGTH_INFO, o)) throw new IOException("Cannot read the size of the device: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        return BitConverter.ToInt64(o);
    }

    static string PhysicalDrive(int n) => @"\\.\PhysicalDrive" + n;

    sealed record Vol(string Name, int Disk, long Offset, long Length, int Extents);

    /// Every volume Windows knows, with where it sits; one row per extent.
    static List<Vol> Volumes()
    {
        var list = new List<Vol>();
        var name = new StringBuilder(64);
        var find = FindFirstVolume(name, name.Capacity);
        if (find == new IntPtr(-1)) return list;
        try
        {
            do
            {
                var v = name.ToString();
                using var h = CreateFile(v.TrimEnd('\\'), 0, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                var o = new byte[8 + 24 * 16];
                if (!Ioctl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, o)) continue;
                var count = BitConverter.ToInt32(o, 0);
                for (int i = 0; i < Math.Min(count, 16); i++)
                    list.Add(new Vol(v, BitConverter.ToInt32(o, 8 + 24 * i), BitConverter.ToInt64(o, 16 + 24 * i), BitConverter.ToInt64(o, 24 + 24 * i), count));
            }
            while (FindNextVolume(find, name, name.Capacity));
        }
        finally { FindVolumeClose(find); }
        return list;
    }

    /// The physical disk a path lives on, or -1 (network path, spanned volume, not found).
    public static int DiskOf(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return -1;
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(root, name, name.Capacity)) return -1;
        var hits = Volumes().Where(v => v.Name.Equals(name.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count == 1 ? hits[0].Disk : -1;
    }

    static string FileSystem(string volume)
    {
        var fs = new StringBuilder(32);
        return GetVolumeInformation(volume, null, 0, out _, out _, out _, fs, fs.Capacity) ? fs.ToString() : "";
    }

    /// The used parts of an NTFS volume, as byte ranges from its start, from its cluster bitmap; null if it
    /// isn't NTFS or the bitmap can't be read. Gaps under 1 MiB are kept in: fewer, longer reads.
    static List<(long Start, long Length)>? UsedRanges(string device, long length)
    {
        using var h = CreateFile(device, GENERIC_READ, SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var data = new byte[128];
        if (!Ioctl(h, FSCTL_GET_NTFS_VOLUME_DATA, data)) return null;
        long total = BitConverter.ToInt64(data, 16), cluster = BitConverter.ToUInt32(data, 44);
        var ranges = new List<(long Start, long Length)>();
        var input = new byte[8];
        var output = new byte[16 + (1 << 20)];
        long lcn = 0, run = -1;
        void Close(long end) { if (run >= 0) ranges.Add((run * cluster, (end - run) * cluster)); run = -1; }
        while (lcn < total)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, lcn);
            var ok = DeviceIoControl(h, FSCTL_GET_VOLUME_BITMAP, input, 8, output, output.Length, out var got, IntPtr.Zero);
            if (!ok && Marshal.GetLastWin32Error() != 234) return null; // 234: more data, ask again from where it stopped
            long start = BitConverter.ToInt64(output, 0), bits = Math.Min(BitConverter.ToInt64(output, 8), (got - 16) * 8L);
            for (long i = 0; i < bits; i++)
            {
                var b = output[16 + (i >> 3)];
                if ((i & 7) == 0 && i + 8 <= bits && (b == 0 || b == 0xFF))
                {   // a whole byte the same way: the common case on a big disk
                    if (b == 0xFF) { if (run < 0) run = start + i; } else Close(start + i);
                    i += 7;
                    continue;
                }
                if ((b & (1 << (int)(i & 7))) != 0) { if (run < 0) run = start + i; } else Close(start + i);
            }
            lcn = start + bits;
            if (ok || bits == 0) break;
        }
        Close(Math.Min(lcn, total));
        var fsEnd = total * cluster; // past the last cluster: NTFS's backup boot sector
        if (fsEnd < length) ranges.Add((fsEnd, length - fsEnd));
        var merged = new List<(long Start, long Length)>();
        foreach (var r in ranges)
            if (merged.Count > 0 && r.Start - (merged[^1].Start + merged[^1].Length) < (1 << 20))
                merged[^1] = (merged[^1].Start, r.Start + r.Length - merged[^1].Start);
            else merged.Add(r);
        return merged;
    }

    /// Aligned memory for unbuffered device I/O.
    sealed class Block
    {
        readonly byte[] raw = GC.AllocateArray<byte>(Chunk + 4096, pinned: true);
        readonly int offset;
        public Block() { var at = Marshal.UnsafeAddrOfPinnedArrayElement(raw, 0).ToInt64(); offset = (int)((4096 - at % 4096) % 4096); }
        public Memory<byte> Mem(int length) => raw.AsMemory(offset, length);
        public Span<byte> Span(int length) => raw.AsSpan(offset, length);
    }

    static int ReadFull(SafeFileHandle h, Span<byte> into, long at)
    {
        int done = 0;
        while (done < into.Length)
        {
            var n = RandomAccess.Read(h, into[done..], at + done);
            if (n == 0) break;
            done += n;
        }
        return done;
    }

    /// Lock and dismount every volume of a disk about to be overwritten, keeping the locks until it's done.
    static List<SafeFileHandle> TakeVolumes(int disk, Action<string> log)
    {
        var held = new List<SafeFileHandle>();
        foreach (var name in Volumes().Where(v => v.Disk == disk).Select(v => v.Name).Distinct())
        {
            var h = Open(name.TrimEnd('\\'), write: true, raw: false);
            if (!Ioctl(h, FSCTL_LOCK_VOLUME))
            {   // something has a file open there: dismounting closes it, then the lock takes
                Ioctl(h, FSCTL_DISMOUNT_VOLUME);
                if (!Ioctl(h, FSCTL_LOCK_VOLUME)) { h.Dispose(); throw new IOException($"A volume on Disk {disk} is in use and couldn't be taken offline."); }
                log($"Disk {disk}: closed the files open on {name}");
            }
            Ioctl(h, FSCTL_DISMOUNT_VOLUME);
            held.Add(h);
        }
        return held;
    }

    // ---- the copy ----

    sealed record Piece(long At, long Length, SafeFileHandle From, long FromAt);

    public static CloneResult Run(CloneRequest r, Action<string> log, Action<double, string>? progress, CancellationToken ct)
    {
        var disks = Disks();
        if (Problem(r, disks) is string problem) throw new InvalidOperationException(problem);
        if (r.Mode != "ImageToDisk" && disks.First(d => d.Number == r.SourceDisk).Id != r.SourceId
            || r.Mode != "DiskToImage" && disks.First(d => d.Number == r.TargetDisk).Id != r.TargetId)
            throw new InvalidOperationException("The disks changed since you chose them. Check the list and start again.");

        var notes = new List<string>();
        var handles = new List<SafeFileHandle>();
        var shadows = new List<Engine.Shadow>();
        var locks = new List<SafeFileHandle>();
        string? partial = null;
        try
        {
            // ---- where the bytes come from ----
            SafeFileHandle source;
            long size;
            var pieces = new List<Piece>();
            DiskInfo? src = null;
            int sector = 512;
            if (r.Mode == "ImageToDisk")
            {
                source = File.OpenHandle(r.SourceImage!, FileMode.Open, FileAccess.Read, FileShare.Read);
                handles.Add(source);
                size = RandomAccess.GetLength(source);
                pieces.Add(new Piece(0, size, source, 0));
            }
            else
            {
                src = disks.First(d => d.Number == r.SourceDisk);
                sector = src.SectorSize;
                source = Open(PhysicalDrive(src.Number), write: false);
                handles.Add(source);
                size = LengthOf(source);
                pieces.AddRange(Plan(src, source, size, r.UsedOnly, handles, shadows, notes, log, ct));
            }
            // the first megabyte: the partition table, read through aligned memory (raw devices insist)
            var first = new Block();
            var head = first.Span((int)Math.Min(Head, size))[..ReadFull(source, first.Span((int)Math.Min(Head, size)), 0)].ToArray();

            // ---- where they go ----
            SafeFileHandle target;
            long limit = size;
            DiskInfo? dst = null;
            bool sparse = false;
            if (r.Mode == "DiskToImage")
            {
                partial = r.TargetImage + ".partial";
                Directory.CreateDirectory(Path.GetDirectoryName(r.TargetImage!)!);
                target = File.OpenHandle(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                handles.Add(target);
                sparse = Ioctl(target, FSCTL_SET_SPARSE); // FAT and exFAT can't: the empty space is written out
                if (!sparse) notes.Add("The image's drive can't hold sparse files, so empty space takes room in the image too.");
            }
            else
            {
                dst = disks.First(d => d.Number == r.TargetDisk);
                if (src == null) sector = dst.SectorSize;
                locks = TakeVolumes(dst.Number, log);
                target = Open(PhysicalDrive(dst.Number), write: true);
                handles.Add(target);
                var room = LengthOf(target);
                limit = Math.Min(size, room);
                var needed = Needed(head, sector, size);
                if (needed > room) throw new InvalidOperationException($"Disk {dst.Number} is too small: the partitions need {Engine.Size(needed)}.");
                if (size > room) notes.Add($"Disk {dst.Number} is smaller than the original; everything the partitions hold fits, the empty end was left out.");
            }
            pieces = Clip(pieces, limit);

            // ---- copy: the first megabyte last on a disk, two blocks so reading and writing overlap ----
            var chunks = Chunks(pieces).ToList();
            if (dst != null) chunks = chunks.Where(c => c.At >= Head).Concat(chunks.Where(c => c.At < Head)).ToList();
            long phaseTotal = chunks.Sum(c => (long)c.Len), phaseDone = 0, read = 0, written = 0;
            double phases = r.Verify ? 2 : 1, phase = 0;
            var clock = Stopwatch.StartNew();
            var tick = Stopwatch.StartNew();
            void Report(string what, long bytes)
            {
                phaseDone += bytes;
                if (progress == null || tick.ElapsedMilliseconds < 150) return;
                tick.Restart();
                var speed = phaseDone / Math.Max(0.5, clock.Elapsed.TotalSeconds);
                progress((phase + (phaseTotal == 0 ? 1 : (double)phaseDone / phaseTotal)) / phases,
                         $"{what} · {Engine.Size(phaseDone)} of {Engine.Size(phaseTotal)} · {Engine.Size((long)speed)}/s");
            }
            int Rounded(int n) => dst == null ? n : (n + sector - 1) / sector * sector;
            var hashes = new List<byte[]>(chunks.Count);
            var blocks = new[] { new Block(), new Block() };
            var b = 0;
            Task pending = Task.CompletedTask;
            foreach (var c in chunks)
            {
                ct.ThrowIfCancellationRequested();
                var block = blocks[b];
                b ^= 1;
                var len = Rounded(c.Len);
                var got = ReadFull(c.From, block.Span(c.Len), c.FromAt);
                block.Span(len)[got..].Clear(); // past the end of an image, and the padding to a whole sector
                read += got;
                if (r.Verify) hashes.Add(SHA256.HashData(block.Span(len))[..16]);
                pending.GetAwaiter().GetResult(); // the other block's write; its exception surfaces here
                var at = c.At;
                var skip = sparse && !block.Span(len).ContainsAnyExcept((byte)0);
                if (!skip) written += len;
                pending = skip ? Task.CompletedTask : Task.Run(() => RandomAccess.Write(target, block.Mem(len).Span, at));
                Report(dst == null ? "Copying" : $"Writing Disk {dst.Number}", c.Len);
            }
            pending.GetAwaiter().GetResult();
            if (dst == null) RandomAccess.SetLength(target, size);

            if (r.Verify)
            {
                phase = 1;
                phaseDone = 0;
                clock.Restart();
                var block = blocks[0];
                for (int i = 0; i < chunks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var c = chunks[i];
                    var len = Rounded(c.Len);
                    var got = ReadFull(target, block.Span(len), c.At);
                    block.Span(len)[got..].Clear();
                    if (!SHA256.HashData(block.Span(len))[..16].AsSpan().SequenceEqual(hashes[i]))
                        throw new IOException($"Verification failed {Engine.Size(c.At)} into the copy: what was read back differs from what was written. The {(dst == null ? "drive" : "disk")} may be faulty.");
                    Report("Verifying", c.Len);
                }
                notes.Add("Every block was read back and matched.");
            }

            if (dst != null)
            {
                var grow = Finish(head, sector, size, target, dst, r.Grow, notes, log);
                Ioctl(target, IOCTL_DISK_UPDATE_PROPERTIES); // Windows reads the new partition table
                foreach (var l in locks) l.Dispose();
                locks.Clear();
                if (grow is long offset) GrowPartition(dst.Number, offset, notes, log);
            }
            else
            {
                foreach (var h in handles) h.Dispose();
                handles.Clear();
                File.Move(partial!, r.TargetImage!, true);
                partial = null;
            }
            var where = dst != null ? $"Disk {dst.Number} ({dst.Model})" : r.TargetImage!;
            log($"Clone done: {(src != null ? $"Disk {src.Number}" : r.SourceImage)} → {where}: read {Engine.Size(read)}, wrote {Engine.Size(written)}");
            return new CloneResult(where, read, written, r.Verify, notes);
        }
        finally
        {
            foreach (var h in handles) h.Dispose();
            foreach (var l in locks) l.Dispose();
            foreach (var s in shadows) s.Dispose();
            if (partial != null) try { File.Delete(partial); } catch (IOException) { }
        }
    }

    /// What to read from a disk: its volumes through shadow copies where they are NTFS (consistent even
    /// while in use), only their used clusters with usedOnly; everything between and around them raw.
    static IEnumerable<Piece> Plan(DiskInfo d, SafeFileHandle disk, long size, bool usedOnly, List<SafeFileHandle> handles,
        List<Engine.Shadow> shadows, List<string> notes, Action<string> log, CancellationToken ct)
    {
        var bootVolume = new StringBuilder(64);
        GetVolumeNameForVolumeMountPoint(Path.GetPathRoot(Environment.SystemDirectory)!, bootVolume, bootVolume.Capacity);
        var volumes = Volumes().Where(v => v.Disk == d.Number && v.Extents == 1 && v.Offset + v.Length <= size).OrderBy(v => v.Offset).ToList();
        var pieces = new List<Piece>();
        // Space between and around volumes is read as it is. With usedOnly, space outside every partition is
        // skipped: it holds nothing, except the partition tables in the first and last megabyte.
        var spans = d.Parts.Select(p => (Start: p.Offset, End: p.Offset + p.Size)).ToList();
        spans.Add((0, Head));
        spans.Add((size - Head, size));
        var kept = new List<(long Start, long End)>();
        foreach (var x in spans.OrderBy(x => x.Start))
            if (kept.Count > 0 && x.Start <= kept[^1].End) kept[^1] = (kept[^1].Start, Math.Max(kept[^1].End, x.End));
            else kept.Add(x);
        void Raw(long from, long to)
        {
            if (!usedOnly) { if (to > from) pieces.Add(new Piece(from, to - from, disk, from)); return; }
            foreach (var (s, e) in kept)
            {
                var a = Math.Max(from, s);
                var b = Math.Min(to, e);
                if (b > a) pieces.Add(new Piece(a, b - a, disk, a));
            }
        }
        long cursor = 0;
        var probe = new Block();
        foreach (var v in volumes)
        {
            ct.ThrowIfCancellationRequested();
            if (v.Offset < cursor) continue;
            Raw(cursor, v.Offset);
            var letter = d.Parts.FirstOrDefault(p => p.Offset == v.Offset)?.Letter ?? '\0';
            var label = letter != '\0' ? letter + ":" : "a volume";
            var fs = FileSystem(v.Name);
            var isBoot = v.Name.Equals(bootVolume.ToString(), StringComparison.OrdinalIgnoreCase);
            ReadFull(disk, probe.Span(d.SectorSize), v.Offset);
            var bitlocker = Encoding.ASCII.GetString(probe.Span(d.SectorSize).Slice(3, 8)) == "-FVE-FS-";

            Engine.Shadow? shadow = null;
            SafeFileHandle from = disk;
            long fromBase = v.Offset, readable = v.Length;
            // volumes in use have letters; hidden ones (recovery, EFI) practically never change, and VSS
            // refuses some of them, which can upset the next shadow copy too
            if (fs == "NTFS" && (letter != '\0' || isBoot))
                try
                {
                    try { shadow = Engine.Shadow.Create(v.Name); }
                    catch (COMException) { Thread.Sleep(2000); shadow = Engine.Shadow.Create(v.Name); } // VSS busy finishing the last one
                    shadows.Add(shadow);
                    from = Open(shadow.Device, write: false);
                    handles.Add(from);
                    fromBase = 0;
                    readable = Math.Min(v.Length, LengthOf(from));
                    log($"Disk {d.Number}: {label} read through a shadow copy");
                    if (bitlocker) notes.Add($"{label} is encrypted with BitLocker. The copy is not: turn BitLocker on for it if it needs to be.");
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or COMException)
                {
                    if (isBoot) throw new InvalidOperationException($"Windows' own volume {label} couldn't be shadow-copied, and copying it while Windows writes to it would give a broken copy. {e.Message}");
                    notes.Add($"{label} couldn't be shadow-copied, so it was read as it was being used: copy it again if files changed meanwhile.");
                    log($"Disk {d.Number}: no shadow copy of {label}: {e.Message}");
                    shadow = null;
                }

            // used clusters only: from the shadow's bitmap, or the volume's own when its raw sectors are its data
            var ranges = usedOnly && fs == "NTFS" && (shadow != null || !bitlocker)
                ? UsedRanges(shadow?.Device ?? v.Name.TrimEnd('\\'), v.Length) : null;
            if (usedOnly && ranges != null)
            {
                var used = ranges.Sum(x => x.Length);
                log($"Disk {d.Number}: {label} uses {Engine.Size(used)} of {Engine.Size(v.Length)}");
            }
            foreach (var (s, l) in ranges ?? new() { (0L, v.Length) })
            {
                var inShadow = Math.Max(0, Math.Min(l, readable - s));
                if (inShadow > 0) pieces.Add(new Piece(v.Offset + s, inShadow, from, fromBase + s));
                if (inShadow < l) pieces.Add(new Piece(v.Offset + s + inShadow, l - inShadow, disk, v.Offset + s + inShadow));
            }
            cursor = v.Offset + v.Length;
        }
        Raw(cursor, size);
        if (d.System) notes.Add($"Disk {d.Number} is the disk Windows runs from; its volumes were copied as they were at the start.");
        return pieces;
    }

    static List<Piece> Clip(List<Piece> pieces, long limit) =>
        pieces.Where(p => p.At < limit).Select(p => p.At + p.Length <= limit ? p : p with { Length = limit - p.At }).ToList();

    /// Pieces cut into blocks of at most 4 MiB, never across the first-megabyte boundary.
    static IEnumerable<(long At, int Len, SafeFileHandle From, long FromAt)> Chunks(List<Piece> pieces)
    {
        foreach (var p in pieces)
            for (long o = 0; o < p.Length;)
            {
                var at = p.At + o;
                var n = (int)Math.Min(Math.Min(Chunk, p.Length - o), at < Head ? Head - at : long.MaxValue);
                yield return (at, n, p.From, p.FromAt + o);
                o += n;
            }
    }

    /// The partition table, made to fit the disk it now lives on. Returns the offset of the partition that
    /// should take the extra space, when Grow was asked and there is one.
    static long? Finish(byte[] head, int sector, long size, SafeFileHandle disk, DiskInfo dst, bool grow, List<string> notes, Action<string> log)
    {
        var sectors = LengthOf(disk) / sector;
        var gpt = Gpt.Parse(head, sector);
        if (gpt == null)
        {   // MBR keeps no copy at the end; it only needs the last partition to be the one that grows
            if (!grow || sectors * sector <= size || head.Length < 512 || head[510] != 0x55) return null;
            var last = Enumerable.Range(0, 4).Select(i => head.AsSpan(446 + 16 * i, 16).ToArray())
                .Where(e => e[4] != 0).OrderBy(e => BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(8))).LastOrDefault();
            if (last != null && last[4] == 0x07) return (long)BinaryPrimitives.ReadUInt32LittleEndian(last.AsSpan(8)) * 512;
            notes.Add("The extra space on the new disk was left unallocated.");
            return null;
        }
        if (sectors * sector == size && !grow) return null; // same size: the copy is exact as it stands

        long? grows = null;
        var parts = gpt.Parts().OrderBy(p => p.First).ToList();
        if (grow && sectors * sector > size && parts.Count > 0)
        {
            var lastPart = parts[^1];
            var lastUsable = sectors - 1 - gpt.EntrySectors - 1;
            if ((lastPart.Type == Recovery || (lastPart.Last - lastPart.First + 1) * sector <= 2L << 30 && lastPart.Type != BasicData) && parts.Count > 1)
            {   // a recovery partition at the end: move it to the new end, so the one before it can grow
                var length = lastPart.Last - lastPart.First + 1;
                var align = Math.Max(1, (1 << 20) / sector);
                var newFirst = (lastUsable + 1 - length) / align * align;
                if (newFirst > lastPart.First)
                {
                    MoveSectors(disk, lastPart.First * sector, newFirst * sector, length * sector);
                    gpt.Move(lastPart.Index, newFirst);
                    log($"Disk {dst.Number}: moved the {(lastPart.Type == Recovery ? "recovery" : "last")} partition to the end of the disk");
                    var before = parts[^2];
                    if (before.Type == BasicData) grows = before.First * sector;
                }
            }
            else if (lastPart.Type == BasicData) grows = lastPart.First * sector;
            if (grows == null) notes.Add("The extra space on the new disk was left unallocated.");
        }
        foreach (var (lba, data) in gpt.Relocated(sectors, head.AsSpan(0, sector).ToArray()))
            RandomAccess.Write(disk, Aligned(data), lba * sector);
        log($"Disk {dst.Number}: GPT fitted to {Engine.Size(sectors * sector)}");
        return grows;
    }

    static ReadOnlySpan<byte> Aligned(byte[] data)
    {
        var b = new Block();
        data.CopyTo(b.Span(data.Length));
        return b.Span(data.Length);
    }

    /// Copies a range of the disk onto a later, possibly overlapping range: from the end backwards.
    static void MoveSectors(SafeFileHandle disk, long from, long to, long length)
    {
        var b = new Block();
        for (long left = length; left > 0;)
        {
            var n = (int)Math.Min(Chunk, left);
            left -= n;
            ReadFull(disk, b.Span(n), from + left);
            RandomAccess.Write(disk, b.Span(n), to + left);
        }
    }

    /// Gives a partition of the new disk the free space after it, file system included, through Windows'
    /// storage management. That needs the disk online, which Windows refuses while an identical disk (the
    /// original) is connected; then it says how to finish later.
    static void GrowPartition(int disk, long offset, List<string> notes, Action<string> log)
    {
        for (int i = 0; i < 15; i++)
        {
            Thread.Sleep(1000);
            try
            {
                var wmi = Storage();
                foreach (var p in Each(wmi.ExecQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk} AND Offset={offset}")))
                {
                    dynamic part = p;
                    dynamic sizes = part.ExecMethod_("GetSupportedSize");
                    var max = L(sizes.Properties_.Item("SizeMax").Value);
                    var now = L(P(part, "Size"));
                    if (max <= now + (16 << 20)) return;
                    dynamic input = part.Methods_.Item("Resize").InParameters.SpawnInstance_();
                    input.Properties_.Item("Size").Value = max.ToString(CultureInfo.InvariantCulture);
                    dynamic result = part.ExecMethod_("Resize", input);
                    var code = L(result.Properties_.Item("ReturnValue").Value);
                    if (code != 0)
                    {   // Windows grows NTFS (and ReFS) only: an exFAT or FAT32 card keeps its size
                        notes.Add($"The partition kept its size ({Engine.Size(now)}): Windows can only grow NTFS volumes. The rest of the disk is free space; format it as a new volume in Disk Management if you want to use it.");
                        log($"Disk {disk}: resize refused, code {code}");
                        return;
                    }
                    notes.Add($"The partition grew from {Engine.Size(now)} to {Engine.Size(max)}, file system included.");
                    log($"Disk {disk}: grew the partition at {offset} to {max}");
                    return;
                }
            }
            catch (Exception e) when (e is COMException or InvalidCastException or IOException) { log($"Disk {disk}: resize not possible yet: {e.Message}"); }
        }
        notes.Add("Windows keeps the new disk offline while the original is connected, because the two are identical. Once you swap them, " +
                  "Disk Management → right-click the partition → Extend Volume gives it the free space after it.");
    }

    // ---- GPT ----

    public sealed class Gpt
    {
        public int Sector;
        long entriesLba;
        int count, entrySize;
        byte[] header = null!, entries = null!;
        public int EntrySectors => (count * entrySize + Sector - 1) / Sector;

        public static Gpt? Parse(byte[] head, int sector)
        {
            if (head.Length < 2 * sector) return null;
            var h = head.AsSpan(sector);
            if (!h[..8].SequenceEqual("EFI PART"u8)) return null;
            var headerSize = BinaryPrimitives.ReadInt32LittleEndian(h[12..]);
            var lba = BinaryPrimitives.ReadInt64LittleEndian(h[72..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(h[80..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(h[84..]);
            if (headerSize is < 92 or > 512 || headerSize > sector || size < 128 || size > 1024 || count is <= 0 or > 1024 || lba < 2) return null;
            var start = lba * sector;
            var bytes = (long)count * size;
            if (start + bytes > head.Length) return null;
            return new Gpt { Sector = sector, entriesLba = lba, count = count, entrySize = size,
                             header = h[..headerSize].ToArray(), entries = head.AsSpan((int)start, (int)bytes).ToArray() };
        }

        public IEnumerable<(int Index, long First, long Last, Guid Type)> Parts()
        {
            for (int i = 0; i < count; i++)
            {
                var e = entries.AsSpan(i * entrySize, entrySize);
                var type = new Guid(e[..16]);
                if (type != Guid.Empty) yield return (i, BinaryPrimitives.ReadInt64LittleEndian(e[32..]), BinaryPrimitives.ReadInt64LittleEndian(e[40..]), type);
            }
        }

        public long LastUsed => Parts().Select(p => p.Last).DefaultIfEmpty(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40)) - 1).Max();

        public void Move(int index, long first)
        {
            var e = entries.AsSpan(index * entrySize, entrySize);
            var length = BinaryPrimitives.ReadInt64LittleEndian(e[40..]) - BinaryPrimitives.ReadInt64LittleEndian(e[32..]);
            BinaryPrimitives.WriteInt64LittleEndian(e[32..], first);
            BinaryPrimitives.WriteInt64LittleEndian(e[40..], first + length);
        }

        /// The sectors to write so this table describes a disk of `sectors` sectors: the protective MBR's
        /// size, the primary header and entries, and the backup entries and header at the very end.
        public List<(long Lba, byte[] Data)> Relocated(long sectors, byte[] mbr)
        {
            var last = sectors - 1;
            var backupEntries = last - EntrySectors;
            var table = new byte[EntrySectors * Sector];
            entries.CopyTo(table, 0);
            var entriesCrc = Crc32(entries);
            byte[] Header(long my, long alternate, long at)
            {
                var h = new byte[Sector];
                header.CopyTo(h, 0);
                BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), my);
                BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), alternate);
                BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), backupEntries - 1); // last usable
                BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), at);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), entriesCrc);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 0);
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32(h.AsSpan(0, header.Length)));
                return h;
            }
            var pmbr = (byte[])mbr.Clone();
            if (pmbr.Length >= 512 && pmbr[446 + 4] == 0xEE)
                BinaryPrimitives.WriteUInt32LittleEndian(pmbr.AsSpan(446 + 12), (uint)Math.Min(last, uint.MaxValue));
            return new() { (0, pmbr), (1, Header(1, last, entriesLba)), (entriesLba, table), (backupEntries, table), (last, Header(last, 1, backupEntries)) };
        }
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
