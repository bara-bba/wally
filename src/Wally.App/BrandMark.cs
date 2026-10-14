using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using Point = System.Drawing.Point;

namespace Wally.App;

/// <summary>
/// The wally brand mark — a white rounded tile with a dark monitor showing a
/// sun-over-hill wallpaper motif. Drawn with GDI+ at runtime so there is a
/// single source of truth for the tray icon, the window/taskbar icon, and the
/// generated .ico, with no external asset to keep in sync.
/// </summary>
public static class BrandMark
{
    /// <summary>Render the brand mark into a square bitmap of the given size.</summary>
    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Scale the 32px reference design to the requested size.
        float s = size / 32f;
        float Sx(float v) => v * s;

        // White rounded tile (near full-bleed; small margin for the rounding).
        using var tile = new GraphicsPath();
        float r = Sx(6);
        var rect = new RectangleF(Sx(0.5f), Sx(0.5f), size - Sx(1), size - Sx(1));
        tile.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        tile.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        tile.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        tile.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        tile.CloseFigure();
        g.FillPath(Brushes.White, tile);

        // Monitor as a FILLED dark rounded body with a light inset screen, so
        // it reads clearly at taskbar sizes (a thin-stroke rectangle collapses
        // when scaled down). Reference box is 32px. The whole mark — body +
        // stand — is vertically balanced so it sits centred in the tile.
        var dark = Color.FromArgb(0x20, 0x20, 0x20);
        using var darkBrush = new SolidBrush(dark);

        // Monitor body (filled, slightly rounded).
        var body = new RectangleF(Sx(6), Sx(6.5f), Sx(20), Sx(14));
        using (var bodyPath = RoundedRect(body, Sx(2)))
        {
            g.FillPath(darkBrush, bodyPath);
        }

        // Light inset screen showing the sun + hill wallpaper motif.
        var screen = new RectangleF(body.X + Sx(2), body.Y + Sx(2), body.Width - Sx(4), body.Height - Sx(4));
        g.FillRectangle(Brushes.White, screen);

        var oldClip = g.Clip;
        g.SetClip(screen);
        // Sun.
        g.FillEllipse(darkBrush, screen.X + Sx(2), screen.Y + Sx(1.5f), Sx(4), Sx(4));
        // Hill.
        using (var hill = new GraphicsPath())
        {
            hill.AddPolygon(new[]
            {
                new PointF(screen.X, screen.Bottom),
                new PointF(screen.X + Sx(5.5f), screen.Y + Sx(5)),
                new PointF(screen.X + Sx(10), screen.Y + Sx(7.5f)),
                new PointF(screen.Right, screen.Y + Sx(4)),
                new PointF(screen.Right, screen.Bottom),
            });
            g.FillPath(darkBrush, hill);
        }
        g.Clip = oldClip;

        // Neck + base stand, centred under the body.
        float cx = body.X + body.Width / 2f;
        g.FillRectangle(darkBrush, cx - Sx(1.25f), body.Bottom, Sx(2.5f), Sx(2.5f));
        using (var basePath = RoundedRect(new RectangleF(cx - Sx(5), body.Bottom + Sx(2.5f), Sx(10), Sx(2.25f)), Sx(1)))
        {
            g.FillPath(darkBrush, basePath);
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
