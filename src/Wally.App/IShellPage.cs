using System.Windows;

namespace Wally.App;

/// <summary>
/// A page hosted by the shell. The shell header (clony layout) shows the page
/// title centred and the page's action buttons beside the window buttons.
/// </summary>
public interface IShellPage
{
    /// <summary>Title shown centred in the app header.</summary>
    string PageTitle { get; }

    /// <summary>Buttons shown at the right of the app header, or null.</summary>
    FrameworkElement? HeaderActions { get; }
}
