using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Backup;

/// What's new, as the studio's other apps show it (Uproot's window, in Backup Labs' colours): every release in a list
/// on the left, under the month it came out, and the chosen one on the right under its 960 x 170 banner. The notes are
/// whatsnew.txt, built into the exe, so it works offline and shows every release. A release with a picture of its own
/// (WhatsNew\x.y.jpg, built in) shows it; the others get the app's colours and a line of backups drawn from the version.
public sealed class WhatsNewWindow : Window
{
    internal sealed record Release(string Version, DateTime Date, string Title, List<string> Lines);

    static WhatsNewWindow? open;
    readonly List<Release> releases = Load();
    readonly StackPanel nav = new();
    readonly Border banner = new() { CornerRadius = new CornerRadius(10), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel notes = new();
    readonly ScrollViewer notesScroll;
    int shown = -1;

    /// Brings the window up, or to the front when it is already open. Null when Backup Labs sits in the tray: WPF
    /// refuses an owner that was never shown.
    public static void Open(Window? owner)
    {
        if (open != null) { open.Activate(); return; }
        open = new WhatsNewWindow(owner);
        open.Closed += (_, _) => open = null;
        open.Show();
    }

    /// Once after an update; a first install has nothing new to tell. Marked seen when the window closes, not when it
    /// opens, so one that never made it to the screen comes back next time.
    public static void OpenOnceAfterUpdate(AppSettings app, Window? owner, Action save)
    {
        var current = Updater.Current.ToString();
        if (app.LastSeenVersion == current) return;
        if (app.LastSeenVersion == "") { app.LastSeenVersion = current; save(); return; }
        Open(owner);
        open!.Closed += (_, _) => { app.LastSeenVersion = current; save(); };
    }

    internal static List<Release> Load() => Parse(new StreamReader(typeof(WhatsNewWindow).Assembly.GetManifestResourceStream("whatsnew.txt")!).ReadToEnd());

    /// "## 1.6.0 | 2026-10-23 | Title", then the release's lines; "# " lines are comments.
    internal static List<Release> Parse(string text)
    {
        var list = new List<Release>();
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("## ") && line[3..].Split(" | ", 3) is [var v, var d, var t])
                list.Add(new Release(v.Trim(), DateTime.ParseExact(d.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture), t.Trim(), new()));
            else if (line.StartsWith("# ") || line.Trim() == "" || list.Count == 0) continue;
            else list[^1].Lines.Add(line);
        }
        return list;
    }

    WhatsNewWindow(Window? owner)
    {
        if (owner != null) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "What’s new — Backup Labs";
        Width = 1240; Height = 720; MinWidth = 700; MinHeight = 420; // room for the list and a 960 x 170 banner
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        SetResourceReference(ForegroundProperty, "Text");
        SourceInitialized += (_, _) => MainWindow.Glass(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Down or Key.Up) { Show(Math.Clamp(shown + (e.Key == Key.Down ? 1 : -1), 0, releases.Count - 1)); e.Handled = true; }
        };

        var close = new Button { Content = "Close", Style = (Style)FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => Close();

        var navScroll = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 200, Margin = new Thickness(0, 0, 16, 0) };
        notesScroll = new ScrollViewer { Content = notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        var right = new DockPanel();
        DockPanel.SetDock(banner, Dock.Top);
        right.Children.Add(banner);
        right.Children.Add(notesScroll);

        var body = new DockPanel { Margin = new Thickness(18, 16, 18, 14) };
        DockPanel.SetDock(close, Dock.Bottom);
        DockPanel.SetDock(navScroll, Dock.Left);
        body.Children.Add(close);
        body.Children.Add(navScroll);
        body.Children.Add(right);
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x0A, 0x0D, 0x13)), Child = body }; // the main window's floor

        BuildNav();
        // the version running, which is the newest unless this build is older than its own notes
        Show(Math.Max(0, releases.FindIndex(r => r.Version == Updater.Current.ToString())));
    }

    void BuildNav()
    {
        string? month = null;
        for (int i = 0; i < releases.Count; i++)
        {
            var r = releases[i];
            var m = r.Date.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
            if (m != month)
            {
                month = m;
                nav.Children.Add(Dim(new TextBlock { Text = m, FontSize = 11.5, Margin = new Thickness(8, nav.Children.Count == 0 ? 0 : 12, 0, 4) }));
            }
            int index = i;
            var row = new DockPanel { Width = 166 }; // the button's template centres its content: the row spans it
            var when = Dim(new TextBlock { Text = i == 0 ? "Latest" : r.Date.ToString("d MMM", CultureInfo.InvariantCulture), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            row.Children.Add(new TextBlock { Text = r.Version, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Padding = new Thickness(0, 7, 0, 7), Margin = new Thickness(0, 1, 0, 1), ToolTip = r.Title };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{r.Version}, {r.Title}");
            button.Click += (_, _) => Show(index);
            nav.Children.Add(button);
        }
    }

    void Show(int i)
    {
        if (i == shown || i < 0 || i >= releases.Count) return;
        shown = i;
        var r = releases[i];
        var buttons = nav.Children.OfType<Button>().ToList();
        for (int b = 0; b < buttons.Count; b++)
            if (b == i) buttons[b].SetResourceReference(BackgroundProperty, "AccentSoft");
            else buttons[b].Background = Brushes.Transparent;
        buttons[i].BringIntoView();

        banner.Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = Banner(r) };
        notes.Children.Clear();
        notes.Children.Add(Dim(new TextBlock { Text = r.Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 8) }));
        foreach (var line in r.Lines)
        {
            var text = line.Trim();
            if (text.StartsWith("- "))
            {
                var point = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var dot = new TextBlock { Text = "•", Margin = new Thickness(2, 0, 10, 0) };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                DockPanel.SetDock(dot, Dock.Left);
                point.Children.Add(dot);
                point.Children.Add(Rich(text[2..]));
                notes.Children.Add(point);
            }
            else if (text.StartsWith("**") && text.EndsWith("**") && text.Length > 4 && !text[2..^2].Contains("**"))
                notes.Children.Add(new TextBlock { Text = text[2..^2], FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 12, 0, 4) });
            else
            {
                var p = Rich(text);
                p.Margin = new Thickness(0, 4, 0, 6);
                notes.Children.Add(p);
            }
        }
        notesScroll.ScrollToTop();
    }

    static TextBlock Dim(TextBlock t)
    {
        t.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        return t;
    }

    /// A line of the notes with its **bold** parts in bold.
    static TextBlock Rich(string text)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xDE, 0xEA)) };
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
            if (parts[i] != "") t.Inlines.Add(new Run(parts[i]) { FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal });
        return t;
    }

    /// The release's picture (WhatsNew\x.y.jpg, built in), or Backup Labs' colours under its version and title.
    static UIElement Banner(Release r)
    {
        var minor = string.Join('.', r.Version.Split('.').Take(2));
        using var stream = typeof(WhatsNewWindow).Assembly.GetManifestResourceStream($"WhatsNew.{minor}.jpg");
        if (stream != null)
        {
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.StreamSource = stream;
            picture.EndInit();
            picture.Freeze();
            return new Image { Source = picture, Width = 960, Height = 170, Stretch = Stretch.UniformToFill };
        }

        // the site's colours: night blue, the accent #4c8dff and violet #b15cff; a row of backups, one per day,
        // each a stack of the files it keeps, seeded by the version so a release looks the same every time
        var ground = new LinearGradientBrush(Color.FromRgb(0x0A, 0x0D, 0x13), Color.FromRgb(0x12, 0x1A, 0x2E), 20);
        var art = new Canvas { Width = 960, Height = 170, ClipToBounds = true };
        var glow = new Ellipse { Width = 560, Height = 380, Fill = new RadialGradientBrush(Color.FromArgb(0x50, 0x4C, 0x8D, 0xFF), Color.FromArgb(0, 0x4C, 0x8D, 0xFF)) };
        Canvas.SetLeft(glow, 520); Canvas.SetTop(glow, -150);
        art.Children.Add(glow);
        var v = r.Version.Split('.').Select(int.Parse).ToArray();
        var rnd = new Random(v[0] * 10000 + v[1] * 100 + v[2]);
        for (int day = 0; day < 9; day++)
        {
            double x = 420 + day * 58, height = 30 + rnd.NextDouble() * 80;
            bool lit = day == 8;
            var color = lit ? Color.FromRgb(0xB1, 0x5C, 0xFF) : Color.FromArgb((byte)(0x50 + day * 0x10), 0x4C, 0x8D, 0xFF);
            for (double y = 140; y > 140 - height; y -= 12)
            {
                var sheet = new Rectangle { Width = 40, Height = 8, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(color) };
                Canvas.SetLeft(sheet, x); Canvas.SetTop(sheet, y);
                art.Children.Add(sheet);
            }
        }
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0), MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
        words.Children.Add(new TextBlock { Text = "WHAT’S NEW IN", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)) });
        words.Children.Add(new TextBlock { Text = r.Version, FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        words.Children.Add(new TextBlock { Text = r.Title, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)) });
        return new Grid { Width = 960, Height = 170, Background = ground, Children = { art, words } };
    }
}
