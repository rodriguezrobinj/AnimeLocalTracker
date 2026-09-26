using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Logros;

namespace AnimeLocalTracker.Services.Minijuegos;

/// <summary>Lo que pasó al registrar una partida: si batió el récord, cuál era antes y cómo quedan los totales.</summary>
public sealed record ResultadoPartida(bool EsNuevoRecord, int MejorAnterior, RecordsMinijuego Records);

public interface IMinijuegosRecordsService
{
    Task<RecordsMinijuego> ObtenerAsync(string juegoId);

    /// <summary>Guarda la partida terminada, dice si es récord y evalúa los logros (avisando de los nuevos).</summary>
    Task<ResultadoPartida> RegistrarAsync(PartidaMinijuego partida);
}

public sealed class MinijuegosRecordsService : IMinijuegosRecordsService
{
    private readonly IDatabaseService _databaseService;
    private readonly ILogrosService? _logrosService;

    public MinijuegosRecordsService(IDatabaseService databaseService, ILogrosService? logrosService = null)
    {
        _databaseService = databaseService;
        _logrosService = logrosService;
    }

    public async Task<RecordsMinijuego> ObtenerAsync(string juegoId)
    {
        var partidas = await _databaseService.ObtenerPartidasMinijuegoAsync() ?? new List<PartidaMinijuego>();
        return RecordsMinijuego.Calcular(partidas.Where(p => p.JuegoId == juegoId));
    }

    public async Task<ResultadoPartida> RegistrarAsync(PartidaMinijuego partida)
    {
        var previas = (await _databaseService.ObtenerPartidasMinijuegoAsync() ?? new List<PartidaMinijuego>())
            .Where(p => p.JuegoId == partida.JuegoId)
            .ToList();
        var recordsPrevios = RecordsMinijuego.Calcular(previas);

        await _databaseService.GuardarPartidaMinijuegoAsync(partida);

        // Récord = superar una marca anterior. En la primera partida no hay nada que superar (sería un "récord" trivial),
        // y una partida sin puntos nunca lo es.
        bool esRecord = recordsPrevios.HayPartidas && partida.Puntos > recordsPrevios.MejorPuntuacion;

        await EvaluarLogrosAsync();

        return new ResultadoPartida(esRecord, recordsPrevios.MejorPuntuacion, RecordsMinijuego.Calcular(previas.Append(partida)));
    }

    /// <summary>Los logros son un extra: si su evaluación falla, la partida ya está guardada y no se pierde nada.</summary>
    private async Task EvaluarLogrosAsync()
    {
        if (_logrosService == null) return;

        try
        {
            await _logrosService.EvaluarAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("MinijuegosRecordsService", "No se pudieron evaluar los logros tras la partida", ex);
        }
    }
}
