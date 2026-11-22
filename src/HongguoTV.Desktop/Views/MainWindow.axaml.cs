using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HongguoTV.Desktop.ViewModels;

namespace HongguoTV.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _playerFullScreen;

    public MainWindow()
    {
        InitializeComponent();

        void BindPlayer()
        {
            if (DataContext is MainViewModel model && PlayerView.MediaPlayer != model.Player)
                PlayerView.MediaPlayer = model.Player;
        }

        PlayerView.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(BindPlayer);
        DataContextChanged += (_, _) => Dispatcher.UIThread.Post(BindPlayer);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => { BindPlayer(); UpdateMaximizeIcon(); });

        NavBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
                BeginMoveDrag(e);
        };
        NavBar.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Button) ToggleWindowState();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (_playerFullScreen)
            {
                SetPlayerFullScreen(false);
                e.Handled = true;
                return;
            }
            if (DataContext is not MainViewModel model || !model.PlayerVisible) return;
            SetPlayerFullScreen(false);
            model.StopCommand.Execute(null);
            e.Handled = true;
        };
    }

    private void WindowButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender == MinimizeButton) WindowState = WindowState.Minimized;
        else if (sender == MaximizeButton) ToggleWindowState();
        else if (sender == CloseButton) Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeIcon();
    }

    private void UpdateMaximizeIcon()
    {
        MaximizeIcon.Data = (Avalonia.Media.StreamGeometry)(Application.Current!.Resources[
            WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize"]!);
    }

    private void FullScreenButton_Click(object? sender, RoutedEventArgs e) => SetPlayerFullScreen(!_playerFullScreen);

    private void SetPlayerFullScreen(bool on)
    {
        _playerFullScreen = on;
        NavBar.IsVisible = !on;
        BackBar.IsVisible = !on && (DataContext is MainViewModel model && model.DetailOpen);
        ContentArea.Margin = on ? new Thickness(0) : new Thickness(22, 8, 22, 12);
        DetailGrid.ColumnDefinitions[0].Width = on ? new GridLength(0) : new GridLength(200);
        DetailGrid.ColumnDefinitions[1].Width = on ? new GridLength(0) : new GridLength(480);
        PlayerBorder.CornerRadius = on ? new CornerRadius(0) : new CornerRadius(10);
        FullScreenIcon.Data = (Avalonia.Media.StreamGeometry)(Application.Current!.Resources[on ? "IconFullscreenExit" : "IconFullscreen"]!);
    }
}
