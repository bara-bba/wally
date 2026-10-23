using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wally.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using StackPanel = System.Windows.Controls.StackPanel;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Cursors = System.Windows.Input.Cursors;
using UserControl = System.Windows.Controls.UserControl;
using FontFamily = System.Windows.Media.FontFamily;

namespace Wally.App;

/// <summary>
/// The Settings view. Mirrors the sibling "Study" app's settings: an accent
/// colour selector (preset swatches + a #RRGGBB box with a live preview) that
/// applies instantly and persists, plus honest placeholders for the behaviour
/// toggles still to come.
/// </summary>
public sealed class SettingsView : UserControl, IShellPage
{
    public string PageTitle => "Settings";

    public FrameworkElement? HeaderActions => null;

    private WallyConfig _config;
    private TextBox? _hexBox;
    private Border? _swatch;
    private TextBlock? _status;
    private readonly List<Border> _presetChips = new();

    public SettingsView()
    {
        try { _config = ConfigStore.Load(); }
        catch { _config = new WallyConfig(); }

        Build();
    }

    private void Build()
    {
        var root = new StackPanel();

        root.Children.Add(BuildAccentCard());
        root.Children.Add(BuildComingNextCard());

        // Scroll in case the window is short.
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root,
        };
        scroller.Resources.Add(
            typeof(System.Windows.Controls.Primitives.ScrollBar),
            Res<Style>("SlimScrollBar"));
        Content = scroller;
    }

    private Border BuildAccentCard()
    {
        var card = new Border { Style = Res<Style>("Card") };
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock { Text = "ACCENT COLOUR", Style = Res<Style>("CardLabel") });
        panel.Children.Add(new TextBlock
        {
            Text = "Used for the primary buttons, the selected-display ring, the nav highlight, and the logo.",
            Foreground = Res<Brush>("TextTertiary"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 12),
        });

        // Preset swatches.
        var presets = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, color) in AccentTheme.Presets)
        {
            presets.Children.Add(BuildPresetChip(name, color));
        }
        panel.Children.Add(presets);

        // Hex entry + live swatch.
        var hexRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _swatch = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            BorderBrush = Res<Brush>("CardBorder"),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(AccentTheme.Current),
            Margin = new Thickness(0, 0, 10, 0),
        };
        hexRow.Children.Add(_swatch);

        _hexBox = new TextBox
        {
            Text = AccentTheme.ToHex(AccentTheme.Current),
            Width = 120,
            Style = Res<Style>("DarkTextBox"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Enter a colour as #RRGGBB, e.g. #0078D4",
        };
        _hexBox.TextChanged += (_, _) => OnHexChanged();
        hexRow.Children.Add(_hexBox);

        var apply = new Button
        {
            Content = "Apply",
            Style = Res<Style>("AccentButton"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        apply.Click += (_, _) => ApplyFromHex();
        hexRow.Children.Add(apply);

        var reset = new Button
        {
            Content = "Reset to default",
            Style = Res<Style>("SubtleButton"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        reset.Click += (_, _) => ApplyColor(AccentTheme.Default);
        hexRow.Children.Add(reset);

        panel.Children.Add(hexRow);

        _status = new TextBlock
        {
            Foreground = Res<Brush>("TextTertiary"),
            FontSize = 11,
            Margin = new Thickness(2, 10, 0, 0),
        };
        panel.Children.Add(_status);

        card.Child = panel;
        return card;
    }

    private Border BuildPresetChip(string name, Color color)
    {
        var chip = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(color),
            BorderBrush = Res<Brush>("CardBorder"),
            BorderThickness = new Thickness(2),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            ToolTip = $"{name} · {AccentTheme.ToHex(color)}",
        };
        chip.MouseLeftButtonUp += (_, _) => ApplyColor(color);
        _presetChips.Add(chip);
        return chip;
    }

    private void OnHexChanged()
    {
        if (_swatch is null)
        {
            return;
        }

        if (AccentTheme.TryParse(_hexBox!.Text, out var c))
        {
            _swatch.Background = new SolidColorBrush(c);
            SetStatus("");
        }
        else
        {
            SetStatus("Enter a colour as #RRGGBB, e.g. #0078D4.", error: true);
        }
    }

    private void ApplyFromHex()
    {
        if (!AccentTheme.TryParse(_hexBox!.Text, out var c))
        {
            SetStatus("Enter a colour as #RRGGBB, e.g. #0078D4.", error: true);
            return;
        }

        ApplyColor(c);
    }

    /// <summary>Apply the colour live, persist it, and refresh accent-driven views.</summary>
    private void ApplyColor(Color c)
    {
        AccentTheme.Apply(c);

        if (_hexBox is not null)
        {
            _hexBox.Text = AccentTheme.ToHex(c);
        }

        if (_swatch is not null)
        {
            _swatch.Background = new SolidColorBrush(c);
        }

        UpdatePresetSelection(c);

        try
        {
            var hex = AccentTheme.ToHex(c);
            _config = ConfigStore.Update(cfg => cfg with { AccentColor = hex });
            SetStatus($"Accent set to {AccentTheme.ToHex(c)} and saved.");
        }
        catch (Exception ex)
        {
            SetStatus($"Applied, but could not save: {ex.Message}", error: true);
        }
    }

    private void UpdatePresetSelection(Color c)
    {
        for (int i = 0; i < _presetChips.Count && i < AccentTheme.Presets.Length; i++)
        {
            var isSel = AccentTheme.Presets[i].Color == c;
            _presetChips[i].BorderBrush = isSel ? Res<Brush>("Accent") : Res<Brush>("CardBorder");
        }
    }

    private Border BuildComingNextCard()
    {
        var card = new Border { Style = Res<Style>("Card") };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "BEHAVIOUR", Style = Res<Style>("CardLabel") });
        panel.Children.Add(new TextBlock
        {
            Text = "Coming next: start-on-login, the new-desktop behaviour toggle (open the manager vs auto-assign), "
                 + "and the rotation pool editor.",
            Foreground = Res<Brush>("TextSecondary"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        card.Child = panel;
        return card;
    }

    private void SetStatus(string text, bool error = false)
    {
        if (_status is null)
        {
            return;
        }

        _status.Text = text;
        _status.Foreground = error
            ? Res<Brush>("Bad")
            : Res<Brush>("Accent");
    }

    private T Res<T>(string key) => (T)FindResource(key);
}
