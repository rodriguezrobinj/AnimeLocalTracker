using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Minijuegos;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Estado y textos comunes a los minijuegos de 4 opciones (inicio → rondas → resumen, puntos, aciertos, biblioteca
/// disponible). Cada juego añade lo suyo: sus pistas, su material (portada, audio…) y sus comandos.
/// Son singleton para que una partida en curso sobreviva al cambiar de pestaña.
/// </summary>
public abstract partial class MinijuegoViewModelBase : ObservableObject, IRecipient<IdiomaCambiadoMensaje>
{
    private readonly IDatabaseService _databaseService;
    private readonly IMinijuegosRecordsService? _records;
    private int _rachaActual;
    private int _rachaMaxima;

    /// <summary>Animes utilizables de la biblioteca (con id y título), recargados al entrar al juego.</summary>
    protected List<AnimeItem> Biblioteca { get; private set; } = new();

    /// <summary>Solo para pruebas: semilla fija = partidas reproducibles.</summary>
    internal Random Rng { get; set; } = Random.Shared;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsInicio))]
    [NotifyPropertyChangedFor(nameof(EsJugando))]
    [NotifyPropertyChangedFor(nameof(EsResumen))]
    [NotifyPropertyChangedFor(nameof(MostrarSinAnimes))]
    [NotifyPropertyChangedFor(nameof(MostrarPresentacion))]
    private EstadoMinijuego _estado = EstadoMinijuego.Inicio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarSinAnimes))]
    [NotifyPropertyChangedFor(nameof(MostrarPresentacion))]
    private bool _estaCargando;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeJugar))]
    [NotifyPropertyChangedFor(nameof(MostrarSinAnimes))]
    [NotifyPropertyChangedFor(nameof(MostrarPresentacion))]
    [NotifyPropertyChangedFor(nameof(SinAnimesTexto))]
    private int _animesDisponibles;

    [ObservableProperty] private ObservableCollection<OpcionRespuesta> _opciones = new();

    [ObservableProperty] private int _rondaNumero;
    [ObservableProperty] private int _totalRondas;
    [ObservableProperty] private int _puntos;
    [ObservableProperty] private int _aciertos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedePedirPista))]
    [NotifyPropertyChangedFor(nameof(MostrarSiguiente))]
    private bool _haRespondido;

    [ObservableProperty] private bool _ultimoFueAcierto;
    [ObservableProperty] private string _resultadoTexto = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayRecord))]
    [NotifyPropertyChangedFor(nameof(RecordTexto))]
    [NotifyPropertyChangedFor(nameof(ResumenRecordTexto))]
    [NotifyPropertyChangedFor(nameof(MostrarRecordEnResumen))]
    private RecordsMinijuego _recordsJuego = RecordsMinijuego.Vacio;

    /// <summary>La partida que acaba de terminar superó el récord anterior.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResumenRecordTexto))]
    [NotifyPropertyChangedFor(nameof(MostrarRecordEnResumen))]
    private bool _esNuevoRecord;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResumenRecordTexto))]
    private int _mejorAnterior;

    public bool EsInicio => Estado == EstadoMinijuego.Inicio;
    public bool EsJugando => Estado == EstadoMinijuego.Jugando;
    public bool EsResumen => Estado == EstadoMinijuego.Resumen;

    public bool PuedeJugar => AnimesDisponibles >= AdivinaAnimeJuego.MinimoAnimes;
    public bool MostrarSinAnimes => EsInicio && !EstaCargando && !PuedeJugar;
    public bool MostrarPresentacion => EsInicio && !EstaCargando && PuedeJugar;

    public abstract bool PuedePedirPista { get; }
    public bool MostrarSiguiente => HaRespondido;

    /// <summary>True si tras la ronda actual queda alguna otra (decide entre "Siguiente" y "Ver resultado").</summary>
    protected abstract bool HayMasRondas { get; }

    public abstract string ReglasTexto { get; }

    /// <summary>Identificador estable del juego en la base de datos (<see cref="JuegosMinijuego"/>).</summary>
    protected abstract string JuegoId { get; }

    public bool HayRecord => RecordsJuego.HayPartidas;
    public bool MostrarRecordEnResumen => EsNuevoRecord || HayRecord;

    /// <summary>"Tu récord: 760 pts · Partidas jugadas: 12 · Aciertos: 68 %" (vacío si aún no hay partidas).</summary>
    public string RecordTexto => HayRecord
        ? string.Format(LocalizationService.T("Mini_RecordFormato"), RecordsJuego.MejorPuntuacion, RecordsJuego.Partidas, RecordsJuego.PrecisionPorcentaje)
        : string.Empty;

    /// <summary>Línea de récord del resumen: cuánto se superó, o cuál es el récord actual.</summary>
    public string ResumenRecordTexto =>
        EsNuevoRecord ? string.Format(LocalizationService.T("Mini_RecordAnteriorFormato"), MejorAnterior)
        : HayRecord ? string.Format(LocalizationService.T("Mini_TuRecordFormato"), RecordsJuego.MejorPuntuacion)
        : string.Empty;

    /// <summary>Atajos de teclado del juego, como texto de ayuda bajo las opciones.</summary>
    public abstract string AtajosTexto { get; }

    public string TextoSiguiente => LocalizationService.T(HayMasRondas ? "Mini_Siguiente" : "Mini_VerResultado");
    public string RondaTexto => string.Format(LocalizationService.T("Mini_RondaFormato"), RondaNumero, TotalRondas);
    public string PuntosTexto => string.Format(LocalizationService.T("Mini_PuntosFormato"), Puntos);
    public string SinAnimesTexto => string.Format(LocalizationService.T("Mini_NoHayAnimesMsjFormato"), AdivinaAnimeJuego.MinimoAnimes, AnimesDisponibles);
    public string ResumenAciertosTexto => string.Format(
        LocalizationService.T(Aciertos == 1 ? "Mini_ResumenAciertoUnoFormato" : "Mini_ResumenAciertosFormato"), Aciertos, RondaNumero);
    public string ResumenPuntosTexto => string.Format(LocalizationService.T("Mini_ResumenPuntosFormato"), Puntos);

    protected MinijuegoViewModelBase(IDatabaseService databaseService, IMinijuegosRecordsService? records = null)
    {
        _databaseService = databaseService;
        _records = records;
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    partial void OnRondaNumeroChanged(int value) { OnPropertyChanged(nameof(RondaTexto)); OnPropertyChanged(nameof(ResumenAciertosTexto)); }
    partial void OnTotalRondasChanged(int value) => OnPropertyChanged(nameof(RondaTexto));
    partial void OnPuntosChanged(int value) { OnPropertyChanged(nameof(PuntosTexto)); OnPropertyChanged(nameof(ResumenPuntosTexto)); }
    partial void OnAciertosChanged(int value) => OnPropertyChanged(nameof(ResumenAciertosTexto));

    /// <summary>Al entrar al juego: recuenta los animes disponibles. No toca una partida en curso.</summary>
    public virtual async Task PrepararAsync()
    {
        await RefrescarRecordsAsync();
        if (EsJugando || EstaCargando) return;

        try
        {
            EstaCargando = true;
            var todos = await _databaseService.ObtenerTodosLosAnimesAsync() ?? new List<AnimeItem>();
            Biblioteca = todos.Where(AdivinaAnimeJuego.EsUtilizable).ToList();
            AnimesDisponibles = Biblioteca.Count;
        }
        catch (Exception ex)
        {
            AppLogger.Error(GetType().Name, "Error al cargar la biblioteca para el minijuego", ex);
        }
        finally
        {
            EstaCargando = false;
        }
    }

    /// <summary>Vuelve a leer los récords guardados (al entrar al menú o al juego). Sin servicio o si falla, no hay récords.</summary>
    public async Task RefrescarRecordsAsync()
    {
        if (_records == null) return;

        try
        {
            RecordsJuego = await _records.ObtenerAsync(JuegoId);
        }
        catch (Exception ex)
        {
            AppLogger.Error(GetType().Name, "Error al leer los récords del minijuego", ex);
        }
    }

    /// <summary>Pone a cero puntos, aciertos, racha y ronda para una partida nueva.</summary>
    protected void ReiniciarPartida()
    {
        Puntos = 0;
        Aciertos = 0;
        RondaNumero = 0;
        _rachaActual = 0;
        _rachaMaxima = 0;
        EsNuevoRecord = false;
        MejorAnterior = 0;
    }

    /// <summary>Lleva la cuenta de aciertos seguidos (la mejor racha de la partida alimenta un logro).</summary>
    protected void ContabilizarRonda(bool acierto)
    {
        _rachaActual = acierto ? _rachaActual + 1 : 0;
        _rachaMaxima = Math.Max(_rachaMaxima, _rachaActual);
    }

    /// <summary>
    /// Pasa al resumen y guarda la partida (récords y logros). Se muestra el resumen ya, antes de guardar: si guardar falla,
    /// el jugador ve igualmente su resultado. Una partida sin rondas jugadas no se guarda.
    /// </summary>
    protected async Task TerminarPartidaAsync()
    {
        Estado = EstadoMinijuego.Resumen;
        if (_records == null || RondaNumero == 0) return;

        try
        {
            var resultado = await _records.RegistrarAsync(new PartidaMinijuego
            {
                JuegoId = JuegoId,
                FechaUtc = DateTime.UtcNow,
                Puntos = Puntos,
                Rondas = RondaNumero,
                Aciertos = Aciertos,
                RachaMaxima = _rachaMaxima
            });

            MejorAnterior = resultado.MejorAnterior;
            EsNuevoRecord = resultado.EsNuevoRecord;
            RecordsJuego = resultado.Records;
        }
        catch (Exception ex)
        {
            AppLogger.Error(GetType().Name, "Error al guardar la partida del minijuego", ex);
        }
    }

    /// <summary>Corta lo que esté sonando o descargándose (al salir de la pestaña o del juego). Sin efecto por defecto.</summary>
    public virtual void Detener() { }

    protected static ObservableCollection<OpcionRespuesta> CrearOpciones(IEnumerable<AnimeItem> animes) =>
        new(animes.Select((a, i) => new OpcionRespuesta(i, a.Titulo)));

    public void Receive(IdiomaCambiadoMensaje message)
    {
        // Pistas, resultado y etiquetas se construyen con LocalizationService.T(): rehacerlos.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefrescarTextos);
            return;
        }
        RefrescarTextos();
    }

    protected virtual void RefrescarTextos()
    {
        foreach (var opcion in Opciones) opcion.RefrescarTextos();
        OnPropertyChanged(nameof(TextoSiguiente));
        OnPropertyChanged(nameof(RondaTexto));
        OnPropertyChanged(nameof(PuntosTexto));
        OnPropertyChanged(nameof(ReglasTexto));
        OnPropertyChanged(nameof(AtajosTexto));
        OnPropertyChanged(nameof(SinAnimesTexto));
        OnPropertyChanged(nameof(ResumenAciertosTexto));
        OnPropertyChanged(nameof(ResumenPuntosTexto));
        OnPropertyChanged(nameof(RecordTexto));
        OnPropertyChanged(nameof(ResumenRecordTexto));
    }
}
