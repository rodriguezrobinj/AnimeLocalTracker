using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>Cuánto queda por organizar en la carpeta de música: canciones y animes a los que pertenecen.</summary>
public sealed record PendientesMusica(int Canciones, IReadOnlyList<int> Animes);

/// <summary>Resultado de "Organizar mi música". <see cref="SinDatos"/> = canciones de animes que ya no están en AnimeThemes (o
/// sin conexión y sin lista guardada): se quedan como estaban.</summary>
public sealed record ResultadoOrganizarMusica(int Renombradas, int Etiquetadas, int SinDatos, bool Cancelado);

public interface IOrganizadorMusicaService
{
    /// <summary>Recuento sin red de lo que falta por organizar (nombre técnico antiguo o sin etiquetas).</summary>
    PendientesMusica ContarPendientes();

    /// <summary>
    /// Pone nombre legible, etiquetas y portada a todos los openings/endings ya descargados, anime por anime, sin volver a
    /// descargar nada. Informa del avance (animes hechos, total).
    /// </summary>
    Task<ResultadoOrganizarMusica> OrganizarTodoAsync(IReadOnlyList<int> animes, IProgress<(int Hechos, int Total)>? progreso, CancellationToken ct);
}

/// <summary>
/// "Organizar mi música" (Configuración): los mp3 descargados antes de que la app los nombrara y etiquetara. Para cada anime
/// usa su lista de openings/endings (la guardada en disco si la hay; si no, se pide a AnimeThemes espaciando las peticiones
/// para no rozar su límite de 90 por minuto) y reutiliza lo mismo que hace la ficha al abrirse.
/// </summary>
public sealed class OrganizadorMusicaService : IOrganizadorMusicaService
{
    private readonly IAnimeThemesService _themes;
    private readonly IAnimeThemesDownloadService _descargas;

    /// <summary>Pausa tras cada anime que haya tenido que preguntar a AnimeThemes (2 peticiones). Ajustable en pruebas.</summary>
    internal TimeSpan PausaTrasConsultarApi { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Por debajo de esto, la lista salió de la caché (memoria o disco) y no hace falta pausa.</summary>
    private static readonly TimeSpan UmbralRespuestaDeCache = TimeSpan.FromMilliseconds(150);

    public OrganizadorMusicaService(IAnimeThemesService themes, IAnimeThemesDownloadService descargas)
    {
        _themes = themes;
        _descargas = descargas;
    }

    public PendientesMusica ContarPendientes()
    {
        int canciones = 0;
        var animes = new List<int>();
        foreach (int id in _descargas.AnimesConDescargas())
        {
            int pendientes = _descargas.ContarPendientesDeOrganizar(id);
            if (pendientes <= 0) continue;
            canciones += pendientes;
            animes.Add(id);
        }
        return new PendientesMusica(canciones, animes);
    }

    public async Task<ResultadoOrganizarMusica> OrganizarTodoAsync(IReadOnlyList<int> animes, IProgress<(int Hechos, int Total)>? progreso, CancellationToken ct)
    {
        int renombradas = 0, etiquetadas = 0, sinDatos = 0, hechos = 0;
        progreso?.Report((0, animes.Count));

        try
        {
            foreach (int id in animes)
            {
                ct.ThrowIfCancellationRequested();

                var reloj = Stopwatch.StartNew();
                var catalogo = await _themes.ObtenerTemasAsync(id, ct);
                bool preguntoALaApi = reloj.Elapsed > UmbralRespuestaDeCache;
                ct.ThrowIfCancellationRequested();

                if (catalogo.Count == 0)
                {
                    sinDatos += _descargas.ContarPendientesDeOrganizar(id);
                }
                else
                {
                    renombradas += _descargas.ReconciliarDescargasLocales(id, catalogo);
                    etiquetadas += await _descargas.EtiquetarDescargasLocalesAsync(id, catalogo, ct);
                    sinDatos += _descargas.ContarPendientesDeOrganizar(id); // lo que quedó (archivos en uso, temas retirados…)
                }

                progreso?.Report((++hechos, animes.Count));
                if (preguntoALaApi && hechos < animes.Count) await Task.Delay(PausaTrasConsultarApi, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ResultadoOrganizarMusica(renombradas, etiquetadas, sinDatos, Cancelado: true);
        }

        AppLogger.Info("OrganizadorMusica", $"Música organizada: {renombradas} renombradas, {etiquetadas} etiquetadas, {sinDatos} sin datos, {animes.Count} animes.");
        return new ResultadoOrganizarMusica(renombradas, etiquetadas, sinDatos, Cancelado: false);
    }
}
