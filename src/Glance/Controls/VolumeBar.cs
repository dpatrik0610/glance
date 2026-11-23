using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glance.Services;

namespace Glance;

/// Upright slider over a mute button, for a player's app volume. Show() re-reads it, since the mixer can change it too.
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
            Style = Skin.Style("Ghost"),
            FontFamily = Skin.Icons,
            FontSize = 14, Padding = new Thickness(7, 5, 7, 5), Focusable = false, ToolTip = "Mute",
        };
        icon.Click += (_, _) =>
        {
            if (player?.Volume is not { } v) return;
            player.SetMuted(!v.Muted);
            Show(player);
        };
        slider = new Slider
        {
            Style = Skin.Style("VSlider"), Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, Focusable = false,
            Margin = new Thickness(0, 2, 0, 4), SmallChange = 0.05, LargeChange = 0.1,
        };
        slider.MouseWheel += (_, e) => slider.Value += e.Delta > 0 ? 0.05 : -0.05; // the wheel nudges it
        slider.ValueChanged += (_, e) =>
        {
            if (loading || player is null) return;
            player.SetVolume((float)e.NewValue);
            Glyph(e.NewValue, false);
        };
        SetDock(icon, Dock.Bottom);
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
