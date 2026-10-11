using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Flyleaf detrás de <see cref="IMotorVideo"/>. Aquí vive todo lo que solo tiene sentido con Flyleaf: su configuración, el parche de
/// su lector de subtítulos, sus contadores privados de fotogramas y la lectura adelantada con un demuxer aparte.
/// </summary>
public sealed class MotorFlyleaf : IMotorVideo
{
    /// <summary>El reproductor de Flyleaf, para la vista clásica (su anfitrión de video y el menú de subtítulos se enlazan a él).</summary>
    public Player Player { get; }

    private INotifyPropertyChanged? _videoObservado;

    private MotorFlyleaf(Player player)
    {
        Player = player;
        player.OpenCompleted += (_, e) => AperturaTerminada?.Invoke(this,
            new AperturaTerminada(e.IsSubtitles ? ClaseApertura.Subtitulos : ClaseApertura.Video, e.Success, e.Error));
        _videoObservado = player.Video;
        if (_videoObservado != null) _videoObservado.PropertyChanged += Video_PropertyChanged;
    }

    /// <summary>Null bajo el ejecutor de pruebas (Flyleaf puede colgarse sin ventana) o si el motor no arranca en este equipo.</summary>
    public static MotorFlyleaf? Crear(AjustesInicialesMotor ajustes)
    {
        if (ViewModels.ReproductorViewModel.EsEntornoPruebas()) return null;

        try
        {
            MotorVideo.AsegurarIniciado();

            var config = new Config();

            // 1. Seeking rápido instantáneo por Keyframe (no frame-accurate) -> 0 ms seek latency
            if (config.Player != null)
            {
                config.Player.SeekAccurate = false;
                config.Player.AutoPlay = true;

                // El hilo de reproducción es el que alimenta el audio: con la ventana sin foco y
                // otras apps compitiendo por CPU, en prioridad Normal el sonido se entrecorta.
                config.Player.ThreadPriority = ThreadPriority.Highest;
                // Contador de fotogramas mostrados: con él se sabe cuándo aparece la imagen de verdad.
                config.Player.Stats = true;
            }

            // 2. Decoder multi-hilos para decodificación suave de AV1 y HEVC 10-bit
            if (config.Decoder != null)
            {
                // Todos los hilos: el AV1 va por software en gráficas sin soporte (Intel HD 620) y un salto preciso decodifica hasta
                // ~10 s de video; con 4 hilos en vez de 2 se decodifica un ~15 % más rápido (medido con ffmpeg: 231 → 267 fps).
                config.Decoder.VideoThreads = Math.Max(2, Environment.ProcessorCount);
            }

            // 2b. Decodificación por GPU (Direct3D) explícita: si falla, FlyleafLib cae solo a software.
            if (config.Video != null)
            {
                config.Video.VideoAcceleration = ajustes.AceleracionPorHardware;

                // Tarjeta gráfica elegida en Configuración (portátiles con integrada + dedicada: Windows suele dar la integrada).
                string? patronTarjeta = MotorVideo.PatronTarjeta(ajustes.TarjetaGrafica);
                if (patronTarjeta != null) config.Video.GPUAdapter = patronTarjeta;
            }

            // 3. Buffer de Demuxer en RAM (30 segundos precargados en memoria para reproducción sin tirones)
            if (config.Demuxer != null)
            {
                config.Demuxer.BufferDuration = 300_000_000L;
            }

            // 4. Subtítulos: Flyleaf arranca siempre con los suyos apagados para que no abra (ni lea) ninguna pista por su cuenta;
            // la app elige la pista y la lee ella misma.
            if (config.Subtitles != null)
            {
                config.Subtitles.Enabled = false;
            }

            // 5. Filtros de audio guardados: ecualizador y Modo Noche
            if (config.Audio != null && ajustes.FiltrosAudio.Count > 0)
            {
                config.Audio.Filters = AFiltrosDeFlyleaf(ajustes.FiltrosAudio);
            }

            // Red de seguridad para cuando Flyleaf sí decodifica una pista de texto: su lector de estilos ASS cerraba la app.
            ParcheSubtitulosFlyleaf.Aplicar();

            return new MotorFlyleaf(new Player(config));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MotorFlyleaf", $"Player nativo no disponible en este entorno: {ex.Message}");
            return null;
        }
    }

    public event EventHandler<AperturaTerminada>? AperturaTerminada;
    public event EventHandler? ProporcionVideoCambiada;

    public bool Desechado => Player.IsDisposed;

    public EstadoMotor Estado => Player.Status switch
    {
        Status.Opening => EstadoMotor.Abriendo,
        Status.Playing => EstadoMotor.Reproduciendo,
        Status.Paused => EstadoMotor.EnPausa,
        Status.Ended => EstadoMotor.Terminado,
        Status.Failed => EstadoMotor.Fallido,
        _ => EstadoMotor.Detenido
    };

    public TimeSpan Posicion => TimeSpan.FromTicks(Player.CurTime);
    public TimeSpan Duracion => TimeSpan.FromTicks(Player.Duration);

    public void Abrir(string ruta) => Player.OpenAsync(ruta);
    public void Reproducir() => Player.Play();
    public void Pausar() => Player.Pause();
    public void Detener() => Player.Stop();

    public void Saltar(TimeSpan destino, bool exacto)
    {
        if (exacto) Player.SeekAccurate((int)Math.Round(destino.TotalMilliseconds));
        else Player.CurTime = destino.Ticks;
    }

    public double Velocidad { set => Player.Speed = (float)value; }
    public int Volumen { set { if (Player.Audio != null) Player.Audio.Volume = value; } }
    public bool Silencio { set { if (Player.Audio != null) Player.Audio.Mute = value; } }

    // Contadores internos de Flyleaf que suben con CADA fotograma presentado (reproduciendo y en pausa/salto). El público
    // Video.FramesDisplayed solo se refresca una vez por segundo: esperar a él retrasaba hasta 1 s el destape del video.
    private static readonly System.Reflection.FieldInfo? CampoFotogramasReproducidos =
        typeof(Player).GetField("framesDisplayed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? CampoFotogramasMostradosEnPausa =
        typeof(Player).GetField("showFrameCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public long FotogramasMostrados
    {
        get
        {
            try
            {
                if (CampoFotogramasReproducidos != null && CampoFotogramasMostradosEnPausa != null)
                    return (uint)CampoFotogramasReproducidos.GetValue(Player)! + (long)(uint)CampoFotogramasMostradosEnPausa.GetValue(Player)!;
                return Player.Video?.FramesDisplayed ?? 0; // si una versión nueva de Flyleaf cambia los nombres: el contador público
            }
            catch { return 0; }
        }
    }

    public bool DecodificaPorHardware => Player.VideoDecoder?.VideoAccelerated ?? false;
    public bool AceleracionPorHardware { set => Player.Config.Video.VideoAcceleration = value; }
    public string? CodecVideo => Player.Video?.Codec;

    /// <summary>Justo al abrir, Player.Video aún vale 0x0 (se rellena con el primer fotograma): entonces se toma de la pista de video
    /// del contenedor, que ya está leída.</summary>
    public (int Ancho, int Alto) TamanoVideo
    {
        get
        {
            int ancho = Player.Video?.Width ?? 0, alto = Player.Video?.Height ?? 0;
            if (ancho > 0 && alto > 0) return (ancho, alto);

            var pista = Player.VideoDemuxer?.VideoStream ?? Player.VideoDemuxer?.VideoStreams?.FirstOrDefault();
            return pista == null ? (0, 0) : ((int)pista.Width, (int)pista.Height);
        }
    }

    public double ProporcionVideo => Player.Video?.AspectRatio.Value ?? 0;

    private void Video_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Video.AspectRatio)) ProporcionVideoCambiada?.Invoke(this, EventArgs.Empty);
    }

    public TarjetaGrafica? TarjetaEnUso
    {
        get
        {
            var tarjeta = Player.Renderer?.GPUAdapter;
            return tarjeta == null
                ? null
                : new TarjetaGrafica(tarjeta.Description, MotorVideo.FabricanteDe(tarjeta.Vendor), (long)((ulong)tarjeta.VideoMemory / (1024 * 1024)));
        }
    }

    public bool EscaladoInteligente
    {
        get => Player.Config.Video.SuperResolution;
        set { if (Player.Config.Video.SuperResolution != value) Player.Config.Video.SuperResolution = value; }
    }

    public BitmapSource? CapturarFotograma() => Player.TakeSnapshotToBitmapSource(0, 0);

    public IReadOnlyList<PistaAudioMotor> PistasAudio =>
        (Player.Audio?.Streams?.ToList() ?? []).Select(s => new PistaAudioMotor(s.StreamIndex, IdiomaDosLetras(s), s.Title)).ToList();

    public int IndicePistaAudioActual => Player.Audio?.StreamIndex ?? -1;

    public void ElegirAudio(PistaAudioMotor pista)
    {
        var nativa = Player.Audio?.Streams?.FirstOrDefault(s => s.StreamIndex == pista.Indice);
        if (nativa != null) Player.OpenAsync(nativa);
    }

    public IReadOnlyList<PistaSubtitulosMotor> PistasSubtitulos =>
        (Player.Subtitles?.Streams?.ToList() ?? []).Select(APista).ToList();

    public int IndicePistaSubtitulosDelMotor => Player.Subtitles?.StreamIndex ?? -1;

    public PistaSubtitulosMotor? PistaSubtitulosDe(object nativa) => nativa is SubtitlesStream s ? APista(s) : null;

    public void MostrarSubtitulosDelMotor(PistaSubtitulosMotor pista)
    {
        var nativa = Player.Subtitles?.Streams?.FirstOrDefault(s => s.StreamIndex == pista.Indice && s.ExternalStream?.Url == pista.RutaExterna);
        if (nativa == null) return;
        if (Player.Config?.Subtitles != null) Player.Config.Subtitles.Enabled = true;
        Player.OpenAsync(nativa);
    }

    public void ApagarSubtitulosDelMotor()
    {
        if (Player.Config?.Subtitles != null) Player.Config.Subtitles.Enabled = false;
    }

    // Las pistas de texto las lee y las pinta la app (ver ReproductorViewModel.AbrirPistaSubtitulos): aquí no hay estilo que aplicar.
    public bool DibujaSubtitulosDeTexto => false;
    public void AplicarEstiloSubtitulos(Models.EstiloSubtitulos estilo, bool tambienEnAss) { }

    private static PistaSubtitulosMotor APista(SubtitlesStream s) => new(
        s.StreamIndex, IdiomaDosLetras(s), s.Title, s.IsBitmap,
        s.CodecID is Flyleaf.FFmpeg.AVCodecID.Ass or Flyleaf.FFmpeg.AVCodecID.Ssa, s.ExternalStream?.Url);

    private static string? IdiomaDosLetras(StreamBase s)
    {
        try
        {
            string? idioma = s.Language?.TopCulture?.TwoLetterISOLanguageName;
            return idioma is "iv" ? null : idioma; // cultura invariante = sin idioma
        }
        catch { return null; }
    }

    private static List<Filter> AFiltrosDeFlyleaf(IReadOnlyList<FiltroAudio> filtros) =>
        filtros.Select(f => new Filter { Id = f.Id, Name = f.Nombre, Args = f.Argumentos }).ToList();

    public bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros)
    {
        if (Player.Config?.Audio == null) return true;
        Player.Config.Audio.Filters = AFiltrosDeFlyleaf(filtros);
        return Player.Config.Audio.ReloadFilters() >= 0;
    }

    public bool CambiarFiltroEnVivo(string id, string clave, string valor) =>
        Player.Config?.Audio == null || Player.Config.Audio.UpdateFilter(id, clave, valor) >= 0;

    /// <summary>
    /// Deja apuntada la cadena sin recargarla. El cambio en vivo solo toca el filtro que está sonando; esta lista es la que Flyleaf
    /// monta al abrir el siguiente episodio, y sin ponerla al día volvían las ganancias anteriores.
    /// </summary>
    public void RecordarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros)
    {
        if (Player.Config?.Audio != null) Player.Config.Audio.Filters = AFiltrosDeFlyleaf(filtros);
    }

    /// <summary>
    /// Abre un <see cref="Demuxer"/> APARTE del Player en reproducción contra el archivo y lo descarta enseguida: adelanta el sondeo del
    /// contenedor y calienta la caché de E/S de Windows para ese archivo. Nunca toca el Player, y cualquier fallo se descarta.
    /// </summary>
    public void PrecalentarArchivo(string ruta, CancellationToken ct) => _ = Task.Run(() =>
    {
        try
        {
            if (ct.IsCancellationRequested) return;

            var demuxer = new Demuxer(new Config().Demuxer, MediaType.Video);
            try
            {
                string error = demuxer.Open(ruta);
                if (!string.IsNullOrEmpty(error))
                    AppLogger.Debug("MotorFlyleaf", $"Precarga del siguiente episodio no pudo abrir '{ruta}': {error}");
            }
            finally
            {
                demuxer.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MotorFlyleaf", $"Precarga del siguiente episodio falló (no afecta la reproducción actual): {ex.Message}");
        }
    }, ct);

    public void Dispose()
    {
        if (_videoObservado != null) _videoObservado.PropertyChanged -= Video_PropertyChanged;
        _videoObservado = null;
        Player.Dispose();
    }
}
