using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Backup;

public partial class MainWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunValue = "Backup Labs";
    static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x5C));

    readonly AppSettings app;
    Job job;                        // the one on screen
    Job? running;                   // the one backing up or restoring
    CancellationTokenSource? cts;
    string? note;                   // outcome of the last restore, shown until the next backup
    bool loading;                   // filling the editor: control events must not write back
    readonly List<FileSystemWatcher> watchers = new();
    readonly Forms.NotifyIcon tray;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(20) };
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    UpdateDialog? updateDialog;
    bool exiting;

    public bool Busy => cts != null;

    public MainWindow(EventWaitHandle show)
    {
        InitializeComponent();
        app = Store.Load();
        job = app.Jobs[0];
        VersionText.Text = "v" + Updater.Current;

        var icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        var menu = new Forms.ContextMenuStrip { Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenu()), ForeColor = Drawing.Color.FromArgb(0xE8, 0xEE, 0xF6), ShowImageMargin = false };
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Back up all jobs now", null, (_, _) => { foreach (var j in app.Jobs) j.Requested = true; Pump(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        tray = new Forms.NotifyIcon { Icon = icon, Text = "Backup Labs", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };

        AutostartBox.IsChecked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunValue) != null;
        if (AutostartBox.IsChecked == true) SetAutostart(true); // the exe may have moved since
        Bind(FormatBar, v => { job.Format = v; });
        Bind(CompressionPanel, v => job.Compression = Enum.Parse<CompressionLevel>(v));
        Bind(IntervalPanel, v => { job.IntervalHours = int.Parse(v); job.RetryAfter = default; });
        Bind(KeepBar, v => job.SmartKeep = v == "Smart");

        JobList.ItemsSource = app.Jobs;
        JobList.SelectedItem = job;
        UpdateJobStatuses();
        Watch();

        timer.Tick += (_, _) => { Pump(); Refresh(); };
        timer.Start();
        StartUpdateChecks();
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => Dispatcher.Invoke(() => { ShowWindow(); NewJobFor(Store.Take()); }), null, -1, false);
        SourceInitialized += (_, _) => Glass(this);
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        // a window handle even while hidden in the tray, to hear about drives being plugged in
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd).AddHook(DeviceChanges);
    }

    // ---- the editor ----

    // Segmented controls: save the Tag of the button the user checks.
    void Bind(Panel bar, Action<string> set)
    {
        foreach (RadioButton r in bar.Children)
            r.Checked += (_, _) => { if (loading) return; set((string)r.Tag); Save(); Refresh(); };
    }

    static void Select(Panel bar, string value)
    {
        foreach (RadioButton r in bar.Children) r.IsChecked = (string)r.Tag == value;
    }

    void ShowJob()
    {
        loading = true;
        NameBox.Text = job.Name;
        SourceList.ItemsSource = job.Sources;
        SourceList.Items.Refresh();
        DestBox.Text = job.Destination;
        Select(FormatBar, job.Format);
        Select(CompressionPanel, job.Compression.ToString());
        Select(IntervalPanel, job.IntervalHours.ToString());
        Select(KeepBar, job.SmartKeep ? "Smart" : "Last");
        KeepBox.Text = job.Keep.ToString();
        ExcludeBox.Text = string.Join("; ", job.Exclude);
        RealTimeBox.IsChecked = job.RealTime;
        DriveBox.IsChecked = job.OnDriveConnected;
        OpenFilesBox.IsChecked = job.OpenFiles;
        PasswordBox.Clear();
        PasswordAgain.Clear();
        loading = false;
        Refresh();
    }

    void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobList.SelectedItem is not Job j) return;
        job = j;
        ShowRestore(false);
        ShowJob();
    }

    void Refresh()
    {
        EmptyHint.Visibility = job.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var zip = job.Format != "Folders";
        CompressionLabel.Visibility = CompressionBar.Visibility = zip ? Visibility.Visible : Visibility.Collapsed;
        PasswordLabel.Visibility = PasswordRow.Visibility = job.Format == "Encrypted" ? Visibility.Visible : Visibility.Collapsed;
        PasswordState.Text = job.Password == "" ? "Not set yet." : "Saved.";
        PasswordState.Foreground = job.Password == "" ? Warning : (Brush)FindResource("Dim");
        KeepBox.Visibility = job.SmartKeep ? Visibility.Collapsed : Visibility.Visible;
        KeepText.Text = job.SmartKeep ? "All from the last 24 h, one a day for a month, one a week for a year." : "backups";
        DeleteJobButton.IsEnabled = app.Jobs.Count > 1 && running != job;

        var last = job.LastRun is DateTime l ? l.ToString("d MMM, HH:mm") : "never";
        var when = new List<string>();
        if (job.IntervalHours > 0)
        {
            var due = job.LastRun is DateTime x ? x.AddHours(job.IntervalHours) : DateTime.Now;
            if (job.RetryAfter > due) due = job.RetryAfter;
            when.Add(due <= DateTime.Now ? "within a minute" : due.ToString("d MMM, HH:mm"));
        }
        if (job.RealTime) when.Add("on changes");
        if (job.OnDriveConnected) when.Add("when the drive is plugged in");
        Summary.Text = $"Last backup {last}  ·  Next {(when.Count == 0 ? "manual only" : string.Join(", ", when))}";

        if (cts != null) return;
        var problem = Problem(job);
        Status.Text = problem ?? note ?? job.LastResult;
        Status.Foreground = problem != null ? Warning : (Brush)FindResource("Dim");
        var failed = app.Jobs.FirstOrDefault(j => j.LastResult.StartsWith("Failed"));
        var tip = "Backup Labs · " + (failed != null ? $"{failed.Name}: {failed.LastResult}" : "all good");
        tray.Text = tip.Length > 127 ? tip[..127] : tip; // NotifyIcon's limit
    }

    static string? Problem(Job j) =>
        Engine.Problem(j.Sources, j.Destination)
        ?? (j.Format == "Encrypted" && j.Password == "" ? "Set a password for the encrypted backups." : null);

    void UpdateJobStatuses()
    {
        foreach (var j in app.Jobs)
            j.Status = j == running ? "Running…"
                : Problem(j) is string p ? p
                : j.LastRun is DateTime l ? $"{l:d MMM, HH:mm} · {(j.LastResult.StartsWith("Failed") ? "failed" : j.LastResult.StartsWith("Cancelled") ? "cancelled" : "ok")}"
                : "Not backed up yet";
        JobList.Items.Refresh();
    }

    void Save()
    {
        Store.Save(app);
        UpdateJobStatuses();
    }

    void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name != "" && name != job.Name) { job.Name = name; Save(); }
        NameBox.Text = job.Name;
    }

    void NewJob_Click(object sender, RoutedEventArgs e)
    {
        var j = new Job { Name = $"Backup {app.Jobs.Count + 1}" };
        app.Jobs.Add(j);
        Save();
        JobList.SelectedItem = j;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// A job for what another program handed over (File Labs' "Back up with Backup Labs"): the paths
    /// become its sources and the window opens on it, waiting for a destination.
    internal void NewJobFor(IEnumerable<string> paths)
    {
        var sources = paths.Where(p => Path.IsPathFullyQualified(p) && Path.Exists(p))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) return;
        // the untouched job a fresh install starts with is filled in, not left empty beside a new one
        var j = app.Jobs.FirstOrDefault(x => x.Sources.Count == 0 && x.Destination == "" && x.LastRun == null);
        if (j == null) app.Jobs.Add(j = new Job());
        j.Name = Path.GetFileName(sources[0].TrimEnd('\\')) is { Length: > 0 } name ? name : sources[0];
        j.Sources.AddRange(sources);
        Save();
        Watch();
        job = j;
        JobList.SelectedItem = j; // says nothing when it was selected already, hence the two calls below
        ShowRestore(false);
        ShowJob();
        ShowWindow();
        DestBox.Focus();
    }

    void DeleteJob_Click(object sender, RoutedEventArgs e)
    {
        if (app.Jobs.Count < 2 || running == job) return;
        if (MessageBox.Show(this, $"Delete the job \"{job.Name}\"? Its backups stay where they are.", "Delete job",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        app.Jobs.Remove(job);
        Save();
        Watch();
        JobList.SelectedItem = app.Jobs[0];
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose folders to back up", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames)
            if (!job.Sources.Contains(f, StringComparer.OrdinalIgnoreCase)) job.Sources.Add(f);
        SourceList.Items.Refresh();
        Save();
        Watch();
        Refresh();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in SourceList.SelectedItems.Cast<string>().ToList()) job.Sources.Remove(f);
        SourceList.Items.Refresh();
        Save();
        Watch();
        Refresh();
    }

    void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RemoveButton.IsEnabled = SourceList.SelectedItems.Count > 0;
    void SourceList_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Delete) Remove_Click(sender, e); }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Where should backups be saved?" };
        if (dlg.ShowDialog(this) != true) return;
        DestBox.Text = dlg.FolderName;
        DestBox_LostFocus(sender, e);
    }

    void DestBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var dest = DestBox.Text.Trim();
        if (dest == job.Destination) return;
        job.Destination = dest;
        job.DestVolume = Drives.Volume(dest);
        job.RetryAfter = default;
        Save();
        Refresh();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(job.Destination)) Process.Start("explorer.exe", $"\"{job.Destination}\"");
    }

    // File Labs is a sister app, found through the uninstall entry its installer writes ("never
    // change AppId", its script says); without it the button opens its page.
    void OpenInFileLabs_Click(object sender, RoutedEventArgs e)
    {
        var exe = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7E4C2B9A-3F1D-4C8E-9A6B-51D0C2E8F4A7}_is1", "DisplayIcon", null) as string;
        if (exe == null || !File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo("https://protagonistlabs.app/filelabs/?utm_source=backuplabs&utm_medium=app&utm_campaign=open-in-filelabs") { UseShellExecute = true });
            return;
        }
        if (!Directory.Exists(job.Destination)) return;
        // ArgumentList, not a quoted string: a drive root ends in a backslash, which would escape the closing quote
        var start = new ProcessStartInfo(exe);
        start.ArgumentList.Add(job.Destination);
        Process.Start(start);
    }

    void Log_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(Store.LogPath)) Process.Start(new ProcessStartInfo(Store.LogPath) { UseShellExecute = true });
    }

    void KeepBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(KeepBox.Text, out var n) && n >= 1 && n != job.Keep) { job.Keep = n; Save(); }
        KeepBox.Text = job.Keep.ToString();
    }

    void ExcludeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        job.Exclude = Engine.ParsePatterns(ExcludeBox.Text);
        ExcludeBox.Text = string.Join("; ", job.Exclude);
        Save();
    }

    void SavePassword_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBox.Password.Length < 8) { PasswordState.Text = "Use at least 8 characters."; PasswordState.Foreground = Warning; return; }
        if (PasswordBox.Password != PasswordAgain.Password) { PasswordState.Text = "The two passwords differ."; PasswordState.Foreground = Warning; return; }
        var changed = job.Password != "";
        job.Password = Crypto.Protect(PasswordBox.Password);
        PasswordBox.Clear();
        PasswordAgain.Clear();
        Save();
        Refresh();
        if (changed) PasswordState.Text = "Saved. Older backups still open only with the password they were made with.";
    }

    void Triggers_Click(object sender, RoutedEventArgs e)
    {
        job.RealTime = RealTimeBox.IsChecked == true;
        job.OnDriveConnected = DriveBox.IsChecked == true;
        job.OpenFiles = OpenFilesBox.IsChecked == true;
        if (job.OnDriveConnected && job.DestVolume == "") job.DestVolume = Drives.Volume(job.Destination);
        Save();
        Watch();
        Refresh();
    }

    void Autostart_Click(object sender, RoutedEventArgs e) => SetAutostart(AutostartBox.IsChecked == true);

    static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue(RunValue, false);
    }

    // ---- when jobs run: schedule, changes, a drive plugged in, or by hand; one at a time ----

    bool Due(Job j)
    {
        if (DateTime.Now < j.RetryAfter || Problem(j) != null) return false;
        if (j.OnDriveConnected && !Drives.Present(j)) return false; // waits for the drive, silently
        if (j.Requested) return true;
        if (j.RealTime && j.ChangedAt > j.StartedAt && DateTime.Now - j.ChangedAt >= TimeSpan.FromMinutes(1)) return true;
        return j.IntervalHours > 0 && (j.LastRun is not DateTime last || DateTime.Now - last >= TimeSpan.FromHours(j.IntervalHours));
    }

    void Pump()
    {
        if (cts != null) return;
        var next = app.Jobs.FirstOrDefault(Due);
        if (next != null) _ = RunAsync(next);
    }

    void Watch()
    {
        foreach (var w in watchers) w.Dispose();
        watchers.Clear();
        foreach (var j in app.Jobs.Where(j => j.RealTime))
            foreach (var src in j.Sources.Where(Path.Exists))
            {
                // a single file is watched through its folder, by name
                bool file = File.Exists(src);
                var w = new FileSystemWatcher(file ? Path.GetDirectoryName(src)! : src)
                {
                    IncludeSubdirectories = !file,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                if (file) w.Filter = Path.GetFileName(src);
                FileSystemEventHandler changed = (_, e) => { if (!Engine.ExcludedPath(j.Exclude, e.FullPath)) j.ChangedAt = DateTime.Now; };
                w.Changed += changed; w.Created += changed; w.Deleted += changed;
                w.Renamed += (s, e) => changed(s, e);
                w.Error += (_, _) => j.ChangedAt = DateTime.Now; // buffer overflow: something changed, back up to be sure
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
    }

    IntPtr DeviceChanges(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVTYP_VOLUME = 2;
        if (msg == WM_DEVICECHANGE && (int)wParam == DBT_DEVICEARRIVAL && lParam != IntPtr.Zero && Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_VOLUME)
        {
            var settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; // let Windows finish mounting it
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                foreach (var j in app.Jobs.Where(j => j.OnDriveConnected && Drives.Present(j)))
                    if (j.LastRun is not DateTime last || DateTime.Now - last > TimeSpan.FromMinutes(10)) j.Requested = true;
                Save();
                Pump();
            };
            settle.Start();
        }
        return IntPtr.Zero;
    }

    // ---- running: one backup or restore at a time, sharing the progress bar and the Cancel button ----

    Action<double, string> StartJob(Job j, string start)
    {
        running = j;
        cts = new CancellationTokenSource();
        RunButton.Content = "Cancel";
        RestoreOpenButton.IsEnabled = false;
        ProgressLine.Value = 0;
        ProgressLine.Visibility = Visibility.Visible;
        Status.Foreground = (Brush)FindResource("Dim");
        Status.Text = "Counting files…";
        UpdateJobStatuses();
        Refresh();
        Store.Log(start);
        IProgress<(double f, string t)> p = new Progress<(double f, string t)>(x =>
        {
            ProgressLine.Value = x.f;
            Status.Text = (app.Jobs.Count > 1 ? running!.Name + "  ·  " : "") + $"{x.f:P0}  ·  {x.t}";
        });
        return (f, t) => p.Report((f, t));
    }

    void EndJob()
    {
        cts!.Dispose();
        cts = null;
        running = null;
        RunButton.Content = "Back up now";
        RestoreOpenButton.IsEnabled = true;
        ProgressLine.Visibility = Visibility.Collapsed;
        UpdateJobStatuses();
        Refresh();
    }

    async Task RunAsync(Job j)
    {
        j.Requested = false;
        j.StartedAt = DateTime.Now;
        Drives.Present(j); // follow a USB drive to its current letter
        var progress = StartJob(j, $"{j.Name}: backup started ({j.Format.ToLowerInvariant()})");
        var token = cts!.Token;
        if (j == job) note = null;
        try
        {
            var o = j.Options();
            var r = j.OpenFiles ? await RunElevated(j, progress, token) : await Task.Run(() => Engine.Run(o, Store.Log, progress, token));
            j.LastRun = DateTime.Now;
            j.LastResult = o.Zip == null
                ? $"{r.Copied:N0} copied ({Size(r.BytesCopied)}), {r.Linked:N0} unchanged"
                : $"{r.Copied:N0} files archived ({Size(r.BytesCopied)} → {Size(new FileInfo(r.Snapshot).Length)})";
            if (r.Failed > 0)
            {
                j.LastResult += $", {r.Failed:N0} skipped (see Log)";
                tray.ShowBalloonTip(5000, $"{j.Name} finished with problems", $"{r.Failed:N0} items were skipped. Open the log for details.", Forms.ToolTipIcon.Warning);
            }
            Store.Log($"{j.Name}: backup done: {Path.GetFileName(r.Snapshot)}: {j.LastResult}");
        }
        catch (OperationCanceledException)
        {
            j.LastResult = "Cancelled";
            j.RetryAfter = DateTime.Now.AddHours(Math.Max(1, j.IntervalHours)); // skip this slot, don't restart in a minute
            Store.Log($"{j.Name}: backup cancelled");
        }
        catch (Exception e)
        {
            j.LastResult = "Failed: " + e.Message;
            j.RetryAfter = DateTime.Now.AddMinutes(30);
            Store.Log($"{j.Name}: backup failed: {e}");
            tray.ShowBalloonTip(5000, $"{j.Name}: backup failed", e.Message + " Retrying in 30 minutes.", Forms.ToolTipIcon.Error);
        }
        Save();
        EndJob();
        Pump(); // another job may have come due meanwhile
    }

    // Open files: the same job in an elevated copy of the app (see Elevated in Program.cs).
    async Task<RunResult> RunElevated(Job j, Action<double, string> progress, CancellationToken token)
    {
        string F(string kind) => Elevated.FileFor(j.Id, kind);
        foreach (var kind in new[] { "progress", "cancel", "result" }) File.Delete(F(kind));
        Store.Save(app); // the elevated copy reads the job from the settings file
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--elevated-job {j.Id}") { UseShellExecute = true, Verb = "runas" })!;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Administrator permission was declined, so open files couldn't be included.");
        }
        while (!process.HasExited)
        {
            await Task.Delay(250);
            if (token.IsCancellationRequested && !File.Exists(F("cancel"))) File.WriteAllText(F("cancel"), "");
            try
            {
                var parts = File.ReadAllText(F("progress")).Split('\n', 2);
                if (parts.Length == 2) progress(double.Parse(parts[0], CultureInfo.InvariantCulture), parts[1]);
            }
            catch (Exception e) when (e is IOException or FormatException) { } // not written yet, or mid-write
        }
        var outcome = File.Exists(F("result")) ? JsonSerializer.Deserialize<Elevated.Outcome>(File.ReadAllText(F("result"))) : null;
        foreach (var kind in new[] { "progress", "cancel", "result" }) File.Delete(F(kind));
        if (outcome == null) throw new InvalidOperationException("The administrator copy of Backup Labs ended without a result.");
        if (outcome.Cancelled) throw new OperationCanceledException();
        if (outcome.Error != null) throw new InvalidOperationException(outcome.Error);
        return outcome.Result!;
    }

    internal static string Size(long b) => b < 1024 ? $"{b} B" : b < 1 << 20 ? $"{b / 1024.0:0.#} KB" : b < 1 << 30 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    void Run_Click(object sender, RoutedEventArgs e)
    {
        if (cts != null) { cts.Cancel(); Status.Text = "Cancelling…"; return; }
        job.Requested = true;
        job.RetryAfter = default;
        if (!Due(job)) { Status.Text = job.OnDriveConnected && !Drives.Present(job) ? "The backup drive isn't connected." : Problem(job) ?? ""; Status.Foreground = Warning; job.Requested = false; return; }
        Pump();
    }

    // ---- restore panel ----

    public sealed record Snapshot(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string Label => Engine.Date(Path).ToString("d MMMM yyyy, HH:mm:ss");
        public string Detail => Path.EndsWith(".bkl") ? "encrypted · " + Size(new FileInfo(Path).Length)
                              : Path.EndsWith(".zip") ? "zip · " + Size(new FileInfo(Path).Length) : "folders";
    }

    public sealed record VersionItem(FileVersion V)
    {
        public string Name => V.Name;
        public string Folder => System.IO.Path.GetDirectoryName(V.OriginalPath) ?? "";
        public string When => V.Modified.ToString("d MMM yyyy, HH:mm");
        public string Detail => $"{Size(V.Size)} · backup of {Engine.Date(V.Snapshot):d MMM}";
    }

    void RestoreOpen_Click(object sender, RoutedEventArgs e)
    {
        var list = Engine.Snapshots(job.Destination).Select(p => new Snapshot(p)).Reverse().ToList();
        SnapshotList.ItemsSource = list;
        NoSnapshots.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count > 0) SnapshotList.SelectedIndex = 0;
        RestoreSub.Text = $"{job.Name}  ·  {list.Count} backup{(list.Count == 1 ? "" : "s")} in {job.Destination}";
        RestoreStatus.Text = "";
        WholeTab.IsChecked = true;
        TargetOriginal.IsChecked = true;
        Target_Checked(sender, e);
        ShowRestore(true);
    }

    void ShowRestore(bool on)
    {
        RestorePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MainView.Visibility = on ? Visibility.Hidden : Visibility.Visible;
    }

    void RestoreMode_Checked(object sender, RoutedEventArgs e)
    {
        var file = FileTab.IsChecked == true;
        WholeView.Visibility = file ? Visibility.Collapsed : Visibility.Visible;
        FileView.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        if (file) SearchBox.Focus();
        RestoreSelection_Changed(sender, e);
    }

    void RestoreSelection_Changed(object sender, RoutedEventArgs e) =>
        RestoreStartButton.IsEnabled = (FileTab.IsChecked == true ? VersionList.SelectedItem : SnapshotList.SelectedItem) != null;

    void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Search_Click(sender, e); }

    async void Search_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (query == "") return;
        SearchButton.IsEnabled = false;
        RestoreStatus.Text = "Searching…";
        try
        {
            var snapshots = Engine.Snapshots(job.Destination);
            snapshots.Reverse();
            var password = job.PlainPassword();
            var found = await Task.Run(() => Engine.Find(snapshots, query, password, CancellationToken.None));
            VersionList.ItemsSource = found.Take(2000).Select(v => new VersionItem(v)).ToList();
            SearchHint.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchHint.Text = "Nothing by that name in any backup of this job.";
            var files = found.Select(v => v.Rel.ToLowerInvariant()).Distinct().Count();
            RestoreStatus.Text = found.Count == 0 ? "" : $"{found.Count:N0} version{(found.Count == 1 ? "" : "s")} of {files:N0} file{(files == 1 ? "" : "s")}"
                                                    + (found.Count > 2000 ? ", showing the first 2,000" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or System.Security.Cryptography.CryptographicException)
        {
            RestoreStatus.Text = ex.Message;
        }
        SearchButton.IsEnabled = true;
    }

    void Target_Checked(object sender, RoutedEventArgs e)
    {
        var another = TargetFolder.IsChecked == true;
        RestoreFolderRow.Visibility = another ? Visibility.Visible : Visibility.Collapsed;
        RestoreNote.Foreground = another ? (Brush)FindResource("Dim") : Warning;
        RestoreNote.Text = another
            ? "The backup's layout is recreated inside this folder: drive letter first, then the original path."
            : "Files that differ are overwritten with the backed-up version. Files that aren't in the backup are left alone.";
    }

    void RestoreBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Restore into which folder?" };
        if (dlg.ShowDialog(this) == true) RestoreBox.Text = dlg.FolderName;
    }

    void RestoreBack_Click(object sender, RoutedEventArgs e) => ShowRestore(false);

    void RestoreStart_Click(object sender, RoutedEventArgs e)
    {
        if (cts != null) return;
        string? into = null;
        if (TargetFolder.IsChecked == true)
        {
            into = RestoreBox.Text.Trim();
            if (!Path.IsPathFullyQualified(into)) { RestoreNote.Text = "Choose a full folder path, like D:\\Restored."; return; }
        }
        if (FileTab.IsChecked == true)
        {
            if (VersionList.SelectedItem is not VersionItem item) return;
            if (into == null && MessageBox.Show(this, $"Replace {Path.Combine(item.Folder, item.Name)} with the version from {item.When}?",
                    "Restore", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                var to = Engine.RestoreFile(item.V, into, job.PlainPassword());
                RestoreStatus.Text = "Restored to " + to;
                Store.Log($"{job.Name}: restored {to} from {Path.GetFileName(item.V.Snapshot)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                RestoreStatus.Text = "Could not restore: " + ex.Message;
            }
            return;
        }
        if (SnapshotList.SelectedItem is not Snapshot snap) return;
        if (into == null && MessageBox.Show(this, $"Overwrite changed files in their original locations with the versions from {snap.Label}?",
                "Restore", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ShowRestore(false);
        _ = RestoreAsync(job, snap, into);
    }

    async Task RestoreAsync(Job j, Snapshot snap, string? into)
    {
        var progress = StartJob(j, $"{j.Name}: restore of {snap.Name} to {into ?? "original locations"} started");
        var token = cts!.Token;
        try
        {
            var password = snap.Path.EndsWith(".bkl") ? j.PlainPassword() : null;
            var r = await Task.Run(() => Engine.Restore(snap.Path, into, password, Store.Log, progress, token));
            note = $"Restored {r.Copied:N0} files ({Size(r.BytesCopied)}), {r.Linked:N0} already up to date"
                 + (r.Failed > 0 ? $", {r.Failed:N0} failed (see Log)" : "");
        }
        catch (OperationCanceledException) { note = "Restore cancelled; files restored so far stay in place"; }
        catch (Exception e) { note = "Restore failed: " + e.Message; Store.Log($"{j.Name}: restore failed: {e}"); }
        Store.Log($"{j.Name}: {note}");
        EndJob();
        Pump();
    }

    // ---- self-update, File Labs' schedule: 3 s after launch, then every 10 minutes ----

    void StartUpdateChecks()
    {
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await ShowWhatsNew();
            await CheckForUpdate(manual: false);
        };
        first.Start();
        updateTimer.Tick += async (_, _) => await CheckForUpdate(manual: false);
        updateTimer.Start();
    }

    /// Returns what to tell the user (only used by the manual check).
    async Task<string?> CheckForUpdate(bool manual)
    {
        if (updateDialog != null) { updateDialog.Activate(); return null; }
        var release = await Updater.Check(manual);
        if (release == null) return manual ? "Up to date" : null;
        if (updateDialog != null) return null; // a manual and a timed check can race over the await
        updateDialog = new UpdateDialog(IsVisible ? this : null, release);
        updateDialog.Closed += (_, _) => { Updater.Dismiss(release); updateDialog = null; };
        updateDialog.Show();
        return null;
    }

    // First launch after an update: the release notes for the version now running.
    async Task ShowWhatsNew()
    {
        var current = Updater.Current.ToString();
        if (app.LastSeenVersion == current) return;
        bool upgraded = app.LastSeenVersion != "";       // empty = fresh install, nothing to show
        app.LastSeenVersion = current;
        Save();
        if (!upgraded) return;
        // Check() only returns something newer than us; right after updating, the latest IS us.
        var latest = await Updater.Latest();
        if (latest?.Version.ToString() == current) new UpdateDialog(IsVisible ? this : null, latest, notesOnly: true).Show();
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateCheckButton.Content = "Checking…";
        var answer = await CheckForUpdate(manual: true);
        UpdateCheckButton.IsEnabled = true;
        UpdateCheckButton.Content = answer ?? "Check for updates";
        if (answer == null) return;
        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        back.Tick += (_, _) => { back.Stop(); UpdateCheckButton.Content = "Check for updates"; };
        back.Start();
    }

    // ---- window and tray ----

    void ShowWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal void Exit()
    {
        exiting = true;
        cts?.Cancel(); // an unfinished backup's .partial is cleaned up by the next run
        foreach (var w in watchers) w.Dispose();
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }

    // ---- look: DWM acrylic behind a transparent WPF window (see Branding → WPF) ----

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    struct Margins { public int Left, Right, Top, Bottom; }

    static int Dwm(IntPtr h, int attr, int value) => DwmSetWindowAttribute(h, attr, ref value, sizeof(int));

    internal static void Glass(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent; // else partial alpha composes over black
        Dwm(h, 20, 1);                                   // dark title bar
        var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(h, ref m);
        if (Dwm(h, 38, 3) != 0)                          // acrylic; missing before Windows 11 22H2
            w.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0D, 0x13));
        Dwm(h, 33, 2);                                   // rounded corners
        Dwm(h, 34, 0x003A2F2A);                          // border, 0x00BBGGRR
        Dwm(h, 36, 0x00F6EEE8);                          // caption text
    }

    // Tray menu in the app palette; the gradient overrides are what stop the blue Windows hover.
    sealed class DarkMenu : Forms.ProfessionalColorTable
    {
        static readonly Drawing.Color Bg = Drawing.Color.FromArgb(0x14, 0x18, 0x21), Hi = Drawing.Color.FromArgb(0x24, 0x34, 0x52), Line = Drawing.Color.FromArgb(0x2A, 0x2F, 0x3A);
        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hi;
        public override Drawing.Color MenuItemSelected => Hi;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hi;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hi;
        public override Drawing.Color MenuItemPressedGradientBegin => Hi;
        public override Drawing.Color MenuItemPressedGradientMiddle => Hi;
        public override Drawing.Color MenuItemPressedGradientEnd => Hi;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Line;
    }
}
