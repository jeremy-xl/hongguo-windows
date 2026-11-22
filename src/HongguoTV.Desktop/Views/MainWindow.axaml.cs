using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using HongguoTV.Desktop.ViewModels;

namespace HongguoTV.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel model) return;
            PlayerView.MediaPlayer = model.Player;
            Dispatcher.UIThread.Post(() => PlayerView.MediaPlayer = model.Player);
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainViewModel model) PlayerView.MediaPlayer = model.Player;
        });
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not MainViewModel model || !model.PlayerVisible) return;
            model.StopCommand.Execute(null);
            e.Handled = true;
        };
    }
}
