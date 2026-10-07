using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Services;

/// <inheritdoc cref="ILimpiadorDeEpisodios"/>
public sealed class LimpiadorDeEpisodios : ILimpiadorDeEpisodios
{
    private readonly IDatabaseService _database;
    private readonly IFileScannerService _escaner;
    private readonly ISettingsService _ajustes;
    private readonly IDialogService _dialogos;

    public LimpiadorDeEpisodios(IDatabaseService database, IFileScannerService escaner, ISettingsService ajustes, IDialogService dialogos)
    {
        _database = database;
        _escaner = escaner;
        _ajustes = ajustes;
        _dialogos = dialogos;
    }

    /// <summary>Pausa antes de empezar: el reproductor que acaba de cerrar el video necesita un instante para soltar el archivo. Ajustable solo en pruebas.</summary>
    internal TimeSpan EsperaInicial { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Cuánto se espera a que la base de datos muestre el episodio como visto (el guardado local es asíncrono). Ajustable solo en pruebas.</summary>
    internal TimeSpan EsperaMaximaMarcaVisto { get; set; } = TimeSpan.FromSeconds(10);

    internal TimeSpan EsperaEntreComprobaciones { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Intentos de borrado de cada video si está en uso (200 ms entre intentos). Ajustable solo en pruebas.</summary>
    internal int IntentosBorrado { get; set; } = BorradoDeArchivos.IntentosPorDefecto;

    public async Task AplicarTrasVerAsync(int aniListId, int episodioTerminado)
    {
        try
        {
            var config = _ajustes.ObtenerConfiguracion();
            string modo = ModoEliminarTrasVerValores.Normalizar(config.ModoEliminarTrasVer);
            if (modo == ModoEliminarTrasVerValores.Apagado || aniListId <= 0 || episodioTerminado <= 0) return;

            if (EsperaInicial > TimeSpan.Zero) await Task.Delay(EsperaInicial).ConfigureAwait(false);

            var anime = await _database.ObtenerAnimePorIdAsync(aniListId).ConfigureAwait(false);
            if (anime == null || string.IsNullOrWhiteSpace(anime.RutaCarpeta)) return;
            if (anime.ConservarVideos)
            {
                AppLogger.Debug("LimpiadorDeEpisodios", $"{anime.Titulo} tiene activado 'Conservar los videos': no se borra nada.");
                return;
            }

            var registros = await EsperarMarcaDeVistoAsync(aniListId, episodioTerminado).ConfigureAwait(false);
            if (registros == null)
            {
                AppLogger.Debug("LimpiadorDeEpisodios", $"El episodio {episodioTerminado} de {anime.Titulo} no quedó marcado como visto: no se borra nada.");
                return;
            }

            var vistos = registros.Where(r => r.VistoLocal).Select(r => r.NumeroEpisodio).ToHashSet();
            var encontrados = await _escaner.EscanearEpisodiosAsync(anime.RutaCarpeta).ConfigureAwait(false);
            var archivos = encontrados
                .Where(e => vistos.Contains(e.NumeroEpisodio) && EstaDentroDe(anime.RutaCarpeta, e.RutaCompleta) && File.Exists(e.RutaCompleta))
                .GroupBy(e => e.NumeroEpisodio)
                .ToDictionary(g => g.Key, g => g.First().RutaCompleta);

            var aBorrar = LimpiezaTrasVer.Decidir(modo, Math.Clamp(config.EpisodiosAConservar, 1, 10), episodioTerminado, anime.TotalEpisodios, archivos.Keys.ToList());
            if (aBorrar.Count == 0) return;

            if (modo == ModoEliminarTrasVerValores.AlCompletarSerie && !await ConfirmarAsync(anime, aBorrar.Select(n => archivos[n]).ToList()).ConfigureAwait(false)) return;

            // Se vuelve a leer: entre la primera comprobación y aquí pasó el escaneo del disco y, en "Al completar la serie", el
            // diálogo de confirmación abierto. Si en ese rato se activó "Conservar los videos", no se borra.
            if (await _database.ObtenerConservarVideosAsync(aniListId).ConfigureAwait(false))
            {
                AppLogger.Debug("LimpiadorDeEpisodios", $"{anime.Titulo} pasó a 'Conservar los videos' antes de borrar: no se borra nada.");
                return;
            }

            await BorrarAsync(anime, aBorrar.Select(n => (Numero: n, Ruta: archivos[n])).ToList()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LimpiadorDeEpisodios", $"No se pudo aplicar 'Eliminar tras ver' (anime {aniListId}, episodio {episodioTerminado}): {ex.Message}");
        }
    }

    /// <summary>Espera a que el episodio figure como visto en la base de datos; null si no llega a tiempo.</summary>
    private async Task<List<RegistroEpisodio>?> EsperarMarcaDeVistoAsync(int aniListId, int episodio)
    {
        var limite = DateTime.UtcNow + EsperaMaximaMarcaVisto;
        while (true)
        {
            var registros = await _database.ObtenerRegistrosPorAnimeAsync(aniListId).ConfigureAwait(false);
            if (registros != null && registros.Any(r => r.NumeroEpisodio == episodio && r.VistoLocal)) return registros;
            if (DateTime.UtcNow >= limite) return null;
            await Task.Delay(EsperaEntreComprobaciones).ConfigureAwait(false);
        }
    }

    /// <summary>El archivo debe estar dentro de la carpeta del anime: nunca se borra nada fuera de ella.</summary>
    internal static bool EstaDentroDe(string carpeta, string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return false;
        string raiz = Path.GetFullPath(carpeta).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(ruta).StartsWith(raiz, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ConfirmarAsync(AnimeItem anime, List<string> rutas)
    {
        long bytes = await Task.Run(() =>
        {
            long total = 0;
            foreach (string ruta in rutas)
            {
                try { total += new FileInfo(ruta).Length; }
                catch (IOException) { /* archivo en uso o desaparecido: no cuenta */ }
            }
            return total;
        }).ConfigureAwait(false);

        return await _dialogos.MostrarDialogoAsync(
            LocalizationService.T("Lim_CompletarTitulo"),
            string.Format(LocalizationService.T("Lim_CompletarMsjFormato"), anime.Titulo, Formato.Tamano(bytes), rutas.Count),
            true, "DeleteSweepOutline", "#EF4444").ConfigureAwait(false);
    }

    private async Task BorrarAsync(AnimeItem anime, List<(int Numero, string Ruta)> episodios)
    {
        long liberados = 0;
        int borrados = 0;

        foreach (var (numero, ruta) in episodios)
        {
            try
            {
                liberados += await Task.Run(() => BorradoDeEpisodio.BorrarVideoYMiniatura(ruta, IntentosBorrado)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("LimpiadorDeEpisodios", $"No se pudo borrar el episodio {numero} de {anime.Titulo} (se deja como estaba): {ex.Message}");
                continue;
            }

            borrados++;
            AppLogger.Info("LimpiadorDeEpisodios", $"Eliminar tras ver: borrado el episodio {numero} de {anime.Titulo}.");

            // El historial es permanente: borrar el archivo NO borra que se vio el episodio.
            try { await _database.ConservarRegistroTrasEliminarArchivoAsync(anime.AniListId, numero).ConfigureAwait(false); }
            catch (Exception ex) { AppLogger.Debug("LimpiadorDeEpisodios", $"No se pudo conservar el registro del episodio {numero}: {ex.Message}"); }

            WeakReferenceMessenger.Default.Send(new ArchivoEpisodioEliminadoMensaje(anime.AniListId, numero));
        }

        if (borrados > 0)
        {
            _dialogos.MostrarToast(
                LocalizationService.T("Lim_LiberadoTitulo"),
                string.Format(LocalizationService.T("Lim_LiberadoMsjFormato"), borrados, Formato.Tamano(liberados)),
                "DeleteSweepOutline", "#4CAF50");
        }
    }
}
