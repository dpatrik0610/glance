using System.Windows;
using System.Windows.Threading;

namespace Glance.Services;

/// Runs an action once calls stop coming for a moment: a save after a burst of edits or a slider drag.
/// A run still pending when Glance exits happens then. Only a pending run is hooked to exit, so a page's
/// Debounce never keeps the page alive.
public sealed class Debounce(TimeSpan delay, Action action)
{
    DispatcherTimer? timer;

    bool Pending => timer?.IsEnabled == true;

    /// Starts the wait over.
    public void Poke()
    {
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) => Flush();
        }
        if (!timer.IsEnabled) Application.Current.Exit += Exiting;
        timer.Stop();
        timer.Start();
    }

    /// Runs a pending action now.
    public void Flush()
    {
        if (!Pending) return;
        Cancel();
        action();
    }

    /// Drops a pending run (the caller is doing the work itself).
    public void Cancel()
    {
        if (!Pending) return;
        timer!.Stop();
        Application.Current.Exit -= Exiting;
    }

    void Exiting(object sender, ExitEventArgs e) => Flush();
}
