using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeLocalTracker.Converters;

/// <summary>
/// Efecto "bones": indica si la portada de ESTA tarjeta en concreto todavía no está
/// disponible en memoria, para mostrar el shimmer solo mientras esa portada carga
/// (y no en toda la lista, ni en tarjetas cuya portada ya está cacheada).
/// </summary>
public class AnimeCoverLoadingConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values != null && values.Length > 0 && values[0] is AnimeItem anime)
        {
            var cache = App.ServiceProvider?.GetService<IImageCacheService>();
            if (cache != null)
            {
                bool cargada = cache.ObtenerPortadaEnMemoria(anime.AniListId) != null;
                return cargada ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
