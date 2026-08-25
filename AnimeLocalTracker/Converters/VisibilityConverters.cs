using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AnimeLocalTracker.Converters;

/// <summary>
/// Devuelve Visible cuando el string recibido es igual al parámetro (ignora mayúsculas/espacios).
/// Se usa para resaltar el día actual en el calendario sin exponer 7 bools del ViewModel.
/// </summary>
public class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? a = value?.ToString()?.Trim();
        string? b = parameter?.ToString()?.Trim();
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Devuelve Visible cuando el conteo es cero (estados vacíos por columna del calendario).
/// </summary>
public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is int n && n > 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
