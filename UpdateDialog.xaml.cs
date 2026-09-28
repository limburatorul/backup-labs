using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Backup;

// "Update available" — and, after an update has landed, the same window as a read-only "What's new".
public partial class UpdateDialog : Window
{
    readonly Updater.Release release;
    readonly CancellationTokenSource cancel = new();

    // owner is null while the main window sits hidden in the tray (WPF refuses a never-shown owner)
    public UpdateDialog(Window? owner, Updater.Release release, bool notesOnly = false)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
        else { WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true; }
        this.release = release;
        SourceInitialized += (_, _) => MainWindow.Glass(this);
        Notes.Text = release.Notes.Trim() == "" ? "No release notes." : release.Notes.Trim();

        if (notesOnly)
        {
            Title = "What's new";
            Heading.Text = $"Backup Labs {release.Version}";
            Sub.Text = "Updated on this machine.";
            UpdateButton.Visibility = Visibility.Collapsed;
            LaterButton.Content = "Close";
        }
        else
        {
            Heading.Text = $"Backup Labs {release.Version} is available";
            Sub.Text = $"You have {Updater.Current}. The installer is {MainWindow.Size(release.AssetSize)}.";
        }
        Closed += (_, _) => cancel.Cancel();
    }

    void Later_Click(object s, RoutedEventArgs e) => Close();

    async void Update_Click(object s, RoutedEventArgs e)
    {
        // quitting mid-run would leave a half-made backup or a half-done restore
        if (Application.Current.MainWindow is MainWindow { Busy: true })
        {
            Status.Text = "Wait for the running backup or restore to finish.";
            return;
        }
        UpdateButton.IsEnabled = LaterButton.IsEnabled = false;
        DownloadBar.Visibility = Visibility.Visible;
        Status.Text = "Downloading…";
        try
        {
            var progress = new Progress<double>(p => { DownloadBar.Value = p; Status.Text = $"Downloading… {p * 100:0}%"; });
            var installer = await Updater.Download(release, progress, cancel.Token);
            Status.Text = "Installing — Backup Labs will restart.";
            Updater.InstallAndRestart(installer);
            ((MainWindow)Application.Current.MainWindow).Exit(); // the installer replaces our files; nothing of ours may stay
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            DownloadBar.Visibility = Visibility.Collapsed;
            Status.Text = ex.Message;
            UpdateButton.IsEnabled = LaterButton.IsEnabled = true;
        }
    }
}
