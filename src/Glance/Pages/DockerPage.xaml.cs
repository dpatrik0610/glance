using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

public partial class DockerPage : UserControl, IHeader
{
    public FrameworkElement Header { get; }
    public FrameworkElement State { get; }

    readonly DispatcherTimer timer;
    bool busy;

    public DockerPage()
    {
        InitializeComponent();
        State = MainWindow.Lift(Count);
        Header = MainWindow.Lift(Bar);
        // Polls only while visible: picks up containers started from a terminal, and Docker Desktop finishing its start.
        timer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) => Load(), Dispatcher);
        IsVisibleChanged += (_, e) =>
        {
            timer.IsEnabled = (bool)e.NewValue;
            if (timer.IsEnabled) Load();
        };
    }

    async void Load()
    {
        if (busy) return;
        busy = true;
        try
        {
            var (state, list) = await Docker.Containers();
            Rows.ItemsSource = list;
            Count.Text = state == Docker.State.Ok ? $"{list.Count(c => c.Running)} running · {list.Count} total" : "";
            Columns.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Empty.Visibility = list.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyText.Text = state switch
            {
                Docker.State.NotInstalled => "Docker isn't installed",
                Docker.State.NotRunning => "Docker isn't running",
                _ => "No containers",
            };
            StartDesktop.Visibility = state == Docker.State.NotRunning && File.Exists(Docker.Desktop) ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { busy = false; }
    }

    void Refresh(object sender, RoutedEventArgs e) => Load();

    void StartDocker(object sender, RoutedEventArgs e)
    {
        Services.Shell.Open(Docker.Desktop);
        EmptyText.Text = "Starting Docker…";
        StartDesktop.Visibility = Visibility.Collapsed;
    }

    static Container Of(object sender) => (Container)((FrameworkElement)sender).DataContext;

    async void Act(object sender, string verb)
    {
        var c = Of(sender);
        ((Button)sender).IsEnabled = false;
        if (await Docker.Do(verb, c.ID) is { } error) Dialog.Info("Docker", $"Couldn't {verb} {c.Names}", error);
        Load();
    }

    void Toggle(object sender, RoutedEventArgs e) => Act(sender, Of(sender).Running ? "stop" : "start");

    void Restart(object sender, RoutedEventArgs e) => Act(sender, "restart");

    // Container names are [a-zA-Z0-9][a-zA-Z0-9_.-]+, safe to put in a command line as is.
    void Logs(object sender, RoutedEventArgs e) =>
        Terminal($"docker logs -f --tail 200 {Of(sender).Names}", Of(sender).Names + " logs");

    void Shell(object sender, RoutedEventArgs e) =>
        Terminal($"docker exec -it {Of(sender).Names} sh -c 'command -v bash >/dev/null && exec bash || exec sh'", Of(sender).Names);

    static void Terminal(string command, string name) =>
        ((App)Application.Current).OpenCli(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), command, name);
}
