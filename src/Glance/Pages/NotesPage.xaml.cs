using System.Windows;
using System.Windows.Controls;
using Glance.Services;

namespace Glance.Pages;

public partial class NotesPage : UserControl, IDisposable
{
    public NotesPage()
    {
        InitializeComponent();
        List.ItemsSource = NoteStore.Notes;
        NoteStore.FocusRequested += FocusNote;
        NoteStore.Notes.CollectionChanged += NotesChanged;
        List.SelectedIndex = NoteStore.Notes.Count > 0 ? 0 : -1;
        ShowSelected();
    }

    void FocusNote(Note n) => List.SelectedItem = n;
    void NotesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ShowSelected();

    public void Dispose()
    {
        NoteStore.FocusRequested -= FocusNote;
        NoteStore.Notes.CollectionChanged -= NotesChanged;
    }

    Note? Current => List.SelectedItem as Note;

    void Selected(object sender, SelectionChangedEventArgs e) => ShowSelected();

    void ShowSelected()
    {
        Editor.DataContext = Current;
        Editor.Visibility = Current is null ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = NoteStore.Notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void NewText(object sender, RoutedEventArgs e) => Add(NoteKind.Text);

    void NewChecklist(object sender, RoutedEventArgs e) => Add(NoteKind.Checklist);

    void OpenToday(object sender, RoutedEventArgs e) => List.SelectedItem = NoteStore.Today();

    void Add(NoteKind kind)
    {
        var n = new Note { Kind = kind };
        NoteStore.Notes.Insert(0, n);
        List.SelectedItem = n;
        TitleBox.Focus();
    }

    void DeleteNote(object sender, RoutedEventArgs e)
    {
        if (Current is not { } n) return;
        if (!Dialog.Confirm("Delete note", $"Delete \"{n.DisplayTitle}\"?", "This can't be undone.", "Delete")) return;
        var i = List.SelectedIndex;
        NoteStore.Notes.Remove(n);
        List.SelectedIndex = Math.Min(i, NoteStore.Notes.Count - 1);
    }

    void FontClick(object sender, RoutedEventArgs e) => Body.ShowFontMenu((UIElement)sender);
}
