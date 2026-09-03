using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Detecta episodios nuevos en las carpetas de la biblioteca (archivos que aún no
/// tienen registro en la base de datos) y avisa con un toast. La deduplicación se
/// persiste en un JSON local para no notificar dos veces el mismo episodio.
/// </summary>
public class NewEpisodeNotifier
{
    private readonly IDatabaseService _databaseService;
    private readonly IFileScannerService _fileScannerService;
    private readonly ISettingsService _settingsService;
    private readonly string _notificadosPath = Path.Combine(AppDataPaths.DataRoot, "episodios_notificados.json");

    public NewEpisodeNotifier(
        IDatabaseService databaseService,
        IFileScannerService fileScannerService,
        ISettingsService settingsService,
        string? customNotificadosPath = null)
    {
        _databaseService = databaseService;
        _fileScannerService = fileScannerService;
        _settingsService = settingsService;
        if (!string.IsNullOrWhiteSpace(customNotificadosPath))
        {
            _notificadosPath = customNotificadosPath;
        }
    }

    /// <summary>
    /// Busca episodios nuevos y, si los hay, envía <see cref="NuevosEpisodiosMensaje"/>.
    /// Devuelve la cantidad detectada (0 si la notificación está desactivada).
    /// </summary>
    public async Task<int> BuscarYNotificarNuevosAsync()
    {
        var config = _settingsService.ObtenerConfiguracion();
        if (config == null || !config.NotificarNuevosEpisodios) return 0;

        var animes = await _databaseService.ObtenerAnimesLigerosAsync() ?? new List<Models.AnimeItem>();
        if (animes.Count == 0) return 0;

        var registros = await _databaseService.ObtenerTodosLosRegistrosAsync() ?? new List<Models.RegistroEpisodio>();
        var registrados = new HashSet<string>(registros.Select(r => $"{r.AniListId}:{r.NumeroEpisodio}"));

        var notificados = CargarNotificados();
        var nuevos = new List<(string Titulo, int Episodio)>();

        const int LimitePorPasada = 20;
        foreach (var anime in animes)
        {
            if (string.IsNullOrWhiteSpace(anime.RutaCarpeta) || !Directory.Exists(anime.RutaCarpeta)) continue;

            try
            {
                var episodios = await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta);
                foreach (var ep in episodios)
                {
                    string key = $"{anime.AniListId}:{ep.NumeroEpisodio}";
                    if (!registrados.Contains(key) && !notificados.Contains(key))
                    {
                        notificados.Add(key);
                        nuevos.Add((string.IsNullOrWhiteSpace(anime.Titulo) ? LocalizationService.T("Notif_SinTitulo") : anime.Titulo, ep.NumeroEpisodio));
                        if (nuevos.Count >= LimitePorPasada) break;
                    }
                }
            }
            catch { }

            if (nuevos.Count >= LimitePorPasada) break;
        }

        if (nuevos.Count == 0) return 0;

        GuardarNotificados(notificados);

        var resumen = string.Join("\n", nuevos.Take(5).Select(n => $"{n.Titulo} — Ep {n.Episodio}"));
        if (nuevos.Count > 5) resumen += $"\n+{nuevos.Count - 5} {LocalizationService.T("Notif_ResumenNuevos")}";

        WeakReferenceMessenger.Default.Send(new NuevosEpisodiosMensaje(nuevos.Count, resumen));
        return nuevos.Count;
    }

    /// <summary>
    /// FUN-008: monitoreo periódico de episodios nuevos. Primer chequeo a los 3 segundos
    /// (tras cargar la biblioteca) y luego cada <paramref name="periodicidad"/> mientras la
    /// app siga abierta. Antes solo se comprobaba UNA vez al arrancar.
    /// </summary>
    public void IniciarMonitoreoPeriodico(TimeSpan periodicidad)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                await EjecutarPasadaSeguraAsync();

                while (true)
                {
                    await Task.Delay(periodicidad);
                    await EjecutarPasadaSeguraAsync();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("NewEpisodeNotifier", $"Monitoreo periódico terminó: {ex.Message}");
            }
        });
    }

    private async Task EjecutarPasadaSeguraAsync()
    {
        try
        {
            await BuscarYNotificarNuevosAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NewEpisodeNotifier", $"Error en pasada de episodios nuevos: {ex.Message}");
        }
    }

    private HashSet<string> CargarNotificados()
    {
        try
        {
            if (!File.Exists(_notificadosPath)) return new HashSet<string>();
            var json = File.ReadAllText(_notificadosPath);
            var lista = JsonSerializer.Deserialize<List<string>>(json);
            return lista != null ? new HashSet<string>(lista) : new HashSet<string>();
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private void GuardarNotificados(HashSet<string> notificados)
    {
        try
        {
            // UX-02 (NOT-02, decisión tomada): el JSON es solo dedup temporal. Con >5000
            // entradas se descartan las antiguas y podrían re-notificarse si el escaneo
            // vuelve a verlas como "nuevas" — aceptado: la fuente de verdad son los
            // registros de la DB (un episodio registrado nunca se notifica dos veces).
            // Migrar el dedup a la DB eliminaría este límite (pendiente Fase 6).
            var lista = notificados.Count > 5000
                ? notificados.TakeLast(5000).ToList()
                : notificados.ToList();

            var dir = Path.GetDirectoryName(_notificadosPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // NOT-01: escritura atómica — un crash a mitad de escritura no puede dejar el
            // JSON corrupto (que CargarNotificados degradaría a vacío y re-notificaría todo)
            string tmp = _notificadosPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(lista));
            File.Move(tmp, _notificadosPath, overwrite: true);
        }
        catch { }
    }
}
