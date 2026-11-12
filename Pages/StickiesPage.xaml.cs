using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Glance.Services;

namespace Glance.Pages;

public partial class StickiesPage : UserControl
{
    public StickiesPage()
    {
        InitializeComponent();
        List.ItemsSource = Stickies.Items;
        Stickies.Items.CollectionChanged += (_, _) => ShowSelected();
        List.SelectedIndex = Stickies.Items.Count > 0 ? 0 : -1;
        IsVisibleChanged += (_, _) => AllBox.IsChecked = App.Settings.StickiesShown; // the tray has the same switch
        ShowSelected();
    }

    Sticky? Current => List.SelectedItem as Sticky;

    void Selected(object sender, SelectionChangedEventArgs e) => ShowSelected();

    void ShowSelected()
    {
        var s = Current;
        Editor.DataContext = s;
        Editor.Visibility = s is null ? Visibility.Collapsed : Visibility.Visible;
        Empty.Visibility = Stickies.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (s is null) return;
        Solid.Value = s.Opacity;
        ShowColors();
    }

    void ShowColors()
    {
        ColorDots.Children.Clear();
        foreach (var (name, _, dot) in Sticky.Colors)
        {
            var b = new Button { Style = (Style)FindResource("Ghost"), Padding = new(5), ToolTip = name };
            b.Content = new Ellipse
            {
                Width = 16, Height = 16, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dot)),
                Stroke = name == Current?.Color ? Brushes.White : null, StrokeThickness = 2,
            };
            b.Click += (_, _) => { if (Current is { } s) { s.Color = name; ShowColors(); } };
            ColorDots.Children.Add(b);
        }
    }

    void SolidChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SolidText is null) return; // during InitializeComponent
        SolidText.Text = $"{e.NewValue:P0}";
        if (Current is { } s) s.Opacity = Math.Round(e.NewValue, 2);
    }

    void NewText(object sender, RoutedEventArgs e) => Add(NoteKind.Text);

    void NewChecklist(object sender, RoutedEventArgs e) => Add(NoteKind.Checklist);

    void Add(NoteKind kind)
    {
        List.SelectedItem = Stickies.Add(kind);
        AllBox.IsChecked = App.Settings.StickiesShown;
        TitleBox.Focus();
    }

    void RevealClick(object sender, RoutedEventArgs e)
    {
        if (Current is { } s) Stickies.Reveal(s);
        AllBox.IsChecked = App.Settings.StickiesShown;
    }

    void AllToggled(object sender, RoutedEventArgs e) => Stickies.SetShown(AllBox.IsChecked == true);

    void DeleteSticky(object sender, RoutedEventArgs e)
    {
        if (Current is not { } s) return;
        if (!Dialog.Confirm("Delete sticky", $"Delete \"{s.DisplayTitle}\"?", "This can't be undone.", "Delete")) return;
        var i = List.SelectedIndex;
        Stickies.Items.Remove(s);
        List.SelectedIndex = Math.Min(i, Stickies.Items.Count - 1);
    }
}
