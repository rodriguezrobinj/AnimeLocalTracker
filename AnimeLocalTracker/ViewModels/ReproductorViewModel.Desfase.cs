using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Desfase de subtítulos y audio (teclas Z/X y J/K): el usuario corrige un episodio con los subtítulos o la voz a destiempo, lo ve en
/// un aviso y queda recordado para ese capítulo (<see cref="Models.DesfaseEpisodio"/>).
/// </summary>
public partial class ReproductorViewModel
{
    public const int PasoDesfaseMs = 50;
    public const int DesfaseMaximoMs = 10_000;

    private int _desfaseSubtitulosMs;
    private int _desfaseAudioMs;
    private CancellationTokenSource? _guardadoDesfaseCts;
    private (int AnimeId, int Episodio) _destinoDesfasePendiente;

    /// <summary>Lectura del desfase guardado del capítulo que se abrió. Las pruebas la esperan.</summary>
    internal Task TareaDesfase { get; private set; } = Task.CompletedTask;

    [RelayCommand] private void RetrasarSubtitulos() => CambiarDesfase(subtitulos: true, PasoDesfaseMs);
    [RelayCommand] private void AdelantarSubtitulos() => CambiarDesfase(subtitulos: true, -PasoDesfaseMs);
    [RelayCommand] private void RetrasarAudio() => CambiarDesfase(subtitulos: false, PasoDesfaseMs);
    [RelayCommand] private void AdelantarAudio() => CambiarDesfase(subtitulos: false, -PasoDesfaseMs);

    private void CambiarDesfase(bool subtitulos, int pasoMs)
    {
        int valor;
        if (subtitulos) valor = _desfaseSubtitulosMs = Math.Clamp(_desfaseSubtitulosMs + pasoMs, -DesfaseMaximoMs, DesfaseMaximoMs);
        else valor = _desfaseAudioMs = Math.Clamp(_desfaseAudioMs + pasoMs, -DesfaseMaximoMs, DesfaseMaximoMs);
        AplicarDesfaseAlMotor();

        string formato = LocalizationService.T(subtitulos ? "Player_DesfaseSubtitulosFormato" : "Player_DesfaseAudioFormato");
        AvisarEnReproductor(LocalizationService.T("Player_DesfaseTitulo"),
            string.Format(formato, valor.ToString("+0;−0;0", LocalizationService.Cultura)), "TimerOutline", "#3F51B5");
        GuardarDesfaseEnDiferido();
    }

    private void AplicarDesfaseAlMotor()
    {
        var motor = Motor;
        if (motor == null) return;
        motor.DesfaseSubtitulosMs = _desfaseSubtitulosMs;
        motor.DesfaseAudioMs = _desfaseAudioMs;
    }

    /// <summary>
    /// Al abrir un capítulo: el motor conserva el desfase del archivo anterior, así que se vuelve a cero y luego se aplica lo guardado
    /// de este (si lo hay).
    /// </summary>
    private void CargarDesfaseDelEpisodio(int animeId, int episodio, int version)
    {
        _desfaseSubtitulosMs = _desfaseAudioMs = 0;
        AplicarDesfaseAlMotor();
        if (animeId <= 0) return;
        TareaDesfase = CargarDesfaseGuardadoAsync(animeId, episodio, version);
    }

    private async Task CargarDesfaseGuardadoAsync(int animeId, int episodio, int version)
    {
        try
        {
            var guardado = await _databaseService.ObtenerDesfaseEpisodioAsync(animeId, episodio);
            if (guardado == null) return;
            Core.HiloUi.Ejecutar(() =>
            {
                if (version != _versionCarga) return; // ya se abrió otro capítulo
                _desfaseSubtitulosMs = Math.Clamp(guardado.SubtitulosMs, -DesfaseMaximoMs, DesfaseMaximoMs);
                _desfaseAudioMs = Math.Clamp(guardado.AudioMs, -DesfaseMaximoMs, DesfaseMaximoMs);
                AplicarDesfaseAlMotor();
            });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudo leer el desfase guardado: {ex.Message}");
        }
    }

    /// <summary>
    /// Se guarda medio segundo después de la última pulsación (mantener Z pulsada genera decenas). Si en ese medio segundo se pasa a
    /// otro capítulo, el cambio se guarda en el que se hizo.
    /// </summary>
    private void GuardarDesfaseEnDiferido()
    {
        int animeId = _animeId, episodio = _episodio;
        if (animeId <= 0) return; // video fuera de la biblioteca: el ajuste vale mientras se ve, pero no se guarda

        if ((animeId, episodio) == _destinoDesfasePendiente) _guardadoDesfaseCts?.Cancel();
        _destinoDesfasePendiente = (animeId, episodio);
        var cts = new CancellationTokenSource();
        _guardadoDesfaseCts = cts;

        int subtitulos = _desfaseSubtitulosMs, audio = _desfaseAudioMs;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                await _databaseService.GuardarDesfaseEpisodioAsync(animeId, episodio, subtitulos, audio);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.Debug("ReproductorViewModel", $"No se pudo guardar el desfase: {ex.Message}"); }
        });
    }
}
