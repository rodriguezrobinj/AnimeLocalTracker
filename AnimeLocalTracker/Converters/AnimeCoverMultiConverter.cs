using System;
using System.Globalization;
using System.Windows.Data;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AnimeLocalTracker.Converters;

public class AnimeCoverMultiConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values != null && values.Length > 0 && values[0] is AnimeItem anime)
        {
            var cache = App.ServiceProvider?.GetService<IImageCacheService>();
            if (cache != null)
            {
                // Obtenemos de memoria sincrónicamente.
                // Cuando el ViewModel termina de descargarla llama a NotificarPortadaActualizada(),
                // lo que cambia PortadaVisible y re-evalúa este binding.
                return cache.ObtenerPortadaEnMemoria(anime.AniListId);
            }
        }
        return null;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
