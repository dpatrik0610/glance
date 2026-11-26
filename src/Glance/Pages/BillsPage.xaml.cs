using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.Services;

namespace Glance.Pages;

/// Bills: what comes back and when it's due. "Paid" books it in Budget and moves it to the next due date.
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
        string When, string DueDate, Brush DueBrush, Brush DueBg, string Amount);

    Bill? editing;
    readonly DispatcherTimer toastTimer;

    public BillsPage()
    {
        InitializeComponent();
        Header = MainWindow.Lift(Bar);
        State = MainWindow.Lift(Count);
        foreach (var r in Enum.GetValues<Repeat>())
            RepeatChips.Children.Add(new RadioButton { Style = Skin.Style("Chip"), Content = r == Repeat.Once ? "Once" : r.ToString(), Tag = r, GroupName = "repeat" });
        foreach (var k in Bill.Kinds)
            KindChips.Children.Add(new RadioButton { Style = Skin.Style("Chip"), Content = k, Tag = k, GroupName = "kind", Margin = new(0, 0, 4, 4) });
        toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.6), DispatcherPriority.Background, (_, _) => { Toast.Visibility = Visibility.Collapsed; toastTimer!.Stop(); }, Dispatcher);
        toastTimer.Stop();
        Money.Bills.CollectionChanged += BillsChanged;
        PreviewKeyDown += Key;
        IsVisibleChanged += (_, _) => { if (IsVisible) Render(); }; // "in 3 days" moves on with the date
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
            var perMonth = b.Repeat is Repeat.Monthly or Repeat.Once ? "" : $"≈ {Money.Format(b.PerMonth, plus: false)} a month";
            return new Row(b, b.Name, b.Kind, icon, new SolidColorBrush(hue), Tint(hue),
                repeats, perMonth, b.When(today), b.Due.ToString("dddd, MMM d yyyy", En), dueBrush,
                due == "Sub" ? Skin.Brush("CardBg") : Tint(Skin.BrushColor(due)),
                Money.Format(b.Amount, plus: false));
        }).ToArray();
        var none = Money.Bills.Count == 0;
        Empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        Columns.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        Header.Visibility = none ? Visibility.Collapsed : Visibility.Visible; // the empty state has its own button

        var late = Money.Bills.Count(b => b.Due.Date < today);
        Count.Inlines.Clear();
        if (none) return;
        Count.Inlines.Add($"{Money.Bills.Count} {(Money.Bills.Count == 1 ? "bill" : "bills")} · {Money.Format(Money.Bills.Sum(b => b.PerMonth), plus: false)} a month");
        if (late > 0) Count.Inlines.Add(new System.Windows.Documents.Run($" · {late} late") { Foreground = Skin.Brush("Red") });
    }

    static string Ordinal(int d) => d + (d is 11 or 12 or 13 ? "th" : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    static Bill Of(object sender) => ((Row)((FrameworkElement)sender).DataContext).Bill;

    void Paid(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var b = Of(sender);
        Money.Pay(b);
        Say($"{Money.Format(-b.Amount)} added to Budget");
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
            if (x is Button) return true;
        return false;
    }

    void Delete(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var b = Of(sender);
        if (Dialog.Confirm("Delete", $"Delete {b.Name}?", "It's gone from Bills. What you already paid stays in Budget.", "Delete")) Money.Bills.Remove(b);
    }

    // ---- The editor ----

    void NewBill(object sender, RoutedEventArgs e) => Open(null);

    /// Opens the editor here; Today uses it to edit a bill too.
    internal void Open(Bill? b)
    {
        editing = b;
        EditorTitle.Text = b is null ? "New bill" : "Edit bill";
        NameBox.Text = b?.Name ?? "";
        AmountBox.Text = b is null ? "" : b.Amount.ToString("0.##", CultureInfo.CurrentCulture);
        DueBox.Text = b is null ? "" : b.Due.ToString("yyyy-MM-dd", En);
        Pick(RepeatChips.Children, b?.Repeat ?? Repeat.Monthly);
        Pick(KindChips.Children, b?.Kind ?? "Subscription");
        RemindBox.IsChecked = b?.Remind ?? true;
        DeleteButton.Visibility = b is null ? Visibility.Collapsed : Visibility.Visible;
        Hint(null);
        Editor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => NameBox.Focus(), DispatcherPriority.Input);
    }

    static void Pick(UIElementCollection chips, object value)
    {
        foreach (RadioButton c in chips) c.IsChecked = Equals(c.Tag, value);
    }

    static object? Picked(UIElementCollection chips) => chips.OfType<RadioButton>().FirstOrDefault(c => c.IsChecked == true)?.Tag;

    void Save(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Hint("Give it a name, like Rent."); NameBox.Focus(); return; }
        if (!ExchangePage.TryParse(AmountBox.Text, out var amount) || amount <= 0) { Hint("Type an amount, like 4500."); AmountBox.Focus(); return; }
        if (Due.Parse(DueBox.Text, DateTime.Now) is not { } due) { Hint("Can't read the date: try \"dec 5\", \"2026-12-05\" or \"fri\"."); DueBox.Focus(); return; }
        var bill = new Bill(name, amount, due.Date, (Repeat)Picked(RepeatChips.Children)!, (string)Picked(KindChips.Children)!, RemindBox.IsChecked == true, Day: due.Day);
        // Same due date: keep "already reminded", so saving an edit doesn't remind again.
        if (editing is not null) Money.Replace(editing, bill with { Reminded = editing.Due == bill.Due ? editing.Reminded : null });
        else Money.Bills.Add(bill);
        CloseEditor(sender, e);
    }

    void DeleteEditing(object sender, RoutedEventArgs e)
    {
        if (editing is null || !Dialog.Confirm("Delete", $"Delete {editing.Name}?", "It's gone from Bills. What you already paid stays in Budget.", "Delete")) return;
        Money.Bills.Remove(editing);
        CloseEditor(sender, e);
    }

    void CloseEditor(object sender, RoutedEventArgs e)
    {
        Editor.Visibility = Visibility.Collapsed;
        editing = null;
    }

    void EditorBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Editor) CloseEditor(sender, e);
    }

    void EditorKey(object sender, KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || e.OriginalSource is RadioButton or CheckBox or Button) return;
        e.Handled = true;
        Save(sender, e);
    }

    // Esc closes the editor before anything else; Ctrl+N starts a new bill.
    void Key(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (e.Key == System.Windows.Input.Key.Escape && Editor.IsVisible) CloseEditor(sender, e);
        else if (ctrl && e.Key == System.Windows.Input.Key.N && !Editor.IsVisible) Open(null);
        else return;
        e.Handled = true;
    }

    void Hint(string? text)
    {
        EditorHint.Text = text ?? "";
        EditorHint.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    void Say(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }
}
