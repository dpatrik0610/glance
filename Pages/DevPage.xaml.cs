using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Glance.Services;

namespace Glance.Pages;

public partial class DevPage : UserControl
{
    List<PortInfo> all = [];

    public DevPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Load(); };
    }

    async void Load()
    {
        all = await Task.Run(Native.ListeningPorts);
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var f = Filter.Text.Trim();
        Ports.ItemsSource = f.Length == 0 ? all
            : all.Where(p => p.Port.ToString().Contains(f) || p.Process.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    void Refresh(object sender, RoutedEventArgs e) => Load();

    void FilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    void OpenPort(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"http://localhost:{((PortInfo)((FrameworkElement)sender).DataContext).Port}") { UseShellExecute = true });

    void Kill(object sender, RoutedEventArgs e)
    {
        var p = (PortInfo)((FrameworkElement)sender).DataContext;
        if (MessageBox.Show($"Kill {p.Process} (PID {p.Pid}) listening on :{p.Port}?", "Glance",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            using var proc = Process.GetProcessById(p.Pid);
            proc.Kill();
            proc.WaitForExit(2000);
            Db.Info($"killed {p.Process} pid={p.Pid} port={p.Port}");
        }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "Glance"); // already gone, or needs admin
        }
        Load();
    }
}
