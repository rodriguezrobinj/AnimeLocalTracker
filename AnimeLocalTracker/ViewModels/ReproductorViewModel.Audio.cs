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
    /// <summary>Carga en curso de los ajustes de sonido propios del anime o del capítulo abierto (las pruebas la esperan).</summary>
    internal Task TareaAudio { get; private set; } = Task.CompletedTask;

    private bool _modoNocheActivo = false;
    /// <summary>
    /// "Modo noche": compresor de rango dinámico (FFmpeg acompressor) sobre el audio para que
    /// el diálogo se escuche parejo sin que las escenas/openings fuertes suenen a todo volumen.
    /// Se guarda junto con el volumen y el ecualizador (ver <see cref="GuardarAudioEnDiferido"/>).
    /// </summary>
    public bool ModoNocheActivo
    {
        get => _modoNocheActivo;
        set
        {
            if (SetProperty(ref _modoNocheActivo, value))
            {
                AplicarModoNocheAlPlayer(value);
                GuardarAudioEnDiferido();
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
    /// <param name="videoAbriendose">Los ajustes propios del anime o del capítulo llegan mientras el video se está abriendo: si el
    /// audio aún no existe no hay nada que recargar (no es un fallo) y la apertura ya monta la lista recién puesta.</param>
    private void AplicarFiltrosAudio(bool videoAbriendose = false)
    {
        if (Player?.Config?.Audio == null) return;
        try
        {
            Player.Config.Audio.Filters = ConstruirFiltrosAudio();
            int resultado = Player.Config.Audio.ReloadFilters();
            if (resultado < 0 && !videoAbriendose) AppLogger.Warn("ReproductorViewModel", $"Flyleaf no pudo montar los filtros de audio ({resultado}).");
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

    private void CargarEcualizador(bool activo, IEnumerable<double>? gananciasGuardadas)
    {
        var ganancias = Core.EcualizadorAudio.Normalizar(gananciasGuardadas);
        foreach (var banda in BandasEcualizador) banda.PropertyChanged -= Banda_PropertyChanged;
        BandasEcualizador.Clear();
        for (int i = 0; i < ganancias.Length; i++)
        {
            var banda = new BandaEcualizador(i, Core.EcualizadorAudio.Frecuencias[i], ganancias[i]);
            banda.PropertyChanged += Banda_PropertyChanged;
            BandasEcualizador.Add(banda);
        }
        string preset = Core.EcualizadorAudio.PresetDe(ganancias);
        // La lista de ajustes del menú se crea una vez: al cargar los de otro capítulo solo cambia cuál está resaltado.
        if (PresetsEcualizador.Count == 0)
        {
            PresetsEcualizador = Core.EcualizadorAudio.Presets
                .Select(p => new OpcionPresetEcualizador(p.Clave, LocalizationService.T("Eq_Preset_" + p.Clave)))
                .ToList();
        }
        foreach (var opcion in PresetsEcualizador) opcion.EsActual = opcion.Clave == preset;

        bool yaCargando = _cargandoAudio;
        _cargandoAudio = true; // lo que se lee no se vuelve a guardar
        try
        {
            EcualizadorActivo = activo;
            PresetEcualizador = preset;
        }
        finally
        {
            _cargandoAudio = yaCargando;
        }
    }

    private bool _cargandoAudio;

    partial void OnEcualizadorActivoChanged(bool value)
    {
        if (_cargandoAudio) return; // quien carga monta los filtros al final (o aún no hay Player y los monta CreateOptimizedPlayer)
        AplicarFiltrosAudio();
        GuardarAudioEnDiferido();
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
                GuardarAudioEnDiferido();
            }
        }
    }

    private void ActualizarFiltroBanda(BandaEcualizador banda)
    {
        if (!EcualizadorActivo || Player?.Config?.Audio == null) return;
        try
        {
            var audio = Player.Config.Audio;
            // El cambio en vivo solo toca el filtro que está sonando. La lista de la configuración es la que Flyleaf usa para
            // montar la cadena al abrir el siguiente episodio: sin ponerla al día, ahí volvían las ganancias anteriores.
            audio.Filters = ConstruirFiltrosAudio();
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
        GuardarAudioEnDiferido();
    }

    [RelayCommand]
    private void RestablecerEcualizador() => AplicarPresetEcualizador(Core.EcualizadorAudio.PresetPlano);

    /// <summary>
    /// Volumen, ecualizador y Modo Noche se guardan medio segundo después del último cambio (arrastrar un deslizador genera
    /// decenas). Dónde: en los ajustes de la app, o en lo propio del anime o del capítulo que se está viendo, según
    /// <see cref="AppSettings.AmbitoAjustesAudio"/>. Todo se captura ahora: si en ese medio segundo se pasa a otro capítulo, el
    /// cambio se guarda en el que se hizo.
    /// </summary>
    private void GuardarAudioEnDiferido()
    {
        if (_cargandoAudio || _settingsService == null) return;

        int animeId = _animeId;
        int? episodioDondeGuardar = AmbitoAudio.EpisodioDondeGuardar(_settingsService.ObtenerConfiguracion()?.AmbitoAjustesAudio, animeId, _episodio);
        (int, int?) destino = (animeId, episodioDondeGuardar);
        // Un guardado pendiente para OTRO destino (el capítulo anterior) se deja terminar; solo se sustituye el del mismo.
        if (destino == _destinoGuardadoPendiente) _guardadoEcualizadorCts?.Cancel();
        _destinoGuardadoPendiente = destino;
        var cts = new CancellationTokenSource();
        _guardadoEcualizadorCts = cts;

        var ganancias = BandasEcualizador.Select(b => b.Ganancia).ToList();
        bool activo = EcualizadorActivo;
        bool modoNoche = ModoNocheActivo;
        int volumen = _volumenAGuardar;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, cts.Token);
                if (episodioDondeGuardar is int episodio)
                {
                    await _databaseService.GuardarAjusteAudioAsync(new AjusteAudio
                    {
                        Clave = AjusteAudio.ClaveDe(animeId, episodio), AniListId = animeId, NumeroEpisodio = episodio,
                        Volumen = volumen, EcualizadorActivo = activo, Ganancias = ganancias, ModoNoche = modoNoche,
                    });
                }
                else
                {
                    await _settingsService.ActualizarAsync(c =>
                    {
                        c.VolumenReproductor = volumen;
                        c.EcualizadorActivo = activo;
                        c.EcualizadorGanancias = ganancias;
                        c.ModoNocheActivo = modoNoche;
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLogger.Debug("ReproductorViewModel", $"No se pudieron guardar los ajustes de sonido: {ex.Message}"); }
        });
    }

    // ── Ajustes de sonido propios del anime o del capítulo ──

    /// <summary>El último volumen audible (silenciar o bajar a cero no lo cambian): es el que se guarda.</summary>
    private int _volumenAGuardar = 100;
    private (int AnimeId, int? Episodio) _destinoGuardadoPendiente;
    private bool _yaSeAbrioUnVideo;

    /// <summary>
    /// Al abrir un video (o pasar a otro capítulo): si los ajustes de sonido son propios de cada anime o de cada capítulo, se
    /// buscan los suyos. Va en paralelo con la apertura del video (que arranca en silencio) para no retrasarla.
    /// </summary>
    private void CargarAudioDelAmbito(int animeId, int episodio, int version)
    {
        string? ambito = _settingsService?.ObtenerConfiguracion()?.AmbitoAjustesAudio;
        bool primerVideo = !_yaSeAbrioUnVideo;
        _yaSeAbrioUnVideo = true;

        int[] dondeBuscar = AmbitoAudio.EpisodiosDondeBuscar(ambito, animeId, episodio);
        // Global: vale lo que se cargó al crear el reproductor. Por anime: al pasar de capítulo el anime es el mismo.
        if (dondeBuscar.Length == 0 || (!primerVideo && AmbitoAudio.Normalizar(ambito) == AmbitoAudio.PorAnime)) return;

        TareaAudio = CargarAudioGuardadoAsync(animeId, dondeBuscar, version);
    }

    private async Task CargarAudioGuardadoAsync(int animeId, int[] dondeBuscar, int version)
    {
        try
        {
            AjusteAudio? guardado = null;
            foreach (int episodio in dondeBuscar)
            {
                guardado = await _databaseService.ObtenerAjusteAudioAsync(animeId, episodio);
                if (guardado != null) break;
            }

            // Sin nada propio todavía, empieza con los ajustes globales (y solo se separa cuando se cambie algo aquí).
            var global = _settingsService?.ObtenerConfiguracion();
            int volumen = guardado?.Volumen ?? global?.VolumenReproductor ?? 100;
            bool ecualizador = guardado?.EcualizadorActivo ?? global?.EcualizadorActivo ?? false;
            IEnumerable<double>? ganancias = guardado != null ? guardado.Ganancias : global?.EcualizadorGanancias;
            bool modoNoche = guardado?.ModoNoche ?? global?.ModoNocheActivo ?? false;

            Core.HiloUi.Ejecutar(() =>
            {
                if (version != _versionCarga) return; // ya se abrió otro capítulo: sus ajustes los trae su propia carga
                AplicarAudioCargado(volumen, ecualizador, ganancias, modoNoche);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudieron leer los ajustes de sonido guardados: {ex.Message}");
        }
    }

    private void AplicarAudioCargado(int volumen, bool ecualizador, IEnumerable<double>? ganancias, bool modoNoche)
    {
        _cargandoAudio = true; // lo que se lee no se vuelve a guardar
        try
        {
            volumen = Math.Clamp(volumen, 0, 100);
            _volumenAGuardar = volumen;
            // Silenciado a propósito: sigue en silencio, y al quitarlo vuelve con el volumen de este capítulo.
            if (IsMuted) _volumenPrevioMute = volumen;
            else Volumen = volumen;

            CargarEcualizador(ecualizador, ganancias);
            if (_modoNocheActivo != modoNoche)
            {
                _modoNocheActivo = modoNoche;
                OnPropertyChanged(nameof(ModoNocheActivo));
            }
        }
        finally
        {
            _cargandoAudio = false;
        }
        AplicarFiltrosAudio(videoAbriendose: true);
    }

    [RelayCommand]
    private void ToggleModoNoche() => ModoNocheActivo = !ModoNocheActivo;
}
