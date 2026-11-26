using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Glance.Services;

namespace Glance.Pages;

public partial class ClipboardPage : UserControl, IHeader
{
    public FrameworkElement Header { get; }

    readonly ICollectionView view = CollectionViewSource.GetDefaultView(ClipboardHistory.Items);

    public ClipboardPage()
    {
        InitializeComponent();
        Header = MainWindow.Lift(Bar);
        List.ItemsSource = view;
        view.Filter = o => Search.Text.Length == 0 || ((ClipItem)o).Text.Contains(Search.Text, StringComparison.OrdinalIgnoreCase);
        ShowSwitches();
        IsVisibleChanged += (_, _) => ShowSwitches(); // Settings has the same switches
    }

    void ShowSwitches()
    {
        RecordBox.IsChecked = App.Settings.ClipboardHistory;
        ImagesBox.IsChecked = App.Settings.ClipboardImages;
        ImagesBox.IsEnabled = App.Settings.ClipboardHistory;
    }

    void SearchChanged(object sender, TextChangedEventArgs e) => view.Refresh();

    void CopyItem(object sender, MouseButtonEventArgs e)
    {
        var item = (ClipItem)((FrameworkElement)sender).DataContext;
        var ok = item.IsImage ? ClipboardHistory.CopyImage(item) : ClipboardHistory.Copy(item.Text);
        Status.Text = ok ? "Copied." : "Clipboard busy, try again.";
    }

    void OpenItem(object sender, RoutedEventArgs e)
    {
        try { ClipboardHistory.Open((ClipItem)((FrameworkElement)sender).DataContext); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Status.Text = "Couldn't open it: " + ex.Message;
        }
        e.Handled = true; // don't also copy the row
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
        App.Settings.ClipboardImages = ImagesBox.IsChecked == true;
        ImagesBox.IsEnabled = App.Settings.ClipboardHistory;
        App.ApplySettings();
    }
}
