using System.Windows;
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

    private void OnRerolledOption(object sender, RoutedEventArgs e)
    {
        ViewModel.Augments.MarkRerolled(CardName(sender));
        AugmentSearch.Focus(); // type the new card right away
    }

    private void OnAugmentSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ViewModel.Augments.AddTopSuggestion();
        else if (e.Key == Key.Escape)
            ViewModel.Augments.SearchText = "";
    }
}
