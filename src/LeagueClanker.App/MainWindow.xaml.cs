using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LeagueClanker.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnPinClick(object sender, RoutedEventArgs e) => Topmost = !Topmost;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnAcceptPivot(object sender, RoutedEventArgs e) => ViewModel.AcceptPivot();

    private void OnDeclinePivot(object sender, RoutedEventArgs e) => ViewModel.DeclinePivot();

    private void OnSwitchToLatest(object sender, RoutedEventArgs e) => ViewModel.SwitchToLatest();

    // Augment picker rows carry their card as DataContext.
    private static string CardName(object sender) => ((FrameworkElement)sender).DataContext switch
    {
        AugmentRow row => row.Name,
        AugmentOptionRow option => option.Name,
        _ => "",
    };

    private void OnOfferSuggestion(object sender, RoutedEventArgs e) => ViewModel.Augments.AddToOffer(CardName(sender));

    private void OnPickedSuggestion(object sender, RoutedEventArgs e) => ViewModel.Augments.AddToPicked(CardName(sender));

    private void OnRemoveOffer(object sender, RoutedEventArgs e) => ViewModel.Augments.RemoveFromOffer(CardName(sender));

    private void OnRemovePicked(object sender, RoutedEventArgs e) => ViewModel.Augments.RemoveFromPicked(CardName(sender));

    private void OnPickOption(object sender, RoutedEventArgs e) => ViewModel.Augments.Pick(CardName(sender));

    // Mouse, keyboard and accessibility tools all toggle IsChecked. A freshly built row also sets it from
    // the view model, which matches the row already and is ignored.
    private void OnGoldenChanged(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { DataContext: AugmentOptionRow row } toggle && toggle.IsChecked != row.IsGolden)
            ViewModel.Augments.ToggleGolden(row.Name);
    }

    private void OnRerolledOption(object sender, RoutedEventArgs e)
    {
        ViewModel.Augments.MarkRerolled(CardName(sender));
        AugmentSearch.Focus(); // type the new card right away
    }

    // Chips are rebuilt on every change. Building a chip checks the selected one, which matches the view model and is ignored.
    private void OnPlaystyleChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: PlaystyleOption { IsSelected: false } option })
            ViewModel.ChampSelect.SelectPlaystyle(option.Value);
    }

    private void OnToggleSettings(object sender, RoutedEventArgs e) => ViewModel.ToggleSettings();

    private void OnSwiftplaySlotChecked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: SlotOption { IsSelected: false } slot })
            ViewModel.ChampSelect.SelectSwiftplaySlot(slot.Index);
    }

    private void OnToggleCompact(object sender, RoutedEventArgs e) => ViewModel.ToggleCompact();

    private void OnSaveSnapshot(object sender, RoutedEventArgs e) => ViewModel.SaveSnapshot();

    private void OnOpenLog(object sender, RoutedEventArgs e) => Shell.Open(AppPaths.LogFile);

    private void OnOpenSnapshots(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppPaths.SnapshotFolder);
        Shell.Open(AppPaths.SnapshotFolder);
    }

    private void OnOpenUpdate(object sender, RoutedEventArgs e)
    {
        if (ViewModel.UpdateUrl is { } url)
            Shell.Open(url);
    }

    private async void OnApplyRunes(object sender, RoutedEventArgs e) => await ViewModel.ChampSelect.ApplyAsync();

    private void OnPlaystyleMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var option in ViewModel.LivePlaystyles)
        {
            var item = new MenuItem { Header = option.Label, IsCheckable = true, IsChecked = option.IsSelected };
            item.Click += (_, _) => ViewModel.ChangePlaystyle(option.Value);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnAugmentSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ViewModel.Augments.AddTopSuggestion();
        else if (e.Key == Key.Escape)
            ViewModel.Augments.SearchText = "";
    }
}
