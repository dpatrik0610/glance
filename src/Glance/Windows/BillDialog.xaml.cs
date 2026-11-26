using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Pages;
using Glance.Services;

namespace Glance;

/// New or existing bill, in the same glass dialog as the event editor. The date is typed like due dates ("dec 5", "fri");
/// the line under it says how it was read and what the bill costs a month.
public partial class BillDialog : Window
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    readonly Bill? existing;
    string kind;
    Repeat repeat;

    /// Saves or deletes in Money.Bills; nothing when cancelled.
    public static void Edit(Bill? existing)
    {
        var d = new BillDialog(existing);
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
    }

    BillDialog(Bill? b)
    {
        InitializeComponent();
        existing = b;
        kind = b?.Kind ?? "Subscription";
        repeat = b?.Repeat ?? Repeat.Monthly;
        Label.Text = string.Join(" ", (b is null ? "NEW BILL" : "BILL").ToCharArray());
        NameBox.Text = b?.Name ?? "";
        AmountBox.Text = b is null ? "" : b.Amount.ToString("0.##", CultureInfo.CurrentCulture);
        DueBox.Text = b is null ? "" : b.Due.ToString("yyyy-MM-dd", En);
        RemindBox.IsChecked = b?.Remind ?? true;
        DeleteButton.Visibility = b is null ? Visibility.Collapsed : Visibility.Visible;

        // Kind chips carry their icon, in their hue.
        foreach (var k in Bill.Kinds)
        {
            var (icon, hue) = BillsPage.Look(k);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = icon, FontFamily = Skin.Icons, FontSize = 12, Foreground = new SolidColorBrush(hue), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 7, 0) });
            content.Children.Add(new TextBlock { Text = k, VerticalAlignment = VerticalAlignment.Center });
            var chip = new RadioButton { Content = content, GroupName = "kind", IsChecked = k == kind, Style = Skin.Style("Chip"), Margin = new(0, 0, 6, 6) };
            chip.Checked += (_, _) => { kind = k; ShowKind(); };
            KindChips.Children.Add(chip);
        }
        foreach (var r in Enum.GetValues<Repeat>())
        {
            var chip = new RadioButton { Content = r.ToString(), GroupName = "repeat", IsChecked = r == repeat, Style = Skin.Style("Chip"), Margin = new(0, 0, 6, 6) };
            chip.Checked += (_, _) => { repeat = r; Changed(this, new RoutedEventArgs()); };
            RepeatChips.Children.Add(chip);
        }
        ShowKind();
        Changed(this, new RoutedEventArgs());
        Loaded += (_, _) => { if (b is null) NameBox.Focus(); };
    }

    void ShowKind()
    {
        var (icon, hue) = BillsPage.Look(kind);
        Dot.Fill = TileIcon.Foreground = new SolidColorBrush(hue);
        Tile.Background = BillsPage.Tint(hue);
        TileIcon.Text = icon;
    }

    DateTime? DueDate => Due.Parse(DueBox.Text, DateTime.Now)?.Date;

    /// "Saturday, December 5 · in 9 days · ≈ 1 250 Ft a month", or why the date can't be read.
    void Changed(object sender, RoutedEventArgs e)
    {
        if (ReadText is null) return; // still loading
        if (DueBox.Text.Trim().Length == 0) { ReadText.Text = "When it's due next: dec 5, 12.05, fri, tomorrow"; return; }
        if (DueDate is not { } d) { ReadText.Text = "Can't read that date yet: try \"dec 5\" or \"2026-12-05\"."; return; }
        var days = (d - DateTime.Today).Days;
        var rel = days switch { < -1 => $"{-days} days ago", -1 => "yesterday", 0 => "today", 1 => "tomorrow", _ => $"in {days} days" };
        var probe = new Bill("", ExchangePage.TryParse(AmountBox.Text, out var a) ? a : 0, d, repeat, kind);
        ReadText.Text = d.ToString(d.Year == DateTime.Today.Year ? "dddd, MMMM d" : "dddd, MMMM d yyyy", En) + " · " + rel
            + (probe.Amount > 0 && repeat is not (Repeat.Monthly or Repeat.Once) ? $" · ≈ {Money.Format(probe.PerMonth, plus: false)} a month" : "");
    }

    void SaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Fail("Give it a name, like Rent.", NameBox); return; }
        if (!ExchangePage.TryParse(AmountBox.Text, out var amount) || amount <= 0) { Fail("Type an amount, like 4500.", AmountBox); return; }
        if (DueDate is not { } due) { Fail("Can't read the date: try \"dec 5\", \"2026-12-05\" or \"fri\".", DueBox); return; }
        var bill = new Bill(name, amount, due, repeat, kind, RemindBox.IsChecked == true, Day: due.Day);
        // Same due date: keep "already reminded", so saving an edit doesn't remind again.
        if (existing is not null) Money.Replace(existing, bill with { Reminded = existing.Due == bill.Due ? existing.Reminded : null });
        else Money.Bills.Add(bill);
        Close();
    }

    void DeleteClick(object sender, RoutedEventArgs e)
    {
        if (existing is null || !Dialog.Confirm("Delete bill", $"Delete \"{existing.Name}\"?", "It's gone from Bills. What you already paid stays in Budget.", "Delete")) return;
        Money.Bills.Remove(existing);
        Close();
    }

    void Fail(string message, Control box)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
        box.Focus();
    }

    void Drag(object sender, MouseButtonEventArgs e) => DragMove();
}
