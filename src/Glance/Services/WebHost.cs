using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Glance.Services;

/// The embedded web pages (terminal, map): one shared browser process, transparent, no browser chrome.
public static class WebHost
{
    static Task<CoreWebView2Environment>? env;

    // Same user data folder for every WebView2 in Glance, created once, so they share one browser process.
    static Task<CoreWebView2Environment> Env => env ??= CoreWebView2Environment.CreateAsync(null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glance", "WebView2"));

    /// Serves `folder` (next to the exe) as https://`host`/ and opens `page`. `setup` runs before navigation, for handlers.
    public static async Task Open(WebView2 web, string host, string folder, string page, Action<CoreWebView2> setup)
    {
        web.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        await web.EnsureCoreWebView2Async(await Env);
        var core = web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no Ctrl+F/F5/Ctrl+P stealing keys
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(host, Path.Combine(AppContext.BaseDirectory, folder), CoreWebView2HostResourceAccessKind.DenyCors);
        // Off screen (other page, Glance hidden), the browser gives back what it can: caches, unused heap. Shells keep running.
        web.IsVisibleChanged += (_, e) =>
        {
            if (web.CoreWebView2 is { } c)
                c.MemoryUsageTargetLevel = (bool)e.NewValue ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
        };
        setup(core);
        core.Navigate($"https://{host}/{page}");
    }
}
