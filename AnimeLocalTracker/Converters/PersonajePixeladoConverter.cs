using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace AnimeLocalTracker.Converters;

/// <summary>
/// Imagen de "Adivina el personaje": (ruta del archivo, ancho en píxeles) → imagen reducida a ese ancho. Se muestra agrandada
/// sin suavizar (NearestNeighbor en la vista), así que se ven bloques; con un ancho de 0 sale entera.
/// </summary>
public sealed class PersonajePixeladoConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2 || values[0] is not string ruta || string.IsNullOrWhiteSpace(ruta)) return null;
        int lado = values[1] is int n ? n : 0;
        return Cargar(ruta, lado);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Null si el archivo no existe o no se puede leer. La imagen sale congelada y sin bloquear el archivo.</summary>
    internal static BitmapSource? Cargar(string ruta, int lado)
    {
        try
        {
            if (!File.Exists(ruta)) return null;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // lee todo y suelta el archivo
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (lado > 0) bitmap.DecodePixelWidth = lado;
            bitmap.UriSource = new Uri(ruta, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            AnimeLocalTracker.Services.AppLogger.Debug("PersonajePixeladoConverter", $"No se pudo cargar la imagen '{ruta}': {ex.Message}");
            return null;
        }
    }
}
