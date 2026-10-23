using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Backup;

// "Update available": the release's notes from GitHub, and Update now. (What's new after an update is WhatsNewWindow.)
public partial class UpdateDialog : Window
{
    readonly Updater.Release release;
    readonly CancellationTokenSource cancel = new();

    // owner is null while the main window sits hidden in the tray (WPF refuses a never-shown owner)
    public UpdateDialog(Window? owner, Updater.Release release)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
        else { WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = true; }
        this.release = release;
        SourceInitialized += (_, _) => MainWindow.Glass(this);
        Notes.Text = release.Notes.Trim() == "" ? "No release notes." : release.Notes.Trim();
        Heading.Text = $"Backup Labs {release.Version} is available";
        Sub.Text = $"You have {Updater.Current}. The installer is {MainWindow.Size(release.AssetSize)}. Windows will ask for permission to install it.";
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
            Status.Text = "Waiting for Windows' permission…";
            try { Updater.Install(installer); }
            catch (System.ComponentModel.Win32Exception) // "No" on the UAC prompt: this version goes on as it was
            {
                DownloadBar.Visibility = Visibility.Collapsed;
                Status.Text = "Update not installed.";
                UpdateButton.IsEnabled = LaterButton.IsEnabled = true;
                return;
            }
            ((MainWindow)Application.Current.MainWindow).Exit(); // the installer replaces our files and starts the new version
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            DownloadBar.Visibility = Visibility.Collapsed;
            Status.Text = ex.Message;
            UpdateButton.IsEnabled = LaterButton.IsEnabled = true;
        }
    }
}
