using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DpsMeter.App;

/// <summary>Colours, fonts and drawing helpers of the dark overlay look.</summary>
internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(18, 20, 26);
    public static readonly Color Panel = Color.FromArgb(27, 30, 38);
    public static readonly Color Header = Color.FromArgb(32, 36, 46);
    public static readonly Color Line = Color.FromArgb(48, 53, 66);
    public static readonly Color Hover = Color.FromArgb(40, 45, 58);
    public static readonly Color Text = Color.FromArgb(230, 232, 238);
    public static readonly Color Dim = Color.FromArgb(140, 146, 162);
    public static readonly Color Faint = Color.FromArgb(96, 102, 118);
    public static readonly Color Accent = Color.FromArgb(240, 180, 60);
    public static readonly Color Live = Color.FromArgb(96, 200, 120);
    public static readonly Color Danger = Color.FromArgb(224, 90, 90);

    /// <summary>Symbol font of Windows 11 (falls back to the Windows 10 one).</summary>
    public static readonly string IconFont =
        new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public const string IconMenu = "";
    public const string IconHistory = "";
    public const string IconReset = "";
    public const string IconLock = "";
    public const string IconUnlock = "";
    public const string IconClose = "";
    public const string IconPrev = "";
    public const string IconNext = "";
    public const string IconDetails = "";

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style, GraphicsUnit.Point);

    public static Font Icons(float size) => new(IconFont, size, FontStyle.Regular, GraphicsUnit.Point);

    public static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static Color Alpha(Color c, int alpha) => Color.FromArgb(alpha, c);

    public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                                        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    public const TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                                         TextFormatFlags.SingleLine;

    public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                          TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    public static void Str(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, r, color, flags);

    /// <summary>Text with a soft dark shadow, readable on light class colours.</summary>
    public static void StrShadow(Graphics g, string text, Font font, Rectangle r, Color color, TextFormatFlags flags)
    {
        var shadow = r;
        shadow.Offset(1, 1);
        TextRenderer.DrawText(g, text, font, shadow, Color.FromArgb(10, 10, 14), flags);
        TextRenderer.DrawText(g, text, font, r, color, flags);
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Dark colours for the context menus.</summary>
    public sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Header;
        public override Color MenuItemPressedGradientEnd => Header;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Header;
        public override Color CheckSelectedBackground => Header;
        public override Color CheckPressedBackground => Header;
    }

    public sealed class MenuRenderer() : ToolStripProfessionalRenderer(new MenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Dim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var pen = new Pen(Accent, 2f);
            Setup(e.Graphics);
            e.Graphics.DrawLines(pen, new PointF[]
            {
                new(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.5f),
                new(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f),
            });
        }
    }

    public static ContextMenuStrip Menu()
    {
        var m = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        m.Font = Ui(9.5f);
        return m;
    }

    // ----- borderless windows: move, resize, click-through ------------------------------------------------------

    public const int WM_NCHITTEST = 0x84;
    public const int HTCLIENT = 1, HTCAPTION = 2, HTTRANSPARENT = -1;
    public const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16,
        HTBOTTOMRIGHT = 17;
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8,
        WS_EX_NOACTIVATE = 0x8000000;
    private const int GWL_EXSTYLE = -20;

    /// <summary>Resize borders of a borderless window: the hit-test code for a point, or <see cref="HTCLIENT"/>.</summary>
    public static int Edge(Point p, Size size, int border)
    {
        var left = p.X < border;
        var right = p.X >= size.Width - border;
        var top = p.Y < border;
        var bottom = p.Y >= size.Height - border;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (top && right) return HTTOPRIGHT;
        if (top && left) return HTTOPLEFT;
        if (right) return HTRIGHT;
        if (left) return HTLEFT;
        if (bottom) return HTBOTTOM;
        if (top) return HTTOP;
        return HTCLIENT;
    }

    public static void ClickThrough(IntPtr handle, bool on)
    {
        var ex = GetWindowLong(handle, GWL_EXSTYLE);
        ex = on ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(handle, GWL_EXSTYLE, ex);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
