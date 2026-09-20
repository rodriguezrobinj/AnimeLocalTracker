using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Logros;

public interface ILogrosService
{
    /// <summary>Lee la biblioteca de la BD, evalúa los logros y (si <paramref name="notificar"/>) avisa de los nuevos.</summary>
    Task<ResumenLogros> EvaluarAsync(bool notificar = true);

    /// <summary>Igual, reutilizando datos que el llamador ya cargó (p. ej. Estadísticas).</summary>
    Task<ResumenLogros> EvaluarAsync(IReadOnlyList<AnimeItem> animes, IReadOnlyList<RegistroEpisodio> registros, bool notificar = true);
}

public sealed class LogrosService : ILogrosService, IDisposable
{
    /// <summary>Fila especial que marca que ya se hizo la primera evaluación (silenciosa).</summary>
    internal const string MarcadorBase = "__base__";

    /// <summary>Más avisos que esto en una sola evaluación se resumen en un mensaje.</summary>
    private const int MaximoNombresEnAviso = 3;

    private readonly IDatabaseService _databaseService;
    private readonly IDialogService _dialogService;

    // Una evaluación a la vez: si Estadísticas y el reproductor evalúan a la vez, la segunda ve
    // ya guardado lo de la primera y no repite el aviso.
    private readonly SemaphoreSlim _candado = new(1, 1);

    public LogrosService(IDatabaseService databaseService, IDialogService dialogService)
    {
        _databaseService = databaseService;
        _dialogService = dialogService;
    }

    public async Task<ResumenLogros> EvaluarAsync(bool notificar = true)
    {
        var animes = await _databaseService.ObtenerTodosLosAnimesAsync() ?? new List<AnimeItem>();
        var registros = await _databaseService.ObtenerTodosLosRegistrosAsync() ?? new List<RegistroEpisodio>();
        return await EvaluarAsync(animes, registros, notificar);
    }

    public async Task<ResumenLogros> EvaluarAsync(
        IReadOnlyList<AnimeItem> animes, IReadOnlyList<RegistroEpisodio> registros, bool notificar = true)
    {
        await _candado.WaitAsync();
        try
        {
            var metricas = await Task.Run(() => MotorLogros.CalcularMetricas(animes, registros));

            var almacenados = await _databaseService.ObtenerLogrosDesbloqueadosAsync() ?? new List<LogroDesbloqueado>();
            bool primeraEvaluacion = !almacenados.Any(l => l.LogroId == MarcadorBase);

            var previos = almacenados
                .Where(l => l.LogroId != MarcadorBase)
                .GroupBy(l => l.LogroId)
                .ToDictionary(
                    g => g.Key,
                    g => (Nivel: g.Max(l => l.Nivel), FechaUtc: g.OrderByDescending(l => l.Nivel).First().FechaUtc));

            var resumen = MotorLogros.Evaluar(metricas, previos);

            var yaGuardados = new HashSet<(string, int)>(almacenados.Select(l => (l.LogroId, l.Nivel)));
            var ahora = DateTime.UtcNow;
            var nuevos = new List<LogroDesbloqueado>();
            foreach (var logro in resumen.Logros)
            {
                for (int nivel = 1; nivel <= logro.NivelActual; nivel++)
                {
                    if (yaGuardados.Contains((logro.Id, nivel))) continue;

                    // Primera evaluación: lo que ya cumplías se guarda SIN fecha (no se conoce la
                    // real) y sin avisos, para no llenar la pantalla de toasts de golpe.
                    nuevos.Add(new LogroDesbloqueado
                    {
                        LogroId = logro.Id,
                        Nivel = nivel,
                        FechaUtc = primeraEvaluacion ? null : ahora
                    });
                }
            }

            var porGuardar = new List<LogroDesbloqueado>(nuevos);
            if (primeraEvaluacion)
            {
                porGuardar.Add(new LogroDesbloqueado { LogroId = MarcadorBase, Nivel = 0, FechaUtc = ahora });
            }

            if (porGuardar.Count > 0)
            {
                await _databaseService.GuardarLogrosDesbloqueadosAsync(porGuardar);
            }

            if (notificar && !primeraEvaluacion && nuevos.Count > 0)
            {
                Notificar(resumen, nuevos);
            }

            return resumen;
        }
        finally
        {
            _candado.Release();
        }
    }

    private void Notificar(ResumenLogros resumen, List<LogroDesbloqueado> nuevos)
    {
        string mensaje;
        string titulo;

        if (nuevos.Count <= MaximoNombresEnAviso)
        {
            var porId = resumen.Logros.ToDictionary(l => l.Id);
            mensaje = string.Join("\n", nuevos.Select(n =>
                string.Format(
                    LocalizationService.T("Logro_Toast_LineaFormato"),
                    LocalizationService.T(porId[n.LogroId].Definicion.ClaveTitulo),
                    LocalizationService.T($"Logro_Nivel_{n.Nivel}"))));
            titulo = LocalizationService.T(nuevos.Count == 1 ? "Logro_Toast_TituloUno" : "Logro_Toast_TituloVarios");
        }
        else
        {
            mensaje = string.Format(LocalizationService.T("Logro_Toast_ResumenFormato"), nuevos.Count);
            titulo = LocalizationService.T("Logro_Toast_TituloVarios");
        }

        // Toast directo por IDialogService (el mismo canal que usan los avisos de descargas).
        _dialogService.MostrarToast(titulo, mensaje, "TrophyAward", "#FBBF24");
    }

    public void Dispose() => _candado.Dispose();
}
