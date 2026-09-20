using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

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

/// <summary>
/// Devuelve Collapsed cuando el valor es null (badges/etiquetas opcionales).
/// </summary>
public class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// string.Format(values[0], values[1]) donde values[0] es la plantilla localizada (con "{0}") y
/// values[1] el valor a insertar. Permite localizar badges tipo "{0} Nuevos" bindeados directo al
/// ítem de una lista (no a un ViewModel con método propio), refrescando también al cambiar idioma
/// porque values[0] viene de LocalizationService, que eleva PropertyChanged("Item[]").
/// </summary>
public class LocalizedFormatConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string formato) return string.Empty;
        return string.Format(culture, formato, values[1]);
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Traduce un género de AniList (siempre en inglés) solo para mostrarlo — el valor real
/// (usado en filtros/comparaciones) nunca pasa por aquí, ver LocalizationService.TraducirGenero.
/// </summary>
public class GeneroTraducidoConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => AnimeLocalTracker.Services.LocalizationService.TraducirGenero(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Niega un bool — usado para IsEnabled ligado a un flag "ocupado/cargando".</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !(value is true);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !(value is true);
}

/// <summary>
/// values[0]=número de episodio del ítem, values[1]=episodio actualmente reproduciéndose.
/// Usado en el cajón lateral de episodios para resaltar la fila del episodio en curso.
/// </summary>
public class EpisodioActualBrushConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not int numero || values[1] is not int actual)
            return Brushes.Transparent;
        return numero == actual ? new SolidColorBrush(Color.FromArgb(0x33, 0x60, 0xA5, 0xFA)) : Brushes.Transparent;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
