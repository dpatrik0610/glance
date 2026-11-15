using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Glance;

/// The few code-driven animations (page, window, palette, dialogs, toasts). Hover/press motion lives in Theme.xaml.
static class Motion
{
    static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    public static void To(IAnimatable target, DependencyProperty prop, double to, int ms, double? from = null, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        if (from is { } f) a.From = f;
        if (done is not null) a.Completed += (_, _) => done(); // must attach before BeginAnimation
        target.BeginAnimation(prop, a);
    }

    /// Fades in while settling up from a few pixels below.
    public static void Rise(UIElement e, TranslateTransform shift, double dy = 10, int ms = 260)
    {
        To(e, UIElement.OpacityProperty, 1, ms * 3 / 4, 0);
        To(shift, TranslateTransform.YProperty, 0, ms, dy);
    }

    static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
