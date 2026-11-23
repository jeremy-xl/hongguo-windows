using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HongguoTV.Desktop.ViewModels;

namespace HongguoTV.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _playerFullScreen;
    private bool _screenFullScreen;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = $"v{version?.Major}.{version?.Minor}.{version?.Build}";
        Title = $"红果 TV · Windows {versionText}";
        VersionTextBlock.Text = $"版本 {versionText}";

        void BindPlayer()
        {
            if (DataContext is MainViewModel model && PlayerView.MediaPlayer != model.Player)
                PlayerView.MediaPlayer = model.Player;
        }

        PlayerView.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(BindPlayer);
        DataContextChanged += (_, _) => Dispatcher.UIThread.Post(BindPlayer);
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            BindPlayer();
            UpdateMaximizeIcon();
            UpdateResizeHandles();
            UpdateFullScreenIcons();
        });

        NavBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
                BeginMoveDrag(e);
        };
        NavBar.DoubleTapped += (_, e) =>
        {
            if (e.Source is not Button) ToggleWindowState();
        };

        SetupResizeHandles();

        KeyDown += (_, e) =>
        {
            if (DataContext is not MainViewModel model) return;

            if (e.Key == Key.Escape)
            {
                if (model.SettingsOpen)
                {
                    model.SettingsOpen = false;
                    e.Handled = true;
                    return;
                }
                if (_screenFullScreen)
                {
                    SetScreenFullScreen(false);
                    e.Handled = true;
                    return;
                }
                if (_playerFullScreen)
                {
                    SetPlayerFullScreen(false);
                    e.Handled = true;
                    return;
                }
                if (!model.PlayerVisible) return;
                model.StopCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Space || model.SettingsOpen || !model.PlayerVisible) return;
            if (e.Source is TextBox or ComboBox or Button or CheckBox or RadioButton) return;
            model.PlayPauseCommand.Execute(null);
            e.Handled = true;
        };
    }

    private void SettingsPopup_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is MainViewModel model) model.SettingsOpen = false;
        e.Handled = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        UpdateMaximizeIcon();
        UpdateResizeHandles();
        if (WindowState != WindowState.FullScreen && _screenFullScreen)
            SetScreenFullScreen(false);
    }

    private void SetupResizeHandles()
    {
        SetupResizeHandle(ResizeTop, WindowEdge.North, StandardCursorType.SizeNorthSouth);
        SetupResizeHandle(ResizeBottom, WindowEdge.South, StandardCursorType.SizeNorthSouth);
        SetupResizeHandle(ResizeLeft, WindowEdge.West, StandardCursorType.SizeWestEast);
        SetupResizeHandle(ResizeRight, WindowEdge.East, StandardCursorType.SizeWestEast);
        SetupResizeHandle(ResizeTopLeft, WindowEdge.NorthWest, StandardCursorType.TopLeftCorner);
        SetupResizeHandle(ResizeTopRight, WindowEdge.NorthEast, StandardCursorType.TopRightCorner);
        SetupResizeHandle(ResizeBottomLeft, WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner);
        SetupResizeHandle(ResizeBottomRight, WindowEdge.SouthEast, StandardCursorType.BottomRightCorner);
    }

    private void SetupResizeHandle(Border handle, WindowEdge edge, StandardCursorType cursorType)
    {
        handle.Cursor = new Cursor(cursorType);
        handle.PointerPressed += (_, e) =>
        {
            if (WindowState != WindowState.Normal) return;
            if (e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                BeginResizeDrag(edge, e);
        };
    }

    private void UpdateResizeHandles()
    {
        bool visible = WindowState == WindowState.Normal;
        ResizeTop.IsVisible = ResizeBottom.IsVisible = ResizeLeft.IsVisible = ResizeRight.IsVisible = visible;
        ResizeTopLeft.IsVisible = ResizeTopRight.IsVisible = ResizeBottomLeft.IsVisible = ResizeBottomRight.IsVisible = visible;
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
        MaximizeIcon.Data = (StreamGeometry)(Application.Current!.Resources[
            WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize"]!);
    }

    private void FullScreenButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_screenFullScreen) return;
        SetPlayerFullScreen(!_playerFullScreen);
    }

    private void ScreenFullScreenButton_Click(object? sender, RoutedEventArgs e) => SetScreenFullScreen(!_screenFullScreen);

    private void SetScreenFullScreen(bool on)
    {
        if (on)
        {
            if (WindowState != WindowState.FullScreen) _stateBeforeFullScreen = WindowState;
            _screenFullScreen = true;
            SetPlayerFullScreen(true);
            WindowState = WindowState.FullScreen;
        }
        else
        {
            _screenFullScreen = false;
            if (WindowState == WindowState.FullScreen) WindowState = _stateBeforeFullScreen;
            SetPlayerFullScreen(false);
        }
        UpdateFullScreenIcons();
    }

    private void SetPlayerFullScreen(bool on)
    {
        _playerFullScreen = on;
        NavBar.IsVisible = !on;
        BackBar.IsVisible = !on && (DataContext is MainViewModel model && model.DetailOpen);
        ContentArea.Margin = on ? new Thickness(0) : new Thickness(22, 8, 22, 12);
        DetailGrid.ColumnDefinitions[0].Width = on ? new GridLength(0) : new GridLength(200);
        DetailGrid.ColumnDefinitions[1].Width = on ? new GridLength(0) : new GridLength(480);
        PlayerBorder.CornerRadius = on ? new CornerRadius(0) : new CornerRadius(10);
        UpdateFullScreenIcons();
    }

    private void UpdateFullScreenIcons()
    {
        var resources = Application.Current!.Resources;
        FullScreenIcon.Data = (StreamGeometry)(resources[_playerFullScreen ? "IconFullscreenExit" : "IconFullscreen"]!);
        ScreenFullScreenIcon.Data = (StreamGeometry)(resources[_screenFullScreen ? "IconScreenFullscreenExit" : "IconScreenFullscreen"]!);
        ScreenFullScreenIcon.Fill = new SolidColorBrush(_screenFullScreen
            ? Color.FromRgb(0xFF, 0x6B, 0x6B)
            : Color.FromRgb(0xE6, 0xED, 0xF3));
    }
}
