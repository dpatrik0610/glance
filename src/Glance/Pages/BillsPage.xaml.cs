using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// Bills: what comes back and when it's due. The tick books it in Budget and moves it to the next due date; it stays ticked
/// until that one is close, and unticking undoes it.
public partial class BillsPage : UserControl, IHeader, IDisposable
{
    public FrameworkElement Header { get; }
    public FrameworkElement State { get; }

    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// A kind's icon and hue (fixed: kinds don't follow the palette).
    internal static (string Icon, Color Hue) Look(string kind) => kind switch
    {
        "Subscription" => ("", Color.FromRgb(0xB0, 0x7C, 0xFF)),
        "Housing" => ("", Color.FromRgb(0x5A, 0xA9, 0xFF)),
        "Utilities" => ("", Color.FromRgb(0xE6, 0xA6, 0x5D)),
        "Insurance" => ("", Color.FromRgb(0x4C, 0xC3, 0x7A)),
        "Loan" => ("", Color.FromRgb(0xF3, 0x80, 0x20)),
        _ => ("", Color.FromRgb(0x3C, 0xC8, 0xC8)),
    };

    /// A hue at 0x26 alpha: the background under that hue's text or icon.
    internal static Brush Tint(Color c) => new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));

    /// Late: Red; within three days: Amber; else dim.
    internal static string DueKey(Bill b, DateTime today) => (b.Due.Date - today).Days switch { < 0 => "Red", <= 3 => "Amber", _ => "Sub" };

    public sealed record Row(Bill Bill, string Name, string Kind, string Icon, Brush KindBrush, Brush KindBg, string Repeats, string PerMonth,
        string When, string DueDate, Brush DueBrush, Brush DueBg, string Amount, string InHome, bool Paid);

    readonly DispatcherTimer toastTimer;

    public BillsPage()
    {
        InitializeComponent();
        Header = MainWindow.Lift(Bar);
        State = MainWindow.Lift(Count);
        toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.6), DispatcherPriority.Background, (_, _) => { Toast.Visibility = Visibility.Collapsed; toastTimer!.Stop(); }, Dispatcher);
        toastTimer.Stop();
        Money.Bills.CollectionChanged += BillsChanged;
        PreviewKeyDown += Key;
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) return;
            Render(); // "in 3 days" moves on with the date
            if (Money.Bills.Any(b => b.Currency != Money.Home) && await Rates.Refresh()) Render(); // bills in another currency
        };
        Render();
    }

    void BillsChanged(object? s, NotifyCollectionChangedEventArgs e) => Render();

    public void Dispose() => Money.Bills.CollectionChanged -= BillsChanged;

    void Render()
    {
        var today = DateTime.Today;
        Rows.ItemsSource = Money.Bills.OrderBy(b => b.Due).ThenBy(b => b.Name).Select(b =>
        {
            var (icon, hue) = Look(b.Kind);
            var due = DueKey(b, today);
            var dueBrush = Skin.Brush(due);
            var repeats = b.Repeat switch
            {
                Repeat.Weekly => "Weekly · " + b.Due.ToString("dddd", En) + "s",
                Repeat.Monthly => $"Monthly · on the {Ordinal(b.Day > 0 ? b.Day : b.Due.Day)}",
                Repeat.Quarterly => "Every 3 months",
                Repeat.Yearly => "Yearly · " + b.Due.ToString("MMM d", En),
                _ => "Once",
            };
            var perMonth = b.Repeat is Repeat.Monthly or Repeat.Once ? "" : $"≈ {Money.Format(b.PerMonth, plus: false, currency: b.Currency)} a month";
            var inHome = b.Currency == Money.Home ? "" : Money.InHome(b.Amount, b.Currency) is { } h ? $"≈ {Money.Format(h, plus: false)}" : "no rate yet";
            return new Row(b, b.Name, b.Kind, icon, new SolidColorBrush(hue), Tint(hue),
                repeats, perMonth, b.When(today), b.Due.ToString("dddd, MMM d yyyy", En), dueBrush,
                due == "Sub" ? Skin.Brush("CardBg") : Tint(Skin.BrushColor(due)),
                b.AmountText, inHome, b.PaidUp(today));
        }).ToArray();
        var none = Money.Bills.Count == 0;
        Empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        Columns.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        Header.Visibility = none ? Visibility.Collapsed : Visibility.Visible; // the empty state has its own button

        var late = Money.Bills.Count(b => b.Due.Date < today);
        Count.Inlines.Clear();
        if (none) return;
        // Bills in another currency count at today's rate; one without a rate yet is left out, and the total says so.
        var month = Money.Bills.Select(b => Money.InHome(b.PerMonth, b.Currency)).ToList();
        Count.Inlines.Add($"{Money.Bills.Count} {(Money.Bills.Count == 1 ? "bill" : "bills")} · {(month.Any(m => m is null) ? "at least " : "")}{Money.Format(month.Sum(m => m ?? 0), plus: false)} a month");
        if (late > 0) Count.Inlines.Add(new System.Windows.Documents.Run($" · {late} late") { Foreground = Skin.Brush("Red") });
    }

    static string Ordinal(int d) => d + (d is 11 or 12 or 13 ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    static Bill Of(object sender) => ((Row)((FrameworkElement)sender).DataContext).Bill;

    void Paid(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var b = Of(sender);
        if (b.PaidUp(DateTime.Today)) { Money.Unpay(b); Say("Not paid: taken out of Budget"); return; }
        if (!Money.Pay(b)) { Render(); Say($"No {b.Currency} rate yet: connect to the internet and try again"); return; } // Render: untick
        Say($"{Money.Format(-b.Amount, currency: b.Currency)} added to Budget");
    }

    void Edit(object sender, RoutedEventArgs e) { e.Handled = true; Open(Of(sender)); }

    void RowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindButton(d)) return; // a row action, not the row
        Open(Of(sender));
    }

    static bool FindButton(DependencyObject d)
    {
        for (DependencyObject? x = d; x is not null and not Border { Name: "Row" }; x = VisualTreeHelper.GetParent(x))
            if (x is System.Windows.Controls.Primitives.ButtonBase) return true; // the tick too
        return false;
    }

    void Delete(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var b = Of(sender);
        if (Dialog.Confirm("Delete", $"Delete {b.Name}?", "It's gone from Bills. What you already paid stays in Budget.", "Delete")) Money.Bills.Remove(b);
    }

    void NewBill(object sender, RoutedEventArgs e) => BillDialog.Edit(null);

    void Open(Bill b) => BillDialog.Edit(b);

    // Ctrl+N starts a new bill.
    void Key(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (!ctrl || e.Key != System.Windows.Input.Key.N) return;
        e.Handled = true;
        BillDialog.Edit(null);
    }

    void Say(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }
}
