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
}
