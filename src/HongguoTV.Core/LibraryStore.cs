using System.Text.Json;
using System.Text.Json.Serialization;

namespace HongguoTV.Core;

public sealed record Favorite(string Id, string Title, string Cover, long AddedAt);
public sealed record HistoryItem(string Id, string Title, string Cover, string EpisodeId,
    int EpisodeIndex, long PositionTicks, long UpdatedAt, IReadOnlyList<string> EpisodeIds);

public sealed class LibraryData
{
    public List<Favorite> Favorites { get; set; } = new();
    public List<HistoryItem> History { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
}

public sealed class AppSettings
{
    /// <summary>true = GPU 硬件解码（D3D11VA）；false = CPU 软件解码。</summary>
    public bool UseGpuDecode { get; set; } = true;
}

[JsonSerializable(typeof(LibraryData))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LibraryJsonContext : JsonSerializerContext
{
}

public sealed class LibraryStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private LibraryData _data;

    public LibraryStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HongguoTV.Desktop");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "library.json");
        _data = Load();
    }

    private LibraryData Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path), LibraryJsonContext.Default.LibraryData) ?? new LibraryData();
        }
        catch { }
        return new LibraryData();
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_data, LibraryJsonContext.Default.LibraryData)); }
        catch { }
    }

    public IReadOnlyList<Favorite> Favorites { get { lock (_lock) return _data.Favorites.ToList(); } }
    public IReadOnlyList<HistoryItem> History { get { lock (_lock) return _data.History.OrderByDescending(h => h.UpdatedAt).ToList(); } }

    public bool UseGpuDecode { get { lock (_lock) return _data.Settings.UseGpuDecode; } }

    public void SetUseGpuDecode(bool value)
    {
        lock (_lock)
        {
            if (_data.Settings.UseGpuDecode == value) return;
            _data.Settings.UseGpuDecode = value;
            Save();
        }
    }

    public bool IsFavorite(string id) { lock (_lock) return _data.Favorites.Any(f => f.Id == id); }

    public void ToggleFavorite(Series series)
    {
        lock (_lock)
        {
            var existing = _data.Favorites.FirstOrDefault(f => f.Id == series.Id);
            if (existing is not null) _data.Favorites.Remove(existing);
            else _data.Favorites.Insert(0, new Favorite(series.Id, series.Title, series.Cover, DateTimeOffset.UtcNow.Ticks));
            Save();
        }
    }

    public void SaveProgress(Detail detail, int episodeIndex, long positionTicks)
    {
        if (episodeIndex < 0 || episodeIndex >= detail.Episodes.Count) return;
        lock (_lock)
        {
            var item = _data.History.FirstOrDefault(h => h.Id == detail.Series.Id);
            if (item is null)
            {
                item = new HistoryItem(detail.Series.Id, detail.Series.Title, detail.Series.Cover,
                    detail.Episodes[episodeIndex], episodeIndex, positionTicks,
                    DateTimeOffset.UtcNow.Ticks, detail.Episodes);
                _data.History.Insert(0, item);
            }
            else
            {
                _data.History.Remove(item);
                _data.History.Insert(0, item with
                {
                    EpisodeId = detail.Episodes[episodeIndex],
                    EpisodeIndex = episodeIndex,
                    PositionTicks = positionTicks,
                    UpdatedAt = DateTimeOffset.UtcNow.Ticks,
                    EpisodeIds = detail.Episodes,
                });
            }
            Save();
        }
    }

    public HistoryItem? Find(string id) { lock (_lock) return _data.History.FirstOrDefault(h => h.Id == id); }
}
