using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Backup;

// The "Clone a drive" panel. Reading and writing whole disks happens in the elevated copy of the app
// (Elevated.RunClone), one UAC prompt per clone; this side lists the disks, checks the choice and follows it.
public partial class MainWindow
{
    public sealed record DiskItem(DiskInfo D, bool Usable, string Why)
    {
        public string Title => $"Disk {D.Number} · {D.Model}";
        public string Detail => (Why != "" ? Why + " · " : "") + $"{Engine.Size(D.Size)} · {D.Bus}"
            + (D.Letters != "" ? " · " + D.Letters : "") + (D.System && Why == "" ? " · Windows runs from it" : "");
    }

    List<DiskInfo> disks = new();
    bool cloning;
    string? cloneId; // the running clone's, so Exit can stop it

    string CloneMode => CloneModeBar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "DiskToImage";
    bool UsedOnly => CopyBar.Children.OfType<RadioButton>().Any(r => r.IsChecked == true && (string)r.Tag == "Used");
    DiskInfo? SourceDisk => (SourceDisks.SelectedItem as DiskItem)?.D;
    DiskInfo? TargetDisk => (TargetDisks.SelectedItem as DiskItem)?.D;

    void CloneOpen_Click(object sender, RoutedEventArgs e)
    {
        ShowRestore(false);
        ShowClone(true);
        if (CloneModeBar.Children.OfType<RadioButton>().All(r => r.IsChecked != true))
        {
            ((RadioButton)CloneModeBar.Children[0]).IsChecked = true;
            ((RadioButton)CopyBar.Children[0]).IsChecked = true;
        }
        _ = RefreshDisks();
    }

    void ShowClone(bool on)
    {
        if (!on && cloning) return; // the clone keeps its panel until it's done
        ClonePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MainView.Visibility = on ? Visibility.Hidden : Visibility.Visible;
    }

    void CloneBack_Click(object sender, RoutedEventArgs e) => ShowClone(false);
    void CloneRefresh_Click(object sender, RoutedEventArgs e) => _ = RefreshDisks();

    async Task RefreshDisks()
    {
        CloneRefreshButton.IsEnabled = false;
        CloneStatus.Text = "Looking for disks…";
        try { disks = await Task.Run(Clone.Disks); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            disks = new();
            CloneStatus.Text = "Windows didn't list the disks: " + e.Message;
        }
        CloneRefreshButton.IsEnabled = true;
        var keep = SourceDisk?.Number;
        SourceDisks.ItemsSource = disks.Select(d => new DiskItem(d, true, "")).ToList();
        SourceDisks.SelectedItem = SourceDisks.Items.Cast<DiskItem>().FirstOrDefault(i => i.D.Number == keep);
        FillTargets();
        UpdateClone();
    }

    /// The disks that could be written to, the others greyed with the reason.
    void FillTargets()
    {
        var keep = TargetDisk?.Number;
        var mode = CloneMode;
        long? needed = mode == "DiskToDisk" ? SourceDisk?.Needed
                     : mode == "ImageToDisk" && File.Exists(ImageInBox.Text.Trim()) ? ImageNeeded(ImageInBox.Text.Trim()) : null;
        var imageDisk = mode == "ImageToDisk" && Path.IsPathFullyQualified(ImageInBox.Text.Trim()) ? Clone.DiskOf(ImageInBox.Text.Trim()) : -1;
        TargetDisks.ItemsSource = disks.Select(d =>
        {
            var why = d.System || d.Boot ? "Windows runs from it"
                    : mode == "DiskToDisk" && SourceDisk?.Number == d.Number ? "the disk being copied"
                    : d.Number == imageDisk ? "holds the image file"
                    : needed > d.Size ? $"too small, needs {Engine.Size(needed.Value)}"
                    : "";
            return new DiskItem(d, why == "", why);
        }).ToList();
        TargetDisks.SelectedItem = TargetDisks.Items.Cast<DiskItem>().FirstOrDefault(i => i.D.Number == keep && i.Usable);
    }

    static long? ImageNeeded(string image)
    {
        try
        {
            using var f = File.OpenRead(image);
            var head = new byte[Math.Min(1 << 20, f.Length)];
            f.ReadExactly(head);
            return Clone.Needed(head, 512, f.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    void CloneMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        FillTargets();
        UpdateClone();
    }

    void CloneInputs_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || cloning) return;
        if (sender == SourceDisks || sender == ImageInBox) FillTargets();
        UpdateClone();
    }

    CloneRequest Request() => new(CloneMode, SourceDisk?.Number ?? -1, CloneMode == "ImageToDisk" ? ImageInBox.Text.Trim() : null,
        TargetDisk?.Number ?? -1, CloneMode == "DiskToImage" ? ImageOutBox.Text.Trim() : null,
        UsedOnly, CloneVerify.IsChecked == true, CloneGrow.IsChecked == true, SourceDisk?.Id ?? "", TargetDisk?.Id ?? "");

    /// Shows what fits the mode, and says plainly what will happen, or what's in the way.
    void UpdateClone()
    {
        var mode = CloneMode;
        var fromDisk = mode != "ImageToDisk";
        var toDisk = mode != "DiskToImage";
        ImageInRow.Visibility = fromDisk ? Visibility.Collapsed : Visibility.Visible;
        SourceDisksBox.Visibility = fromDisk ? Visibility.Visible : Visibility.Collapsed;
        ImageOutRow.Visibility = toDisk ? Visibility.Collapsed : Visibility.Visible;
        TargetDisksBox.Visibility = toDisk ? Visibility.Visible : Visibility.Collapsed;
        CopyRow.Visibility = fromDisk ? Visibility.Visible : Visibility.Collapsed;
        CloneGrow.Visibility = toDisk ? Visibility.Visible : Visibility.Collapsed;
        ImageInInfo.Text = !fromDisk && File.Exists(ImageInBox.Text.Trim())
            ? $"{Engine.Size(new FileInfo(ImageInBox.Text.Trim()).Length)}; its partitions need {Engine.Size(ImageNeeded(ImageInBox.Text.Trim()) ?? 0)} on the disk." : "";
        CloneStartButton.Content = cloning ? "Cancel" : toDisk && TargetDisk is { } t ? $"Erase Disk {t.Number} and write" : toDisk ? "Write to disk" : "Create image";

        if (cloning) return;
        var problem = Clone.Problem(Request(), disks);
        CloneStartButton.IsEnabled = problem == null;
        CloneStatus.Text = problem ?? "";
        CloneStatus.Foreground = (Brush)FindResource("Dim");

        var notes = new List<string>();
        if (fromDisk && SourceDisk is { System: true } s)
            notes.Add($"Disk {s.Number} is the disk Windows runs from: its volumes are read through a shadow copy, so the copy is as they were at one moment.");
        if (toDisk && TargetDisk is { } target)
            notes.Add($"Everything on Disk {target.Number} ({target.Model}, {Engine.Size(target.Size)}{(target.Letters != "" ? ", " + target.Letters : "")}) will be replaced. This can't be undone.");
        if (toDisk) notes.Add("The copy keeps the original's disk and partition ids, so it boots and its programs find their drives; Windows keeps it offline while the original is connected too.");
        notes.Add("Windows asks for administrator permission: whole disks can only be read and written with it.");
        CloneNote.Text = string.Join("\n", notes);
        CloneNote.Foreground = toDisk && TargetDisk != null ? Warning : (Brush)FindResource("Dim");
    }

    void CloneImageIn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Image file to write", Filter = "Disk images (*.img;*.bin;*.raw;*.iso)|*.img;*.bin;*.raw;*.iso|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        ImageInBox.Text = dlg.FileName;
        CloneInputs_Changed(ImageInBox, e);
    }

    void CloneImageOut_Click(object sender, RoutedEventArgs e)
    {
        var name = SourceDisk is { } s ? $"Disk {s.Number} {string.Concat(s.Model.Split(Path.GetInvalidFileNameChars()))} {DateTime.Now:yyyy-MM-dd}.img" : "disk.img";
        var dlg = new SaveFileDialog { Title = "Save the image as", FileName = name, DefaultExt = ".img", Filter = "Disk image (*.img)|*.img|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        ImageOutBox.Text = dlg.FileName;
        CloneInputs_Changed(ImageOutBox, e);
    }

    async void CloneStart_Click(object sender, RoutedEventArgs e)
    {
        if (cloning) { cts?.Cancel(); CloneStatus.Text = "Cancelling…"; return; }
        if (cts != null) { CloneStatus.Text = "Wait for the backup that's running to finish."; CloneStatus.Foreground = Warning; return; }
        var r = Request();
        if (Clone.Problem(r, disks) is string problem) { CloneStatus.Text = problem; return; }
        if (r.Mode != "DiskToImage")
        {
            var t = TargetDisk!;
            var from = r.Mode == "ImageToDisk" ? Path.GetFileName(r.SourceImage) : $"Disk {SourceDisk!.Number} ({SourceDisk.Model})";
            if (MessageBox.Show(this, $"Erase Disk {t.Number} ({t.Model}, {Engine.Size(t.Size)}{(t.Letters != "" ? ", " + t.Letters : "")}) and write {from} onto it?\n\nEverything on Disk {t.Number} is replaced. This can't be undone.",
                    "Clone a drive", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        if (r.Mode == "DiskToImage" && File.Exists(r.TargetImage)
            && MessageBox.Show(this, $"{Path.GetFileName(r.TargetImage)} already exists. Replace it?", "Clone a drive",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        cloning = true;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        foreach (var c in new UIElement[] { CloneBackButton, CloneRefreshButton, CloneModeBar, SourceDisks, TargetDisks, ImageInRow, ImageOutRow, CopyRow, CloneVerify, CloneGrow, CloneOpenButton })
            c.IsEnabled = false;
        CloneProgress.Value = 0;
        CloneProgress.Visibility = Visibility.Visible;
        CloneStatus.Foreground = (Brush)FindResource("Dim");
        CloneStatus.Text = "Waiting for administrator permission…";
        UpdateClone();
        IProgress<(double f, string t)> p = new Progress<(double f, string t)>(x => { CloneProgress.Value = x.f; CloneStatus.Text = $"{x.f:P0}  ·  {x.t}"; });
        var id = cloneId = "clone-" + Guid.NewGuid().ToString("N")[..12];
        Store.Log($"Clone started: {r.Mode} {(r.Mode == "ImageToDisk" ? r.SourceImage : "Disk " + r.SourceDisk)} → {(r.Mode == "DiskToImage" ? r.TargetImage : "Disk " + r.TargetDisk)}{(r.UsedOnly ? ", used space only" : "")}");
        try
        {
            File.WriteAllText(Elevated.FileFor(id, "request"), JsonSerializer.Serialize(r));
            var outcome = await Elevate(id, $"--elevated-clone {id}", (f, t) => p.Report((f, t)), token,
                "Administrator permission was declined: whole disks can only be read and written with it.");
            var done = outcome.Cloned!;
            CloneStatus.Text = $"Done: {Engine.Size(done.Read)} read, {Engine.Size(done.Written)} written to {done.Target}.";
            CloneNote.Text = string.Join("\n", done.Notes);
            CloneNote.Foreground = (Brush)FindResource("Dim");
        }
        catch (OperationCanceledException)
        {
            CloneStatus.Text = "Cancelled.";
            CloneNote.Text = r.Mode == "DiskToImage" ? "The unfinished image was deleted."
                : $"Disk {r.TargetDisk} was left partly written: clone onto it again, or format it in Disk Management.";
            CloneNote.Foreground = Warning;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            CloneStatus.Text = "Failed: " + ex.Message;
            CloneStatus.Foreground = Warning;
            Store.Log("Clone failed: " + ex.Message);
        }
        cts.Dispose();
        cts = null;
        cloning = false;
        cloneId = null;
        foreach (var c in new UIElement[] { CloneBackButton, CloneRefreshButton, CloneModeBar, SourceDisks, TargetDisks, ImageInRow, ImageOutRow, CopyRow, CloneVerify, CloneGrow, CloneOpenButton })
            c.IsEnabled = true;
        CloneProgress.Visibility = Visibility.Collapsed;
        var status = (CloneStatus.Text, CloneStatus.Foreground, CloneNote.Text, CloneNote.Foreground);
        await RefreshDisks(); // the new partitions, letters and sizes
        (CloneStatus.Text, CloneStatus.Foreground, CloneNote.Text, CloneNote.Foreground) = status; // the outcome stays readable
        Pump();
    }
}
