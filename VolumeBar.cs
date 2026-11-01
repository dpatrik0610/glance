using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glance.Services;

namespace Glance;

/// Mute button + slider for a player's app volume (the mixer's). Show() re-reads it, since the mixer can change it too.
public class VolumeBar : DockPanel
{
    readonly Button icon;
    readonly Slider slider;
    Player? player;
    bool loading;

    public VolumeBar()
    {
        icon = new Button
        {
            Style = (Style)Application.Current.FindResource("Ghost"),
            FontFamily = (FontFamily)Application.Current.FindResource("Icons"),
            FontSize = 14, Padding = new Thickness(7, 5, 7, 5), Focusable = false, ToolTip = "Mute",
        };
        icon.Click += (_, _) =>
        {
            if (player?.Volume is not { } v) return;
            player.SetMuted(!v.Muted);
            Show(player);
        };
        slider = new Slider { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        slider.ValueChanged += (_, e) =>
        {
            if (loading || player is null) return;
            player.SetVolume((float)e.NewValue);
            Glyph(e.NewValue, false);
        };
        SetDock(icon, Dock.Left);
        Children.Add(icon);
        Children.Add(slider);
    }

    /// Hidden when the player has no audio session (nothing coming out, or not on the default output).
    public void Show(Player? p)
    {
        player = p;
        var v = p?.Volume;
        Visibility = v is null ? Visibility.Collapsed : Visibility.Visible;
        if (v is not { } vol) return;
        if (!slider.IsMouseCaptureWithin)
        {
            loading = true;
            slider.Value = vol.Level;
            loading = false;
        }
        Glyph(vol.Level, vol.Muted);
        ToolTip = p!.IsBrowser ? $"{p.Source} volume (all its tabs)" : $"{p.Source} volume";
    }

    void Glyph(double level, bool muted) =>
        icon.Content = muted || level <= 0 ? "" : level < 0.34 ? "" : level < 0.67 ? "" : "";
}
