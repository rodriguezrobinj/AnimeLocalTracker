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
/// "Adivina el personaje": aparece la imagen muy pixelada de un personaje de un anime de tu biblioteca y hay que decir quién
/// es. Cada pista aclara la imagen y resta puntos. Los personajes salen de AniList (la primera vez; después quedan
/// guardados y se juega sin conexión). Toda la partida se prepara al empezar —personajes e imágenes— para que entre ronda y
/// ronda no haya esperas. La lógica pura vive en <see cref="AdivinaPersonajeJuego"/>.
/// </summary>
public sealed partial class AdivinaPersonajeViewModel : MinijuegoViewModelBase, IDisposable
{
    /// <summary>Animes que se consultan por encima de los necesarios: algunos no tendrán personajes o imagen aprovechables.</summary>
    internal const int AnimesExtra = 4;

    /// <summary>Rondas de reserva por si alguna imagen no se puede descargar.</summary>
    internal const int RondasExtra = 2;

    /// <summary>Descargas de imágenes a la vez.</summary>
    private const int DescargasSimultaneas = 4;

    /// <summary>Tiempo máximo preparando la partida (consulta a AniList + imágenes). Ajustable solo en pruebas.</summary>
    internal TimeSpan TiempoMaximoPreparacion { get; set; } = TimeSpan.FromSeconds(40);

    private readonly IPersonajesService _personajes;

    private CancellationTokenSource _cts = new();
    private Queue<RondaPreparada> _pendientes = new();
    private RondaPreparada? _actual;
    private int _pistasReveladas;
    private bool _avanzando;
    private bool _preparando;

    private sealed record RondaPreparada(RondaAdivinaPersonaje Ronda, string RutaImagen);

    /// <summary>Solo para pruebas: posición de la opción correcta de la ronda actual (-1 si no hay ronda).</summary>
    internal int IndiceCorrecto => IndiceCorrectoRonda;

    [ObservableProperty] private string _imagenRonda = string.Empty;

    /// <summary>Ancho al que se reduce la imagen (bloques); 0 = imagen entera (tras responder).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstaPixelada))]
    private int _ladoPixelado;

    /// <summary>Solo tras responder: de qué anime es el personaje.</summary>
    public string AnimeReveladoTexto => DetalleRespuestaTexto;

    public bool EstaPixelada => LadoPixelado > 0;

    public override string Icono => "AccountQuestionOutline";
    protected override string ClaveTitulo => "Mini_PersonajeTitulo";
    protected override string ClaveDescripcion => "Mini_PersonajeDesc";

    /// <summary>Mientras se buscan los personajes y se descargan las imágenes de la partida.</summary>
    public override string PreparandoTexto => EstaPreparando ? LocalizationService.T("Mini_PersonajePreparando") : string.Empty;

    protected override int IndiceCorrectoRonda => _actual?.Ronda.IndiceCorrecto ?? -1;
    protected override string RespuestaRonda => _actual?.Ronda.Respuesta.Nombre ?? string.Empty;
    protected override int PuntosSiAcierta => AdivinaPersonajeJuego.Puntos(_pistasReveladas);

    /// <summary>Al responder la imagen sale entera.</summary>
    protected override void AlResponder() => LadoPixelado = 0;

    protected override void AlVolverAlInicio() => CancelarTrabajo();

    public override bool PuedePedirPista => EsJugando && !HaRespondido && _actual != null && _pistasReveladas < _actual.Ronda.Pistas.Count;
    protected override bool HayMasRondas => _pendientes.Count > 0;
    public override string ReglasTexto => string.Format(LocalizationService.T("Mini_PersonajeReglasFormato"), AdivinaAnimeJuego.RondasPorPartida, AdivinaAnimeJuego.OpcionesPorRonda);
    public override string AtajosTexto => LocalizationService.T("Mini_Atajos");

    protected override string JuegoId => JuegosMinijuego.AdivinaPersonaje;

    private readonly GuardiaConexion? _guardiaConexion;

    public AdivinaPersonajeViewModel(IDatabaseService databaseService, IPersonajesService personajes, IMinijuegosRecordsService? records = null,
        GuardiaConexion? guardiaConexion = null)
        : base(databaseService, records)
    {
        _personajes = personajes;
        _guardiaConexion = guardiaConexion;
    }

    [RelayCommand]
    private async Task IniciarPartidaAsync()
    {
        if (!PuedeJugar || _preparando || _avanzando) return;

        CancelarTrabajo();
        var ct = _cts.Token;

        _preparando = true;
        MensajeError = string.Empty;
        EstaPreparando = true;
        EstaCargando = true; // oculta la presentación mientras se prepara
        try
        {
            var rondas = await PrepararRondasAsync(ct);
            if (ct.IsCancellationRequested) return;

            if (rondas.Count == 0)
            {
                MensajeError = LocalizationService.T("Mini_PersonajeSinRondas");
                return;
            }

            _pendientes = new Queue<RondaPreparada>(rondas);
            ReiniciarPartida();
            TotalRondas = rondas.Count;
            _actual = null;
            HaRespondido = false;
            Estado = EstadoMinijuego.Jugando;
        }
        finally
        {
            _preparando = false;
            EstaPreparando = false;
            EstaCargando = false;
        }

        await AvanzarRondaAsync();
    }

    [RelayCommand]
    private void PedirPista()
    {
        if (!PuedePedirPista || _actual == null) return;

        _pistasReveladas++;
        RefrescarRonda();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>Sale al menú: si se estaba preparando una partida, se cancela.</summary>
    public override void Detener()
    {
        if (_preparando) CancelarTrabajo();
    }

    private void CancelarTrabajo()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    // === Preparación de la partida ===

    /// <summary>
    /// Elige los animes, consigue sus personajes (copia local o AniList), arma las rondas y descarga las imágenes de las
    /// respuestas. Devuelve solo rondas completas (con imagen); vacía si no se pudo armar ninguna.
    /// </summary>
    private async Task<List<RondaPreparada>> PrepararRondasAsync(CancellationToken ct)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TiempoMaximoPreparacion);

        try
        {
            // Sin conexión solo sirven personajes ya guardados con su imagen: se mira toda la biblioteca (es leer de disco) en vez de
            // unos pocos animes al azar, que casi nunca alcanzaban para una partida entera.
            bool sinConexion = _guardiaConexion?.PareceSinConexion == true;
            var mezclados = Biblioteca.OrderBy(_ => Rng.Next());
            var candidatos = (sinConexion ? mezclados : mezclados.Take(AdivinaAnimeJuego.RondasPorPartida + AnimesExtra)).ToList();
            var porAnime = await _personajes.ObtenerAsync(candidatos.Select(a => a.AniListId).ToList(), limite.Token);

            var respuestasPosibles = !sinConexion ? porAnime : porAnime.ToDictionary(
                kv => kv.Key, kv => kv.Value.Where(_personajes.TieneImagenLocal).ToList());
            var elegidas = AdivinaPersonajeJuego.ElegirRespuestas(candidatos, respuestasPosibles, AdivinaAnimeJuego.RondasPorPartida + RondasExtra, Rng);
            var rondas = new List<RondaAdivinaPersonaje>();
            foreach (var (anime, personaje) in elegidas)
            {
                var ronda = AdivinaPersonajeJuego.CrearRonda(anime, personaje, porAnime, AdivinaAnimeJuego.OpcionesPorRonda, Rng);
                if (ronda != null) rondas.Add(ronda);
            }

            return await DescargarImagenesAsync(rondas, limite.Token);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
                AppLogger.Warn("AdivinaPersonajeViewModel", "Se agotó el tiempo preparando la partida de personajes.");
            return new List<RondaPreparada>();
        }
        catch (Exception ex)
        {
            AppLogger.Error("AdivinaPersonajeViewModel", "Error preparando la partida de personajes", ex);
            return new List<RondaPreparada>();
        }
    }

    private async Task<List<RondaPreparada>> DescargarImagenesAsync(List<RondaAdivinaPersonaje> rondas, CancellationToken ct)
    {
        using var semaforo = new SemaphoreSlim(DescargasSimultaneas);
        var tareas = rondas.Select(async ronda =>
        {
            await semaforo.WaitAsync(ct);
            try
            {
                string? ruta = await _personajes.ObtenerImagenAsync(ronda.Respuesta, ct);
                return ruta == null ? null : new RondaPreparada(ronda, ruta);
            }
            finally
            {
                semaforo.Release();
            }
        }).ToList();

        var listas = await Task.WhenAll(tareas);
        return listas.OfType<RondaPreparada>().Take(AdivinaAnimeJuego.RondasPorPartida).ToList();
    }

    // === Flujo de rondas ===

    protected override async Task AvanzarRondaAsync()
    {
        if (_avanzando) return;
        _avanzando = true;
        try
        {
            if (_pendientes.Count == 0)
            {
                await TerminarPartidaAsync();
                return;
            }

            MostrarRonda(_pendientes.Dequeue());
        }
        finally
        {
            _avanzando = false;
        }
    }

    private void MostrarRonda(RondaPreparada preparada)
    {
        _actual = preparada;
        _pistasReveladas = 1;

        Opciones = new ObservableCollection<OpcionRespuesta>(preparada.Ronda.Opciones.Select((p, i) => new OpcionRespuesta(i, p.Nombre)));
        ImagenRonda = preparada.RutaImagen;
        DetalleRespuestaTexto = string.Empty;
        RondaNumero++;
        HaRespondido = false;
        UltimoFueAcierto = false;
        ResultadoTexto = string.Empty;

        RefrescarRonda();
        OnPropertyChanged(nameof(TextoSiguiente));
    }

    /// <summary>Reconstruye lo que depende de cuántas pistas hay visibles (lista, pixelado, puntos posibles).</summary>
    private void RefrescarRonda()
    {
        if (_actual == null) return;

        var ronda = _actual.Ronda;
        Pistas = new ObservableCollection<PistaItem>(ronda.Pistas.Take(_pistasReveladas).Select(ConvertirPista));
        if (!HaRespondido)
        {
            LadoPixelado = AdivinaPersonajeJuego.LadoPixelado(_pistasReveladas, ronda.Pistas.Count);
            PuntosPosiblesTexto = string.Format(LocalizationService.T("Mini_PuntosPosiblesFormato"), AdivinaPersonajeJuego.Puntos(_pistasReveladas));
        }
        OnPropertyChanged(nameof(PuedePedirPista));
    }

    protected override void ActualizarResultadoTexto()
    {
        if (_actual == null || !HaRespondido) return;

        base.ActualizarResultadoTexto();
        DetalleRespuestaTexto = string.Format(LocalizationService.T("Mini_PersonajeSaleEnFormato"), _actual.Ronda.Anime.Titulo);
    }

    internal static PistaItem ConvertirPista(PistaPersonaje pista) => pista.Tipo switch
    {
        TipoPistaPersonaje.Rol => new PistaItem("StarCircleOutline", LocalizationService.T("Mini_Pista_Rol"), TextoRol(pista.Valor)),
        TipoPistaPersonaje.Genero => new PistaItem("GenderMaleFemale", LocalizationService.T("Mini_Pista_Genero"), TextoGenero(pista.Valor)),
        TipoPistaPersonaje.Edad => new PistaItem("CakeVariantOutline", LocalizationService.T("Mini_Pista_Edad"),
            string.Format(LocalizationService.T("Mini_EdadFormato"), pista.Valor)),
        TipoPistaPersonaje.Apodo => new PistaItem("TagText", LocalizationService.T("Mini_Pista_Apodo"), pista.Valor),
        TipoPistaPersonaje.Anime => new PistaItem("PlayBoxOutline", LocalizationService.T("Mini_Pista_Anime"), pista.Valor),
        _ => new PistaItem("Alphabetical", LocalizationService.T("Mini_Pista_Inicial"), pista.Valor)
    };

    /// <summary>Rol de AniList (MAIN/SUPPORTING/BACKGROUND) en el idioma de la app.</summary>
    internal static string TextoRol(string rol) => rol.ToUpperInvariant() switch
    {
        "MAIN" => LocalizationService.T("Mini_Rol_Principal"),
        "SUPPORTING" => LocalizationService.T("Mini_Rol_Secundario"),
        _ => LocalizationService.T("Mini_Rol_Fondo")
    };

    internal static string TextoGenero(string genero) =>
        LocalizationService.T(genero.Equals("Female", StringComparison.OrdinalIgnoreCase) ? "Mini_Genero_Femenino" : "Mini_Genero_Masculino");

    protected override void RefrescarTextos()
    {
        RefrescarRonda();
        ActualizarResultadoTexto();
        if (HayError) MensajeError = LocalizationService.T("Mini_PersonajeSinRondas");
        base.RefrescarTextos();
    }
}
