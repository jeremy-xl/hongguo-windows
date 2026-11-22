namespace HongguoTV.Core;

public sealed record Series(string Id, string Title, string Cover = "", string Description = "",
    string Badge = "", string Tags = "");

public sealed record Detail(Series Series, IReadOnlyList<string> Episodes);

public sealed record CatalogPage(IReadOnlyList<Series> Items, bool HasMore, string? UpdatedText = null);

public sealed record StreamInfo(string Url, byte[]? Key, string Quality, string? BackupUrl = null);

public sealed record Rendition(int Quality, string Codec, int? Width, int? Height, string Url,
    byte[]? Key, string? BackupUrl = null)
{
    public StreamInfo ToStreamInfo() => new(Url, Key, Quality > 0 ? $"{Quality}P" : "自动", BackupUrl);
}

public sealed class StreamLadder
{
    private static readonly HashSet<string> AvcLabels = new(StringComparer.OrdinalIgnoreCase) { "h264", "avc", "avc1", "avc3" };

    public StreamLadder(IReadOnlyList<Rendition> renditions) => Renditions = renditions;

    public IReadOnlyList<Rendition> Renditions { get; }

    private static int Compare(Rendition a, Rendition b)
    {
        var c = a.Quality.CompareTo(b.Quality);
        if (c != 0) return c;
        return (AvcLabels.Contains(a.Codec) ? 1 : 0).CompareTo(AvcLabels.Contains(b.Codec) ? 1 : 0);
    }

    public Rendition? Select(int maxQuality, Func<Rendition, bool>? supported = null)
    {
        var pool = Renditions.Where(supported ?? (_ => true)).ToList();
        if (pool.Count == 0) pool = Renditions.ToList();
        var comparer = Comparer<Rendition>.Create(Compare);
        return pool.Where(r => r.Quality > 0 && r.Quality <= maxQuality).MaxBy(r => r, comparer)
            ?? pool.Where(r => r.Quality <= 0).MaxBy(r => r, comparer)
            ?? pool.MinBy(r => r, comparer);
    }

    public IReadOnlyList<Rendition> PlaybackOrder(int maxQuality, Func<Rendition, bool>? supported = null)
    {
        var pick = Select(maxQuality, supported);
        if (pick is null) return Array.Empty<Rendition>();
        var alternates = Renditions.Where(r => !ReferenceEquals(r, pick) && r.Quality <= pick.Quality)
            .OrderByDescending(r => r, Comparer<Rendition>.Create(Compare));
        return new[] { pick }.Concat(alternates).ToList();
    }
}

public sealed class ContentException : IOException
{
    public ContentException(string message) : base(message) { }
    public ContentException(string message, Exception inner) : base(message, inner) { }
}
