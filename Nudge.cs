using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Backup;

// The "Buy me a pizza" nudge, the same file in every Protagonist Labs WPF app: 3 s after the window first shows,
// then every 10 s while it is on screen, a 700 ms double pulse (scale 1 â†’ 1.06 â†’ 1 â†’ 1.03 â†’ 1, the red brightening
// to #F25555 at both peaks). Never while the pointer is on the button or with Windows' animation effects off, and the
// timer stops while the window is minimized or in the tray. The button's style gives the red and the hover colours.
internal static class Nudge
{
    static readonly KeySpline Out = new(0.23, 1, 0.32, 1);

    public static void Attach(Button button, Window window)
    {
        var scale = new ScaleTransform();
        var brush = ((SolidColorBrush)button.Background).Clone(); // the style's brush is frozen; this one can move
        button.RenderTransform = scale;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.Background = brush;
        Color rest = brush.Color, peak = Color.FromRgb(0xF2, 0x55, 0x55);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Interval = TimeSpan.FromSeconds(10);
            if (button.IsMouseOver || !SystemParameters.ClientAreaAnimation) return; // read each time: the setting can change live
            var grow = new DoubleAnimationUsingKeyFrames();
            var glow = new ColorAnimationUsingKeyFrames();
            foreach (var (ms, size, color) in new[] { (175, 1.06, peak), (350, 1.0, rest), (525, 1.03, peak), (700, 1.0, rest) })
            {
                var at = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));
                grow.KeyFrames.Add(new SplineDoubleKeyFrame(size, at, Out));
                glow.KeyFrames.Add(new SplineColorKeyFrame(color, at, Out));
            }
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, glow);
        };

        void Follow() => timer.IsEnabled = window.IsVisible && window.WindowState != WindowState.Minimized;
        window.IsVisibleChanged += (_, _) => Follow();
        window.StateChanged += (_, _) => Follow();
        Follow();
    }
}
