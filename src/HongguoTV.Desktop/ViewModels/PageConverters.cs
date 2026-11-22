using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HongguoTV.Desktop.ViewModels;

public static class PageConverters
{
    public static readonly IValueConverter IsHome = new FuncValueConverter<string?, bool>(p => p == "home");
    public static readonly IValueConverter IsSearch = new FuncValueConverter<string?, bool>(p => p == "search");
    public static readonly IValueConverter IsFavorites = new FuncValueConverter<string?, bool>(p => p == "favorites");
    public static readonly IValueConverter IsHistory = new FuncValueConverter<string?, bool>(p => p == "history");
    public static readonly IValueConverter IsPaged = new FuncValueConverter<string?, bool>(p => p is "home" or "search");
    public static readonly IValueConverter FavoriteText = new FuncValueConverter<bool, string>(v => v ? "★ 已收藏" : "☆ 收藏");
    public static readonly IValueConverter PlayText = new FuncValueConverter<bool, string>(v => v ? "暂停" : "播放");

    public static readonly IValueConverter PlayingBackground = new FuncValueConverter<bool, IBrush>(v =>
        v ? new SolidColorBrush(Color.Parse("#E5484D")) : new SolidColorBrush(Color.Parse("#1C2431")));
}
