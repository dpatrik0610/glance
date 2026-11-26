using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

/// Notes and stickies in one list: a sticky is a note with Sticky on (it's also out on the desktop).
public partial class NotesPage : UserControl, IDisposable, IHeader
{
    public FrameworkElement Header { get; }
    public FrameworkElement State { get; } = new TextBlock { Style = Skin.Style("Label") };

    readonly ListCollectionView view = new(NoteStore.Notes);

    public NotesPage()
    {
        InitializeComponent();
        Header = MainWindow.Lift(Bar);
        // The body sits straight on the page like the title; the boxed look is for forms
        Body.TextEditor.Background = Brushes.Transparent;
        Body.TextEditor.BorderThickness = new(0);
        Body.TextEditor.Padding = new(0, 4, 8, 4);
        view.Filter = Shows;
        List.ItemsSource = view;
        NoteStore.FocusRequested += FocusNote;
        NoteStore.Changed += Count;
        NoteStore.Notes.CollectionChanged += NotesChanged;
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            view.Refresh(); // fresh "5 min ago"s
            Count();
        };
        SelectFirst();
        Count();
    }

    public void Dispose()
    {
        NoteStore.FocusRequested -= FocusNote;
        NoteStore.Changed -= Count;
        NoteStore.Notes.CollectionChanged -= NotesChanged;
        view.DetachFromSourceCollection();
    }

    Note? Current => List.SelectedItem as Note;

    bool Shows(object o) => o is Note n && (StickyChip.IsChecked == true ? n.Sticky : DueChip.IsChecked != true || n.NextDue != "");

    // Counts on the chips and the state line; on every edit (cheap: a pass over the notes).
    void Count()
    {
        var notes = NoteStore.Notes;
        var stickies = notes.Count(n => n.Sticky);
        AllCount.Text = $"{notes.Count}";
        StickyCount.Text = $"{stickies}";
        DueCount.Text = $"{notes.Count(n => n.NextDue != "")}";
        var next = notes.SelectMany(n => n.Items).Where(i => !i.Done && i.Due is not null).MinBy(i => i.Due);
        var parts = new List<string> { notes.Count == 1 ? "1 note" : $"{notes.Count} notes" };
        if (next is not null) parts.Add("next due " + next.DueText);
        if (stickies > 0 && !App.Settings.StickiesShown) parts.Add("stickies hidden (tray)");
        ((TextBlock)State).Text = notes.Count == 0 ? "" : string.Join(" · ", parts);
    }

    void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return; // the first chip is checked while the page is built
        view.Refresh();
        if (Current is null || !view.Contains(Current)) SelectFirst();
        ShowSelected();
    }

    void SelectFirst() => List.SelectedIndex = List.Items.Count > 0 ? 0 : -1;

    void FocusNote(Note n)
    {
        if (!view.Contains(n)) AllChip.IsChecked = true;
        List.SelectedItem = n;
        List.ScrollIntoView(n);
    }

    void NotesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ShowSelected();

    void Selected(object sender, SelectionChangedEventArgs e) => ShowSelected();

    void ShowSelected()
    {
        Editor.DataContext = Current;
        Editor.Visibility = Current is null ? Visibility.Collapsed : Visibility.Visible;
        var any = NoteStore.Notes.Count > 0;
        Split.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ListEmpty.Visibility = any && List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListEmpty.Text = StickyChip.IsChecked == true
            ? "No stickies. Turn on Sticky on a note to keep it on the desktop."
            : "Nothing due. Give a checklist item a due time and it shows up here.";
    }

    void NewText(object sender, RoutedEventArgs e) => Add(NoteKind.Text);

    void NewChecklist(object sender, RoutedEventArgs e) => Add(NoteKind.Checklist);

    void OpenToday(object sender, RoutedEventArgs e) => FocusNote(NoteStore.Today());

    void Add(NoteKind kind)
    {
        AllChip.IsChecked = true;
        var n = new Note { Kind = kind };
        NoteStore.Notes.Insert(0, n);
        FocusNote(n);
        TitleBox.Focus();
    }

    // Turned on: out on the desktop now, even if stickies were hidden from the tray.
    void StickyClick(object sender, RoutedEventArgs e)
    {
        if (Current is { Sticky: true } n) Stickies.Reveal(n);
        Count();
    }

    void LookClick(object sender, RoutedEventArgs e)
    {
        if (Current is { } n) StickyWindow.ShowLook(n, (UIElement)sender);
    }

    void DeleteNote(object sender, RoutedEventArgs e)
    {
        if (Current is not { } n) return;
        if (!Dialog.Confirm("Delete note", $"Delete \"{n.DisplayTitle}\"?", n.Sticky ? "It also leaves the desktop. This can't be undone." : "This can't be undone.", "Delete")) return;
        var i = List.SelectedIndex;
        NoteStore.Notes.Remove(n);
        List.SelectedIndex = Math.Min(i, List.Items.Count - 1);
    }

    void FontClick(object sender, RoutedEventArgs e) => Body.ShowFontMenu((UIElement)sender);
}
