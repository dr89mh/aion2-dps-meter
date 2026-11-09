using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using DpsMeter.Meter;

namespace DpsMeter.App;

/// <summary>The class icons of the game (embedded, 40×42), scaled once per size for crisp small rows.</summary>
internal static class ClassIcons
{
    private static readonly Dictionary<string, Bitmap?> Sources = [];
    private static readonly Dictionary<(int Class, int Size), Bitmap?> Scaled = [];

    /// <summary>Draws the icon of a class centred in a square; does nothing for unknown classes.</summary>
    public static void Draw(Graphics g, int cls, Rectangle square)
    {
        var size = Math.Min(square.Width, square.Height);
        var icon = Get(cls, size);
        if (icon is null) return;
        g.DrawImageUnscaled(icon, square.X + (square.Width - icon.Width) / 2, square.Y + (square.Height - icon.Height) / 2);
    }

    /// <summary>The icon on a dark rounded tile, so it stands out on a bar of its own colour.</summary>
    public static void Badge(Graphics g, int cls, Rectangle square, int radius)
    {
        if (ClassInfo.Icon(cls) is null) return;
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Rounded(square, radius))
        using (var b = new SolidBrush(Color.FromArgb(170, 12, 14, 18)))
            g.FillPath(b, path);
        g.SmoothingMode = mode;
        var inset = Math.Max(1, square.Height / 10);
        Draw(g, cls, Rectangle.Inflate(square, -inset, -inset));
    }

    /// <summary>The icon of a class scaled to fit <paramref name="size"/> pixels high, or null.</summary>
    public static Bitmap? Get(int cls, int size)
    {
        if (size < 4) return null;
        lock (Scaled)
        {
            if (Scaled.TryGetValue((cls, size), out var cached)) return cached;
            var src = Source(cls);
            Bitmap? bmp = null;
            if (src is not null)
            {
                var h = size;
                var w = Math.Max(1, (int)Math.Round(src.Width * (double)size / src.Height));
                bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
            }
            Scaled[(cls, size)] = bmp;
            return bmp;
        }
    }

    private static Bitmap? Source(int cls)
    {
        var name = ClassInfo.Icon(cls);
        if (name is null) return null;
        if (Sources.TryGetValue(name, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s is not null)
            {
                using var img = Image.FromStream(s);
                bmp = new Bitmap(img);
            }
        }
        catch (ArgumentException)
        {
            bmp = null;
        }
        Sources[name] = bmp;
        return bmp;
    }
}
