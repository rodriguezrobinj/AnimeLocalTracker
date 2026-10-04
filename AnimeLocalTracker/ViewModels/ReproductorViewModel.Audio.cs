using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaPlayer;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Logros;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Audio del reproductor: Modo Noche y ecualizador de 10 bandas (filtros de FFmpeg sobre el Player).</summary>
public partial class ReproductorViewModel
{
    private bool _modoNocheActivo = false;
    /// <summary>
    /// "Modo noche": compresor de rango dinámico (FFmpeg acompressor) sobre el audio para que
    /// el diálogo se escuche parejo sin que las escenas/openings fuertes suenen a todo volumen.
    /// Se persiste como preferencia por defecto (AppSettings.ModoNocheActivo).
    /// </summary>
    public bool ModoNocheActivo
    {
        get => _modoNocheActivo;
        set
        {
            if (SetProperty(ref _modoNocheActivo, value))
            {
                AplicarModoNocheAlPlayer(value);
                GuardarModoNochePreferencia(value);
            }
        }
    }

    private const string ArgumentosModoNoche = "threshold=0.089:ratio=9:attack=200:release=1000:makeup=2";

    private void AplicarModoNocheAlPlayer(bool activo) => AplicarFiltrosAudio();

    /// <summary>Cadena de filtros de audio actual (ecualizador y Modo Noche) en el formato de Flyleaf.</summary>
    private List<Filter> ConstruirFiltrosAudio() =>
        Core.EcualizadorAudio.ConstruirFiltros(EcualizadorActivo, BandasEcualizador.Select(b => b.Ganancia).ToList(), ModoNocheActivo, ArgumentosModoNoche)
            .Select(f => new Filter { Id = f.Id, Name = f.Nombre, Args = f.Argumentos })
            .ToList();

    /// <summary>Reconstruye la cadena de filtros (al encender/apagar el ecualizador o el Modo Noche; un corte de milisegundos).</summary>
    private void AplicarFiltrosAudio()
    {
        if (Player?.Config?.Audio == null) return;
        try
        {
            Player.Config.Audio.Filters = ConstruirFiltrosAudio();
            int resultado = Player.Config.Audio.ReloadFilters();
            if (resultado < 0) AppLogger.Warn("ReproductorViewModel", $"Flyleaf no pudo montar los filtros de audio ({resultado}).");
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error aplicando los filtros de audio: {ex.Message}");
        }
    }

    // ── Ecualizador ──

    public sealed partial class BandaEcualizador : ObservableObject
    {
        public BandaEcualizador(int indice, int frecuencia, double ganancia)
        {
            Indice = indice;
            Etiqueta = Core.EcualizadorAudio.EtiquetaFrecuencia(frecuencia);
            _ganancia = ganancia;
        }

        public int Indice { get; }
        public string Etiqueta { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TextoGanancia))]
        private double _ganancia;

        public string TextoGanancia => Ganancia > 0 ? $"+{Ganancia:0}" : $"{Ganancia:0}";
    }

    /// <summary>Un ajuste predefinido tal como se ve en el menú (EsActual resalta el que está puesto).</summary>
    public sealed partial class OpcionPresetEcualizador : ObservableObject
    {
        public OpcionPresetEcualizador(string clave, string nombre)
        {
            Clave = clave;
            Nombre = nombre;
        }

        public string Clave { get; }
        public string Nombre { get; }

        [ObservableProperty] private bool _esActual;
    }

    public System.Collections.ObjectModel.ObservableCollection<BandaEcualizador> BandasEcualizador { get; } = new();

    public IReadOnlyList<OpcionPresetEcualizador> PresetsEcualizador { get; private set; } = Array.Empty<OpcionPresetEcualizador>();

    [ObservableProperty] private bool _ecualizadorActivo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NombrePresetEcualizador))]
    private string _presetEcualizador = Core.EcualizadorAudio.PresetPlano;

    /// <summary>"Voces claras", "Personalizado"…</summary>
    public string NombrePresetEcualizador => LocalizationService.T("Eq_Preset_" + PresetEcualizador);

    partial void OnPresetEcualizadorChanged(string value)
    {
        foreach (var opcion in PresetsEcualizador) opcion.EsActual = opcion.Clave == value;
    }

    private bool _aplicandoPreset;
    private CancellationTokenSource? _guardadoEcualizadorCts;

    private void CargarEcualizador(AppSettings? config)
    {
        var ganancias = Core.EcualizadorAudio.Normalizar(config?.EcualizadorGanancias);
        foreach (var banda in BandasEcualizador) banda.PropertyChanged -= Banda_PropertyChanged;
        BandasEcualizador.Clear();
        for (int i = 0; i < ganancias.Length; i++)
        {
            var banda = new BandaEcualizador(i, Core.EcualizadorAudio.Frecuencias[i], ganancias[i]);
            banda.PropertyChanged += Banda_PropertyChanged;
            BandasEcualizador.Add(banda);
        }
        string preset = Core.EcualizadorAudio.PresetDe(ganancias);
        PresetsEcualizador = Core.EcualizadorAudio.Presets
            .Select(p => new OpcionPresetEcualizador(p.Clave, LocalizationService.T("Eq_Preset_" + p.Clave)) { EsActual = p.Clave == preset })
            .ToList();

        _cargandoEcualizador = true; // lo que se lee de los ajustes no se vuelve a guardar
        try
        {
            EcualizadorActivo = config?.EcualizadorActivo ?? false;
            PresetEcualizador = preset;
        }
        finally
        {
            _cargandoEcualizador = false;
        }
    }

    private bool _cargandoEcualizador;

    partial void OnEcualizadorActivoChanged(bool value)
    {
        if (_cargandoEcualizador) return; // aún no hay Player: CreateOptimizedPlayer ya monta los filtros guardados
        AplicarFiltrosAudio();
        GuardarEcualizadorEnDiferido();
    }

    /// <summary>Mover una banda: solo se le manda la ganancia nueva a su filtro (sin reconstruir la cadena, sin cortes).</summary>
    private void Banda_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BandaEcualizador.Ganancia) || sender is not BandaEcualizador banda) return;

        if (!_aplicandoPreset)
        {
            PresetEcualizador = Core.EcualizadorAudio.PresetDe(BandasEcualizador.Select(b => b.Ganancia).ToList());
            if (!EcualizadorActivo) EcualizadorActivo = true; // mover una banda con el ecualizador apagado lo enciende (ya monta la cadena y guarda)
            else
            {
                ActualizarFiltroBanda(banda);
                GuardarEcualizadorEnDiferido();
            }
        }
    }

    private void ActualizarFiltroBanda(BandaEcualizador banda)
    {
        if (!EcualizadorActivo || Player?.Config?.Audio == null) return;
        try
        {
            var audio = Player.Config.Audio;
            int r1 = audio.UpdateFilter(Core.EcualizadorAudio.IdBanda(banda.Indice), "g", Core.EcualizadorAudio.TextoGanancia(banda.Ganancia));
            double pre = Core.EcualizadorAudio.Preamplificacion(BandasEcualizador.Select(b => b.Ganancia).ToList());
            int r2 = audio.UpdateFilter(Core.EcualizadorAudio.IdPreamplificador, "volume", Core.EcualizadorAudio.TextoVolumen(pre));
            if (r1 < 0 || r2 < 0) AplicarFiltrosAudio(); // si el filtro no admite el cambio en vivo, se monta la cadena de nuevo
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"Error cambiando una banda del ecualizador: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AplicarPresetEcualizador(string? clave)
    {
        var preset = Core.EcualizadorAudio.Presets.FirstOrDefault(p => p.Clave == clave);
        if (preset == null) return;

        _aplicandoPreset = true;
        try
        {
            for (int i = 0; i < BandasEcualizador.Count; i++) BandasEcualizador[i].Ganancia = preset.Ganancias[i];
        }
        finally
        {
            _aplicandoPreset = false;
        }
        PresetEcualizador = preset.Clave;
        if (!EcualizadorActivo) EcualizadorActivo = true; // elegir un ajuste enciende el ecualizador (ya reconstruye la cadena)
        else AplicarFiltrosAudio();
        GuardarEcualizadorEnDiferido();
    }

    [RelayCommand]
    private void RestablecerEcualizador() => AplicarPresetEcualizador(Core.EcualizadorAudio.PresetPlano);

    /// <summary>Se guarda en los ajustes medio segundo después del último cambio (arrastrar un deslizador genera decenas).</summary>
    private void GuardarEcualizadorEnDiferido()
    {
        if (_settingsService == null) return;
        _guardadoEcualizadorCts?.Cancel();
        var cts = new CancellationTokenSource();
        _guardadoEcualizadorCts = cts;
        var ganancias = BandasEcualizador.Select(b => b.Ganancia).ToList();
        bool activo = EcualizadorActivo;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                await _settingsService.ActualizarAsync(c =>
                {
                    c.EcualizadorActivo = activo;
                    c.EcualizadorGanancias = ganancias;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.Debug("ReproductorViewModel", $"No se pudo guardar el ecualizador: {ex.Message}"); }
        });
    }

    private void GuardarModoNochePreferencia(bool activo)
    {
        if (_settingsService == null) return;
        var config = _settingsService.ObtenerConfiguracion();
        if (config == null || config.ModoNocheActivo == activo) return;
        config.ModoNocheActivo = activo;
        _ = _settingsService.GuardarConfiguracionAsync(config);
    }

    [RelayCommand]
    private void ToggleModoNoche() => ModoNocheActivo = !ModoNocheActivo;
}
