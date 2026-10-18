using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Torrents;

/// <summary>Un archivo dentro de un torrent (ruta relativa y tamaño).</summary>
public sealed record ArchivoTorrent(string Ruta, long Tamano);

/// <summary>
/// Qué episodios trae un torrent. <paramref name="Todos"/>: los números reconocidos (ordenados); <paramref name="Nuevos"/>: los que no
/// se tienen ya; <paramref name="SinNumero"/>: vídeos de un lote cuyo nombre no dice el episodio (se omiten).
/// </summary>
public sealed record RepartoEpisodios(IReadOnlyList<int> Todos, IReadOnlyList<int> Nuevos, int YaEstan, int SinNumero, int Videos);

/// <summary>
/// Torrent soltado en la ventana (ver docs/plan-torrents-arrastrar-magnet.md): de qué anime de la biblioteca es y qué episodios trae.
/// Cálculo puro: reutiliza el analizador de releases de Nyaa (<see cref="NyaaRssParser"/>), que ya distingue temporadas.
/// </summary>
public static class AnalisisTorrentImportado
{
    private static readonly string[] ExtensionesVideo = { ".mkv", ".mp4", ".avi" };

    public static bool EsVideo(string ruta) => ExtensionesVideo.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> TitulosDe(AnimeItem anime)
    {
        var titulos = new List<string> { anime.Titulo };
        if (!string.IsNullOrWhiteSpace(anime.NombresAlternativos))
            titulos.AddRange(anime.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return titulos.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>El anime de la biblioteca al que pertenece el torrent, si es uno solo; con ninguno o varios, null (lo elige el usuario).</summary>
    public static AnimeItem? ReconocerAnime(string nombreTorrent, IEnumerable<AnimeItem> biblioteca)
    {
        string nombre = EsVideo(nombreTorrent) ? Path.GetFileNameWithoutExtension(nombreTorrent) : nombreTorrent;
        var release = NyaaRssParser.AnalizarRelease(nombre);
        var coincidencias = biblioteca.Where(a => NyaaRssParser.EsDelAnime(release, TitulosDe(a))).Take(2).ToList();
        return coincidencias.Count == 1 ? coincidencias[0] : null;
    }

    public static RepartoEpisodios Repartir(IEnumerable<ArchivoTorrent> archivos, Func<int, bool> yaLoTiene)
    {
        var videos = archivos.Where(a => EsVideo(a.Ruta)).ToList();
        var numeros = videos.Select(v => NyaaRssParser.ExtraerNumeroEpisodio(Path.GetFileName(v.Ruta))).ToList();

        // Un solo vídeo sin número (una película): episodio 1. El motor ya cae al vídeo más grande en ese caso.
        if (videos.Count == 1 && numeros[0] is not > 0) numeros[0] = 1;

        var todos = numeros.Where(n => n is > 0).Select(n => n!.Value).Distinct().OrderBy(n => n).ToList();
        var nuevos = todos.Where(n => !yaLoTiene(n)).ToList();
        return new RepartoEpisodios(todos, nuevos, todos.Count - nuevos.Count, numeros.Count(n => n is not > 0), videos.Count);
    }

    /// <summary>[1,2,3,5,7,8] → "1–3, 5, 7–8".</summary>
    public static string FormatearRangos(IReadOnlyList<int> numeros)
    {
        var tramos = new List<string>();
        for (int i = 0; i < numeros.Count;)
        {
            int j = i;
            while (j + 1 < numeros.Count && numeros[j + 1] == numeros[j] + 1) j++;
            tramos.Add(j > i ? $"{numeros[i]}–{numeros[j]}" : numeros[i].ToString(LocalizationService.Cultura));
            i = j + 1;
        }
        return string.Join(", ", tramos);
    }
}
