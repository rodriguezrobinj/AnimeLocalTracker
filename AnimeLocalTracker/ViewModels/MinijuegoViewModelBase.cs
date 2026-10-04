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
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>
    /// La biblioteca ya cargada en memoria (la de la Galería). Sin ella, o si aún está vacía, se lee de la base de datos:
    /// antes cada juego la releía entera (con sinopsis y comprobación de portadas) cada vez que se entraba.
    /// </summary>
    internal Func<IReadOnlyList<AnimeItem>>? FuenteBiblioteca { get; set; }

    /// <summary>
    /// Opción del menú de minijuegos: jugar solo con animes que has visto o estás viendo (los que solo tienes apuntados en
    /// "Planeando" y sin ningún episodio visto quedan fuera, también como opciones falsas).
    /// </summary>
    internal bool SoloVistos { get; set; }

    /// <summary>Las rondas ya jugadas de la partida, para el resumen final.</summary>
    public ObservableCollection<RondaResumen> ResumenRondas { get; } = new();

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

    /// <summary>Pistas ya reveladas de la ronda, listas para mostrar.</summary>
    [ObservableProperty] private ObservableCollection<PistaItem> _pistas = new();

    /// <summary>"Acertar ahora vale 80 pts" (vacío tras responder).</summary>
    [ObservableProperty] private string _puntosPosiblesTexto = string.Empty;

    /// <summary>Solo tras responder: un dato más sobre la respuesta (p. ej. de qué anime es el personaje). Vacío si el juego no lo usa.</summary>
    [ObservableProperty] private string _detalleRespuestaTexto = string.Empty;

    /// <summary>Por qué no se pudo empezar la última partida (sin conexión y sin nada guardado, etc.).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayError))]
    private string _mensajeError = string.Empty;

    public bool HayError => MensajeError.Length > 0;

    /// <summary>Mientras se prepara una partida entera antes de empezar (el juego que lo necesite).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreparandoTexto))]
    private bool _estaPreparando;

    // === Lo que distingue a cada juego en el menú y en su pantalla de inicio ===

    /// <summary>Nombre del icono (PackIconKind) del juego.</summary>
    public abstract string Icono { get; }

    /// <summary>Icono del aviso "no hay animes suficientes".</summary>
    public virtual string IconoSinAnimes => "GamepadVariantOutline";

    protected abstract string ClaveTitulo { get; }
    protected abstract string ClaveDescripcion { get; }

    public string TituloTexto => LocalizationService.T(ClaveTitulo);
    public string DescripcionTexto => LocalizationService.T(ClaveDescripcion);

    /// <summary>Texto bajo el indicador de carga mientras se prepara la partida (vacío si el juego no prepara nada).</summary>
    public virtual string PreparandoTexto => string.Empty;

    // === La ronda en curso, vista desde la base ===

    /// <summary>Posición de la opción correcta de la ronda actual (-1 si no hay ronda).</summary>
    protected abstract int IndiceCorrectoRonda { get; }

    /// <summary>Nombre de la respuesta correcta de la ronda actual (para el texto del resultado y el resumen).</summary>
    protected abstract string RespuestaRonda { get; }

    /// <summary>Puntos que daría acertar ahora mismo, con las pistas ya pedidas.</summary>
    protected abstract int PuntosSiAcierta { get; }

    protected virtual bool PuedeResponder => EsJugando && !HaRespondido && IndiceCorrectoRonda >= 0;

    /// <summary>Puntos que dio la última ronda respondida (0 si se falló).</summary>
    protected int PuntosGanadosRonda { get; private set; }

    /// <summary>Lo propio de cada juego al responder (aclarar la imagen, hacer sonar la canción…).</summary>
    protected virtual void AlResponder() { }

    /// <summary>Muestra la ronda siguiente o, si no quedan, termina la partida.</summary>
    protected abstract Task AvanzarRondaAsync();

    /// <summary>Lo propio de cada juego al volver al inicio (cancelar lo que se estuviera preparando, cortar el sonido).</summary>
    protected virtual void AlVolverAlInicio() { }

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
            var enMemoria = FuenteBiblioteca?.Invoke();
            var todos = enMemoria is { Count: > 0 }
                ? enMemoria
                : await _databaseService.ObtenerTodosLosAnimesAsync() ?? new List<AnimeItem>();
            Biblioteca = todos.Where(AdivinaAnimeJuego.EsUtilizable).Where(a => !SoloVistos || LoHasVisto(a)).ToList();
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
        ResumenRondas.Clear();
    }

    /// <summary>
    /// Anota la ronda recién respondida: la racha de aciertos seguidos (la mejor de la partida alimenta un logro) y la línea
    /// del resumen final (cuál era la respuesta, si se acertó y con cuántos puntos).
    /// </summary>
    protected void ContabilizarRonda(bool acierto, string respuesta, int puntos)
    {
        _rachaActual = acierto ? _rachaActual + 1 : 0;
        _rachaMaxima = Math.Max(_rachaMaxima, _rachaActual);
        ResumenRondas.Add(new RondaResumen(RondaNumero, respuesta, acierto, puntos));
    }

    [RelayCommand]
    private void Responder(OpcionRespuesta? opcion)
    {
        if (opcion == null || !PuedeResponder) return;

        int correcta = IndiceCorrectoRonda;
        HaRespondido = true;
        UltimoFueAcierto = opcion.Indice == correcta;
        Opciones[correcta].EsCorrecta = true;

        if (UltimoFueAcierto)
        {
            PuntosGanadosRonda = PuntosSiAcierta;
            Puntos += PuntosGanadosRonda;
            Aciertos++;
        }
        else
        {
            PuntosGanadosRonda = 0;
            opcion.EsIncorrecta = true;
        }
        ContabilizarRonda(UltimoFueAcierto, RespuestaRonda, PuntosGanadosRonda);

        PuntosPosiblesTexto = string.Empty;
        AlResponder();
        ActualizarResultadoTexto();
        OnPropertyChanged(nameof(TextoSiguiente));
    }

    /// <summary>Atajo de teclado 1–4: responde con la opción de ese número.</summary>
    [RelayCommand]
    private void ResponderNumero(string? numero)
    {
        if (!int.TryParse(numero, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int n)) return;
        Responder(Opciones.FirstOrDefault(o => o.Numero == n));
    }

    [RelayCommand]
    private async Task SiguienteAsync()
    {
        if (!EsJugando || !HaRespondido) return;
        await AvanzarRondaAsync();
    }

    /// <summary>Vuelve a la pantalla de inicio del juego. A mitad de partida es "abandonar": la partida no se guarda.</summary>
    [RelayCommand]
    private async Task VolverAlInicioAsync()
    {
        AlVolverAlInicio();
        Estado = EstadoMinijuego.Inicio;
        await PrepararAsync();
    }

    /// <summary>"¡Correcto! +80 pts" o "Era: …", en el idioma actual. Sin efecto si la ronda aún no se respondió.</summary>
    protected virtual void ActualizarResultadoTexto()
    {
        if (!HaRespondido || IndiceCorrectoRonda < 0) return;

        ResultadoTexto = UltimoFueAcierto
            ? string.Format(LocalizationService.T("Mini_CorrectoFormato"), PuntosGanadosRonda)
            : string.Format(LocalizationService.T("Mini_IncorrectoFormato"), RespuestaRonda);
    }

    /// <summary>Visto = con algún episodio visto o en cualquier estado que no sea "Planeando".</summary>
    private static bool LoHasVisto(AnimeItem anime) =>
        anime.EpisodiosVistos > 0 || anime.EstadoUsuario is "CURRENT" or "REPEATING" or "COMPLETED" or "PAUSED" or "DROPPED";

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

    // Pistas, resultado y etiquetas se construyen con LocalizationService.T(): rehacerlos.
    public void Receive(IdiomaCambiadoMensaje message) => Core.HiloUi.Ejecutar(RefrescarTextos);

    protected virtual void RefrescarTextos()
    {
        foreach (var opcion in Opciones) opcion.RefrescarTextos();
        OnPropertyChanged(nameof(TextoSiguiente));
        OnPropertyChanged(nameof(TituloTexto));
        OnPropertyChanged(nameof(DescripcionTexto));
        OnPropertyChanged(nameof(PreparandoTexto));
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
