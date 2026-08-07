using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Fig.App.Converters
{
    /// <summary>True → 1.0 opacity, false → 0.5 (dims a disabled effect row).</summary>
    public sealed class BoolToOpacityConverter : IValueConverter
    {
        public static readonly BoolToOpacityConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? 1.0 : 0.5;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
