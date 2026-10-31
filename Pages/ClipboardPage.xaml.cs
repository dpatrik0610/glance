using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Glance.Services;

namespace Glance.Pages;

public partial class ClipboardPage : UserControl
{
    readonly ICollectionView view = CollectionViewSource.GetDefaultView(ClipboardHistory.Items);

    public ClipboardPage()
    {
        InitializeComponent();
        List.ItemsSource = view;
        RecordBox.IsChecked = App.Settings.ClipboardHistory;
        view.Filter = o => Search.Text.Length == 0 || ((ClipItem)o).Text.Contains(Search.Text, StringComparison.OrdinalIgnoreCase);
    }

    void SearchChanged(object sender, TextChangedEventArgs e) => view.Refresh();

    void CopyItem(object sender, MouseButtonEventArgs e)
    {
        var item = (ClipItem)((FrameworkElement)sender).DataContext;
        Status.Text = ClipboardHistory.Copy(item.Text) ? "Copied." : "Clipboard busy, try again.";
    }

    void RemoveItem(object sender, RoutedEventArgs e)
    {
        ClipboardHistory.Items.Remove((ClipItem)((FrameworkElement)sender).DataContext);
        e.Handled = true; // don't also copy the row
    }

    void ClearAll(object sender, RoutedEventArgs e) => ClipboardHistory.Items.Clear();

    void RecordToggled(object sender, RoutedEventArgs e)
    {
        App.Settings.ClipboardHistory = RecordBox.IsChecked == true;
        App.ApplySettings();
    }
}
