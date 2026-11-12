using System.Windows;
using System.Windows.Controls;
using Glance.Services;

namespace Glance.Pages;

public partial class NotesPage : UserControl
{
    public NotesPage()
    {
        InitializeComponent();
        List.ItemsSource = NoteStore.Notes;
        NoteStore.FocusRequested += n => List.SelectedItem = n;
        NoteStore.Notes.CollectionChanged += (_, _) => ShowSelected();
        List.SelectedIndex = NoteStore.Notes.Count > 0 ? 0 : -1;
        ShowSelected();
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
}
