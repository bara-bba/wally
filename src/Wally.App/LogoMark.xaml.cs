using UserControl = System.Windows.Controls.UserControl;

namespace Wally.App;

/// <summary>
/// Minimal viewfinder logo mark (four corner brackets + a centred square),
/// drawn as scalable vector shapes in the accent brush. Reused for the nav
/// header and anywhere the brand mark is needed.
/// </summary>
public partial class LogoMark : UserControl
{
    public LogoMark()
    {
        InitializeComponent();
    }
}
