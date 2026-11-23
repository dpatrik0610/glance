using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Glance.Services;

namespace Glance;

/// Quick capture (its own hotkey): one line into today's note (Enter), or a note on the current board (Ctrl+Enter). Esc or clicking away closes it.
public class CaptureWindow : Window
{
    static CaptureWindow? open;
    readonly TextBox box;

    public static void Open()
    {
        if (open is not null) { open.Activate(); return; }
        open = new CaptureWindow();
        open.Show();
        open.Activate();
    }

    CaptureWindow()
    {
        (WindowStyle, AllowsTransparency, Background, ResizeMode, Topmost, ShowInTaskbar) = (WindowStyle.None, true, Brushes.Transparent, ResizeMode.NoResize, true, false);
        (Width, SizeToContent, WindowStartupLocation) = (560, SizeToContent.Height, WindowStartupLocation.CenterScreen);
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = Skin.Brush("Text");
        box = new TextBox { FontSize = 16, Tag = "Into today's note…", BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0) };
        Content = new Border
        {
            Margin = new(24), Padding = new(18, 14, 18, 12), CornerRadius = new(16), Background = Skin.Brush("Popup"),
            BorderBrush = Skin.Brush("Line"), BorderThickness = new(1),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.5, Color = Colors.Black },
            Child = new StackPanel
            {
                Children =
                {
                    box,
                    new TextBlock { Text = "Enter: today's note   ·   Ctrl+Enter: a note on the board   ·   Esc: close", Style = Skin.Style("Label"), FontSize = 11.5, Margin = new(0, 8, 0, 0) },
                },
            },
        };
        box.PreviewKeyDown += KeyPressed;
        Loaded += (_, _) => box.Focus();
        Deactivated += (_, _) => Close();
        Closed += (_, _) => open = null;
    }

    void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var text = box.Text.Trim();
        if (text != "")
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Board(text);
            else Today(text);
        }
        Close();
    }

    /// "- HH:mm text" at the end of today's note.
    static void Today(string text)
    {
        var note = NoteStore.Today();
        note.Text = note.Text.TrimEnd() + (note.Text.Trim() == "" ? "" : "\n") + $"- {DateTime.Now:HH:mm} {text}";
    }

    /// A new note right of the current board's nodes (an open Board page shows it at once).
    static void Board(string text)
    {
        var d = BoardStore.Data;
        var b = d.All.FirstOrDefault(x => x.Id == d.Current) ?? d.All[0];
        var at = b.Items.Count == 0 ? (X: 0.0, Y: 0.0) : (X: b.Items.Max(i => i.X + i.W) + 60, Y: b.Items.Min(i => i.Y));
        var it = new BoardItem { Text = BoardStore.FixDues(text, DateTime.Now), X = at.X, Y = at.Y };
        b.Items.Add(it);
        BoardStore.Changed(it);
    }
}
