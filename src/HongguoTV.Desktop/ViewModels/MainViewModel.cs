using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using HongguoTV.Core;
using LibVLCSharp.Shared;

namespace HongguoTV.Desktop.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ContentRepository _repository;
    private readonly LocalMediaServer _mediaServer;
    private readonly LibraryStore _library;
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private string? _currentMediaUrl;
    private CancellationTokenSource _playbackLoad = new();
    private CancellationTokenSource _suggestionLoad = new();

    public MainViewModel()
    {
        _repository = new ContentRepository();
        _mediaServer = new LocalMediaServer(_repository);
        _mediaServer.Start();
        _library = new LibraryStore();
        InitializePlayer();

        HomeCommand = new RelayCommand(_ => ShowHome());
        SearchCommand = new RelayCommand(_ => ShowSearch());
        FavoritesCommand = new RelayCommand(_ => ShowFavorites());
        HistoryCommand = new RelayCommand(_ => ShowHistory());
        BackCommand = new RelayCommand(_ => CloseDetail());
        SearchNowCommand = new RelayCommand(_ => _ = SearchAsync());
        LoadMoreCommand = new RelayCommand(_ => _ = LoadMoreAsync());
        ToggleFavoriteCommand = new RelayCommand(_ => ToggleFavorite());
        PlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
        StopCommand = new RelayCommand(_ => StopPlayback());

        HomeItems = new ObservableCollection<SeriesViewModel>();
        SearchItems = new ObservableCollection<SeriesViewModel>();
        Suggestions = new ObservableCollection<string>();
        Episodes = new ObservableCollection<EpisodeViewModel>();
        Favorites = new ObservableCollection<SeriesViewModel>();
        History = new ObservableCollection<HistoryViewModel>();

        ShowHome();
        _ = LoadHomeAsync();
        _ = LoadLibraryAsync();
    }

    public ObservableCollection<SeriesViewModel> HomeItems { get; }
    public ObservableCollection<SeriesViewModel> SearchItems { get; }
    public ObservableCollection<string> Suggestions { get; }
    public ObservableCollection<EpisodeViewModel> Episodes { get; }
    public ObservableCollection<SeriesViewModel> Favorites { get; }
    public ObservableCollection<HistoryViewModel> History { get; }

    private string _page = "home";
    public string Page { get => _page; set => SetProperty(ref _page, value); }

    private bool _detailOpen;
    public bool DetailOpen { get => _detailOpen; set => SetProperty(ref _detailOpen, value); }

    private string _title = "";
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    private string _description = "";
    public string Description { get => _description; set => SetProperty(ref _description, value); }

    private string _cover = "";
    public string Cover { get => _cover; set => SetProperty(ref _cover, value); }

    private string _badge = "";
    public string Badge { get => _badge; set => SetProperty(ref _badge, value); }

    private string _tags = "";
    public string Tags { get => _tags; set => SetProperty(ref _tags, value); }

    private string _status = "就绪";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            if (value.Trim().Length == 0) { Suggestions.Clear(); return; }
            _suggestionLoad.Cancel();
            _suggestionLoad = new CancellationTokenSource();
            var token = _suggestionLoad.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(220, token);
                    var items = await _repository.SuggestionsAsync(value.Trim(), token);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        Suggestions.Clear();
                        foreach (var item in items) Suggestions.Add(item);
                    });
                }
                catch { }
            }, token);
        }
    }

    private bool _isFavorite;
    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; set => SetProperty(ref _isPlaying, value); }

    private bool _playerVisible;
    public bool PlayerVisible { get => _playerVisible; set => SetProperty(ref _playerVisible, value); }

    private string _quality = "自动";
    public string Quality { get => _quality; set => SetProperty(ref _quality, value); }

    private float _speed = 1f;
    public float Speed
    {
        get => _speed;
        set
        {
            if (!SetProperty(ref _speed, value)) return;
            if (_player is not null) _player.SetRate(value);
        }
    }

    private int _maxQuality = 1080;
    public int MaxQuality { get => _maxQuality; set => SetProperty(ref _maxQuality, value); }

    public MediaPlayer? Player => _player;

    public ICommand HomeCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand FavoritesCommand { get; }
    public ICommand HistoryCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand SearchNowCommand { get; }
    public ICommand LoadMoreCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }

    private SeriesViewModel? _selected;
    public SeriesViewModel? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value) && value is not null) _ = LoadDetailAsync(value); }
    }

    private int _homePage = 1;
    private int _searchPage = 1;
    private string _lastQuery = "";
    private Detail? _detail;

    private void ShowHome() { Page = "home"; DetailOpen = false; }
    private void ShowSearch() { Page = "search"; DetailOpen = false; }
    private void ShowFavorites() { Page = "favorites"; DetailOpen = false; }
    private void ShowHistory() { Page = "history"; DetailOpen = false; }
    private void CloseDetail() { DetailOpen = false; }

    private async Task LoadHomeAsync()
    {
        try
        {
            Status = "正在加载推荐…";
            var page = await _repository.HomeAsync(1);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HomeItems.Clear();
                foreach (var s in page.Items) HomeItems.Add(ToViewModel(s));
            });
            Status = $"已加载 {page.Items.Count} 部短剧";
        }
        catch (Exception e) { Status = $"加载失败：{e.Message}"; }
    }

    private async Task LoadMoreAsync()
    {
        try
        {
            if (Page == "home")
            {
                var page = await _repository.HomeAsync(++_homePage);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var s in page.Items) HomeItems.Add(ToViewModel(s));
                });
            }
            else if (Page == "search" && _lastQuery.Length > 0)
            {
                await SearchAsync(++_searchPage);
            }
        }
        catch (Exception e) { Status = $"加载更多失败：{e.Message}"; }
    }

    private async Task SearchAsync(int page = 1)
    {
        var query = SearchText.Trim();
        if (query.Length == 0) return;
        if (page == 1) { _searchPage = 1; _lastQuery = query; }
        try
        {
            Status = $"正在搜索「{query}」…";
            var result = await _repository.SearchAsync(query, page);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (page == 1) SearchItems.Clear();
                foreach (var s in result.Items) SearchItems.Add(ToViewModel(s));
            });
            Status = $"找到 {result.Items.Count} 条结果";
        }
        catch (Exception e) { Status = $"搜索失败：{e.Message}"; }
    }

    private async Task LoadDetailAsync(SeriesViewModel series)
    {
        try
        {
            Status = "正在加载剧集详情…";
            var detail = await _repository.DetailAsync(series.Id);
            _detail = detail;
            Title = detail.Series.Title;
            Description = detail.Series.Description;
            Cover = detail.Series.Cover;
            Badge = detail.Series.Badge;
            Tags = detail.Series.Tags;
            IsFavorite = _library.IsFavorite(detail.Series.Id);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Episodes.Clear();
                for (var i = 0; i < detail.Episodes.Count; i++)
                {
                    EpisodeViewModel? episode = null;
                    episode = new EpisodeViewModel(detail.Episodes[i], i + 1, __ =>
                    {
                        var current = episode;
                        if (current is not null) _ = PlayEpisodeAsync(current);
                    });

                    Episodes.Add(episode);
                }
                DetailOpen = true;
            });
            Status = $"共 {detail.Episodes.Count} 集";
        }
        catch (Exception e) { Status = $"详情加载失败：{e.Message}"; }
    }

    private void InitializePlayer()
    {
        if (_libVlc is not null) return;
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC(
            "--no-video-title-show",
            "--network-caching=1500",
            "--clock-jitter=0",
            "--clock-synchro=0",
            "--no-drop-late-frames",
            "--no-skip-frames",
            "--avcodec-hw=none");
        _player = new MediaPlayer(_libVlc);
        _player.Playing += (_, _) => Dispatcher.UIThread.Post(() => IsPlaying = true);
        _player.Paused += (_, _) => Dispatcher.UIThread.Post(() => IsPlaying = false);
        _player.EndReached += (_, _) => Dispatcher.UIThread.Post(() => IsPlaying = false);
    }

    private async Task PlayEpisodeAsync(EpisodeViewModel episode)
    {
        if (_detail is null) return;
        _playbackLoad.Cancel();
        _playbackLoad = new CancellationTokenSource();
        var ct = _playbackLoad.Token;
        try
        {
            Status = "正在解析播放地址…";
            var ladder = await _repository.StreamLadderAsync(episode.Id, ct);
            var rendition = ladder.Select(MaxQuality);
            if (rendition is null) { Status = "没有可播放的清晰度"; return; }
            var url = _mediaServer.Mount(rendition.ToStreamInfo());
            if (!string.IsNullOrWhiteSpace(_currentMediaUrl)) _mediaServer.Unmount(_currentMediaUrl);
            _currentMediaUrl = url;
            Quality = rendition.Quality > 0 ? $"{rendition.Quality}P" : "自动";
            using var media = new Media(_libVlc!, new Uri(url));
            _player!.Play(media);
            PlayerVisible = true;
            foreach (var item in Episodes) item.Playing = false;
            episode.Playing = true;
            Status = $"正在播放 {episode.Label} · {Quality}";
            _library.SaveProgress(_detail, episode.Number - 1, DateTimeOffset.UtcNow.Ticks);
            _ = LoadLibraryAsync();
            _player.SetRate(Speed);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Status = $"播放失败：{e.Message}"; }
    }

    private void TogglePlayPause()
    {
        if (_player is null) return;
        if (_player.IsPlaying) _player.Pause(); else _player.Play();
    }

    private void StopPlayback()
    {
        _player?.Stop();
        if (!string.IsNullOrWhiteSpace(_currentMediaUrl)) _mediaServer.Unmount(_currentMediaUrl);
        _currentMediaUrl = null;
        PlayerVisible = false;
        foreach (var item in Episodes) item.Playing = false;
    }

    private void ToggleFavorite()
    {
        if (_detail is null) return;
        _library.ToggleFavorite(_detail.Series);
        IsFavorite = _library.IsFavorite(_detail.Series.Id);
        _ = LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Favorites.Clear();
            foreach (var f in _library.Favorites)
                Favorites.Add(new SeriesViewModel(f.Id, f.Title, f.Cover, "", "", _ => { }));
            History.Clear();
            foreach (var h in _library.History)
                History.Add(new HistoryViewModel(h, item =>
                    Selected = new SeriesViewModel(item.Id, item.Title, item.Cover, "", "", _ => { })));
        });
    }

    private SeriesViewModel ToViewModel(Series series) => new(series.Id, series.Title, series.Cover,
        series.Badge, series.Tags, item => Selected = item);

    public void Dispose()
    {
        _playbackLoad.Cancel();
        _suggestionLoad.Cancel();
        _player?.Dispose();
        _libVlc?.Dispose();
        _mediaServer.Dispose();
        _repository.Dispose();
    }
}
