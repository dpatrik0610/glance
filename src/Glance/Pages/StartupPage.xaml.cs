using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Glance.Services;

namespace Glance.Pages;

public partial class StartupPage : UserControl, IHeader
{
    public FrameworkElement Header { get; }
    public FrameworkElement State { get; }

    ObservableCollection<StartupItem> items = [];
    string kind = "";
    bool busy;
    DateTime loadedAt;

    public StartupPage()
    {
        InitializeComponent();
        State = MainWindow.Lift(Count);
        Header = MainWindow.Lift(Bar);
        // Nothing here changes by itself often: coming back within half a minute shows what's there; F5 or Refresh reloads.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - loadedAt > TimeSpan.FromSeconds(30)) Load(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F5) { Load(); e.Handled = true; } };
    }

    async void Load()
    {
        if (busy) return;
        busy = true;
        loadedAt = DateTime.Now;
        try
        {
            // On first; a switched row stays where it is until the next refresh, so it doesn't jump from under the mouse.
            items = new((await Task.Run(Startup.List)).OrderBy(i => !i.Enabled));
            Rows.ItemsSource = items;
            CollectionViewSource.GetDefaultView(items).Filter = o => Shown((StartupItem)o);
            Show();
        }
        finally { busy = false; }
    }

    bool Shown(StartupItem i) =>
        (WindowsBox.IsChecked == true || !i.Windows)
        && kind switch { "Apps" => i.Kind is StartupKind.Registry or StartupKind.Folder, "" => true, _ => i.Kind.ToString() == kind };

    void Show()
    {
        CollectionViewSource.GetDefaultView(items).Refresh();
        var shown = items.Where(Shown).ToList();
        var off = shown.Count(i => !i.Enabled);
        var missing = shown.Count(i => i.Missing);
        var running = shown.Count(i => i.Running);
        Count.Text = $"{shown.Count - off} on" + (off > 0 ? $" · {off} off" : "") + $" · {running} running" + (missing > 0 ? $" · {missing} missing" : "")
            + (WindowsBox.IsChecked == true ? "" : $" · {items.Count(i => i.Windows)} Windows entries hidden");
        Columns.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = shown.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = "Nothing starts here";
    }

    void KindPicked(object sender, RoutedEventArgs e)
    {
        kind = (string)((FrameworkElement)sender).Tag;
        if (IsLoaded) Show();
    }

    void WindowsToggled(object sender, RoutedEventArgs e) => Show();

    void Refresh(object sender, RoutedEventArgs e) => Load();

    static StartupItem Of(object sender) => (StartupItem)((FrameworkElement)sender).DataContext;

    async void Toggled(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var item = Of(sender);
        var on = box.IsChecked == true;
        box.IsEnabled = false;
        // A disabled service goes back to the start mode it had before (remembered), else Automatic.
        var modes = App.Settings.StartupServiceModes;
        var service = item.Kind == StartupKind.Service;
        var mode = on ? modes.GetValueOrDefault(item.Target, "auto") : service ? Startup.ModeOf(item) : "auto";
        if (await Startup.Switch(item, on, mode) is { } error)
        {
            Dialog.Info("Startup", $"Couldn't turn {item.Name} {(on ? "on" : "off")}", error);
            Load(); // show what Windows has now
            return;
        }
        var source = item.Source;
        if (service)
        {
            if (on) modes.Remove(item.Target);
            else modes[item.Target] = mode;
            App.Settings.Save();
            source = !on ? "Disabled" : mode switch { "delayed-auto" => "Automatic (delayed)", "demand" => "Manual", _ => "Automatic" }; // Manual or on trigger: the next refresh tells
        }
        var i = items.IndexOf(item);
        if (i >= 0) items[i] = item with { Enabled = on, Source = source }; // re-renders that row only
        Show();
    }

    async void StopNow(object sender, RoutedEventArgs e)
    {
        var item = Of(sender);
        var service = item.Kind == StartupKind.Service;
        if (!Dialog.Confirm("Startup", $"Stop {item.Name}?", service
            ? "Whatever uses it stops working until it starts again: when something asks for it, or at the next start if it's on."
            : $"Every {Path.GetFileName(item.File)} process ends. Unsaved work in it is lost.", "Stop")) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        if (await Startup.StopNow(item) is { } error)
        {
            Dialog.Info("Startup", $"Couldn't stop {item.Name}", error);
            button.IsEnabled = true;
            return;
        }
        var i = items.IndexOf(item);
        if (i >= 0) items[i] = item with { Running = false };
        Show();
    }

    void ShowFile(object sender, RoutedEventArgs e)
    {
        var item = Of(sender);
        if (item.File is { } f && File.Exists(f)) Shell.Reveal(f);
        else Dialog.Info("Startup", item.Missing ? $"{item.Name}: the file is gone" : $"No program path in {item.Name}",
            item.Missing ? $"{item.File} doesn't exist. Turn the entry off, or remove it at its Source." : item.Command);
    }

    void ShowSource(object sender, RoutedEventArgs e)
    {
        try { Startup.ShowSource(Of(sender)); }
        catch (Win32Exception) { } // the administrator prompt for Registry Editor was declined
    }
}
