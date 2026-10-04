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

/// <summary>Subtítulos del reproductor: líneas solapadas (arriba/abajo) y dibujo de pistas ASS/SSA con su estilo original.</summary>
public partial class ReproductorViewModel
{
    // ── Subtítulos que se solapan: hasta 2 líneas a la vez (arriba/abajo), ver SubtitulosSolapadosResolver ──

    /// <summary>
    /// True cuando ya se extrajo la pista completa y se puede resolver el solape por nuestra cuenta; el code-behind
    /// usa esto para decidir si confía en <see cref="SubtituloLineaAbajo"/>/<see cref="SubtituloLineaArriba"/> o si
    /// sigue mostrando el texto único de Flyleaf (<c>Player.Subtitles.SubsText</c>) como respaldo — por ejemplo
    /// mientras la extracción todavía está en curso, o si falló (formato no soportado, archivo dañado, etc.).
    /// </summary>
    [ObservableProperty] private bool _subtitulosDobleLineaActivo;

    [ObservableProperty] private string _subtituloLineaAbajo = string.Empty;
    [ObservableProperty] private string _subtituloLineaArriba = string.Empty;

    private IReadOnlyList<Models.SubtitleCue> _subtitleCues = Array.Empty<Models.SubtitleCue>();
    private CancellationTokenSource? _subtitleCuesCts;

    /// <summary>Olvida la pista extraída (episodio nuevo o cambio de pista) y cancela la extracción que estuviera en curso.</summary>
    private void ReiniciarCuesSubtitulos()
    {
        Core.Cancelacion.Detener(ref _subtitleCuesCts);

        SubtitulosDobleLineaActivo = false;
        _subtitleCues = Array.Empty<Models.SubtitleCue>();
        SubtituloLineaAbajo = string.Empty;
        SubtituloLineaArriba = string.Empty;

        _pistaTextoActual = null;
        PistaActualEsAss = false;
        CerrarDibujoAss();
    }

    /// <summary>
    /// Muestra la pista elegida (sola al abrir el episodio, o por el usuario en el menú). Las pistas de texto incrustadas las lee
    /// la app por su cuenta y Flyleaf ni las abre: su lector de subtítulos ASS cierra la aplicación entera con ciertas etiquetas
    /// de color (caso real: el cartel del título de Re:Zero 4th Season, <c>{\c&amp;H..&amp;\fad(..)..\3a&amp;H37&amp;..}</c>), en un
    /// hilo suyo que no se puede proteger. Solo las pistas de imagen (PGS/VobSub), que la app no sabe dibujar, van por Flyleaf.
    /// </summary>
    private void AbrirPistaSubtitulos(object pista)
    {
        ReiniciarCuesSubtitulos();

        if (pista is FlyleafLib.MediaFramework.MediaStream.SubtitlesStream { IsBitmap: false, ExternalStream: null } texto)
        {
            _subtitleCoordinator.Deshabilitar(Player); // por si Flyleaf tenía abierta otra pista (de imagen)
            IniciarCargaCuesSubtitulos(_rutaVideo, texto.StreamIndex, null, texto);

            // Si además es ASS/SSA se prepara el dibujo con su estilo original; mientras tanto (y si falla) se ve el texto plano.
            _pistaTextoActual = texto;
            PistaActualEsAss = texto.CodecID is Flyleaf.FFmpeg.AVCodecID.Ass or Flyleaf.FFmpeg.AVCodecID.Ssa;
            IniciarDibujoAssSiCorresponde();
            return;
        }

        _subtitleCoordinator.SeleccionarPista(Player, pista);
    }

    /// <summary>
    /// Se llama cuando Flyleaf termina de abrir una pista de subtítulos (de imagen, externa o una de texto que la app no pudo
    /// leer). Se intenta extraer igualmente para resolver los solapes; hasta que termine (o si falla) se muestra el texto único
    /// de Flyleaf.
    /// </summary>
    private void CargarCuesDePistaDeFlyleaf()
    {
        ReiniciarCuesSubtitulos();

        var subtitulos = Player?.Subtitles;
        if (subtitulos == null || subtitulos.StreamIndex < 0) return;
        var stream = subtitulos.Streams?.FirstOrDefault(s => s.StreamIndex == subtitulos.StreamIndex);
        if (stream == null) return;

        // Una pista externa (.srt/.ass suelto junto al video) trae su ruta real en ExternalStream.Url; una pista
        // incrustada en el propio contenedor no tiene ExternalStream y se identifica por su índice de flujo.
        string? rutaExterna = stream.ExternalStream?.Url;
        int? streamIndexEmbebido = rutaExterna == null ? stream.StreamIndex : null;

        IniciarCargaCuesSubtitulos(_rutaVideo, streamIndexEmbebido, rutaExterna, null);
    }

    private void IniciarCargaCuesSubtitulos(string rutaVideo, int? streamIndexEmbebido, string? rutaExterna, object? pistaSinFlyleaf)
    {
        var cts = new CancellationTokenSource();
        _subtitleCuesCts = cts;

        _ = CargarCuesSubtitulosAsync(rutaVideo, streamIndexEmbebido, rutaExterna, pistaSinFlyleaf, cts);
    }

    /// <param name="pistaSinFlyleaf">Pista de texto que Flyleaf no tiene abierta: si la app no consigue leerla, se le entrega a
    /// Flyleaf como último recurso para no dejar el episodio sin subtítulos. Null si Flyleaf ya la está mostrando.</param>
    private async Task CargarCuesSubtitulosAsync(string rutaVideo, int? streamIndexEmbebido, string? rutaExterna, object? pistaSinFlyleaf, CancellationTokenSource cts)
    {
        try
        {
            var cues = await _subtitleCuesExtractor.ExtraerAsync(rutaVideo, streamIndexEmbebido, rutaExterna, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_subtitleCuesCts, cts)) return; // se abrió otra pista/video mientras tanto

            _subtitleCues = cues;
            SubtitulosDobleLineaActivo = cues.Count > 0;
            if (cues.Count > 0)
            {
                AppLogger.Debug("ReproductorViewModel", $"Pista de subtítulos {streamIndexEmbebido?.ToString() ?? "externa"} extraída: {cues.Count} líneas.");
            }
            else if (pistaSinFlyleaf != null && SubtitulosHabilitados)
            {
                AppLogger.Warn("ReproductorViewModel", $"No se pudo leer la pista de subtítulos {streamIndexEmbebido}: se deja en manos de Flyleaf.");
                _subtitleCoordinator.SeleccionarPista(Player, pistaSinFlyleaf);
            }
            else
            {
                AppLogger.Debug("ReproductorViewModel", "Extracción de subtítulos sin resultado: se sigue mostrando lo que dé Flyleaf.");
            }
        }
        catch (OperationCanceledException)
        {
            // Se canceló porque se abrió otra pista/video: nada que hacer, el nuevo pedido ya está en curso.
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudo extraer la pista de subtítulos: {ex.Message}");
        }
    }

    /// <summary>Se llama en el mismo sondeo de progreso (cada 250 ms mientras reproduce): resuelve qué línea va
    /// arriba y cuál abajo para el instante actual. No hace nada si no hay pista extraída (ver <see cref="SubtitulosDobleLineaActivo"/>).</summary>
    private void ActualizarLineasSubtitulosSolapados(double curSeconds)
    {
        if (!SubtitulosHabilitados || !SubtitulosDobleLineaActivo)
        {
            return;
        }

        var (abajo, arriba) = Core.SubtitulosSolapadosResolver.Resolver(_subtitleCues, TimeSpan.FromSeconds(curSeconds));
        SubtituloLineaAbajo = abajo ?? string.Empty;
        SubtituloLineaArriba = arriba ?? string.Empty;
    }

    // ── Subtítulos ASS/SSA con su estilo original (ver docs/investigacion-subtitulos-ass.md) ──

    /// <summary>La vista le pide los fotogramas; el ViewModel decide cuándo se abre y se cierra.</summary>
    public ISubtitleAssRenderer DibujanteAss => _assRenderer;

    /// <summary>El dibujante está listo y manda sobre el texto plano: la vista muestra la capa de imagen solo con esto en true.</summary>
    [ObservableProperty] private bool _subtitulosAssActivo;

    /// <summary>La pista elegida es ASS/SSA: el menú de subtítulos ofrece "Usar mi estilo".</summary>
    [ObservableProperty] private bool _pistaActualEsAss;

    private CancellationTokenSource? _assCts;
    private bool _assListo;
    private FlyleafLib.MediaFramework.MediaStream.SubtitlesStream? _pistaTextoActual;

    private bool _usarMiEstiloEnAss;
    /// <summary>
    /// Con una pista ASS, ver el texto plano con el estilo de Configuración en vez del original del subtítulo. Se guarda como
    /// preferencia. El dibujante no se cierra al encenderlo: así volver al estilo original es inmediato.
    /// </summary>
    public bool UsarMiEstiloEnAss
    {
        get => _usarMiEstiloEnAss;
        set
        {
            if (!SetProperty(ref _usarMiEstiloEnAss, value)) return;

            GuardarUsarMiEstiloEnAss(value);
            if (value) SubtitulosAssActivo = false;
            else if (_assListo) SubtitulosAssActivo = true;
            else IniciarDibujoAssSiCorresponde();
        }
    }

    [RelayCommand]
    private void ToggleUsarMiEstiloEnAss() => UsarMiEstiloEnAss = !UsarMiEstiloEnAss;

    private void GuardarUsarMiEstiloEnAss(bool valor)
    {
        if (_settingsService == null) return;
        var config = _settingsService.ObtenerConfiguracion();
        if (config == null || config.UsarMiEstiloEnAss == valor) return;
        config.UsarMiEstiloEnAss = valor;
        _ = _settingsService.GuardarConfiguracionAsync(config);
    }

    /// <summary>Abre el dibujante para la pista de texto actual si es ASS/SSA incrustada y el usuario no pidió su estilo.</summary>
    private void IniciarDibujoAssSiCorresponde()
    {
        var pista = _pistaTextoActual;
        var player = Player;
        if (pista == null || player == null) return;

        bool esAss = pista.CodecID is Flyleaf.FFmpeg.AVCodecID.Ass or Flyleaf.FFmpeg.AVCodecID.Ssa;
        if (!Core.SubtitulosAss.DebeDibujarse(esAss, pista.IsBitmap, pista.ExternalStream != null, UsarMiEstiloEnAss)) return;

        // ponytail: pantalla principal en unidades de WPF. Con la escala de Windows por encima del 100 % se dibuja algo por
        // debajo de los píxeles reales; si se nota borroso, usar los píxeles del monitor donde está la ventana.
        var (anchoVideo, altoVideo) = TamanoDelVideo(player);
        var (ancho, alto) = Core.SubtitulosAss.TamanoDibujo(
            anchoVideo, altoVideo,
            (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
        int indice = Core.SubtitulosAss.IndiceEntreSubtitulos(
            player.Subtitles.Streams.Where(s => s.ExternalStream == null).Select(s => s.StreamIndex), pista.StreamIndex);
        if (ancho == 0 || indice < 0)
        {
            AppLogger.Debug("ReproductorViewModel", $"Dibujo ASS no disponible (tamaño {ancho}x{alto}, pista {indice}): se queda el texto plano.");
            return;
        }

        IniciarDibujoAss(_rutaVideo, indice, ancho, alto);
    }

    /// <summary>Tamaño del video abierto. Justo al abrir (cuando se elige la pista de subtítulos) Player.Video aún vale 0x0, porque
    /// se rellena con el primer fotograma: entonces se toma de la pista de video del contenedor, que ya está leída.</summary>
    private static (int Ancho, int Alto) TamanoDelVideo(Player player)
    {
        int ancho = player.Video?.Width ?? 0, alto = player.Video?.Height ?? 0;
        if (ancho > 0 && alto > 0) return (ancho, alto);

        var pista = player.VideoDemuxer?.VideoStream ?? player.VideoDemuxer?.VideoStreams?.FirstOrDefault();
        return pista == null ? (0, 0) : ((int)pista.Width, (int)pista.Height);
    }

    internal void IniciarDibujoAss(string ruta, int indice, int ancho, int alto)
    {
        CerrarDibujoAss();

        var cts = new CancellationTokenSource();
        _assCts = cts;
        _ = AbrirDibujoAssAsync(ruta, indice, ancho, alto, cts);
    }

    private async Task AbrirDibujoAssAsync(string ruta, int indice, int ancho, int alto, CancellationTokenSource cts)
    {
        try
        {
            var reloj = Stopwatch.StartNew();
            bool listo = await _assRenderer.AbrirAsync(ruta, indice, ancho, alto, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_assCts, cts)) return; // se abrió otra pista/video mientras tanto

            AppLogger.Debug("ReproductorViewModel", $"[Perf] Dibujo ASS {(listo ? "listo" : "no disponible")} en {reloj.ElapsedMilliseconds} ms ({ancho}x{alto}, pista {indice}).");
            _assListo = listo;
            SubtitulosAssActivo = listo && !UsarMiEstiloEnAss;
        }
        catch (OperationCanceledException)
        {
            // Se abrió otra pista/video: el nuevo pedido ya está en curso.
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ReproductorViewModel", $"No se pudo preparar el dibujo ASS: {ex.Message}");
        }
    }

    internal void CerrarDibujoAss()
    {
        Core.Cancelacion.Detener(ref _assCts);

        _assListo = false;
        SubtitulosAssActivo = false;
        _assRenderer.Cerrar();
    }

    /// <summary>La vista avisa de que un fotograma falló: ese episodio sigue en texto plano, sin reintentos.</summary>
    public void NotificarFalloDibujoAss()
    {
        AppLogger.Warn("ReproductorViewModel", "El dibujo ASS falló a mitad de episodio: se vuelve al texto plano.");
        CerrarDibujoAss();
    }
}
