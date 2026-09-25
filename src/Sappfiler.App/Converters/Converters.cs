using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using System.IO;
using Windows.Storage.Streams;

namespace GameTimeTracker.App.Converters;

public class HeatmapLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value is int l ? l : 0;
        return Controls.HeatmapControl.GetThemeBrush(level);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class StringToImageSourceConverter : IValueConverter
{
    /// <summary>
    /// 本地封面的解码缓存，键为「路径 + 文件最后修改时间」，封面被重新提取后会自然失效。
    ///
    /// 早先这里是一个无上限的「流保活列表」，用于防止支撑流被 GC 回收导致白屏；
    /// 但那种写法在长时间运行中会持续累积（隐性内存泄漏）。改为有界缓存后，
    /// 既能避免重复解码，又保证了内存占用可控。
    /// </summary>
    private static readonly Dictionary<string, BitmapImage> LocalImageCache = new();
    private const int MaxCacheEntries = 200;

    /// <summary>
    /// 清空本地图片缓存。窗口收进托盘时需要调用，否则缓存会拖住这些位图，
    /// 让隐藏后的内存迟迟降不下来。
    /// </summary>
    public static void ClearLocalImageCache()
    {
        lock (LocalImageCache)
        {
            LocalImageCache.Clear();
        }
    }

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        Uri? uri = Uri.TryCreate(path, UriKind.Absolute, out var parsed) ? parsed : null;

        // 1. 远程地址与应用资源：BitmapImage 可以直接加载。
        if (uri is not null &&
            (uri.Scheme == Uri.UriSchemeHttp ||
             uri.Scheme == Uri.UriSchemeHttps ||
             uri.Scheme == "ms-appx"))
        {
            try
            {
                var bmp = new BitmapImage(uri);
                bmp.DecodePixelWidth = 920;
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // 2. 本地文件（裸 "C:\..." 或显式 file://）：
        //    Unpackaged WinUI 3 无法渲染 file:// 协议，且 BitmapImage 遇到这种 URI
        //    不会抛异常、只会静默渲染空白，所以必须主动改走内存流。
        var localPath = path;
        if (uri is not null && uri.IsFile)
        {
            try
            {
                localPath = uri.LocalPath;
            }
            catch
            {
                localPath = path;
            }
        }

        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return null;
        }

        return LoadLocalImage(localPath);
    }

    private static BitmapImage? LoadLocalImage(string path)
    {
        string key;
        try
        {
            key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
        }
        catch
        {
            return null;
        }

        lock (LocalImageCache)
        {
            if (LocalImageCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return null;
            }

            var memStream = new InMemoryRandomAccessStream();

            // 注意：绝对不能对 AsStreamForWrite() 返回的包装流使用 using。
            // 释放该包装流会连带关闭底层 WinRT 流，SetSource 随即失效，
            // 表现为图片区域只剩容器背景色（一块深色方块）。
            var outStream = memStream.AsStreamForWrite();
            outStream.Write(bytes, 0, bytes.Length);
            outStream.Flush();
            memStream.Seek(0);

            var bmp = new BitmapImage();
            bmp.DecodePixelWidth = 920;
            bmp.SetSource(memStream);

            lock (LocalImageCache)
            {
                if (LocalImageCache.Count >= MaxCacheEntries)
                {
                    LocalImageCache.Clear();
                }
                LocalImageCache[key] = bmp;
            }

            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value is bool b && b) ? 1.0 : 0.35;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var boolVal = value is bool b && b;
        if (Invert) boolVal = !boolVal;
        return boolVal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isNullOrEmpty = value == null || (value is string s && string.IsNullOrWhiteSpace(s));
        var visible = !isNullOrEmpty;
        if (Invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
