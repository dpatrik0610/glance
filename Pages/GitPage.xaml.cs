using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Glance.Services;

namespace Glance.Pages;

public partial class GitPage : UserControl
{
    public GitPage()
    {
        InitializeComponent();
        // Coming back within half a minute shows what's there; the Refresh button always reloads.
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue && DateTime.Now - loadedAt > TimeSpan.FromSeconds(30)) Load(); };
    }

    DateTime loadedAt;

    async void Load()
    {
        loadedAt = DateTime.Now;
        var repos = await Git.Repos();
        Repos.ItemsSource = repos;
        Count.Text = $"{repos.Count} in quick folders";
        Empty.Text = "No repos in your CLI quick folders";
        Empty.Visibility = repos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Refresh(object sender, RoutedEventArgs e) => Load();

    static Repo RepoOf(object sender) => (Repo)((FrameworkElement)sender).DataContext;

    void OpenCli(object sender, RoutedEventArgs e) => ((App)Application.Current).OpenCli(RepoOf(sender).Path);

    void OpenFolder(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{RepoOf(sender).Path}\"");

    void OpenWeb(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(RepoOf(sender).Web!) { UseShellExecute = true });
}
