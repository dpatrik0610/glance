using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        var n = Current;
        Editor.DataContext = n;
        Editor.Visibility = n is null ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = NoteStore.Notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (n is null) return;
        TextEditor.Visibility = n.Kind == NoteKind.Text ? Visibility.Visible : Visibility.Collapsed;
        ChecklistEditor.Visibility = n.Kind == NoteKind.Checklist ? Visibility.Visible : Visibility.Collapsed;
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
        if (MessageBox.Show($"Delete \"{n.DisplayTitle}\"?", "Glance", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var i = List.SelectedIndex;
        NoteStore.Notes.Remove(n);
        List.SelectedIndex = Math.Min(i, NoteStore.Notes.Count - 1);
    }

    void NewItemKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Current is not { } n || NewItem.Text.Trim().Length == 0) return;
        n.Items.Add(new TodoItem { Text = NewItem.Text.Trim() });
        NewItem.Clear();
    }

    void DeleteItem(object sender, RoutedEventArgs e) =>
        Current?.Items.Remove((TodoItem)((FrameworkElement)sender).DataContext);

    void ClearDone(object sender, RoutedEventArgs e)
    {
        if (Current is not { } n) return;
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
