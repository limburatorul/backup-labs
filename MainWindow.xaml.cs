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
using System.Windows.Documents;
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
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(60) };
    UpdateDialog? updateDialog;
    bool exiting;
    string? fullDrive; // the drive the last "backup drive is full" balloon was about, for its click

    public bool Busy => cts != null;

    public MainWindow(EventWaitHandle show)
    {
        InitializeComponent();
        app = Store.Load();
        job = app.Jobs[0];
        VersionText.Text = "v" + Updater.Current;
        Ecosystem.WriteCard(Updater.Current.ToString());
        Ecosystem.WriteJobs(app.Jobs.Select(j => j.Shared())); // a PC where they were never shared yet
        ShowWorksWith();

        var icon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        var menu = TrayMenu.Create();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        // only jobs that can run now: a request left on an unfinished job started it the moment it was finished
        menu.Items.Add("Back up all jobs now", null, (_, _) => { foreach (var j in app.Jobs.Where(j => Problem(j) == null)) j.Requested = true; Pump(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
        tray = new Forms.NotifyIcon { Icon = icon, Text = "Backup Labs", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowWindow(); };
        tray.BalloonTipClicked += (_, _) => { if (fullDrive != null && Ecosystem.Exe("spacescan") is string exe) OpenInSpaceScan(exe, fullDrive); fullDrive = null; };
        tray.BalloonTipClosed += (_, _) => fullDrive = null;

        AutostartBox.IsChecked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunValue) != null;
        if (AutostartBox.IsChecked == true) SetAutostart(true); // the exe may have moved since
        Bind(FormatBar, v => { job.Format = v; });
        Bind(CompressionPanel, v => job.Compression = Enum.Parse<CompressionLevel>(v));
        Bind(IntervalPanel, v => { job.IntervalHours = int.Parse(v); Rescheduled(); });
        Bind(DayPanel, v => { job.Day = (DayOfWeek)int.Parse(v); Rescheduled(); });
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
        Select(DayPanel, ((int)job.Day).ToString());
        TimeBox.Text = Clock(job.At);
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

        var timed = job.IntervalHours is 24 or 168;
        WhenRow.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        DayBar.Visibility = job.IntervalHours == 168 ? Visibility.Visible : Visibility.Collapsed;

        var last = job.LastRun is DateTime l ? l.ToString("d MMM, HH:mm") : "never";
        var when = new List<string>();
        if (Schedule.Next(job.IntervalHours, job.At, job.Day, job.LastRun, job.Since) is DateTime due)
        {
            if (job.RetryAfter > due) due = job.RetryAfter;
            when.Add(due <= DateTime.Now ? "within a minute" : due.ToString(job.IntervalHours == 168 ? "ddd d MMM, HH:mm" : "d MMM, HH:mm"));
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
        Ecosystem.WriteJobs(app.Jobs.Select(j => j.Shared()));
        UpdateJobStatuses();
    }

    // A new schedule counts from now: a time already past today waits for tomorrow instead of running at once.
    void Rescheduled()
    {
        job.Since = DateTime.Now;
        job.RetryAfter = default;
    }

    // What or where changed on a job that never ran: it counts from now, so finishing a job's setup never starts it.
    static void Edited(Job j)
    {
        if (j.LastRun == null) j.Since = DateTime.Now;
    }

    static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    void TimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 24-hour time: 21:30, 9:05, or just 21
        if (TimeSpan.TryParseExact(TimeBox.Text.Trim(), new[] { @"h\:mm", @"hh\:mm", "%h", "hh" }, CultureInfo.InvariantCulture, out var t)
            && t < TimeSpan.FromDays(1) && (int)t.TotalMinutes != job.At)
        {
            job.At = (int)t.TotalMinutes;
            Rescheduled();
            Save();
        }
        TimeBox.Text = Clock(job.At);
        Refresh();
    }

    void TimeBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) TimeBox_LostFocus(sender, e); }

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
    /// become its sources and the window opens on it, waiting for a destination. A line holding a whole
    /// job (`--job-from`, see Ecosystem) creates that job, or updates it when its id is already here.
    internal void NewJobFor(IEnumerable<string> items)
    {
        var list = items.ToList();
        foreach (var spec in list.Where(l => l.StartsWith('{')).Select(Ecosystem.ReadSpec).OfType<JobSpec>()) JobFrom(spec);
        // "job:<id>": that job on screen, as it is
        if (list.LastOrDefault(l => l.StartsWith("job:")) is string ask && app.Jobs.FirstOrDefault(x => x.Id == ask[4..]) is Job asked)
        {
            job = asked;
            JobList.SelectedItem = asked;
            ShowRestore(false);
            ShowJob();
            ShowWindow();
        }
        // "open:<file>": a backup opened from Explorer (checked in Program: an existing .bkl or .zip)
        if (list.LastOrDefault(l => l.StartsWith("open:")) is string file) OpenBackup(file[5..]);
        var sources = Ecosystem.Existing(list.Where(l => !l.StartsWith('{') && !l.StartsWith("job:") && !l.StartsWith("open:")));
        if (sources.Count == 0) return;
        var j = BlankJob();
        j.Name = FolderName(sources[0]);
        j.Sources.AddRange(sources);
        Bring(j);
    }

    void JobFrom(JobSpec spec)
    {
        var j = spec.Id == null ? null : app.Jobs.FirstOrDefault(x => x.Id == spec.Id);
        if (j == null)
        {
            j = BlankJob();
            if (spec.Id != null) j.Id = spec.Id;
            j.Name = spec.Name != "" ? spec.Name : FolderName(spec.Sources[0]);
        }
        // the sources are replaced; destination, schedule and name stay the user's
        j.Sources.Clear();
        j.Sources.AddRange(spec.Sources);
        Bring(j);
    }

    // the untouched job a fresh install starts with is filled in, not left empty beside a new one
    Job BlankJob()
    {
        var j = app.Jobs.FirstOrDefault(x => x.Sources.Count == 0 && x.Destination == "" && x.LastRun == null);
        if (j == null) app.Jobs.Add(j = new Job());
        return j;
    }

    static string FolderName(string path) => Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;

    // saved, selected and on screen, focus on the destination
    void Bring(Job j)
    {
        Edited(j);
        Save();
        Watch();
        job = j;
        JobList.SelectedItem = j; // says nothing when it was selected already, hence the two calls below
        ShowRestore(false);
        ShowJob();
        ShowWindow();
        DestBox.Focus();
    }

    // One job for the settings and data of our apps on this PC, waiting for a destination; a second click opens it.
    void AppsJob_Click(object sender, RoutedEventArgs e)
    {
        var j = app.Jobs.FirstOrDefault(x => x.Id == Ecosystem.AppsJobId);
        if (j == null)
        {
            var (sources, exclude) = Ecosystem.AppsJob();
            j = BlankJob();
            j.Id = Ecosystem.AppsJobId;
            j.Name = "Protagonist Labs apps";
            j.Sources = sources;
            j.Exclude = exclude;
            j.IntervalHours = 24;
            Store.Log($"{j.Name}: job made with {sources.Count} folders, {exclude.Count} caches left out");
        }
        Bring(j);
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
        if (dlg.ShowDialog(this) == true) AddSources(dlg.FolderNames);
    }

    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose files to back up", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddSources(dlg.FileNames);
    }

    void AddSources(IEnumerable<string> paths)
    {
        foreach (var f in paths)
            if (!job.Sources.Contains(f, StringComparer.OrdinalIgnoreCase)) job.Sources.Add(f);
        Edited(job);
        SourceList.Items.Refresh();
        Save();
        Watch();
        Refresh();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in SourceList.SelectedItems.Cast<string>().ToList()) job.Sources.Remove(f);
        Edited(job);
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
        Edited(job);
        Save();
        Refresh();
    }

    // In File Labs, the sister app, when it is installed: found through the uninstall entry its
    // installer writes ("never change AppId", its script says). Otherwise in whatever opens folders
    // on this PC, which is File Explorer unless the user chose something else.
    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(job.Destination)) return;
        var exe = FileLabsExe();
        if (exe == null)
        {
            Process.Start(new ProcessStartInfo(job.Destination) { UseShellExecute = true });
            return;
        }
        // ArgumentList, not a quoted string: a drive root ends in a backslash, which would escape the closing quote
        var start = new ProcessStartInfo(exe);
        start.ArgumentList.Add(job.Destination);
        Process.Start(start);
    }

    static string? FileLabsExe() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7E4C2B9A-3F1D-4C8E-9A6B-51D0C2E8F4A7}_is1", "DisplayIcon", null) is string exe && File.Exists(exe) ? exe : null;

    // ---- Works with: the sister apps that add something here, one quiet line each ----

    static readonly (string Id, string Name)[] Paid = { ("shelf", "Shelf"), ("stash", "Stash"), ("frostpane", "Frostpane"), ("reel", "Reel"), ("planner", "Planner"), ("reminder", "Reminder") };

    static string Page(string id, string? code = null) =>
        $"https://protagonistlabs.app/{id}/?{(code == null ? "" : $"code={code}&")}utm_source=backuplabs&utm_medium=app&utm_campaign=works-with";

    void ShowWorksWith()
    {
        var (installed, bought) = Ecosystem.Sisters();
        AppsJobButton.Visibility = installed.Any(id => id != "backuplabs") || app.Jobs.Any(j => j.Id == Ecosystem.AppsJobId) ? Visibility.Visible : Visibility.Collapsed;
        WorksWith.Children.Clear();
        TextBlock Line()
        {
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            WorksWith.Children.Add(t);
            return t;
        }
        // installed: the name alone; missing: the name links to its page
        void Sister(string id, string name, bool here, string does, string would)
        {
            var t = Line();
            t.Inlines.Add(here ? new Run(name) : SiteLink(name, Page(id)));
            t.Inlines.Add(" · " + (here ? does : would));
        }
        Sister("shelf", "Shelf", installed.Contains("shelf"), "keeps the game saves job in step with your games", "would back up every game's saves in one job");
        Sister("stash", "Stash", installed.Contains("stash"), "its Thunderbird and Firefox backups get versions here, on another disk or NAS", "its Thunderbird and Firefox backups would get versions here, on another disk or NAS");
        Sister("filelabs", "File Labs", installed.Contains("filelabs") || FileLabsExe() != null, "opens your backups", "would open your backups");
        // SpaceScan: its name opens it on the job's backup drive, when it is here
        var space = Line();
        if (Ecosystem.Exe("spacescan") is string spaceScan)
        {
            var link = new Hyperlink(new Run("SpaceScan")) { Style = (Style)FindResource("Quiet") };
            link.Click += (_, _) => OpenInSpaceScan(spaceScan, job.Destination);
            space.Inlines.Add(link);
            space.Inlines.Add(" · shows what fills the backup drive, and names your backup folders");
        }
        else
        {
            space.Inlines.Add(SiteLink("SpaceScan", Page("spacescan")));
            space.Inlines.Add(" · would show what fills the backup drive");
        }

        var offers = Paid.Where(p => !installed.Contains(p.Id)).Take(4).ToList();
        if (bought && offers.Count > 0)
        {
            var t = Line();
            t.Inlines.Add("Owners of one of our apps get 30% off the others: ");
            for (int i = 0; i < offers.Count; i++)
            {
                if (i > 0) t.Inlines.Add(" · ");
                t.Inlines.Add(SiteLink(offers[i].Name, Page(offers[i].Id, offers[i].Id == "shelf" ? "SECOND30SHELF" : "SECOND30")));
            }
        }
    }

    /// SpaceScan scanning the backup folder, or its drive when the folder isn't there (yet).
    static void OpenInSpaceScan(string exe, string destination)
    {
        var path = Directory.Exists(destination) ? destination : Path.IsPathFullyQualified(destination) ? Path.GetPathRoot(destination) : null;
        var start = new ProcessStartInfo(exe);
        if (path != null && Directory.Exists(path)) start.ArgumentList.Add(path); // ArgumentList: a drive root ends in a backslash
        Process.Start(start);
    }

    Hyperlink SiteLink(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { Style = (Style)FindResource("Quiet") };
        link.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return link;
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
        if (Ecosystem.TestRoot != null) return; // a test copy must not repoint the user's own autostart entry
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
        // changes made while the job was being set up don't count
        if (j.RealTime && j.ChangedAt > j.StartedAt && j.ChangedAt > j.Since && DateTime.Now - j.ChangedAt >= TimeSpan.FromMinutes(1)) return true;
        return Schedule.Next(j.IntervalHours, j.At, j.Day, j.LastRun, j.Since) is DateTime next && DateTime.Now >= next;
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
        const int WM_DEVICECHANGE = 0x219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_VOLUME = 2;
        if (msg == WM_DEVICECHANGE && (int)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE && ClonePanel.Visibility == Visibility.Visible && !cloning)
        {   // a card or stick in or out: the clone panel's lists follow
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            later.Tick += (_, _) => { later.Stop(); if (!cloning) _ = RefreshDisks(); };
            later.Start();
        }
        if (msg == WM_DEVICECHANGE && (int)wParam == DBT_DEVICEARRIVAL && lParam != IntPtr.Zero && Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_VOLUME)
        {
            var settle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }; // let Windows finish mounting it
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                foreach (var j in app.Jobs.Where(j => j.OnDriveConnected && Problem(j) == null && Drives.Present(j)))
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
        // the job Shelf made follows Shelf's list of save folders, so a game installed since is in it
        if (j.Id == Ecosystem.ShelfJobId && Ecosystem.ShelfSaves() is { } saves && !saves.SequenceEqual(j.Sources, StringComparer.OrdinalIgnoreCase))
        {
            j.Sources.Clear();
            j.Sources.AddRange(saves);
            if (j == job) SourceList.Items.Refresh();
            Watch();
            Store.Log($"{j.Name}: {saves.Count} save folders and files from Shelf");
        }
        // the job Stash made follows Stash's backup folder, moved there or not
        var before = string.Join("; ", j.Sources);
        if (Ecosystem.FollowStash(j.Id, j.Sources))
        {
            if (j == job) SourceList.Items.Refresh();
            Watch();
            Store.Log($"{j.Name}: Stash's backup folder is now {j.Sources[0]} (was {(before == "" ? "nothing" : before)})");
        }
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
        catch (Exception e) when (Engine.DiskFull(e) || e.Message == Elevated.DriveFull)
        {   // retrying in half an hour won't help: say so, and offer SpaceScan to see what fills the drive
            j.LastResult = "Failed: the backup drive is full";
            j.RetryAfter = DateTime.Now.AddHours(Math.Max(1, j.IntervalHours));
            Store.Log($"{j.Name}: backup failed: the backup drive is full ({j.Destination})");
            var spaceScan = Ecosystem.Exe("spacescan") != null;
            fullDrive = spaceScan ? j.Destination : null;
            tray.ShowBalloonTip(8000, $"{j.Name}: the backup drive is full",
                spaceScan ? "Click to see what fills it, in SpaceScan." : "Free some space on it, or keep fewer backups.", Forms.ToolTipIcon.Error);
        }
        catch (Exception e)
        {
            j.LastResult = "Failed: " + e.Message;
            j.RetryAfter = DateTime.Now.AddMinutes(30);
            Store.Log($"{j.Name}: backup failed: {e}");
            fullDrive = null;
            tray.ShowBalloonTip(5000, $"{j.Name}: backup failed", e.Message + " Retrying in 30 minutes.", Forms.ToolTipIcon.Error);
        }
        Save();
        EndJob();
        Pump(); // another job may have come due meanwhile
    }

    // Open files: the same job in an elevated copy of the app (see Elevated in Program.cs).
    async Task<RunResult> RunElevated(Job j, Action<double, string> progress, CancellationToken token)
    {
        Store.Save(app); // the elevated copy reads the job from the settings file
        return (await Elevate(j.Id, $"--elevated-job {j.Id}", progress, token,
            "Administrator permission was declined, so open files couldn't be included.")).Result!;
    }

    /// Runs the elevated copy of the app (one UAC prompt) and follows it through the files it writes.
    async Task<Elevated.Outcome> Elevate(string id, string args, Action<double, string> progress, CancellationToken token, string declined)
    {
        string F(string kind) => Elevated.FileFor(id, kind);
        foreach (var kind in new[] { "progress", "cancel", "result" }) File.Delete(F(kind));
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" })!;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(declined);
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
        foreach (var kind in new[] { "progress", "cancel", "result", "request" }) File.Delete(F(kind));
        if (outcome == null) throw new InvalidOperationException("The administrator copy of Backup Labs ended without a result.");
        if (outcome.Cancelled) throw new OperationCanceledException();
        if (outcome.Error != null) throw new InvalidOperationException(outcome.Error);
        return outcome;
    }

    internal static string Size(long b) => Engine.Size(b);

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
        public string Label => When(Path, "d MMMM yyyy, HH:mm:ss");
        public bool Encrypted { get; } = Sealed(Path); // read once: it opens the file
        public string Detail => !Engine.IsArchive(Path) ? "folders" : (Encrypted ? "encrypted · " : "zip · ") + Size(new FileInfo(Path).Length);
    }

    // the header, not the name; a file that can't be read right now (a drive going away) lists as plain and says why at restore
    static bool Sealed(string snapshot)
    {
        try { return Engine.IsArchive(snapshot) && Engine.Encrypted(snapshot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // a backup opened from Explorer may have been renamed: then its name stands in for its date
    static string When(string snapshot, string format) =>
        Engine.IsStamp(Path.GetFileNameWithoutExtension(snapshot)) ? Engine.Date(snapshot).ToString(format) : Path.GetFileName(snapshot);

    public sealed record VersionItem(FileVersion V)
    {
        public string Name => V.Name;
        public string Folder => System.IO.Path.GetDirectoryName(V.OriginalPath) ?? "";
        public string When => V.Modified.ToString("d MMM yyyy, HH:mm");
        public string Detail => $"{Size(V.Size)} · backup of {When(V.Snapshot, "d MMM")}";
    }

    // A backup opened from Explorer, shown alone in the Restore panel wherever it is; null: the job's backups.
    // `opener` is the job whose destination holds it, if any: only then are the original places offered,
    // and its saved password is tried when none is typed.
    string? opened;
    Job? opener;

    void RestoreOpen_Click(object sender, RoutedEventArgs e)
    {
        opened = null;
        var list = Engine.Snapshots(job.Destination).Select(p => new Snapshot(p)).Reverse().ToList();
        ShowSnapshots(list, $"{job.Name}  ·  {list.Count} backup{(list.Count == 1 ? "" : "s")} in {job.Destination}", "No backups in the destination yet.");
    }

    void OpenBackup(string file)
    {
        opened = file;
        opener = app.Jobs.FirstOrDefault(j => Path.IsPathFullyQualified(j.Destination)
            && Engine.Snapshots(j.Destination).Any(s => string.Equals(Path.GetFullPath(s), file, StringComparison.OrdinalIgnoreCase)));
        if (opener != null && opener != job) { job = opener; JobList.SelectedItem = opener; ShowJob(); }
        var list = new List<Snapshot>();
        string? problem = null;
        try
        {
            if (!Engine.Encrypted(file)) Engine.Probe(file, null); // an encrypted one is checked with its password, at restore
            list.Add(new Snapshot(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { problem = ex.Message; }
        ShowSnapshots(list, $"{Path.GetFileName(file)}  ·  {Path.GetDirectoryName(file)}" + (opener != null ? $"  ·  a backup of {opener.Name}" : ""), problem ?? "");
        ShowWindow();
        if (OpenPasswordRow.Visibility == Visibility.Visible) OpenPassword.Focus();
    }

    void ShowSnapshots(List<Snapshot> list, string sub, string empty)
    {
        SnapshotList.ItemsSource = list;
        NoSnapshots.Text = empty;
        NoSnapshots.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (list.Count > 0) SnapshotList.SelectedIndex = 0;
        RestoreSub.Text = sub;
        RestoreStatus.Text = "";
        VersionList.ItemsSource = null;
        SearchHint.Text = opened == null ? "Every version of a file across all backups of this job." : "Every file in this backup whose name contains this.";
        SearchHint.Visibility = Visibility.Visible;
        OpenPassword.Clear();
        OpenPasswordRow.Visibility = opened != null && list.Count > 0 && list[0].Encrypted ? Visibility.Visible : Visibility.Collapsed;
        WholeTab.IsChecked = true;
        // the original places come from the archive itself; offered only for a job's own backup, not a file from anywhere
        var original = opened == null || opener != null;
        TargetOriginal.Visibility = original ? Visibility.Visible : Visibility.Collapsed;
        (original ? TargetOriginal : TargetFolder).IsChecked = true;
        Target_Checked(this, new RoutedEventArgs());
        ShowRestore(true);
    }

    List<string> Shown()
    {
        if (opened != null) return new() { opened };
        var list = Engine.Snapshots(job.Destination);
        list.Reverse();
        return list;
    }

    // the opened backup's typed password, else the job's saved one
    string? RestorePassword() => opened != null && OpenPassword.Password != "" ? OpenPassword.Password : (opened == null ? job : opener)?.PlainPassword();

    void ShowRestore(bool on)
    {
        RestorePanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!cloning) ClonePanel.Visibility = Visibility.Collapsed; // a running clone keeps its panel
        MainView.Visibility = on || cloning ? Visibility.Hidden : Visibility.Visible;
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
            var snapshots = Shown();
            var password = RestorePassword();
            var found = await Task.Run(() => Engine.Find(snapshots, query, password, CancellationToken.None));
            VersionList.ItemsSource = found.Take(2000).Select(v => new VersionItem(v)).ToList();
            SearchHint.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchHint.Text = opened == null ? "Nothing by that name in any backup of this job." : "Nothing by that name in this backup.";
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

    async void RestoreStart_Click(object sender, RoutedEventArgs e)
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
                var to = Engine.RestoreFile(item.V, into, RestorePassword());
                RestoreStatus.Text = "Restored to " + to;
                Store.Log($"{(opened == null ? job : opener)?.Name ?? "Opened backup"}: restored {to} from {item.V.Snapshot}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                RestoreStatus.Text = "Could not restore: " + ex.Message;
            }
            return;
        }
        if (SnapshotList.SelectedItem is not Snapshot snap) return;
        var password = snap.Encrypted ? RestorePassword() : null;
        if (opened != null)
        {   // a wrong password is said here, where it can be typed again, not after the panel has closed
            RestoreStartButton.IsEnabled = false;
            RestoreStatus.Text = "Checking…";
            try { await Task.Run(() => Engine.Probe(snap.Path, password)); RestoreStatus.Text = ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException) { RestoreStatus.Text = ex.Message; return; }
            finally { RestoreStartButton.IsEnabled = true; }
            if (cts != null) { RestoreStatus.Text = "A backup started meanwhile: restore when it ends."; return; }
        }
        if (into == null && MessageBox.Show(this, $"Overwrite changed files in their original locations with the versions from {snap.Label}?",
                "Restore", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ShowRestore(false);
        // a backup from anywhere has no job: a stand-in named after the file carries the restore and its log lines
        _ = RestoreAsync(opened == null ? job : opener ?? new Job { Name = snap.Name }, snap, into, password);
    }

    async Task RestoreAsync(Job j, Snapshot snap, string? into, string? password)
    {
        var progress = StartJob(j, $"{j.Name}: restore of {snap.Name} to {into ?? "original locations"} started");
        var token = cts!.Token;
        try
        {
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

    // ---- self-update, File Labs' schedule: 3 s after launch, then every 60 minutes ----

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
        Updater.Release? release;
        try { release = await Updater.Check(manual); }
        catch (Exception ex) when (Updater.Failed(ex))
        {
            return manual ? Updater.CheckFailed : null; // the automatic check stays quiet and tries again next hour
        }
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
        Updater.Release? latest;
        try { latest = await Updater.Latest(); }
        catch (Exception ex) when (Updater.Failed(ex)) { return; } // offline: the notes just aren't shown
        if (latest?.Version.ToString() == current) new UpdateDialog(IsVisible ? this : null, latest, notesOnly: true).Show();
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckButton.IsEnabled = false;
        UpdateCheckButton.Content = "Checking…";
        var answer = await CheckForUpdate(manual: true);
        UpdateCheckButton.IsEnabled = true;
        // the sidebar is narrow: a failure is said short on the button, in full in its tooltip
        UpdateCheckButton.Content = answer == Updater.CheckFailed ? "Couldn't check, try later" : answer ?? "Check for updates";
        UpdateCheckButton.ToolTip = answer == Updater.CheckFailed ? answer : null;
        if (answer == null) return;
        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        back.Tick += (_, _) => { back.Stop(); UpdateCheckButton.Content = "Check for updates"; UpdateCheckButton.ToolTip = null; };
        back.Start();
    }

    // ---- window and tray ----

    void ShowWindow()
    {
        ShowWorksWith(); // a sister installed since the last look shows up
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal void Exit()
    {
        exiting = true;
        cts?.Cancel(); // an unfinished backup's .partial is cleaned up by the next run
        // an elevated clone outlives us: tell it to stop, rather than leave it writing a disk unattended
        if (cloneId != null) try { File.WriteAllText(Elevated.FileFor(cloneId, "cancel"), ""); } catch (IOException) { }
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
}
