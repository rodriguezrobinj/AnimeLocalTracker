using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Minijuegos;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// "Adivina el OP/ED": suena un trozo de un opening/ending y hay que decir de qué anime de tu biblioteca es.
/// Los clips salen de AnimeThemes.moe (audio .ogg → mp3 con el ffmpeg de la app) y se guardan en la misma carpeta que
/// las descargas de la ficha; una vez guardados se juega sin conexión. Cada ronda se prepara en segundo plano mientras
/// se juega la anterior, y si AnimeThemes no responde se usan solo los temas ya descargados.
/// La lógica pura vive en <see cref="AdivinaOpEdJuego"/>.
/// </summary>
public sealed partial class AdivinaOpEdViewModel : MinijuegoViewModelBase, IDisposable
{
    /// <summary>Tiempo máximo esperando la lista de temas de un anime (AnimeThemes ya estuvo caída: 20 s por intento). Ajustable solo en pruebas.</summary>
    internal TimeSpan TiempoMaximoApi { get; set; } = TimeSpan.FromSeconds(12);

    /// <summary>Tiempo máximo descargando y convirtiendo un clip. Ajustable solo en pruebas.</summary>
    internal TimeSpan TiempoMaximoDescarga { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Tras tantos animes seguidos sin poder armar ronda se da la partida por imposible.</summary>
    internal const int LimiteFallosSeguidos = 8;

    /// <summary>Tras tantos tiempos agotados seguidos se asume que AnimeThemes no responde y se sigue solo con lo descargado.</summary>
    private const int LimiteTimeoutsSeguidos = 2;

    private readonly IAnimeThemesService _themes;
    private readonly IAnimeThemesDownloadService _descargas;
    private readonly IClipPlayer _player;

    private CancellationTokenSource _cts = new();
    private Queue<AnimeItem> _candidatas = new();
    private Dictionary<int, List<TemaLocalDisponible>> _locales = new();
    private Task<RondaOpEd?>? _siguiente;
    private RondaOpEd? _ronda;
    private int _pasosExtra;
    private int _rondasPedidas;
    private int _puntosGanadosRonda;
    private int _fallosSeguidos;
    private int _timeoutsSeguidos;
    private bool _soloLocal;
    private bool _avanzando;

    /// <summary>Solo para pruebas: posición de la opción correcta de la ronda actual (-1 si no hay ronda).</summary>
    internal int IndiceCorrecto => _ronda?.IndiceCorrecto ?? -1;

    /// <summary>Solo para pruebas: true si tras un tiempo agotado se pasó a jugar solo con lo descargado.</summary>
    internal bool SoloLocal => _soloLocal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarContenidoRonda))]
    [NotifyPropertyChangedFor(nameof(PuedePedirPista))]
    private bool _estaPreparandoRonda;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayError))]
    private string _mensajeError = string.Empty;

    [ObservableProperty] private ObservableCollection<PistaItem> _pistas = new();
    [ObservableProperty] private string _tipoTemaTexto = string.Empty;
    [ObservableProperty] private int _segundosClip = AdivinaOpEdJuego.SegundosIniciales;
    [ObservableProperty] private string _puntosPosiblesTexto = string.Empty;
    [ObservableProperty] private bool _estaSonando;

    /// <summary>Solo tras responder: portada del anime y datos del tema.</summary>
    [ObservableProperty] private string _portadaRespuesta = string.Empty;
    [ObservableProperty] private string _temaReveladoTexto = string.Empty;

    public bool HayError => MensajeError.Length > 0;
    /// <summary>Dentro del panel de partida: la ronda cuando está lista; mientras no, el indicador de "preparando".</summary>
    public bool MostrarContenidoRonda => !EstaPreparandoRonda;
    public string SegundosClipTexto => string.Format(LocalizationService.T("Mini_OpEd_ClipFormato"), SegundosClip);

    public override bool PuedePedirPista => EsJugando && !HaRespondido && !EstaPreparandoRonda && _ronda != null && _pasosExtra < _ronda.Pasos.Count;
    protected override bool HayMasRondas => _siguiente != null;
    public override string ReglasTexto => string.Format(LocalizationService.T("Mini_OpEdReglasFormato"), AdivinaAnimeJuego.RondasPorPartida, AdivinaAnimeJuego.OpcionesPorRonda);

    public override string AtajosTexto => LocalizationService.T("Mini_AtajosOpEd");

    protected override string JuegoId => JuegosMinijuego.AdivinaOpEd;

    public AdivinaOpEdViewModel(IDatabaseService databaseService, IAnimeThemesService themes,
        IAnimeThemesDownloadService descargas, IClipPlayer player, IMinijuegosRecordsService? records = null) : base(databaseService, records)
    {
        _themes = themes;
        _descargas = descargas;
        _player = player;
        _player.ReproduccionTerminada += (_, _) => EstaSonando = false;
    }

    partial void OnSegundosClipChanged(int value) => OnPropertyChanged(nameof(SegundosClipTexto));

    [RelayCommand]
    private async Task IniciarPartidaAsync()
    {
        if (!PuedeJugar || EstaPreparandoRonda || _avanzando) return;

        CancelarTrabajo();
        var ct = _cts.Token;

        MensajeError = string.Empty;
        ReiniciarPartida();
        TotalRondas = Math.Min(AdivinaAnimeJuego.RondasPorPartida, Biblioteca.Count);
        _rondasPedidas = 0;
        _fallosSeguidos = 0;
        _timeoutsSeguidos = 0;
        _soloLocal = false;
        _ronda = null;
        _siguiente = null;
        HaRespondido = false;
        Opciones = new ObservableCollection<OpcionRespuesta>();
        ResultadoTexto = string.Empty;

        _candidatas = new Queue<AnimeItem>(Biblioteca.OrderBy(_ => Rng.Next()));
        Estado = EstadoMinijuego.Jugando;
        EstaPreparandoRonda = true;

        var biblioteca = Biblioteca.ToList();
        _locales = await Task.Run(() => CargarLocales(biblioteca));

        _siguiente = IniciarPreparacion(ct);
        await AvanzarAsync();
    }

    [RelayCommand]
    private async Task SiguienteAsync()
    {
        if (!EsJugando || !HaRespondido) return;
        await AvanzarAsync();
    }

    [RelayCommand]
    private void PedirPista()
    {
        if (!PuedePedirPista || _ronda == null) return;

        _pasosExtra++;
        var paso = _ronda.Pasos[_pasosExtra - 1];
        RefrescarRonda();

        // Un paso de "más audio" alarga el clip: se vuelve a oír ya con la nueva duración.
        if (paso.Tipo == TipoPasoOpEd.MasAudio) Reproducir(SegundosClip);
    }

    [RelayCommand]
    private void Escuchar()
    {
        if (_ronda == null || !EsJugando || EstaPreparandoRonda) return;
        Reproducir(HaRespondido ? AdivinaOpEdJuego.SegundosRevelacion : SegundosClip);
    }

    [RelayCommand]
    private void Responder(OpcionRespuesta? opcion)
    {
        if (opcion == null || _ronda == null || HaRespondido || !EsJugando || EstaPreparandoRonda) return;

        HaRespondido = true;
        UltimoFueAcierto = opcion.Indice == _ronda.IndiceCorrecto;
        Opciones[_ronda.IndiceCorrecto].EsCorrecta = true;
        ContabilizarRonda(UltimoFueAcierto);

        if (UltimoFueAcierto)
        {
            _puntosGanadosRonda = AdivinaAnimeJuego.Puntos(1 + _pasosExtra);
            Puntos += _puntosGanadosRonda;
            Aciertos++;
        }
        else
        {
            _puntosGanadosRonda = 0;
            opcion.EsIncorrecta = true;
        }

        PortadaRespuesta = _ronda.Respuesta.PortadaVisible;
        PuntosPosiblesTexto = string.Empty;
        ActualizarTemaRevelado();
        ActualizarResultadoTexto();
        OnPropertyChanged(nameof(TextoSiguiente));

        // Al responder suena un trozo más largo: se disfruta la canción y se ve de dónde venía.
        Reproducir(AdivinaOpEdJuego.SegundosRevelacion);
    }

    /// <summary>Atajo de teclado 1–4: responde con la opción de ese número.</summary>
    [RelayCommand]
    private void ResponderNumero(string? numero)
    {
        if (!int.TryParse(numero, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return;
        Responder(Opciones.FirstOrDefault(o => o.Numero == n));
    }

    [RelayCommand]
    private async Task VolverAlInicioAsync()
    {
        CancelarTrabajo();
        Detener();
        EstaPreparandoRonda = false;
        Estado = EstadoMinijuego.Inicio;
        await PrepararAsync();
    }

    /// <summary>Al cerrar la app (el contenedor de DI libera los singleton): cancela lo pendiente y corta el sonido.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _player.Detener();
    }

    /// <summary>Al salir de la pestaña o del juego: corta el sonido. La partida y la preparación de la siguiente ronda siguen.</summary>
    public override void Detener()
    {
        _player.Detener();
        EstaSonando = false;
    }

    // === Flujo de rondas ===

    private async Task AvanzarAsync()
    {
        if (_avanzando) return;
        _avanzando = true;
        var ct = _cts.Token;
        try
        {
            _player.Detener();
            EstaSonando = false;
            EstaPreparandoRonda = true;

            var tarea = _siguiente ?? IniciarPreparacion(ct);
            _siguiente = null;

            RondaOpEd? ronda = null;
            if (tarea != null)
            {
                try
                {
                    ronda = await tarea;
                }
                catch (OperationCanceledException)
                {
                    return; // partida cancelada (nueva partida o vuelta al inicio): no se toca nada
                }
                catch (Exception ex)
                {
                    AppLogger.Error("AdivinaOpEdViewModel", "Error preparando la ronda", ex);
                }
            }

            if (ct.IsCancellationRequested) return;

            if (ronda == null)
            {
                await TerminarSinMasRondasAsync();
                return;
            }

            MostrarRonda(ronda);
            _siguiente = IniciarPreparacion(ct); // adelanta la siguiente mientras se juega esta
        }
        finally
        {
            EstaPreparandoRonda = false;
            _avanzando = false;
        }
    }

    private async Task TerminarSinMasRondasAsync()
    {
        if (RondaNumero == 0)
        {
            // Ni una ronda: sin conexión y sin temas descargados, o AnimeThemes caída.
            MensajeError = LocalizationService.T("Mini_OpEdSinRondas");
            Estado = EstadoMinijuego.Inicio;
        }
        else
        {
            // Si algún anime no pudo dar ronda, se jugaron menos de las previstas: el resumen cuenta las reales.
            TotalRondas = RondaNumero;
            await TerminarPartidaAsync();
        }
    }

    /// <summary>Lanza en segundo plano la preparación de la siguiente ronda. Null si ya se pidieron todas.</summary>
    private Task<RondaOpEd?>? IniciarPreparacion(CancellationToken ct)
    {
        if (_rondasPedidas >= TotalRondas || _candidatas.Count == 0) return null;
        _rondasPedidas++;

        // La cola se pasa por parámetro: una tarea cancelada de la partida anterior no debe tocar la cola de la nueva.
        var candidatas = _candidatas;
        return Task.Run(() => ObtenerRondaAsync(candidatas, ct), ct);
    }

    private async Task<RondaOpEd?> ObtenerRondaAsync(Queue<AnimeItem> candidatas, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && candidatas.TryDequeue(out var candidata))
        {
            var ronda = await IntentarRondaAsync(candidata, ct);
            if (ronda != null)
            {
                _fallosSeguidos = 0;
                return ronda;
            }

            if (++_fallosSeguidos >= LimiteFallosSeguidos) return null;
        }

        ct.ThrowIfCancellationRequested();
        return null;
    }

    /// <summary>Intenta armar una ronda con este anime: primero por AnimeThemes; si no hay red, con lo ya descargado.</summary>
    private async Task<RondaOpEd?> IntentarRondaAsync(AnimeItem anime, CancellationToken ct)
    {
        int id = anime.AniListId;

        if (!_soloLocal)
        {
            var tema = await TemaDesdeAnimeThemesAsync(anime, ct);
            if (tema != null)
            {
                var ronda = AdivinaOpEdJuego.CrearRonda(anime, tema, Biblioteca, Rng);
                if (ronda != null) return ronda;
            }
        }

        if (_locales.TryGetValue(id, out var locales))
        {
            var local = AdivinaOpEdJuego.ElegirTemaLocal(locales, Rng);
            if (local != null)
            {
                // Sin conexión solo se conoce lo que dice el nombre del archivo: tipo y número, sin canción ni artista.
                var tema = new TemaParaJugar(local.Tipo, local.Slug, string.Empty, string.Empty, local.RutaArchivo);
                return AdivinaOpEdJuego.CrearRonda(anime, tema, Biblioteca, Rng);
            }
        }

        return null;
    }

    private async Task<TemaParaJugar?> TemaDesdeAnimeThemesAsync(AnimeItem anime, CancellationToken ct)
    {
        int id = anime.AniListId;
        List<AnimeThemeInfo> temas;

        using (var limite = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limite.CancelAfter(TiempoMaximoApi);
            temas = await _themes.ObtenerTemasAsync(id, limite.Token) ?? [];

            // ObtenerTemasAsync no lanza al agotarse el tiempo: devuelve vacío. Aquí se distingue "no tiene temas"
            // de "AnimeThemes no responde" para dejar de esperar 12 s por anime cuando está caída.
            if (temas.Count == 0 && limite.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                if (++_timeoutsSeguidos >= LimiteTimeoutsSeguidos) _soloLocal = true;
                return null;
            }
        }

        if (temas.Count > 0) _timeoutsSeguidos = 0;

        var elegido = AdivinaOpEdJuego.ElegirTema(temas, t => _descargas.EstaDescargado(id, t), Rng);
        if (elegido == null) return null;

        string? ruta;
        if (_descargas.EstaDescargado(id, elegido))
        {
            ruta = _descargas.ObtenerRutaLocalEsperada(id, elegido);
        }
        else
        {
            using var limiteDescarga = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limiteDescarga.CancelAfter(TiempoMaximoDescarga);
            ruta = await _descargas.DescargarYConvertirAsync(id, elegido, limiteDescarga.Token);
        }

        return ruta == null ? null : new TemaParaJugar(elegido.Tipo, elegido.Slug, elegido.TituloCancion, elegido.Artistas, ruta);
    }

    /// <summary>Un directorio por anime con sus mp3 ya descargados (lectura de disco: se llama fuera del hilo de UI).</summary>
    private Dictionary<int, List<TemaLocalDisponible>> CargarLocales(IEnumerable<AnimeItem> biblioteca)
    {
        var resultado = new Dictionary<int, List<TemaLocalDisponible>>();
        foreach (var anime in biblioteca)
        {
            var locales = _descargas.ListarDescargasLocales(anime.AniListId);
            if (locales is { Count: > 0 }) resultado[anime.AniListId] = locales;
        }
        return resultado;
    }

    private void CancelarTrabajo()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _siguiente = null;
    }

    private void MostrarRonda(RondaOpEd ronda)
    {
        _ronda = ronda;
        _pasosExtra = 0;
        _puntosGanadosRonda = 0;

        Opciones = CrearOpciones(ronda.Opciones);
        PortadaRespuesta = string.Empty;
        TemaReveladoTexto = string.Empty;
        RondaNumero++;
        HaRespondido = false;
        UltimoFueAcierto = false;
        ResultadoTexto = string.Empty;

        RefrescarRonda();
        OnPropertyChanged(nameof(TextoSiguiente));

        Reproducir(SegundosClip);
    }

    private void Reproducir(int segundos)
    {
        if (_ronda == null) return;
        _player.Reproducir(_ronda.Tema.RutaLocal, TimeSpan.FromSeconds(segundos), _ronda.PosicionInicio);
        EstaSonando = true;
    }

    /// <summary>Reconstruye lo que depende de cuántos pasos se han pedido (pistas, duración del clip, puntos posibles).</summary>
    private void RefrescarRonda()
    {
        if (_ronda == null) return;

        TipoTemaTexto = TextoTipoTema(_ronda.Tema);
        SegundosClip = AdivinaOpEdJuego.SegundosDeClip(_ronda.Pasos, _pasosExtra);
        Pistas = new ObservableCollection<PistaItem>(
            _ronda.Pasos.Take(_pasosExtra).Where(p => p.Tipo != TipoPasoOpEd.MasAudio).Select(ConvertirPaso));

        if (!HaRespondido)
            PuntosPosiblesTexto = string.Format(LocalizationService.T("Mini_PuntosPosiblesFormato"), AdivinaAnimeJuego.Puntos(1 + _pasosExtra));

        OnPropertyChanged(nameof(PuedePedirPista));
    }

    private void ActualizarResultadoTexto()
    {
        if (_ronda == null || !HaRespondido) return;

        ResultadoTexto = UltimoFueAcierto
            ? string.Format(LocalizationService.T("Mini_CorrectoFormato"), _puntosGanadosRonda)
            : string.Format(LocalizationService.T("Mini_IncorrectoFormato"), _ronda.Respuesta.Titulo);
    }

    private void ActualizarTemaRevelado()
    {
        if (_ronda == null || !HaRespondido) return;
        TemaReveladoTexto = TextoTemaRevelado(_ronda.Tema);
    }

    private static PistaItem ConvertirPaso(PasoOpEd paso)
    {
        return paso.Tipo switch
        {
            TipoPasoOpEd.Artista => new PistaItem("MicrophoneVariant", LocalizationService.T("Mini_Pista_Artista"), paso.Valor),
            TipoPasoOpEd.Estreno => new PistaItem("CalendarOutline", LocalizationService.T("Mini_Pista_Estreno"),
                AdivinaAnimeViewModel.TextoEstreno(new PistaAnime(TipoPista.Estreno, paso.Valor, paso.Extra))),
            TipoPasoOpEd.Generos => new PistaItem("TagMultipleOutline", LocalizationService.T("Mini_Pista_Generos"), AdivinaAnimeViewModel.TextoGeneros(paso.Valor)),
            _ => new PistaItem("MusicNoteOutline", LocalizationService.T("Mini_Pista_Cancion"), paso.Valor)
        };
    }

    /// <summary>"Opening 2" / "Ending" — se muestra siempre: saber que es un OP o un ED no es una pista.</summary>
    internal static string TextoTipoTema(TemaParaJugar tema)
    {
        var (tipo, numero) = AdivinaOpEdJuego.ParsearSlug(tema.Slug, tema.Tipo);
        string baseClave = tipo == "ED" ? "Mini_OpEd_Ending" : "Mini_OpEd_Opening";
        return numero == null
            ? LocalizationService.T(baseClave)
            : string.Format(LocalizationService.T(baseClave + "Num"), numero);
    }

    /// <summary>"Opening 2 · Guren no Yumiya — Linked Horizon" (lo que se conozca; sin conexión solo el tipo).</summary>
    internal static string TextoTemaRevelado(TemaParaJugar tema)
    {
        string cancion = string.IsNullOrWhiteSpace(tema.Titulo)
            ? string.Empty
            : string.IsNullOrWhiteSpace(tema.Artistas) ? tema.Titulo : $"{tema.Titulo} — {tema.Artistas}";
        string tipo = TextoTipoTema(tema);
        return cancion.Length == 0 ? tipo : $"{tipo} · {cancion}";
    }

    protected override void RefrescarTextos()
    {
        RefrescarRonda();
        ActualizarTemaRevelado();
        ActualizarResultadoTexto();
        OnPropertyChanged(nameof(SegundosClipTexto));
        base.RefrescarTextos();
    }
}
