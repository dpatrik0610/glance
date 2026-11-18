using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

/// What goes inside each kind of node. The card around it is BoardPage.Show (canvas) or KanbanCard.
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
            case "stat": return Stat(it);
            case "group": return Group(it, focus);
            case "embed": return Embed(it);
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

    static TextBlock Heading(string text) => new()
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
            ApplyTag();
            if (empty()) Dispatcher.BeginInvoke(() => Remove(it));
        };
        Edit(focus);
        return (body, Edit);
    }

    // ---- note: optional title and Markdown ----

    (FrameworkElement, Action<bool>?, Action?) Note(BoardItem it, bool focus)
    {
        var title = Field(it.Title, "Title (optional)", 15, bold: true);
        var box = Field(it.Text, "Markdown: **bold**, - [ ] task @tomorrow 9, [[link]], #tag…", multiline: true);
        title.Margin = new(0, 0, 0, 6);
        title.TextChanged += (_, _) => { it.Title = title.Text; Changed(); };
        box.TextChanged += (_, _) => { it.Text = box.Text; Changed(); };
        var view = new StackPanel();
        void Draw()
        {
            // "@tomorrow 9" on a task becomes a fixed date when editing ends, so it doesn't move every day.
            if (BoardStore.FixDues(it.Text, DateTime.Now) is var fixedText && fixedText != it.Text) box.Text = fixedText;
            view.Children.Clear();
            view.Children.Add(Heading(it.Title));
            view.Children.Add(it.Text.Trim() == "" && it.Title.Trim() == ""
                ? new TextBlock { Text = "Empty note", Style = (Style)FindResource("Label") }
                : MarkdownView.Render(it.Text, n => box.Text = MarkdownView.Toggle(it.Text, n), Go, ShowTag)); // box saves it
        }
        var (body, edit) = Editable(it, view, new StackPanel { Children = { title, box } }, box, Draw,
            () => it.Title.Trim() == "" && it.Text.Trim() == "", focus);
        return (body, edit, null);
    }

    // ---- group: a labeled frame; dragging it carries what's inside ----

    (FrameworkElement, Action<bool>?, Action?) Group(BoardItem it, bool focus)
    {
        var name = Field(it.Title, "Group name", 15, bold: true);
        name.TextChanged += (_, _) => { it.Title = name.Text; Changed(); };
        var label = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Sub"), TextTrimming = TextTrimming.CharacterEllipsis };
        void Draw() => label.Text = it.Title.Trim() == "" ? "Group" : it.Title;
        var (body, edit) = Editable(it, label, name, name, Draw, () => false, focus);
        body.VerticalAlignment = VerticalAlignment.Top;
        return (body, edit, null);
    }

    // ---- embed: a Notes-page note, shown live; double-click opens it there ----

    (FrameworkElement, Action<bool>?, Action?) Embed(BoardItem it)
    {
        var view = new StackPanel();
        var note = NoteStore.Notes.FirstOrDefault(n => n.Id == it.Text);
        void Open()
        {
            if (note is null) return;
            App.Go("Notes");
            NoteStore.RequestFocus(note);
        }
        void Draw()
        {
            view.Children.Clear();
            if (note is null || !NoteStore.Notes.Contains(note))
            {
                view.Children.Add(new TextBlock { Text = "This note was deleted", Style = (Style)FindResource("Label") });
                return;
            }
            view.Children.Add(new DockPanel
            {
                Margin = new(0, 0, 0, 6),
                Children =
                {
                    new TextBlock { Text = note.Icon, FontFamily = (FontFamily)FindResource("Icons"), Foreground = (Brush)FindResource("Accent"), Margin = new(0, 2, 8, 0), ToolTip = "From Notes (double-click to open it there)" },
                    new TextBlock { Text = note.DisplayTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                },
            });
            if (note.Kind == NoteKind.Text)
                view.Children.Add(MarkdownView.Render(note.Text, n => note.Text = MarkdownView.Toggle(note.Text, n), Go, ShowTag));
            else
                foreach (var item in note.Items)
                {
                    var box = new CheckBox
                    {
                        Style = (Style)FindResource("Check"), IsChecked = item.Done, LayoutTransform = new ScaleTransform(0.8, 0.8),
                        Margin = new(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top,
                    };
                    box.Click += (_, _) => item.Done = box.IsChecked == true;
                    var text = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, Opacity = item.Done ? 0.55 : 1 };
                    if (item.Due is not null) text.Inlines.Add(new System.Windows.Documents.Run("  " + item.DueText) { Foreground = (Brush)FindResource(item.IsOverdue ? "Red" : "Sub"), FontSize = 12 });
                    DockPanel.SetDock(box, Dock.Left);
                    view.Children.Add(new DockPanel { Margin = new(0, 0, 0, 3), Children = { box, text } });
                }
        }
        // Redraws after the note changes, at most once per burst of changes (typing in Notes).
        var pending = false;
        void Soon()
        {
            if (pending) return;
            pending = true;
            Dispatcher.BeginInvoke(() => { pending = false; Draw(); }, System.Windows.Threading.DispatcherPriority.Background);
        }
        PropertyChangedEventHandler changed = (_, _) => Soon();
        NotifyCollectionChangedEventHandler listChanged = (_, _) => Soon();
        if (note is not null)
        {
            note.PropertyChanged += changed;
            note.Items.CollectionChanged += listChanged;
        }
        NoteStore.Notes.CollectionChanged += listChanged; // deleted
        Draw();
        return (view, on => { if (on) Open(); }, () =>
        {
            if (note is not null)
            {
                note.PropertyChanged -= changed;
                note.Items.CollectionChanged -= listChanged;
            }
            NoteStore.Notes.CollectionChanged -= listChanged;
        });
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
    internal static Uri? WebAddress(string t)
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
            var menu = new ContextMenu();
            foreach (var m in Metrics)
            {
                var item = new MenuItem { Header = m };
                item.Click += (_, _) => { it.Text = m; Rebuild(it); };
                menu.Items.Add(item);
            }
            Open(menu, pick);
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
