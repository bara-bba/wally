using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Wally.App;

/// <summary>
/// The Study / Picky toolbar row that sits under the title bar: actions
/// left-aligned (an optional accent primary button, a thin divider, then 38×34
/// icon buttons) and grey status text on the right.
/// </summary>
internal static class PageToolbar
{
    /// <summary>Accent primary button with a glyph and a label, like Study's "＋ Add watch".</summary>
    public static Button Primary(string glyph, string label, string tooltip, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = GlyphIcon.Create(glyph, 14);
        icon.Margin = new Thickness(0, 0, 8, 0);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Style = Res<Style>("AccentButton"),
            Content = content,
            ToolTip = tooltip,
            Margin = new Thickness(0, 0, 8, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Picky's 38×34 icon-only button; the tooltip carries the label.</summary>
    public static Button Tool(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Style = Res<Style>("ToolIconButton"),
            Content = GlyphIcon.Create(glyph),
            ToolTip = tooltip,
            Margin = new Thickness(0, 0, 8, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>The 1px vertical divider Study and Picky put after the primary action.</summary>
    public static Border Divider() => new()
    {
        Width = 1,
        Background = Res<Brush>("CardBorder"),
        Margin = new Thickness(2, 2, 10, 2),
    };

    /// <summary>Right-aligned grey status line (Study's summary / Picky's folder path).</summary>
    public static TextBlock Status() => new()
    {
        Foreground = Res<Brush>("TextTertiary"),
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(12, 0, 0, 0),
    };

    /// <summary>Lays the left-hand actions and the right-hand status into one row.</summary>
    public static Grid Compose(IEnumerable<UIElement> left, UIElement? right)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var element in left)
        {
            actions.Children.Add(element);
        }

        row.Children.Add(actions);

        if (right is FrameworkElement status)
        {
            status.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(status, 1);
            row.Children.Add(status);
        }

        return row;
    }

    /// <summary>Opens the folder in Explorer; returns false if there is no usable folder.</summary>
    public static bool OpenInExplorer(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Status text for a gallery folder: its path and image count.</summary>
    public static string GalleryStatus(string? folder, int imageCount) =>
        string.IsNullOrEmpty(folder)
            ? "No wallpaper folder chosen"
            : $"{folder}  ·  {imageCount} image{(imageCount == 1 ? "" : "s")}";

    private static T Res<T>(string key) => (T)System.Windows.Application.Current.FindResource(key);
}
