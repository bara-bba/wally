using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Point = System.Drawing.Point;

namespace Wally.App;

/// <summary>
/// The wally brand mark — an accent rounded square (same geometry as the
/// sibling "Study" and "Picky" taskbar icons: 4.5% inset, 22% corner radius)
/// with wally's monitor-showing-a-wallpaper glyph on top. Drawn with GDI+ at
/// runtime so there is a single source of truth for the tray icon, the
/// window/taskbar icon, and the generated .ico, with no external asset to keep
/// in sync.
/// </summary>
public static class BrandMark
{
    // Match Study/Picky exactly so the icons sit together on the taskbar.
    private const float PadFraction = 0.045f;   // inset per side
    private const float RadiusFraction = 0.22f; // corner radius

    // Tile = the live accent colour, glyph = its readable foreground, exactly
    // like Study's AppIcon (which draws its square in the chosen accent). So the
    // taskbar, Alt-Tab and tray icons follow Settings → Accent colour.
    private static Color Accent => ToDrawing(AccentTheme.Current);
    private static Color Glyph => ToDrawing(AccentTheme.OnAccent);

    private static Color ToDrawing(System.Windows.Media.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>Render the brand mark into a square bitmap of the given size.</summary>
    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        float s = size;
        float pad = Math.Max(0.5f, s * PadFraction);
        float radius = s * RadiusFraction;

        // Accent rounded square filling the slot — identical footprint and
        // corner radius to Study, so wally reads as the same size next to it.
        var tile = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
        using (var tilePath = RoundedRect(tile, radius))
        using (var bg = new SolidBrush(Accent))
        {
            g.FillPath(bg, tilePath);
        }

        // wally's monitor glyph, laid out as fractions of the tile's inner box
        // so it scales cleanly at every icon size and stays centred.
        float ox = tile.X, oy = tile.Y, tw = tile.Width, th = tile.Height;
        float Fx(float f) => ox + tw * f;
        float Fy(float f) => oy + th * f;

        using var glyphBrush = new SolidBrush(Glyph);

        // Monitor body — a rounded rectangle, horizontally centred, upper-ish
        // so the stand fits below. Spans ~56% wide, ~40% tall of the tile.
        var body = RectangleF.FromLTRB(Fx(0.22f), Fy(0.26f), Fx(0.78f), Fy(0.64f));
        using (var bodyPath = RoundedRect(body, tw * 0.06f))
        {
            g.FillPath(glyphBrush, bodyPath);
        }

        // Screen cut-out showing the sun + hill wallpaper motif, painted in the
        // accent colour so the motif is the tile colour showing "through".
        var screen = new RectangleF(
            body.X + tw * 0.045f, body.Y + th * 0.045f,
            body.Width - tw * 0.09f, body.Height - th * 0.09f);
        using (var accentBrush = new SolidBrush(Accent))
        {
            g.FillRectangle(accentBrush, screen);
        }

        var oldClip = g.Clip;
        g.SetClip(screen);
        // Sun (light).
        g.FillEllipse(glyphBrush,
            screen.X + screen.Width * 0.10f, screen.Y + screen.Height * 0.12f,
            screen.Width * 0.28f, screen.Width * 0.28f);
        // Hill (light).
        using (var hill = new GraphicsPath())
        {
            hill.AddPolygon(new[]
            {
                new PointF(screen.X, screen.Bottom),
                new PointF(screen.X + screen.Width * 0.33f, screen.Y + screen.Height * 0.45f),
                new PointF(screen.X + screen.Width * 0.60f, screen.Y + screen.Height * 0.68f),
                new PointF(screen.Right, screen.Y + screen.Height * 0.35f),
                new PointF(screen.Right, screen.Bottom),
            });
            g.FillPath(glyphBrush, hill);
        }
        g.Clip = oldClip;

        // Neck + base stand, centred under the body.
        float cx = body.X + body.Width / 2f;
        g.FillRectangle(glyphBrush, cx - tw * 0.04f, body.Bottom, tw * 0.08f, th * 0.07f);
        using (var basePath = RoundedRect(
            new RectangleF(cx - tw * 0.16f, body.Bottom + th * 0.07f, tw * 0.32f, th * 0.06f),
            tw * 0.03f))
        {
            g.FillPath(glyphBrush, basePath);
        }

        return bmp;
    }

    /// <summary>A rounded-rectangle path with the given corner radius.</summary>
    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Build a Win32 icon handle from the brand mark (tray use).</summary>
    public static Icon ToIcon(int size = 32)
    {
        using var bmp = Render(size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Build a WPF ImageSource from the brand mark (window Icon use).</summary>
    public static System.Windows.Media.ImageSource ToImageSource(int size = 32)
    {
        using var bmp = Render(size);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;

        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
