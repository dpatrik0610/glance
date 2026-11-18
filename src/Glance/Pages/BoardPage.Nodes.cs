using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

/// What goes inside each kind of node. The card around it (drag, resize, remove) is BoardPage.Show.
public partial class BoardPage
{
    public static readonly string[] Metrics = ["CPU", "GPU", "RAM", "Download", "Upload"];

    /// The node's content, how to switch it into editing (double-click), and what to stop when it goes.
    (FrameworkElement Body, Action<bool>? Edit, Action? Cleanup) Build(BoardItem it, bool focus)
    {
        switch (it.Kind)
        {
            case "image":
                return (LoadPicture(it) is { } pic
                    ? new Image { Source = pic, Stretch = Stretch.Uniform }
                    : new TextBlock { Text = "Picture missing", Style = (Style)FindResource("Label") }, null, null);
            case "link": return Link(it, focus);
            case "timer": return Timer(it, focus);
            case "stat": return Stat(it);
            default: return Note(it, focus);
        }
    }

    /// A borderless text box that blends into the card.
    static TextBox Field(string text, string hint, double size = 14, bool bold = false, bool multiline = false) => new()
    {
        Text = text, Tag = hint, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, Cursor = System.Windows.Input.Cursors.IBeam,
        Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };

    TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6),
        Visibility = text.Trim() == "" ? Visibility.Collapsed : Visibility.Visible,
    };

    Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button { Style = (Style)FindResource("Ghost"), Content = glyph, FontFamily = (FontFamily)FindResource("Icons"), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    /// A node shown as `view` and edited in `editor`: Edit(true) opens the editor, leaving the node closes it and redraws.
    /// A node left empty goes away.
    (FrameworkElement, Action<bool>) Editable(BoardItem it, FrameworkElement view, FrameworkElement editor, UIElement first,
        Action draw, Func<bool> empty, bool focus)
    {
        void Edit(bool on)
        {
            editor.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            view.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            if (on) FocusWhenVisible(first);
            else draw();
        }
        var body = new Grid { Children = { view, editor } };
        body.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is true || editor.Visibility != Visibility.Visible) return;
            Edit(false);
            if (empty()) Dispatcher.BeginInvoke(() => Remove(it));
        };
        Edit(focus);
        return (body, Edit);
    }

    // ---- note: optional title and Markdown ----

    (FrameworkElement, Action<bool>?, Action?) Note(BoardItem it, bool focus)
    {
        var title = Field(it.Title, "Title (optional)", 15, bold: true);
        var box = Field(it.Text, "Markdown: **bold**, - [ ] task, # heading…", multiline: true);
        title.Margin = new(0, 0, 0, 6);
        title.TextChanged += (_, _) => { it.Title = title.Text; Changed(); };
        box.TextChanged += (_, _) => { it.Text = box.Text; Changed(); };
        var view = new StackPanel();
        void Draw()
        {
            view.Children.Clear();
            view.Children.Add(Heading(it.Title));
            view.Children.Add(it.Text.Trim() == "" && it.Title.Trim() == ""
                ? new TextBlock { Text = "Empty note", Style = (Style)FindResource("Label") }
                : MarkdownView.Render(it.Text, at => box.Text = MarkdownView.Toggle(it.Text, at))); // box saves it
        }
        var (body, edit) = Editable(it, view, new StackPanel { Children = { title, box } }, box, Draw,
            () => it.Title.Trim() == "" && it.Text.Trim() == "", focus);
        return (body, edit, null);
    }

    // ---- link: a website, file or folder, opened with its button ----

    (FrameworkElement, Action<bool>?, Action?) Link(BoardItem it, bool focus)
    {
        var name = Field(it.Title, "Name (optional)", 15, bold: true);
        var target = Field(it.Text, "Web address, file or folder");
        name.Margin = new(0, 0, 0, 6);
        name.TextChanged += (_, _) => { it.Title = name.Text; Changed(); };
        target.TextChanged += (_, _) => { it.Text = target.Text; Changed(); };
        var view = new DockPanel();
        void Draw()
        {
            view.Children.Clear();
            var t = it.Text.Trim().Trim('"');
            var web = WebAddress(t);
            var folder = web is null && Directory.Exists(t);
            var open = IconButton("", "Open", () => Open(t));
            DockPanel.SetDock(open, Dock.Right);
            var icon = new TextBlock
            {
                Text = web is not null ? "" : folder ? "" : "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 20,
                Foreground = (Brush)FindResource("Accent"), Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(icon, Dock.Left);
            var shown = it.Title.Trim() != "" ? it.Title : web?.Host.Replace("www.", "") ?? (Path.GetFileName(t.TrimEnd('\\', '/')) is { Length: > 0 } f ? f : t);
            view.Children.Add(open);
            view.Children.Add(icon);
            view.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center, ToolTip = t,
                Children =
                {
                    new TextBlock { Text = shown, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = t, Style = (Style)FindResource("Label"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
        }
        var (body, edit) = Editable(it, view, new StackPanel { Children = { name, target } }, target, Draw, () => it.Text.Trim() == "", focus);
        return (body, edit, null);
    }

    /// http(s) and mailto addresses; "example.com" counts as https://example.com. Null for anything else (paths).
    static Uri? WebAddress(string t)
    {
        if (!t.Contains("://") && !t.StartsWith("mailto:") && !t.Contains('\\') && !t.Contains(' ') && t.Contains('.') && !File.Exists(t)) t = "https://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto" ? u : null;
    }

    /// Only web/mail addresses and files or folders that exist open (in their default app or Explorer).
    static void Open(string t)
    {
        var target = WebAddress(t)?.AbsoluteUri ?? (File.Exists(t) || Directory.Exists(t) ? t : null);
        if (target is null) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Win32Exception) { } // no app for it, or cancelled
    }

    // ---- timer: counts down, alerts when done; keeps running across restarts ----

    (FrameworkElement, Action<bool>?, Action?) Timer(BoardItem it, bool focus)
    {
        var name = Field(it.Title, "Name (optional)", 15, bold: true);
        var length = Field(BoardStore.Clock(it.Seconds), "Minutes, or m:ss");
        name.Margin = new(0, 0, 0, 6);
        name.TextChanged += (_, _) => { it.Title = name.Text; Changed(); };
        length.TextChanged += (_, _) =>
        {
            if (BoardStore.ParseDuration(length.Text) is not { } s) return;
            (it.Seconds, it.Ends, it.Left) = (s, null, 0);
            Changed();
        };

        var heading = Heading(it.Title);
        var clock = new TextBlock { FontSize = 36, FontWeight = FontWeights.Light, HorizontalAlignment = HorizontalAlignment.Center };
        var fill = new Border { Background = (Brush)FindResource("Accent"), CornerRadius = new(2), RenderTransform = new ScaleTransform(1, 1) };
        var bar = new Grid { Height = 4, Margin = new(0, 6, 0, 8), Children = { new Border { Background = (Brush)FindResource("Track"), CornerRadius = new(2) }, fill } };
        Button play = null!;
        play = IconButton("", "Start", () =>
        {
            if (it.Ends is not null) (it.Left, it.Ends) = (Remaining(), null); // pause
            else (it.Ends, it.Left) = (DateTime.Now.AddSeconds(Remaining()), 0);
            Update();
            Changed();
        });
        var reset = IconButton("", "Reset", () => { (it.Ends, it.Left) = (null, 0); Update(); Changed(); });
        var view = new StackPanel
        {
            Children = { heading, clock, bar, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { play, reset } } },
        };

        double Remaining() => it.Ends is { } e ? Math.Max(0, (e - DateTime.Now).TotalSeconds) : it.Left > 0 ? it.Left : it.Seconds;
        void Update()
        {
            var left = Remaining();
            clock.Text = BoardStore.Clock(left);
            ((ScaleTransform)fill.RenderTransform).ScaleX = it.Seconds > 0 ? left / it.Seconds : 0;
            play.Content = it.Ends is null ? "" : "";
            play.ToolTip = it.Ends is null ? "Start" : "Pause";
        }
        void Finish()
        {
            (it.Ends, it.Left) = (null, 0);
            Changed();
            var accent = ((SolidColorBrush)FindResource("Accent")).Color;
            AlertWindow.Show(new Toast(it.Title.Trim() == "" ? "Timer" : $"Timer · {it.Title}", "Time's up", "", "Length",
                BoardStore.Clock(it.Seconds), accent, true), () => { });
        }
        void Draw()
        {
            heading.Text = it.Title;
            heading.Visibility = it.Title.Trim() == "" ? Visibility.Collapsed : Visibility.Visible;
            length.Text = BoardStore.Clock(it.Seconds);
            Update();
        }

        var (body, edit) = Editable(it, view, new StackPanel { Children = { name, length } }, length, Draw, () => false, focus);
        // Only the running tick finishes it, so building the node (opening the page) never alerts by itself.
        void Ticked()
        {
            if (it.Ends is { } end && DateTime.Now >= end) Finish();
            Update();
        }
        Tick += Ticked;
        return (body, edit, () => Tick -= Ticked);
    }

    // ---- live stat: a value and its recent history, from the same samples as the tray and top bar ----

    (FrameworkElement, Action<bool>?, Action?) Stat(BoardItem it)
    {
        var metric = Metrics.Contains(it.Text) ? it.Text : Metrics[0];
        var (color, format, max) = metric switch
        {
            "GPU" => (Color.FromRgb(0x4C, 0xC3, 0x7A), "{0:0}%", 100f),
            "RAM" => (Color.FromRgb(0xF5, 0xB8, 0x3D), "{0:0}%", 100f),
            "Download" => (Color.FromRgb(0x5A, 0xA9, 0xFF), "rate", 0f),
            "Upload" => (Color.FromRgb(0xB0, 0x7C, 0xFF), "rate", 0f),
            _ => (Color.FromRgb(0xE6, 0xA6, 0x5D), "{0:0}%", 100f),
        };
        float? Read(Snapshot s) => metric switch
        {
            "GPU" => s.GpuLoad,
            "RAM" => s.RamUsed / s.RamTotal * 100,
            "Download" => (float)s.NetDown,
            "Upload" => (float)s.NetUp,
            _ => s.CpuLoad,
        };

        var pick = new Button { Style = (Style)FindResource("Ghost"), Padding = new(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Show something else" };
        pick.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = metric, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 9, Margin = new(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        pick.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = pick, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var m in Metrics)
            {
                var item = new MenuItem { Header = m };
                item.Click += (_, _) => { it.Text = m; Rebuild(it); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        var value = new TextBlock { Text = "—", FontSize = 28, FontWeight = FontWeights.Light, Margin = new(0, 2, 0, 4), Foreground = new SolidColorBrush(color) };
        var spark = new Spark { Color = color, Format = format, Max = max, Height = 56 };
        Action<Snapshot> sampled = s => Dispatcher.BeginInvoke(() =>
        {
            var v = Read(s);
            spark.Push(v);
            value.Text = v is { } x ? Spark.Text(format, x) : "—";
        });
        App.Sampled += sampled;
        return (new StackPanel { Children = { pick, value, spark } }, null, () => App.Sampled -= sampled);
    }
}
