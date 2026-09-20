using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace GameTimeTracker.App.Converters;

public class BoolToAccentBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(255, 0x4F, 0x8F, 0xFF));
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value is bool b && b) ? AccentBrush : TransparentBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class BoolToContrastTextBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush WhiteBrush = new(Colors.White);
    private static readonly SolidColorBrush MutedBrush = new(Windows.UI.Color.FromArgb(255, 0x94, 0xA3, 0xB8));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return (value is bool b && b) ? WhiteBrush : MutedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class HexToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush FallbackBrush = new(Windows.UI.Color.FromArgb(255, 0x64, 0x74, 0x8B));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string hex || string.IsNullOrWhiteSpace(hex))
        {
            return FallbackBrush;
        }

        hex = hex.Trim();
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached))
            {
                return cached;
            }
        }

        try
        {
            var clean = hex.TrimStart('#');
            Windows.UI.Color color;
            if (clean.Length == 6)
            {
                byte r = System.Convert.ToByte(clean.Substring(0, 2), 16);
                byte g = System.Convert.ToByte(clean.Substring(2, 2), 16);
                byte b = System.Convert.ToByte(clean.Substring(4, 2), 16);
                color = Windows.UI.Color.FromArgb(255, r, g, b);
            }
            else if (clean.Length == 8)
            {
                byte a = System.Convert.ToByte(clean.Substring(0, 2), 16);
                byte r = System.Convert.ToByte(clean.Substring(2, 2), 16);
                byte g = System.Convert.ToByte(clean.Substring(4, 2), 16);
                byte b = System.Convert.ToByte(clean.Substring(6, 2), 16);
                color = Windows.UI.Color.FromArgb(a, r, g, b);
            }
            else
            {
                return FallbackBrush;
            }

            var brush = new SolidColorBrush(color);
            lock (Cache)
            {
                if (Cache.Count > 100) Cache.Clear();
                Cache[hex] = brush;
            }
            return brush;
        }
        catch
        {
            return FallbackBrush;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class RankToBadgeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GoldBadge = new(Windows.UI.Color.FromArgb(255, 0xFE, 0xF3, 0xC7));
    private static readonly SolidColorBrush SilverBadge = new(Windows.UI.Color.FromArgb(255, 0xE2, 0xE8, 0xF0));
    private static readonly SolidColorBrush BronzeBadge = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xED, 0xD5));
    private static readonly SolidColorBrush NormalBadge = new(Windows.UI.Color.FromArgb(255, 0xF1, 0xF5, 0xF9));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var rank = value is int r ? r : 0;
        return rank switch
        {
            1 => GoldBadge,
            2 => SilverBadge,
            3 => BronzeBadge,
            _ => NormalBadge
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}

public class RankToTextBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GoldText = new(Windows.UI.Color.FromArgb(255, 0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush SilverText = new(Windows.UI.Color.FromArgb(255, 0x47, 0x55, 0x69));
    private static readonly SolidColorBrush BronzeText = new(Windows.UI.Color.FromArgb(255, 0xC2, 0x41, 0x0C));
    private static readonly SolidColorBrush NormalText = new(Windows.UI.Color.FromArgb(255, 0x94, 0xA3, 0xB8));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var rank = value is int r ? r : 0;
        return rank switch
        {
            1 => GoldText,
            2 => SilverText,
            3 => BronzeText,
            _ => NormalText
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
