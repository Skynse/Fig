using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Fig.App.Converters;

public class SecondsToTimeConverter : IValueConverter
{
    public static readonly SecondsToTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double seconds)
            return "";

        var total = (int)Math.Round(seconds);
        var h = total / 3600;
        var m = (total % 3600) / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
