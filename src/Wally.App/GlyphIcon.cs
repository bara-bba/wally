using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Wally.App;

/// <summary>
/// Renders a Unicode symbol (⟳, 🖫, ✎ …) as a vector outline. Symbol-font glyphs
/// carry the font's ascent/descent padding, so a plain TextBlock sits visibly
/// low and off-centre inside a square icon button. Converting the glyph to its
/// outline and trimming to the ink bounds centres the visible shape exactly.
/// The fill follows the hosting button's Foreground, so hover colours still work.
/// </summary>
internal static class GlyphIcon
{
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol, Segoe UI");

    public static Path Create(string glyph, double size = 16)
    {
        var text = new FormattedText(
            glyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(SymbolFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            System.Windows.Media.Brushes.White,
            pixelsPerDip: 1.0);

        var geometry = text.BuildGeometry(new Point(0, 0));
        var bounds = geometry.Bounds;
        if (!bounds.IsEmpty)
        {
            // Shift the ink to the origin so the Path's size is the ink box alone.
            geometry.Transform = new TranslateTransform(-bounds.X, -bounds.Y);
        }

        geometry.Freeze();

        var path = new Path
        {
            Data = geometry,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Button), 1),
        });
        return path;
    }
}
