using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace Wally.App;

/// <summary>
/// Single source of truth for the app accent color (same model as the sibling
/// "Study" app). Replacing the shared brush resources updates every control
/// that reads them — selection rings, the nav pill, status accents, the primary
/// action button, and the logo mark.
///
/// wally's palette is otherwise a near-black monochrome (Picky-inspired); the
/// accent is the one user-tunable colour. Default is wally blue.
/// </summary>
internal static class AccentTheme
{
    /// <summary>Default accent — Windows blue, identical to Picky and Study.</summary>
    public static readonly Color Default = Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>A small set of presets shown as one-click swatches in Settings.</summary>
    public static readonly (string Name, Color Color)[] Presets =
    {
        ("Windows blue (Picky / Study)", Color.FromRgb(0x00, 0x78, 0xD4)),
        ("wally blue", Color.FromRgb(0x2F, 0x6F, 0xED)),
        ("Teal", Color.FromRgb(0x12, 0xA3, 0x9A)),
        ("Green", Color.FromRgb(0x2E, 0xA0, 0x43)),
        ("Purple", Color.FromRgb(0x8B, 0x5C, 0xF6)),
        ("Magenta", Color.FromRgb(0xD1, 0x3A, 0x8E)),
        ("Orange", Color.FromRgb(0xE8, 0x6A, 0x17)),
        ("Mono white", Color.FromRgb(0xFF, 0xFF, 0xFF)),
    };

    public static Color Current { get; private set; } = Default;

    /// <summary>Readable foreground on the accent: near-black on light accents, white on dark.</summary>
    public static Color OnAccent { get; private set; } = Colors.White;

    /// <summary>Parses "#RRGGBB" or "RRGGBB"; returns false for anything else.</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        color = Current;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        color = Color.FromRgb(
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// Apply <paramref name="c"/> as the live accent. Swaps the shared brush
    /// resources in the WPF application so every consumer updates on its next
    /// read / rebuild.
    /// </summary>
    /// <summary>Raised after <see cref="Apply"/> so drawn assets (taskbar/tray icon) can redraw.</summary>
    public static event Action? Changed;

    public static void Apply(Color c)
    {
        Current = c;
        OnAccent = Luminance(c) > 0.5 ? Color.FromRgb(0x1C, 0x1C, 0x1C) : Colors.White;

        SetBrush("Accent", c);
        SetBrush("AccentHover", Lerp(c, Colors.White, 0.12));
        SetBrush("AccentPressed", Lerp(c, Colors.Black, 0.12));
        SetBrush("OnAccent", OnAccent);

        Changed?.Invoke();
    }

    /// <summary>WCAG relative luminance (0 = black, 1 = white).</summary>
    private static double Luminance(Color c)
    {
        static double Channel(double v)
        {
            v /= 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static void SetBrush(string key, Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        System.Windows.Application.Current.Resources[key] = brush;
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
