using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Native;

namespace AnimeLocalTracker.Services;

/// <summary>
/// libmpv detrás de <see cref="IMotorVideo"/>. Las órdenes se le dan desde cualquier hilo; lo que cambia (posición, pausa, fin,
/// pistas) llega por sus eventos a un hilo propio, que deja los valores listos para quien pregunte y da el aviso de apertura.
/// El video lo pinta <c>Controls/SuperficieMpv</c>; sin superficie (pruebas) el motor trabaja igual, sin imagen.
/// </summary>
public sealed partial class MotorMpv : IMotorVideo
{
    private const ulong IdPosicion = 1, IdDuracion = 2, IdPausa = 3, IdFin = 4, IdProporcion = 5;

    private readonly nint _mpv;
    private readonly bool _sinSalida;
    private readonly Thread _hiloEventos;
    private readonly object _candado = new();

    private volatile bool _desechado, _abriendo, _fallido, _hayArchivo, _pausa, _fin;
    private long _posicionTicks, _duracionTicks, _fotogramas, _proporcionBits;
    private List<(int IdMpv, PistaAudioMotor Pista)> _audio = [];
    private List<(int IdMpv, PistaSubtitulosMotor Pista)> _subtitulos = [];

    // Lo que dice el contenedor de su pista de video: se conoce al cargar el archivo, antes de que arranque el decodificador.
    private volatile int _anchoContenedor, _altoContenedor;
    private volatile string? _codecContenedor;

    private MotorMpv(nint mpv, bool sinSalida)
    {
        _mpv = mpv;
        _sinSalida = sinSalida;
        _hiloEventos = new Thread(AtenderEventos) { IsBackground = true, Name = "Eventos de libmpv" };
        _hiloEventos.Start();
    }

    /// <summary>Null si falta la biblioteca o libmpv no arranca en este equipo.</summary>
    /// <param name="sinSalida">Sin imagen ni sonido (pruebas): todo lo demás funciona igual.</param>
    /// <param name="sinSonido">Con imagen pero sin sonido (pruebas del pintado: en el CI no hay tarjeta de sonido).</param>
    public static MotorMpv? Crear(AjustesInicialesMotor ajustes, bool sinSalida = false, bool sinSonido = false)
    {
        if (!LibMpv.Disponible) return null;

        nint mpv = 0;
        try
        {
            mpv = LibMpv.mpv_create();
            if (mpv == 0) return null;

            // No lee la configuración ni los scripts de mpv del usuario, no resuelve direcciones de internet y no atiende teclado ni ratón.
            foreach (var (nombre, valor) in new (string, string)[]
            {
                ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"), ("osc", "no"),
                ("input-default-bindings", "no"), ("input-vo-keyboard", "no"), ("input-cursor", "no"),
                ("idle", "yes"),          // sin archivo se queda esperando el siguiente
                ("keep-open", "yes"),     // al terminar se queda en el último fotograma: el reproductor decide qué hacer
                ("sid", "no"),            // sus subtítulos apagados hasta que la app elija pista (MostrarSubtitulosDelMotor)
                ("sub-auto", "fuzzy"),    // los .srt/.ass sueltos junto al video aparecen como pistas
                ("audio-display", "no"),
                ("hwdec", ajustes.AceleracionPorHardware ? "auto-copy" : "no"),
                ("vo", sinSalida ? "null" : "libmpv"),
                ("screenshot-png-compression", "0"), // la captura se lee y se borra enseguida: sin comprimir tarda 35 ms en vez de 500
                ("screenshot-high-bit-depth", "no"),
                ("af", CadenaDeFiltros(ajustes.FiltrosAudio)),
            })
            {
                LibMpv.mpv_set_option_string(mpv, nombre, valor);
            }
            if (sinSalida || sinSonido) LibMpv.mpv_set_option_string(mpv, "ao", "null");
            // Sin valor, el color se interpola con el filtro de la imagen (lanczos): en una gráfica integrada con video 1080p son
            // 7 de los 17 ms de tarjeta de cada fotograma (medido en una HD 620).
            if (ajustes.ColorLigero) LibMpv.mpv_set_option_string(mpv, "cscale", "bilinear");
            // Compilar los shaders (Anime4K sobre todo) se hace en el hilo de la interfaz, que es donde se pinta: en una HD 620 el
            // primer fotograma tardaba 280 ms y el video se congelaba al abrir, justo con el aviso de «Continuando desde». Con los
            // ya compilados guardados, 120 ms. Si el controlador cambia, libmpv descarta lo guardado y vuelve a compilar.
            if (ajustes.CarpetaShaders != null) LibMpv.mpv_set_option_string(mpv, "gpu-shader-cache-dir", ajustes.CarpetaShaders);
            // Diagnóstico: el registro propio de libmpv, solo si se pide con esta variable de entorno (ruta del archivo).
            if (Environment.GetEnvironmentVariable("ANIMELOCALTRACKER_MPV_LOG") is { Length: > 0 } registroMpv)
            {
                LibMpv.mpv_set_option_string(mpv, "log-file", registroMpv);
                LibMpv.mpv_set_option_string(mpv, "msg-level", "all=v");
            }

            int resultado = LibMpv.mpv_initialize(mpv);
            if (resultado < 0)
            {
                AppLogger.Warn("MotorMpv", $"libmpv no arrancó: {LibMpv.TextoDeError(resultado)}.");
                LibMpv.mpv_terminate_destroy(mpv);
                return null;
            }

            LibMpv.mpv_observe_property(mpv, IdPosicion, "time-pos", LibMpv.FormatoDoble);
            LibMpv.mpv_observe_property(mpv, IdDuracion, "duration", LibMpv.FormatoDoble);
            LibMpv.mpv_observe_property(mpv, IdPausa, "pause", LibMpv.FormatoBandera);
            LibMpv.mpv_observe_property(mpv, IdFin, "eof-reached", LibMpv.FormatoBandera);
            LibMpv.mpv_observe_property(mpv, IdProporcion, "video-params/aspect", LibMpv.FormatoDoble);

            AppLogger.Info("MotorMpv", $"Motor libmpv listo ({LibMpv.Leer(mpv, "mpv-version")}).");
            return new MotorMpv(mpv, sinSalida) { _filtros = ajustes.FiltrosAudio.ToList(), PintaPorSoftware = ajustes.VideoPorSoftware };
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MotorMpv", $"No se pudo crear el motor libmpv: {ex.Message}");
            return null;
        }
    }

    public event EventHandler<AperturaTerminada>? AperturaTerminada;
    public event EventHandler? ProporcionVideoCambiada;

    /// <summary>Para la superficie: el motor está a punto de cerrarse y hay que soltar el pintor ANTES (libmpv lo exige).</summary>
    internal event Action? Desechando;

    /// <summary>La superficie no pudo pintar ni en el lienzo OpenGL ni por software: este motor no va a poder mostrar el video en este equipo.</summary>
    internal event Action<string>? SinSalida;
    internal void AvisarSinSalida(string motivo) => SinSalida?.Invoke(motivo);

    /// <summary>
    /// El video se pinta por software (modo de compatibilidad): lo pide el ajuste o lo decide la superficie si el lienzo OpenGL no
    /// arranca. Sin lienzo no hay tarjeta que informar ni dónde aplicar los sombreadores del escalado inteligente.
    /// </summary>
    internal bool PintaPorSoftware { get; set; }

    internal nint Manejador => _mpv;
    internal void ContarFotograma() => Interlocked.Increment(ref _fotogramas);

    private volatile bool _pintorListo;
    private volatile int _idVideoMpv;
    private string? _rutaEsperandoPintor; // bajo _candado

    /// <summary>
    /// La superficie ya tiene dónde pintar: se carga el archivo que estaba esperando (ver <see cref="Abrir"/>) o, si el pintor se
    /// había soltado con un archivo ya abierto, se le devuelve el video.
    /// </summary>
    internal void PintorCreado()
    {
        string? ruta;
        lock (_candado)
        {
            _pintorListo = true;
            (ruta, _rutaEsperandoPintor) = (_rutaEsperandoPintor, null);
        }
        if (ruta != null) Ordenar("loadfile", ruta, "replace");
        else ReactivarVideoSiHaceFalta();
    }

    internal void PintorSoltado() => _pintorListo = false;

    /// <summary>Solo para pruebas: lo que deja apuntado un aviso de libmpv del archivo anterior que llega tarde (ver el aviso de archivo cargado).</summary>
    internal void SimularAvisoTardio(TimeSpan posicion, bool fin)
    {
        Interlocked.Exchange(ref _posicionTicks, posicion.Ticks);
        _fin = fin;
    }

    /// <summary>
    /// Un archivo que se queda sin dónde pintar pierde su pista de video ("No render context set" → "Video: no video") y libmpv no
    /// la recupera solo. Pedir "vid=auto" no vale: ya está en "auto" y no hace nada. Hay que nombrar la pista.
    /// </summary>
    private void ReactivarVideoSiHaceFalta()
    {
        int id = _idVideoMpv;
        if (!_pintorListo || !_hayArchivo || id <= 0 || Leer("current-tracks/video/id").Length > 0) return;
        AppLogger.Debug("MotorMpv", "El archivo estaba abierto sin dónde pintar: se reactiva su pista de video.");
        Fijar("vid", id.ToString(CultureInfo.InvariantCulture));
    }

    public bool Desechado => _desechado;

    public EstadoMotor Estado =>
        _desechado ? EstadoMotor.Detenido
        : _abriendo ? EstadoMotor.Abriendo
        : _fallido ? EstadoMotor.Fallido
        : !_hayArchivo ? EstadoMotor.Detenido
        : _fin ? EstadoMotor.Terminado
        : _pausa ? EstadoMotor.EnPausa
        : EstadoMotor.Reproduciendo;

    public TimeSpan Posicion => TimeSpan.FromTicks(Interlocked.Read(ref _posicionTicks));
    public TimeSpan Duracion => TimeSpan.FromTicks(Interlocked.Read(ref _duracionTicks));

    public void Abrir(string ruta)
    {
        if (_desechado) return;
        _abriendo = true;
        _fallido = false;
        _fin = false;
        Interlocked.Exchange(ref _fotogramas, 0);
        Interlocked.Exchange(ref _posicionTicks, 0);
        Fijar("pause", "no"); // abrir es empezar a reproducir
        Fijar("sid", "no");   // libmpv conservaría la pista del archivo anterior: cada archivo empieza sin subtítulos y la app elige

        // Un cambio en vivo del ecualizador no toca la opción "af": sin esto el archivo nuevo volvería a las ganancias anteriores.
        IReadOnlyList<FiltroAudio>? pendientes;
        lock (_candado) pendientes = _filtrosPendientes;
        if (pendientes != null) AplicarFiltrosAudio(pendientes);

        // El reproductor abre en cuanto tiene motor, y la vista tarda en tener dónde pintar (hasta 2 s recién abierta la app). Cargando
        // ya, libmpv abre el archivo sin video y lo va reproduciendo: el episodio empezaría con esos segundos consumidos y el video,
        // al llegar, tiene que alcanzar al audio (más de 1 s: el reproductor creía que la tarjeta gráfica no daba imagen y reabría por
        // procesador). Se carga al crearse el pintor. Si nunca llega a haberlo, avisa el reproductor (su tiempo máximo de apertura).
        lock (_candado)
        {
            if (!_sinSalida && !_pintorListo)
            {
                _rutaEsperandoPintor = ruta;
                return;
            }
        }
        Ordenar("loadfile", ruta, "replace");
    }

    public void Reproducir() => Fijar("pause", "no");
    public void Pausar() => Fijar("pause", "yes");

    public void Detener()
    {
        if (_desechado) return;
        lock (_candado) _rutaEsperandoPintor = null;
        Ordenar("stop");
        _abriendo = false;
        _hayArchivo = false;
    }

    public void Saltar(TimeSpan destino, bool exacto)
    {
        if (_desechado || !_hayArchivo) return;
        _fin = false; // retroceder desde el final vuelve a ser "en pausa", no "terminado"
        Ordenar("seek", destino.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture), exacto ? "absolute+exact" : "absolute+keyframes");
    }

    public double Velocidad { set => Fijar("speed", value.ToString("F2", CultureInfo.InvariantCulture)); }
    public int Volumen { set => Fijar("volume", Math.Clamp(value, 0, 100).ToString(CultureInfo.InvariantCulture)); }
    public bool Silencio { set => Fijar("mute", value ? "yes" : "no"); }
    public int DesfaseSubtitulosMs { set => Fijar("sub-delay", (value / 1000.0).ToString(CultureInfo.InvariantCulture)); }
    public int DesfaseAudioMs { set => Fijar("audio-delay", (value / 1000.0).ToString(CultureInfo.InvariantCulture)); }

    public long FotogramasMostrados => Interlocked.Read(ref _fotogramas);
    public bool DecodificaPorHardware => _hayArchivo && Leer("hwdec-current") is { Length: > 0 } actual && actual != "no";
    public bool AceleracionPorHardware { set => Fijar("hwdec", value ? "auto-copy" : "no"); }
    public string? CodecVideo => !_hayArchivo ? null : Leer("video-format") is { Length: > 0 } codec ? codec : _codecContenedor;

    /// <summary>El del video ya decodificado o, mientras el decodificador no ha arrancado, el que declara el contenedor.</summary>
    public (int Ancho, int Alto) TamanoVideo
    {
        get
        {
            if (!_hayArchivo) return (0, 0);
            if (int.TryParse(Leer("width"), out int ancho) && int.TryParse(Leer("height"), out int alto) && ancho > 0 && alto > 0) return (ancho, alto);
            return (_anchoContenedor, _altoContenedor);
        }
    }

    public double ProporcionVideo => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _proporcionBits));

    private volatile TarjetaGrafica? _tarjeta;

    /// <summary>La tarjeta que pinta el lienzo (la que Windows asigna a la app): la dice la superficie al crear el pintor. Memoria desconocida.</summary>
    internal void TarjetaDetectada(string? nombre, string? fabricante)
    {
        if (!string.IsNullOrWhiteSpace(nombre)) _tarjeta = new TarjetaGrafica(nombre.Trim(), MotorVideo.FabricantePorNombre($"{fabricante} {nombre}"), 0);
    }

    public TarjetaGrafica? TarjetaEnUso => PintaPorSoftware ? null : _tarjeta;

    // ── Escalado inteligente: Anime4K (sombreadores que libmpv aplica al pintar) ──

    private volatile bool _escalado;

    /// <summary>Junto a la app. Las pruebas la cambian para simular una instalación incompleta.</summary>
    internal string CarpetaDeSombreadores { get; set; } = Path.Combine(AppContext.BaseDirectory, "Shaders", "Anime4K");

    public long FotogramasPerdidos => _hayArchivo && long.TryParse(Leer("frame-drop-count"), out long perdidos) ? perdidos : 0;

    public bool EscaladoInteligente
    {
        get => _escalado;
        set
        {
            if (value == _escalado) return;
            Ordenar("change-list", "glsl-shaders", "clr", "");
            _escalado = value && !PintaPorSoftware && CargarSombreadores();
        }
    }

    /// <summary>False si falta algún archivo (instalación incompleta): mejor sin escalado que con medio juego de shaders.</summary>
    private bool CargarSombreadores()
    {
        var nivel = NivelPedido();
        var rutas = MotorVideo.SombreadoresAnime4K(nivel).Select(nombre => Path.Combine(CarpetaDeSombreadores, nombre)).ToList();
        if (!rutas.All(File.Exists))
        {
            AppLogger.Warn("MotorMpv", $"Faltan los shaders de Anime4K en '{CarpetaDeSombreadores}': el escalado inteligente no se enciende.");
            return false;
        }
        if (!rutas.All(ruta => Ordenar("change-list", "glsl-shaders", "append", ruta)))
        {
            Ordenar("change-list", "glsl-shaders", "clr", "");
            return false;
        }
        AppLogger.Info("MotorMpv", $"Escalado inteligente encendido: Anime4K, nivel {nivel}.");
        return true;
    }

    /// <summary>El que aguanta la tarjeta, salvo que la variable de diagnóstico ANIMELOCALTRACKER_ANIME4K_NIVEL fuerce otro.</summary>
    private NivelEscalado NivelPedido() =>
        Enum.TryParse(Environment.GetEnvironmentVariable("ANIMELOCALTRACKER_ANIME4K_NIVEL"), ignoreCase: true, out NivelEscalado forzado) && Enum.IsDefined(forzado)
            ? forzado
            : MotorVideo.NivelAnime4K(_tarjeta?.Fabricante ?? FabricanteGrafica.Desconocido);

    /// <summary>El fotograma que se está viendo, con sus subtítulos. libmpv lo escribe a un PNG sin comprimir que se lee y se borra.</summary>
    public BitmapSource? CapturarFotograma()
    {
        if (_desechado || !_hayArchivo) return null;

        string ruta = Path.Combine(Path.GetTempPath(), $"animelocaltracker-captura-{Guid.NewGuid():N}.png");
        try
        {
            if (!Ordenar("screenshot-to-file", ruta, "subtitles")) return null;

            var imagen = new BitmapImage();
            imagen.BeginInit();
            imagen.CacheOption = BitmapCacheOption.OnLoad; // lee el archivo entero ahora: se puede borrar enseguida
            imagen.UriSource = new Uri(ruta);
            imagen.EndInit();
            imagen.Freeze();
            return imagen;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MotorMpv", $"No se pudo capturar el fotograma: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(ruta); } catch { }
        }
    }

    // ── Ecualizador y modo noche ──

    private const string EtiquetaFiltros = "filtros";
    private IReadOnlyList<FiltroAudio> _filtros = [];       // la cadena montada: da el nombre de cada filtro para el cambio en vivo
    private IReadOnlyList<FiltroAudio>? _filtrosPendientes; // tras un cambio en vivo: se montan al abrir el siguiente archivo

    /// <summary>Un solo grafo de FFmpeg, con cada filtro nombrado "nombre@id" para poder cambiarlo en vivo.</summary>
    internal static string CadenaDeFiltros(IReadOnlyList<FiltroAudio> filtros) =>
        filtros.Count == 0 ? "" : $"@{EtiquetaFiltros}:lavfi=[{string.Join(",", filtros.Select(f => $"{f.Nombre}@{f.Id}={f.Argumentos}"))}]";

    public bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros)
    {
        lock (_candado)
        {
            _filtros = filtros.ToList();
            _filtrosPendientes = null;
        }
        return Fijar("af", CadenaDeFiltros(filtros));
    }

    /// <summary>Hay que nombrar el filtro: con el destino por defecto ("todos") libmpv devuelve error aunque el cambio se aplique.</summary>
    public bool CambiarFiltroEnVivo(string id, string clave, string valor)
    {
        string? nombre;
        lock (_candado) nombre = _filtros.FirstOrDefault(f => f.Id == id)?.Nombre;
        return nombre != null && _hayArchivo && Ordenar("af-command", EtiquetaFiltros, clave, valor, $"{nombre}@{id}");
    }

    public void RecordarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros)
    {
        lock (_candado) _filtrosPendientes = filtros.ToList();
    }

    // ── Subtítulos: los dibuja libmpv dentro del video (libass), también los de texto plano ──

    private double _posicionSubtitulos = 100;

    /// <summary>
    /// Altura de los subtítulos en % del alto de la imagen (100 = su sitio). La vista los sube mientras los controles están a la
    /// vista, para que la barra inferior no los tape. Solo se mueven los diálogos de abajo: los carteles con posición propia no.
    /// </summary>
    internal void ColocarSubtitulos(double porcentaje)
    {
        porcentaje = Math.Clamp(porcentaje, 50, 100);
        if (Math.Abs(porcentaje - _posicionSubtitulos) < 0.5) return;
        _posicionSubtitulos = porcentaje;
        Fijar("sub-pos", porcentaje.ToString("0.#", CultureInfo.InvariantCulture));
    }

    // Medidas de Configuración → medidas de libmpv. En Configuración el tamaño es el cuerpo de la letra en píxeles, tal como lo pinta
    // WPF, y no cambia con la ventana. libmpv mide la celda de la fuente (ascendente + descendente: un 16 % más que el cuerpo en
    // Trebuchet MS, distinto en cada fuente) y lo escala todo con el alto del video, tomando 720 como referencia. Se igualan con el
    // video a 768 de alto (pantalla completa en el equipo donde se ajustó el estilo): ahí la letra y el contorno miden lo que dice
    // Configuración, y en ventanas más grandes o más pequeñas (mini reproductor) guardan esa proporción.
    // Medido: la misma línea ocupaba 472 px con WPF y 411 con libmpv antes de corregir (1,148; la celda de Trebuchet MS es 1,161).
    internal const double AltoDeReferencia = 768, AltoDeLibmpv = 720, CeldaPorDefecto = 1.15;

    internal static double EscalaDeVentana => AltoDeLibmpv / AltoDeReferencia;

    /// <summary>Alto de la celda de la fuente respecto a su cuerpo. Si la fuente no está o no se puede medir, un valor típico.</summary>
    internal static double CeldaDeLaFuente(EstiloSubtitulos e)
    {
        try
        {
            var tipo = new Typeface(new FontFamily(e.Fuente), e.Cursiva ? FontStyles.Italic : FontStyles.Normal,
                e.Negrita ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            return tipo.TryGetGlyphTypeface(out var glifos) && glifos.Height is > 0.8 and < 2 ? glifos.Height : CeldaPorDefecto;
        }
        catch
        {
            return CeldaPorDefecto;
        }
    }

    private EstiloSubtitulos _estilo = new(); // bajo _candado, como las dos siguientes
    private bool _tambienEnAss, _pistaMostradaEsAss;

    /// <summary>
    /// Opciones de libmpv para un estilo de Configuración. <paramref name="forzar"/>: usarlo en vez del estilo del propio subtítulo
    /// (el usuario pidió «Usar mi estilo», o la pista es de texto plano y no tiene otro). El borde de la caja no tiene equivalente.
    /// </summary>
    internal static IReadOnlyList<(string Nombre, string Valor)> OpcionesDeEstilo(EstiloSubtitulos e, bool forzar)
    {
        static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        bool caja = e.OpacidadFondo > 0;
        return
        [
            ("sub-font", e.Fuente),
            ("sub-font-size", N(e.Tamano * CeldaDeLaFuente(e) * EscalaDeVentana)),
            ("sub-bold", e.Negrita ? "yes" : "no"),
            ("sub-italic", e.Cursiva ? "yes" : "no"),
            ("sub-color", e.ColorTexto),
            ("sub-outline-color", e.ColorContorno),
            ("sub-outline-size", N(e.GrosorContorno * EscalaDeVentana)),
            ("sub-shadow-offset", "0"),
            ("sub-border-style", caja ? "background-box" : "outline-and-shadow"),
            ("sub-back-color", caja ? $"#{(int)Math.Round(e.OpacidadFondo * 255 / 100.0, MidpointRounding.AwayFromZero):X2}000000" : "#00000000"),
            // El subrayado solo existe como retoque del estilo ASS; sin forzar, subrayaría también los subtítulos con estilo propio.
            ("sub-ass-style-overrides", forzar && e.Subrayado ? "Underline=-1" : ""),
            // "strip" y no "force": con "force" se quedan las cursivas y las posiciones del subtítulo original.
            // Sin forzar, "scale" (el valor de fábrica de mpv) y no "no": la pista ASS conserva su estilo igual, pero "no" le quita
            // también la posición (sub-pos) y los diálogos se quedarían detrás de los controles.
            ("sub-ass-override", forzar ? "strip" : "scale"),
        ];
    }

    public void AplicarEstiloSubtitulos(EstiloSubtitulos estilo, bool tambienEnAss)
    {
        lock (_candado)
        {
            _estilo = estilo;
            _tambienEnAss = tambienEnAss;
        }
        AplicarOpcionesDeEstilo();
    }

    private void AplicarOpcionesDeEstilo()
    {
        EstiloSubtitulos estilo;
        bool forzar;
        lock (_candado)
        {
            estilo = _estilo;
            forzar = _tambienEnAss || !_pistaMostradaEsAss;
        }
        foreach (var (nombre, valor) in OpcionesDeEstilo(estilo, forzar)) Fijar(nombre, valor);
    }

    public IReadOnlyList<PistaAudioMotor> PistasAudio { get { lock (_candado) return _audio.Select(a => a.Pista).ToList(); } }
    public IReadOnlyList<PistaSubtitulosMotor> PistasSubtitulos { get { lock (_candado) return _subtitulos.Select(s => s.Pista).ToList(); } }

    public int IndicePistaAudioActual
    {
        get
        {
            if (!_hayArchivo || !int.TryParse(Leer("aid"), out int id)) return -1;
            lock (_candado) return _audio.Where(a => a.IdMpv == id).Select(a => a.Pista.Indice).DefaultIfEmpty(-1).First();
        }
    }

    public int IndicePistaSubtitulosDelMotor
    {
        get
        {
            if (!_hayArchivo || !int.TryParse(Leer("sid"), out int id)) return -1; // "no" = apagados
            lock (_candado) return _subtitulos.Where(s => s.IdMpv == id).Select(s => s.Pista.Indice).DefaultIfEmpty(-1).First();
        }
    }

    public void ElegirAudio(PistaAudioMotor pista)
    {
        int id;
        lock (_candado) id = _audio.Where(a => a.Pista == pista).Select(a => a.IdMpv).DefaultIfEmpty(-1).First();
        if (id < 0) return;
        Fijar("aid", id.ToString(CultureInfo.InvariantCulture));
        // El reproductor espera el mismo aviso que al abrir para restaurar volumen y silencio.
        _ = Task.Run(() => AperturaTerminada?.Invoke(this, new AperturaTerminada(ClaseApertura.Video, true, null)));
    }

    /// <summary>Los dibuja libmpv dentro del video. No avisa: la app no tiene nada que leer ni que pintar.</summary>
    public void MostrarSubtitulosDelMotor(PistaSubtitulosMotor pista)
    {
        int id;
        lock (_candado)
        {
            id = _subtitulos.Where(s => s.Pista == pista).Select(s => s.IdMpv).DefaultIfEmpty(-1).First();
            if (id >= 0) _pistaMostradaEsAss = pista.EsAss;
        }
        if (id < 0) return; // una pista de otro archivo (la que se guardó del episodio anterior)

        AplicarOpcionesDeEstilo(); // una pista ASS conserva su estilo; una de texto plano usa el de Configuración
        Fijar("sid", id.ToString(CultureInfo.InvariantCulture));
    }

    public void ApagarSubtitulosDelMotor() => Fijar("sid", "no");

    private bool Fijar(string nombre, string valor) => !_desechado && LibMpv.mpv_set_property_string(_mpv, nombre, valor) >= 0;

    private string Leer(string nombre) => _desechado ? string.Empty : LibMpv.Leer(_mpv, nombre);

    /// <summary>Para las pruebas: lo que libmpv dice de una propiedad suya.</summary>
    internal string LeerPropiedad(string nombre) => Leer(nombre);

    private bool Ordenar(params string[] argumentos)
    {
        if (_desechado) return false;
        int resultado = LibMpv.Orden(_mpv, argumentos);
        if (resultado < 0) AppLogger.Debug("MotorMpv", $"La orden '{argumentos[0]}' falló: {LibMpv.TextoDeError(resultado)}.");
        return resultado >= 0;
    }

    private void AtenderEventos()
    {
        try
        {
            while (!_desechado)
            {
                var evento = Marshal.PtrToStructure<LibMpv.Evento>(LibMpv.mpv_wait_event(_mpv, 0.5));
                switch (evento.Id)
                {
                    case LibMpv.EventoArchivoCargado:
                        LeerPistas();
                        // El cambio de "duration" llega como evento aparte, después de este: quien la lea en el aviso de apertura
                        // (el salto de reanudación) vería 0, o la del episodio anterior.
                        if (double.TryParse(Leer("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double duracion))
                            Interlocked.Exchange(ref _duracionTicks, TimeSpan.FromSeconds(Math.Max(0, duracion)).Ticks);
                        // Lo apuntado del archivo anterior deja de valer aquí. Uno de sus avisos puede llegar después de que Abrir lo
                        // pusiera a cero (visto en la app: 30,00 s apuntados con libmpv ya en 0): el reproductor, que mira la posición
                        // en cuanto el motor dice que reproduce, daba el episodio nuevo por visto nada más empezar. Los avisos que
                        // lleguen desde ahora ya son de este archivo.
                        _fin = false;
                        Interlocked.Exchange(ref _posicionTicks,
                            double.TryParse(Leer("time-pos"), NumberStyles.Float, CultureInfo.InvariantCulture, out double posicion)
                                ? TimeSpan.FromSeconds(Math.Max(0, posicion)).Ticks : 0);
                        _hayArchivo = true;
                        _abriendo = false;
                        ReactivarVideoSiHaceFalta(); // por si el pintor se creó justo entre el fallo de la salida y este aviso
                        AperturaTerminada?.Invoke(this, new AperturaTerminada(ClaseApertura.Video, true, null));
                        break;

                    case LibMpv.EventoFinDeArchivo:
                        var fin = Marshal.PtrToStructure<LibMpv.EventoFin>(evento.Datos);
                        if (fin.Razon != LibMpv.FinPorError) break; // parar o cargar otro archivo también pasan por aquí
                        _hayArchivo = false;
                        _abriendo = false;
                        _fallido = true;
                        AperturaTerminada?.Invoke(this, new AperturaTerminada(ClaseApertura.Video, false, LibMpv.TextoDeError(fin.Error)));
                        break;

                    case LibMpv.EventoReinicioDeReproduccion:
                        // Sin superficie nadie cuenta fotogramas: cada arranque tras abrir o saltar vale por los que se habrían pintado.
                        if (_sinSalida) Interlocked.Add(ref _fotogramas, 2);
                        break;

                    case LibMpv.EventoCambioDePropiedad:
                        AtenderPropiedad(evento.Usuario, Marshal.PtrToStructure<LibMpv.EventoPropiedad>(evento.Datos));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MotorMpv", $"El hilo de eventos de libmpv terminó con un error: {ex.Message}");
        }
        finally
        {
            LibMpv.mpv_terminate_destroy(_mpv);
        }
    }

    private void AtenderPropiedad(ulong id, LibMpv.EventoPropiedad propiedad)
    {
        if (propiedad.Datos == 0) return; // la propiedad dejó de tener valor (sin archivo)

        switch (id)
        {
            case IdPosicion when propiedad.Formato == LibMpv.FormatoDoble:
                Interlocked.Exchange(ref _posicionTicks, TimeSpan.FromSeconds(Math.Max(0, Marshal.PtrToStructure<double>(propiedad.Datos))).Ticks);
                break;
            case IdDuracion when propiedad.Formato == LibMpv.FormatoDoble:
                Interlocked.Exchange(ref _duracionTicks, TimeSpan.FromSeconds(Math.Max(0, Marshal.PtrToStructure<double>(propiedad.Datos))).Ticks);
                break;
            case IdPausa when propiedad.Formato == LibMpv.FormatoBandera:
                _pausa = Marshal.ReadInt32(propiedad.Datos) != 0;
                break;
            case IdFin when propiedad.Formato == LibMpv.FormatoBandera:
                _fin = Marshal.ReadInt32(propiedad.Datos) != 0;
                break;
            case IdProporcion when propiedad.Formato == LibMpv.FormatoDoble:
                Interlocked.Exchange(ref _proporcionBits, BitConverter.DoubleToInt64Bits(Marshal.PtrToStructure<double>(propiedad.Datos)));
                ProporcionVideoCambiada?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// Las pistas del archivo recién cargado. El índice que ve la app es el del flujo dentro del contenedor (el que usa ffmpeg para
    /// leer los subtítulos); libmpv las numera a su manera, así que se guarda la pareja.
    /// </summary>
    private void LeerPistas()
    {
        var audio = new List<(int, PistaAudioMotor)>();
        var subtitulos = new List<(int, PistaSubtitulosMotor)>();
        (int Ancho, int Alto, string? Codec) video = (0, 0, null);
        int idVideo = 0;
        int cuantas = int.TryParse(Leer("track-list/count"), out int n) ? n : 0;
        for (int i = 0; i < cuantas; i++)
        {
            string raiz = $"track-list/{i}/";
            if (!int.TryParse(Leer(raiz + "id"), out int id)) continue;
            int indice = int.TryParse(Leer(raiz + "ff-index"), out int flujo) ? flujo : id;
            string? idioma = IdiomaDosLetras(Leer(raiz + "lang"));
            string? titulo = Leer(raiz + "title") is { Length: > 0 } t ? t : null;

            switch (Leer(raiz + "type"))
            {
                case "video" when idVideo == 0: // la primera (un archivo con carátula incrustada trae otra después)
                    idVideo = id;
                    video = (int.TryParse(Leer(raiz + "demux-w"), out int w) ? w : 0, int.TryParse(Leer(raiz + "demux-h"), out int h) ? h : 0,
                        Leer(raiz + "codec") is { Length: > 0 } c ? c : null);
                    break;
                case "audio":
                    audio.Add((id, new PistaAudioMotor(indice, idioma, titulo)));
                    break;
                case "sub":
                    string codec = Leer(raiz + "codec");
                    string? externa = Leer(raiz + "external") == "yes" && Leer(raiz + "external-filename") is { Length: > 0 } ruta ? ruta : null;
                    bool esImagen = codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";
                    subtitulos.Add((id, new PistaSubtitulosMotor(indice, idioma, titulo, esImagen, codec is "ass" or "ssa", externa)));
                    break;
            }
        }
        lock (_candado)
        {
            _audio = audio;
            _subtitulos = subtitulos;
        }
        (_anchoContenedor, _altoContenedor, _codecContenedor) = video;
        _idVideoMpv = idVideo;
    }

    public void Dispose()
    {
        if (_desechado) return;
        try { Desechando?.Invoke(); }
        catch (Exception ex) { AppLogger.Debug("MotorMpv", $"No se pudo soltar el pintor: {ex.Message}"); }
        _desechado = true;
        LibMpv.mpv_wakeup(_mpv); // el hilo de eventos sale de su espera y cierra libmpv
        if (Thread.CurrentThread != _hiloEventos) _hiloEventos.Join(TimeSpan.FromSeconds(5));
    }

    // Códigos de tres letras "bibliográficos" (ISO 639-2/B) que .NET no conoce por ese nombre y que muchos .mkv usan.
    private static readonly Dictionary<string, string> Bibliograficos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ger"] = "de", ["fre"] = "fr", ["chi"] = "zh", ["dut"] = "nl", ["gre"] = "el", ["cze"] = "cs", ["rum"] = "ro",
        ["per"] = "fa", ["may"] = "ms", ["ice"] = "is", ["slo"] = "sk", ["alb"] = "sq", ["arm"] = "hy", ["baq"] = "eu",
        ["bur"] = "my", ["geo"] = "ka", ["mac"] = "mk", ["mao"] = "mi", ["tib"] = "bo", ["wel"] = "cy"
    };

    private static readonly Lazy<Dictionary<string, string>> DeTresADos = new(() => CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(c => c.Name.Length > 0 && c.ThreeLetterISOLanguageName.Length == 3 && c.TwoLetterISOLanguageName.Length == 2)
        .GroupBy(c => c.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().TwoLetterISOLanguageName, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// El idioma de una pista tal como lo escribe el archivo ("spa", "jpn", "es-419", "ger"…) en dos letras, que es como lo compara
    /// la app con el idioma preferido. Null si no dice nada útil ("und", vacío, texto libre).
    /// </summary>
    public static string? IdiomaDosLetras(string? deLaPista)
    {
        string idioma = (deLaPista ?? string.Empty).Trim().ToLowerInvariant();
        int guion = idioma.IndexOf('-');
        if (guion > 0) idioma = idioma[..guion];

        if (idioma.Length == 2 && idioma.All(char.IsAsciiLetter)) return idioma;
        if (idioma.Length != 3) return null;
        if (Bibliograficos.TryGetValue(idioma, out string? bibliografico)) return bibliografico;
        return DeTresADos.Value.TryGetValue(idioma, out string? dos) ? dos : null;
    }
}
