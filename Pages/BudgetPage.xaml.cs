using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Glance.Services;

namespace Glance.Pages;

/// Income and expenses, a month at a time.
public partial class BudgetPage : UserControl
{
    public record Row(Entry Entry, string Day, string Note, string Amount, Brush Brush);

    DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public BudgetPage()
    {
        InitializeComponent();
        Money.Entries.CollectionChanged += (_, _) => Render();
        Render();
    }

    void Render()
    {
        MonthText.Text = month.ToString("MMMM yyyy");
        NextButton.IsEnabled = month < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var these = Money.Entries.Where(e => e.Date.Year == month.Year && e.Date.Month == month.Month).OrderByDescending(e => e.Date).ToArray();
        var income = these.Where(e => e.Amount > 0).Sum(e => e.Amount);
        var spent = these.Where(e => e.Amount < 0).Sum(e => e.Amount);
        IncomeText.Text = Money.Format(income, plus: false);
        ExpenseText.Text = Money.Format(-spent, plus: false);
        BalanceText.Text = Money.Format(income + spent);
        BalanceText.Foreground = Res(income + spent < 0 ? "Red" : "Text");
        List.ItemsSource = these.Select(e => new Row(e, e.Date.ToString("MMM d"), e.Note.Length > 0 ? e.Note : e.Amount > 0 ? "Income" : "Expense",
            Money.Format(e.Amount), Res(e.Amount > 0 ? "Green" : "Red"))).ToArray();
        Empty.Visibility = these.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void PrevMonth(object sender, RoutedEventArgs e) { month = month.AddMonths(-1); Render(); }
    void NextMonth(object sender, RoutedEventArgs e) { month = month.AddMonths(1); Render(); }

    void BoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Add(sender, e);
    }

    void Add(object sender, RoutedEventArgs e)
    {
        if (!ExchangePage.TryParse(AmountBox.Text, out var amount) || amount <= 0) { Say("Type an amount, like 4500."); AmountBox.Focus(); return; }
        var date = DateBox.Text.Trim().Length == 0 ? DateTime.Today : Due.Parse(DateBox.Text, DateTime.Now)?.Date;
        if (date is null) { Say("Can't read the date: try \"yesterday\", \"nov 3\" or \"2026-11-03\"."); DateBox.Focus(); return; }
        Say(null);
        month = new DateTime(date.Value.Year, date.Value.Month, 1); // show where it went
        Money.Entries.Add(new Entry(date.Value, IncomeChip.IsChecked == true ? amount : -amount, NoteBox.Text.Trim())); // renders
        AmountBox.Clear();
        NoteBox.Clear();
        DateBox.Clear();
        AmountBox.Focus();
    }

    void RemoveEntry(object sender, RoutedEventArgs e) => Money.Entries.Remove(((Row)((FrameworkElement)sender).DataContext).Entry);

    void Say(string? text)
    {
        Hint.Text = text ?? "";
        Hint.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}
