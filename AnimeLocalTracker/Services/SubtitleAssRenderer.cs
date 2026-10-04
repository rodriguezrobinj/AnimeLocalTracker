using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using Flyleaf.FFmpeg;
using static Flyleaf.FFmpeg.Raw;

// Los structs de los bindings de FFmpeg (AVFrame, AVFilterContext…) cuentan como "administrados" para el compilador, pero
// aquí solo se usan a través de punteros a memoria que reserva y libera el propio FFmpeg, igual que hace FlyleafLib.
#pragma warning disable CS8500

namespace AnimeLocalTracker.Services;

/// <summary>
/// Dibuja una pista de subtítulos ASS/SSA con su estilo original (posición, colores, fuentes, movimiento) y devuelve,
/// para un instante, un fotograma de fondo transparente listo para pintar sobre el video.
/// Una sola pista abierta a la vez. Ver docs/investigacion-subtitulos-ass.md.
/// </summary>
public interface ISubtitleAssRenderer
{
    /// <summary>Tamaño del fotograma de la pista abierta; 0 si no hay ninguna.</summary>
    int Ancho { get; }
    int Alto { get; }

    /// <summary>
    /// Lee la pista y sus fuentes adjuntas. Puede tardar (lee el archivo): el trabajo va en un hilo de fondo.
    /// <paramref name="indiceEntreSubtitulos"/> cuenta solo las pistas de subtítulos (ver SubtitulosAss.IndiceEntreSubtitulos);
    /// negativo = la primera. False si no se pudo: el llamador sigue con el texto plano.
    /// </summary>
    Task<bool> AbrirAsync(string rutaArchivo, int indiceEntreSubtitulos, int ancho, int alto, CancellationToken ct);

    /// <summary>
    /// Dibuja el instante en <paramref name="destinoBgra"/> (Ancho × Alto × 4 bytes, BGRA premultiplicado = Pbgra32).
    /// El búfer debe ser el mismo de una llamada a otra: solo se reescriben las filas que cambian, y esa franja se devuelve en
    /// <paramref name="filaInicial"/>/<paramref name="filas"/> para repintar solo eso (0 filas = no cambió nada). Un búfer
    /// distinto se limpia entero. False si no hay pista o falla.
    /// </summary>
    bool Renderizar(TimeSpan instante, byte[] destinoBgra, out int filaInicial, out int filas);

    void Cerrar();
}

/// <summary>
/// Usa el libass que ya viene dentro del avfilter de la app, a través del filtro <c>subtitles</c>. Ese filtro solo sabe pintar
/// sobre un fotograma opaco (su modo con canal alfa calcula mal la transparencia parcial), así que cada instante se dibuja dos
/// veces, sobre blanco y sobre negro: la diferencia entre ambas da la transparencia exacta y lo pintado sobre negro es ya el
/// color premultiplicado. Mediciones y motivos en la sección 7.1 del documento de diseño.
/// </summary>
public sealed unsafe class SubtitleAssRenderer : ISubtitleAssRenderer
{
    private readonly object _candado = new();
    private int _generacion;
    private AVFilterGraph* _grafo;
    private AVFilterContext* _origen;
    private AVFilterContext* _destino;
    private AVFrame* _entrada;
    private AVFrame* _sobreBlanco;
    private AVFrame* _sobreNegro;

    private bool _rangoComprimido;
    private byte[]? _ultimoBufer;
    private int _pintadaDesde = -1, _pintadaHasta = -1;

    public int Ancho { get; private set; }
    public int Alto { get; private set; }

    /// <summary>Solo para las pruebas automáticas: en la app las bibliotecas de FFmpeg ya las ha cargado Flyleaf.</summary>
    internal static void CargarBibliotecas(string carpetaFFmpeg) => LoadLibraries(carpetaFFmpeg, LoadProfile.Filters, false);

    public Task<bool> AbrirAsync(string rutaArchivo, int indiceEntreSubtitulos, int ancho, int alto, CancellationToken ct)
    {
        int generacion;
        lock (_candado)
        {
            CerrarSinCandado();
            generacion = ++_generacion;
        }

        if (string.IsNullOrWhiteSpace(rutaArchivo) || !File.Exists(rutaArchivo) || ancho <= 0 || alto <= 0) return Task.FromResult(false);
        return Task.Run(() => Abrir(rutaArchivo, indiceEntreSubtitulos, ancho, alto, generacion, ct));
    }

    /// <summary>El grafo se monta SIN el candado (leer el archivo tarda y Cerrar no debe esperar a eso) y solo al final se
    /// entrega, si entretanto no se pidió otra pista ni se cerró.</summary>
    private bool Abrir(string ruta, int indice, int ancho, int alto, int generacion, CancellationToken ct)
    {
        AVFilterGraph* grafo = null;
        try
        {
            bool rangoComprimido = LeerRangoComprimido(ruta, indice);

            grafo = avfilter_graph_alloc();
            if (grafo == null) return false;

            AVFilterContext* origen = null, destino = null;
            string formato = $"video_size={ancho}x{alto}:pix_fmt={(int)AVPixelFormat.Bgra}:time_base=1/1000:pixel_aspect=1/1";
            if (avfilter_graph_create_filter(&origen, avfilter_get_by_name("buffer"), "entrada", formato, null, grafo) < 0) return false;

            // Las opciones se fijan una a una y no en una cadena de argumentos: así la ruta no necesita escapar ':' , '\' ni comillas.
            AVFilterContext* subtitulos = avfilter_graph_alloc_filter(grafo, avfilter_get_by_name("subtitles"), "subtitulos");
            if (subtitulos == null) return false;
            if (av_opt_set(subtitulos, "filename", ruta, OptSearchFlags.Children) < 0) return false;
            if (indice >= 0 && av_opt_set_int(subtitulos, "stream_index", indice, OptSearchFlags.Children) < 0) return false;
            if (avfilter_init_str(subtitulos, null) < 0) return false; // aquí se lee la pista y sus fuentes: es lo lento

            if (avfilter_graph_create_filter(&destino, avfilter_get_by_name("buffersink"), "salida", null, null, grafo) < 0) return false;
            if (avfilter_link(origen, 0, subtitulos, 0) < 0 || avfilter_link(subtitulos, 0, destino, 0) < 0) return false;
            if (avfilter_graph_config(grafo, null) < 0) return false;

            lock (_candado)
            {
                if (ct.IsCancellationRequested || generacion != _generacion) return false;

                _grafo = grafo;
                _origen = origen;
                _destino = destino;
                _entrada = av_frame_alloc();
                _sobreBlanco = av_frame_alloc();
                _sobreNegro = av_frame_alloc();
                _rangoComprimido = rangoComprimido;
                Ancho = ancho;
                Alto = alto;
                grafo = null; // ya tiene dueño: el finally no debe liberarlo
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SubtitleAssRenderer", $"No se pudo preparar el dibujo ASS de '{ruta}': {ex.Message}");
            return false;
        }
        finally
        {
            if (grafo != null) avfilter_graph_free(&grafo);
        }
    }

    /// <summary>Lee de la cabecera del guion (guardada con la pista en el contenedor, no hace falta leer el archivo entero) si
    /// ffmpeg va a pintar sus colores comprimidos a 16–235. Ante la duda, true: es lo que ffmpeg asume.</summary>
    private static bool LeerRangoComprimido(string ruta, int indice)
    {
        AVFormatContext* contenedor = null;
        try
        {
            if (avformat_open_input(&contenedor, ruta, null, null) < 0) return true;

            int vistas = 0;
            for (uint i = 0; i < contenedor->nb_streams; i++)
            {
                AVCodecParameters* pista = contenedor->streams[i]->codecpar;
                if (pista->codec_type != AVMediaType.Subtitle) continue;
                if (indice >= 0 && vistas++ != indice) continue;

                string? cabecera = pista->extradata == null || pista->extradata_size <= 0
                    ? null
                    : Encoding.UTF8.GetString(pista->extradata, pista->extradata_size);
                return SubtitulosAss.RangoComprimido(cabecera);
            }
            return true;
        }
        finally
        {
            if (contenedor != null) avformat_close_input(&contenedor);
        }
    }

    public bool Renderizar(TimeSpan instante, byte[] destinoBgra, out int filaInicial, out int filas)
    {
        filaInicial = 0;
        filas = 0;

        lock (_candado)
        {
            if (_grafo == null || destinoBgra == null || destinoBgra.Length < Ancho * Alto * 4) return false;

            int fila = Ancho * 4;
            long pts = (long)instante.TotalMilliseconds;

            // Lo que quedó pintado en la llamada anterior se borra; si el búfer es otro, no se sabe qué trae y se limpia entero.
            int previaDesde = _pintadaDesde, previaHasta = _pintadaHasta;
            if (!ReferenceEquals(destinoBgra, _ultimoBufer))
            {
                Array.Clear(destinoBgra, 0, Alto * fila);
                previaDesde = 0;
                previaHasta = Alto - 1;
            }
            else if (previaDesde >= 0)
            {
                Array.Clear(destinoBgra, previaDesde * fila, (previaHasta - previaDesde + 1) * fila);
            }
            _ultimoBufer = null; // si algo falla a medias, la próxima llamada limpia el búfer entero
            _pintadaDesde = _pintadaHasta = -1;

            try
            {
                // Dos pasadas, sobre blanco y sobre negro. Una fila tiene subtítulo si cambió en cualquiera de las dos: mirar solo
                // una no basta (un cartel blanco puro no se distingue sobre blanco, ni uno negro sobre negro).
                if (!Pasada(pts, 255, _sobreBlanco) || !Pasada(pts, 0, _sobreNegro)) return false;
                byte* blanco = (byte*)_sobreBlanco->data[0], negro = (byte*)_sobreNegro->data[0];
                int pasoBlanco = _sobreBlanco->linesize[0], pasoNegro = _sobreNegro->linesize[0];

                int desde = -1, hasta = -1;
                for (int y = 0; y < Alto; y++)
                {
                    // En la pasada negra el cuarto byte (alfa, que el filtro no toca) sigue a 0, igual que el fondo.
                    if (new ReadOnlySpan<byte>(blanco + y * pasoBlanco, fila).IndexOfAnyExcept((byte)255) < 0
                        && new ReadOnlySpan<byte>(negro + y * pasoNegro, fila).IndexOfAnyExcept((byte)0) < 0) continue;
                    if (desde < 0) desde = y;
                    hasta = y;
                }

                if (desde >= 0)
                {
                    fixed (byte* destino = destinoBgra)
                    {
                        for (int y = desde; y <= hasta; y++)
                            CombinarFila(negro + y * pasoNegro, blanco + y * pasoBlanco, destino + y * fila, fila, _rangoComprimido);
                    }
                }

                _ultimoBufer = destinoBgra;
                _pintadaDesde = desde;
                _pintadaHasta = hasta;

                int cambioDesde = previaDesde < 0 ? desde : (desde < 0 ? previaDesde : Math.Min(previaDesde, desde));
                int cambioHasta = Math.Max(previaHasta, hasta);
                if (cambioDesde >= 0)
                {
                    filaInicial = cambioDesde;
                    filas = cambioHasta - cambioDesde + 1;
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("SubtitleAssRenderer", $"Fallo al dibujar el instante {instante}: {ex.Message}");
                return false;
            }
            finally
            {
                av_frame_unref(_entrada);
                av_frame_unref(_sobreBlanco);
                av_frame_unref(_sobreNegro);
            }
        }
    }

    /// <summary>Envía al filtro un fotograma liso del color dado con la marca de tiempo pedida y recoge el resultado.</summary>
    private bool Pasada(long pts, byte fondo, AVFrame* resultado)
    {
        _entrada->format = (int)AVPixelFormat.Bgra;
        _entrada->width = Ancho;
        _entrada->height = Alto;
        if (av_frame_get_buffer(_entrada, 0) < 0) return false;
        new Span<byte>((void*)_entrada->data[0], _entrada->linesize[0] * Alto).Fill(fondo);
        _entrada->pts = pts;

        return av_buffersrc_add_frame(_origen, _entrada) >= 0 && av_buffersink_get_frame(_destino, resultado) >= 0;
    }

    /// <summary>
    /// Por píxel: alfa = 255 − (sobre blanco − sobre negro); color = lo pintado sobre negro (ya premultiplicado), estirado a
    /// 0–255 si ffmpeg lo pintó en rango 16–235. El color nunca supera al alfa (premultiplicado válido para WPF).
    /// </summary>
    private static void CombinarFila(byte* sobreNegro, byte* sobreBlanco, byte* destino, int bytes, bool rangoComprimido)
    {
        for (int x = 0; x < bytes; x += 4)
        {
            int diferencia = sobreBlanco[x + 1] - sobreNegro[x + 1];
            int alfa = diferencia >= 255 ? 0 : diferencia <= 0 ? 255 : 255 - diferencia;
            if (alfa == 0)
            {
                *(uint*)(destino + x) = 0;
                continue;
            }

            destino[x] = Color(sobreNegro[x], alfa, rangoComprimido);
            destino[x + 1] = Color(sobreNegro[x + 1], alfa, rangoComprimido);
            destino[x + 2] = Color(sobreNegro[x + 2], alfa, rangoComprimido);
            destino[x + 3] = (byte)alfa;
        }
    }

    private static byte Color(int valor, int alfa, bool rangoComprimido)
    {
        if (rangoComprimido) valor = (valor * 255 - 16 * alfa) / 219;
        return (byte)(valor < 0 ? 0 : valor > alfa ? alfa : valor);
    }

    public void Cerrar()
    {
        lock (_candado)
        {
            _generacion++; // una apertura en curso ya no debe entregar su grafo
            CerrarSinCandado();
        }
    }

    private void CerrarSinCandado()
    {
        if (_grafo == null) return;

        AVFrame* entrada = _entrada, sobreBlanco = _sobreBlanco, sobreNegro = _sobreNegro;
        av_frame_free(&entrada);
        av_frame_free(&sobreBlanco);
        av_frame_free(&sobreNegro);
        AVFilterGraph* grafo = _grafo;
        avfilter_graph_free(&grafo);

        _entrada = null;
        _sobreBlanco = null;
        _sobreNegro = null;
        _grafo = null;
        _origen = null;
        _destino = null;
        _ultimoBufer = null;
        _pintadaDesde = _pintadaHasta = -1;
        Ancho = 0;
        Alto = 0;
    }
}
