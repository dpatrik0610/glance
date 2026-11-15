using System.Windows;
using Forms = System.Windows.Forms;
using Draw = System.Drawing;

namespace Glance.Services;

/// Tray icon with the Glance logo; live stats go in the hover tooltip.
public sealed class Tray
{
    readonly Forms.NotifyIcon icon;

    public Tray(Forms.ContextMenuStrip menu, Action leftClick)
    {
        // Load the size the tray actually uses, so Windows doesn't rescale a bigger frame.
        using var ico = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Glance.ico")).Stream;
        icon = new Forms.NotifyIcon
        {
            Icon = new Draw.Icon(ico, Forms.SystemInformation.SmallIconSize),
            Text = "Glance",
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.MouseClick += (_, a) => { if (a.Button == Forms.MouseButtons.Left) leftClick(); };
    }

    public void Hide() => icon.Visible = false;

    public void Update(Snapshot s)
    {
        var text = $"Glance\nCPU {s.CpuLoad:0}%{T(s.CpuTemp)}\nGPU {s.GpuLoad:0}%{T(s.GpuTemp)}";
        icon.Text = text.Length > 127 ? text[..127] : text; // NotifyIcon.Text limit
    }

    static string T(float? t) => t is { } v ? $" · {v:0}°C" : "";
}
