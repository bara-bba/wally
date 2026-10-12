using System.Windows;
using System.Windows.Media.Animation;

namespace Wally.App;

/// <summary>
/// WPF has no built-in animation for <see cref="GridLength"/>, so this provides
/// a simple linear tween used to slide the nav rail between its collapsed and
/// expanded widths.
/// </summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
    {
        var from = From.Value;
        var to = To.Value;
        if (clock.CurrentProgress is not double progress)
        {
            return new GridLength(from, GridUnitType.Pixel);
        }

        var value = from + (to - from) * progress;
        return new GridLength(value, GridUnitType.Pixel);
    }
}
