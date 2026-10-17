using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

// The Protagonist Labs tray menu (vault: Proiecte/Tray-menu-spec.md); the same file in every WinForms tray app.
// Follows the Windows app theme, read again each time it opens; hover never changes the text colour, so the
// system highlight (light text on light blue) can't come back; rounded corners on Windows 11.
internal static class TrayMenu
{
    public static Forms.ContextMenuStrip Create()
    {
        Forms.Application.EnableVisualStyles();
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new Drawing.Font("Segoe UI", 9f) };
        menu.Opening += (_, _) => Style(menu);
        return menu;
    }

    static void Style(Forms.ContextMenuStrip menu)
    {
        var c = new Palette(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1);
        menu.Renderer = new Renderer(c);
        menu.BackColor = c.Bg;
        menu.ForeColor = c.Text;
        float scale = menu.DeviceDpi / 96f;
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            int pad = (int)(8 * scale);
            if (item is Forms.ToolStripSeparator)
            {
                item.AutoSize = false;
                item.Height = (int)Math.Round(13 * scale); // 1 px line, 6 px above and below
                continue;
            }
            item.Padding = new Forms.Padding(pad, 0, pad, 0);
            int extra = (int)Math.Round(30 * scale) - item.GetPreferredSize(Drawing.Size.Empty).Height;
            item.Padding = new Forms.Padding(pad, extra / 2, pad, extra - extra / 2);
        }
        menu.PerformLayout(); // Opening comes after the size was measured
        int round = 3; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUNDSMALL; ignored before Windows 11
        DwmSetWindowAttribute(menu.Handle, 33, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    sealed class Palette : Forms.ProfessionalColorTable
    {
        public readonly Drawing.Color Bg, Hover, Border, Line, Text, Disabled;

        public Palette(bool light)
        {
            Bg = light ? Rgb(0xF7F8FA) : Rgb(0x1B2029);
            Hover = light ? Rgb(0xE3E8EF) : Rgb(0x2E3747);
            Border = light ? Rgb(0xD5DAE1) : Rgb(0x2C3440);
            Line = light ? Rgb(0xDDE2E8) : Rgb(0x2C3440);
            Text = light ? Rgb(0x1A1D23) : Rgb(0xE8EEF6);
            Disabled = light ? Rgb(0x9AA2AE) : Rgb(0x6B7584);
        }

        static Drawing.Color Rgb(int rgb) => Drawing.Color.FromArgb(255, Drawing.Color.FromArgb(rgb));

        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientMiddle => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Bg;
    }

    sealed class Renderer : Forms.ToolStripProfessionalRenderer
    {
        readonly Palette c;

        public Renderer(Palette palette) : base(palette) { c = palette; RoundedEdges = false; }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? c.Text : c.Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(c.Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }
    }
}
