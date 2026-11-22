using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace HongguoTV.Desktop.Views;

/// <summary>Cover image with a small in-memory cache; failures fall back to a placeholder.</summary>
public sealed class AsyncImage : Image
{
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new();
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly IImage Placeholder = CreatePlaceholder();

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<AsyncImage, string?>(nameof(Url));

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    static AsyncImage()
    {
        UrlProperty.Changed.AddClassHandler<AsyncImage>((image, _) => image.Load());
    }

    private static IImage CreatePlaceholder()
    {
        var bitmap = new WriteableBitmap(new PixelSize(2, 3), new Vector(96, 96), Avalonia.Platform.PixelFormat.Rgba8888);
        using var buffer = bitmap.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = 0x1c; bytes[i + 1] = 0x24; bytes[i + 2] = 0x31; bytes[i + 3] = 0xff;
        }
        Marshal.Copy(bytes, 0, buffer.Address, bytes.Length);
        return bitmap;
    }

    private void Load()
    {
        var url = Url;
        if (string.IsNullOrWhiteSpace(url)) { Source = Placeholder; return; }
        if (Cache.TryGetValue(url, out var cached)) { Source = cached; return; }
        Source = Placeholder;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Client.GetByteArrayAsync(url);
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                Cache[url] = bitmap;
                await Dispatcher.UIThread.InvokeAsync(() => { if (Url == url) Source = bitmap; });
            }
            catch { }
        });
    }
}
