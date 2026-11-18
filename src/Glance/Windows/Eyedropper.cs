using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Glance.Services;
using Draw = System.Drawing;

namespace Glance;

/// Picks a color from anywhere on screen. A magnifier follows the mouse (live, so video works too); a left click picks
/// and never reaches the app underneath; Esc or a right click cancels. No overlay window: a low-level mouse hook
/// catches the click, so it works across monitors with different scaling.
public sealed class Eyedropper : Window
{
    const int Zoom = 11, Cell = 12; // 11×11 screen pixels, each drawn 12 px wide
    static Eyedropper? active;

    readonly TaskCompletionSource<Color?> done = new();
    readonly DispatcherTimer tick;
    readonly Image lens = new() { Width = Zoom * Cell, Height = Zoom * Cell };
    readonly Border swatch = new() { Width = 14, Height = 14, CornerRadius = new(4), Margin = new(0, 0, 8, 0) };
    readonly TextBlock hex = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = Brushes.White };
    readonly HookProc proc; // kept alive while hooked: the hook holds only a raw pointer to it
    IntPtr hook, hwnd;
    POINT clicked;
    bool swallowUp, finished;

    /// The picked color, or null when cancelled.
    public static Task<Color?> Pick()
    {
        active?.Finish(null); // a second start replaces the first
        active = new Eyedropper();
        active.Show();
        return active.done.Task;
    }

    Eyedropper()
    {
        (WindowStyle, AllowsTransparency, Background, Topmost, ShowInTaskbar, ShowActivated, SizeToContent) =
            (WindowStyle.None, true, Brushes.Transparent, true, false, false, SizeToContent.WidthAndHeight);
        RenderOptions.SetBitmapScalingMode(lens, BitmapScalingMode.NearestNeighbor);
        var grid = new Grid();
        grid.Children.Add(new Border { CornerRadius = new(8), ClipToBounds = true, Child = lens });
        grid.Children.Add(new Rectangle { Width = Cell + 2, Height = Cell + 2, Stroke = Brushes.White, StrokeThickness = 1.5 }); // the pixel you'd pick
        Content = new Border
        {
            CornerRadius = new(12), Padding = new(6), Background = new SolidColorBrush(Skin.Get("SurfaceColor", 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new(1),
            Child = new StackPanel
            {
                Children =
                {
                    grid,
                    new DockPanel { Margin = new(4, 6, 4, 2), Children = { swatch, hex } },
                    new TextBlock { Text = "Click to pick · Esc to cancel", FontSize = 10.5, Margin = new(4, 0, 4, 2),
                        Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0x9C, 0xA3)) },
                },
            },
        };
        proc = Hook;
        tick = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Follow(), Dispatcher);
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.Passive(hwnd);
            hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, proc, GetModuleHandle(null), 0);
            Follow();
            tick.Start();
        };
        Closed += (_, _) => Finish(null);
    }

    // Runs inside every mouse event system-wide: must stay quick, so the real work is posted to the dispatcher.
    IntPtr Hook(int code, IntPtr msg, IntPtr data)
    {
        if (code >= 0 && !finished)
            switch ((int)msg)
            {
                case 0x201: // WM_LBUTTONDOWN
                    clicked = Marshal.PtrToStructure<POINT>(data);
                    swallowUp = true;
                    return 1;
                case 0x204: // WM_RBUTTONDOWN
                    swallowUp = true;
                    Dispatcher.BeginInvoke(() => Finish(null));
                    return 1;
                case 0x202 or 0x205 when swallowUp: // the matching up: no stray click reaches the app below either
                    var (pick, at) = ((int)msg == 0x202, clicked);
                    Dispatcher.BeginInvoke(() => Finish(pick ? PixelAt(at.X, at.Y) : null));
                    return 1;
            }
        return CallNextHookEx(hook, code, msg, data);
    }

    void Follow()
    {
        if ((GetAsyncKeyState(0x1B /* Esc */) & 0x8000) != 0) { Finish(null); return; }
        GetCursorPos(out var at);
        using var shot = Grab(at.X, at.Y, Zoom);
        var mid = shot.GetPixel(Zoom / 2, Zoom / 2);
        var c = Color.FromRgb(mid.R, mid.G, mid.B);
        var h = shot.GetHbitmap();
        lens.Source = Imaging.CreateBitmapSourceFromHBitmap(h, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(h);
        swatch.Background = new SolidColorBrush(c);
        hex.Text = ColorMath.Hex(c);

        // Beside the cursor, flipped near the screen's edges. Physical pixels throughout (the app is per-monitor DPI aware).
        GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        var area = System.Windows.Forms.Screen.FromPoint(new Draw.Point(at.X, at.Y)).WorkingArea;
        var x = at.X + 24 + w > area.Right ? at.X - 24 - w : at.X + 24;
        var y = at.Y + 24 + ht > area.Bottom ? at.Y - 24 - ht : at.Y + 24;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 /* NOSIZE | NOZORDER | NOACTIVATE */);
    }

    static Color PixelAt(int x, int y)
    {
        using var b = Grab(x, y, 1);
        var p = b.GetPixel(0, 0);
        return Color.FromRgb(p.R, p.G, p.B);
    }

    /// size×size screen pixels centered on (x, y).
    static Draw.Bitmap Grab(int x, int y, int size)
    {
        var b = new Draw.Bitmap(size, size, Draw.Imaging.PixelFormat.Format32bppRgb);
        using var g = Draw.Graphics.FromImage(b);
        g.CopyFromScreen(x - size / 2, y - size / 2, 0, 0, new Draw.Size(size, size));
        return b;
    }

    void Finish(Color? c)
    {
        if (finished) return;
        finished = true;
        tick.Stop();
        if (hook != 0) UnhookWindowsHookEx(hook);
        hook = 0;
        if (active == this) active = null;
        if (IsLoaded) Close();
        done.TrySetResult(c);
    }

    delegate IntPtr HookProc(int code, IntPtr msg, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr msg, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
