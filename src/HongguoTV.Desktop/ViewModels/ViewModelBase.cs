using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace HongguoTV.Desktop.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

public sealed class SeriesViewModel : ViewModelBase
{
    public SeriesViewModel(string id, string title, string cover, string badge, string tags, Action<SeriesViewModel> open)
    {
        Id = id; Title = title; Cover = cover; Badge = badge; Tags = tags;
        OpenCommand = new RelayCommand(_ => open(this));
    }

    public string Id { get; }
    public string Title { get; }
    public string Cover { get; }
    public string Badge { get; }
    public string Tags { get; }
    public ICommand OpenCommand { get; }
}

public sealed class HistoryViewModel : ViewModelBase
{
    public HistoryViewModel(Core.HistoryItem item, Action<Core.HistoryItem> open)
    {
        Item = item;
        OpenCommand = new RelayCommand(_ => open(item));
    }

    public Core.HistoryItem Item { get; }
    public string Title => Item.Title;
    public string Cover => Item.Cover;
    public string Progress => $"继续播放第 {Item.EpisodeIndex + 1} 集";
    public ICommand OpenCommand { get; }
}

public sealed class EpisodeViewModel : ViewModelBase
{
    private bool _playing;

    public EpisodeViewModel(string id, int number, Action<EpisodeViewModel> play)
    {
        Id = id; Number = number; Label = $"第 {number} 集";
        PlayCommand = new RelayCommand(_ => play(this));
    }

    public string Id { get; }
    public int Number { get; }
    public string Label { get; }
    public bool Playing { get => _playing; set => SetProperty(ref _playing, value); }
    public ICommand PlayCommand { get; }
}
