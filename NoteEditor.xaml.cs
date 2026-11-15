using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;

namespace Glance;

public partial class NoteEditor : UserControl
{
    public NoteEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ShowKind();
    }

    /// For a sticky's small window: no hint line, due times as plain text, the text straight on the tint.
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(NoteEditor),
        new PropertyMetadata(false, (d, _) => ((NoteEditor)d).ApplyCompact()));

    // What the checklist rows show (read when they're built; Compact is set once, in markup).
    public Visibility Full => Compact ? Visibility.Collapsed : Visibility.Visible;
    public Visibility Brief => Compact ? Visibility.Visible : Visibility.Collapsed;

    Note? Note => DataContext as Note;

    void ShowKind()
    {
        var n = Note;
        TextEditor.Visibility = n?.Kind == NoteKind.Text ? Visibility.Visible : Visibility.Collapsed;
        ChecklistEditor.Visibility = n?.Kind == NoteKind.Checklist ? Visibility.Visible : Visibility.Collapsed;
    }

    void ApplyCompact()
    {
        Hint.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        AddRow.Margin = new Thickness(0, Compact ? 6 : 12, 0, 0);
        if (Compact)
        {
            TextEditor.Background = System.Windows.Media.Brushes.Transparent;
            TextEditor.BorderThickness = new Thickness(0);
            TextEditor.Padding = new Thickness(2, 2, 2, 2);
        }
        else
        {
            TextEditor.ClearValue(BackgroundProperty);
            TextEditor.ClearValue(BorderThicknessProperty);
            TextEditor.Padding = new Thickness(14, 12, 14, 12);
        }
    }

    /// The font and size menu under target. Fonts are listed on first open (Glance's own first).
    public void ShowFontMenu(UIElement target)
    {
        FontList.ItemsSource ??= Fonts.SystemFontFamilies.Select(f => f.Source).Order().Prepend(Note.DefaultFont).Distinct().ToList();
        FontMenu.PlacementTarget = target;
        FontMenu.IsOpen = true;
        FontList.ScrollIntoView(FontList.SelectedItem);
    }

    // Ctrl+wheel: bigger or smaller text, like a browser.
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && Note is { } n)
        {
            n.FontSize += e.Delta > 0 ? 1 : -1;
            e.Handled = true;
        }
        base.OnPreviewMouseWheel(e);
    }

    public void FocusText()
    {
        if (Note?.Kind == NoteKind.Checklist) NewItem.Focus(); else TextEditor.Focus();
    }

    void NewItemKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Note is not { } n || NewItem.Text.Trim().Length == 0) return;
        n.Items.Add(new TodoItem { Text = NewItem.Text.Trim() });
        NewItem.Clear();
    }

    void DeleteItem(object sender, RoutedEventArgs e) =>
        Note?.Items.Remove((TodoItem)((FrameworkElement)sender).DataContext);

    void ClearDone(object sender, RoutedEventArgs e)
    {
        if (Note is not { } n) return;
        foreach (var i in n.Items.Where(i => i.Done).ToList()) n.Items.Remove(i);
    }

    // The due box commits on focus loss by default; Enter should commit too.
    void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }
}
