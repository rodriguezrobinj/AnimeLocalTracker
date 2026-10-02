using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Quitar un anime de la biblioteca (Galería y Ficha comparten el mismo flujo): confirma, pregunta si además se borran sus
/// archivos y los borra solo cuando la carpeta es exclusivamente de ese anime.
/// </summary>
public static class EliminacionAnime
{
    private static readonly Environment.SpecialFolder[] CarpetasDelUsuario =
    [
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.DesktopDirectory,
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.MyVideos,
        Environment.SpecialFolder.MyMusic,
        Environment.SpecialFolder.MyPictures
    ];

    /// <summary>
    /// False si borrar la carpeta se llevaría algo más que ese anime: es la raíz de una unidad, es (o contiene) una carpeta
    /// personal del usuario, u otro anime de la biblioteca usa esa misma carpeta o una de dentro (dos temporadas juntas).
    /// </summary>
    public static bool CarpetaSeguraParaBorrar(string carpeta, IEnumerable<string?> carpetasDeOtrosAnimes)
    {
        string? objetivo = Normalizar(carpeta);
        if (objetivo == null) return false;
        if (Normalizar(Path.GetPathRoot(objetivo + Path.DirectorySeparatorChar)) == objetivo) return false;

        var protegidas = CarpetasDelUsuario.Select(Environment.GetFolderPath).Concat(carpetasDeOtrosAnimes);
        return !protegidas.Select(Normalizar).Any(p => p != null &&
            (p.Equals(objetivo, StringComparison.OrdinalIgnoreCase)
             || p.StartsWith(objetivo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Ruta completa sin la barra final; null si está vacía o no es una ruta válida.</summary>
    private static string? Normalizar(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return null;
        try
        {
            // "D:" a secas significaría "la carpeta actual de la unidad D": aquí siempre se quiere decir la raíz.
            if (ruta.Trim().EndsWith(':')) ruta = ruta.Trim() + Path.DirectorySeparatorChar;
            return Path.GetFullPath(ruta).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True si el anime se quitó de la biblioteca (el usuario confirmó).</summary>
    public static async Task<bool> ConfirmarYEliminarAsync(AnimeItem anime, IDialogService dialogos, IDatabaseService baseDatos)
    {
        bool confirmacion = await dialogos.MostrarDialogoAsync(
            LocalizationService.T("Bib_EliminarTitulo"),
            string.Format(LocalizationService.T("Bib_EliminarMsj"), anime.Titulo),
            true, "HeartBrokenOutline", "#EF4444");
        if (!confirmacion) return false;

        string? carpeta = anime.RutaCarpeta;
        bool hayCarpeta = !string.IsNullOrWhiteSpace(carpeta) && Directory.Exists(carpeta);
        bool carpetaSegura = false;
        bool borrarArchivos = false;
        if (hayCarpeta)
        {
            var otros = (await baseDatos.ObtenerAnimesLigerosAsync() ?? new List<AnimeItem>())
                .Where(a => a.AniListId != anime.AniListId)
                .Select(a => a.RutaCarpeta);
            carpetaSegura = CarpetaSeguraParaBorrar(carpeta!, otros);

            // Solo se ofrece borrar los archivos cuando hay carpeta y es solo de este anime.
            if (carpetaSegura)
            {
                borrarArchivos = await dialogos.MostrarDialogoAsync(
                    LocalizationService.T("Bib_BorrarArchivosTitulo"),
                    string.Format(LocalizationService.T("Bib_BorrarArchivosMsj"), carpeta),
                    true, "FolderOutline", "#EF4444");
            }
        }

        await baseDatos.EliminarAnimeAsync(anime);

        if (hayCarpeta && !carpetaSegura)
        {
            dialogos.MostrarToast(LocalizationService.T("Bib_CarpetaConservadaTitulo"),
                string.Format(LocalizationService.T("Bib_CarpetaConservadaMsj"), carpeta), "FolderOutline", "#60A5FA");
        }
        else if (borrarArchivos)
        {
            try
            {
                // Fuera del hilo de la interfaz: una carpeta grande congelaba la ventana hasta terminar.
                await Task.Run(() => Directory.Delete(carpeta!, recursive: true));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("EliminacionAnime", $"No se pudo borrar la carpeta '{carpeta}': {ex.Message}");
                dialogos.MostrarToast(LocalizationService.T("Bib_BorradoFallidoTitulo"),
                    string.Format(LocalizationService.T("Bib_BorradoFallidoMsj"), carpeta), "AlertCircle", "#EF4444");
            }
        }

        return true;
    }
}
