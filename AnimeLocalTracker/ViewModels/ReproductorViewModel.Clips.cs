using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Clips;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Clips MP4/GIF: <c>I</c> marca el inicio, <c>O</c> el fin, y el panel sobre la barra de progreso crea el clip con un clic. Se
/// incrustan los subtítulos que se están viendo y se usa el audio que se está oyendo (ver docs/plan-clips-y-gif.md).
/// </summary>
public partial class ReproductorViewModel
{
    private readonly IClipService? _clipService;
    private double? _clipInicio;
    private double? _clipFin;

    [ObservableProperty] private bool _mostrarPanelClip;
    [ObservableProperty] private string _textoRangoClip = string.Empty;
    [ObservableProperty] private bool _mp4Disponible;
    [ObservableProperty] private bool _gifDisponible;
    [ObservableProperty] private bool _creandoClip;

    /// <summary>Solo con un archivo local: de un video en streaming no hay nada que recortar.</summary>
    private bool PuedeHacerClip => _clipService != null && !string.IsNullOrEmpty(_rutaVideo)
                                   && !_rutaVideo.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void MarcarInicioClip()
    {
        if (!PuedeHacerClip) return;
        _clipInicio = CurrentSeconds;
        _clipFin = null;
        Mp4Disponible = GifDisponible = false;
        TextoRangoClip = string.Format(LocalizationService.T("Player_ClipInicioFormato"), FormatearTiempo(CurrentSeconds, TotalSeconds), NombreTecla("MarcarFinClip"));
        MostrarPanelClip = true;
    }

    [RelayCommand]
    private void MarcarFinClip()
    {
        if (!PuedeHacerClip) return;
        if (_clipInicio is not double inicio)
        {
            AvisarEnReproductor(LocalizationService.T("Player_ClipTitulo"),
                string.Format(LocalizationService.T("Player_ClipMarcaInicioPrimeroFormato"), NombreTecla("MarcarInicioClip")), "ContentCut", "#2196F3");
            return;
        }

        _clipFin = CurrentSeconds;
        var rango = ArgumentosClip.RangoValido(inicio, CurrentSeconds, FormatoClip.Mp4);
        Mp4Disponible = rango != null;
        GifDisponible = ArgumentosClip.RangoValido(inicio, CurrentSeconds, FormatoClip.Gif) != null;
        TextoRangoClip = rango == null
            ? LocalizationService.T("Player_ClipMuyCorto")
            : string.Format(LocalizationService.T("Player_ClipRangoFormato"), FormatearTiempo(rango.InicioSeg, TotalSeconds),
                  FormatearTiempo(rango.InicioSeg + rango.DuracionSeg, TotalSeconds), Math.Round(rango.DuracionSeg))
              + (rango.Recortado ? " · " + LocalizationService.T("Player_ClipRecortado") : string.Empty);
        MostrarPanelClip = true;
    }

    [RelayCommand]
    private Task CrearClipMp4() => CrearClipAsync(FormatoClip.Mp4);

    [RelayCommand]
    private Task CrearClipGif() => CrearClipAsync(FormatoClip.Gif);

    [RelayCommand]
    private void DescartarClip() => ReiniciarClip();

    private void ReiniciarClip()
    {
        _clipInicio = _clipFin = null;
        Mp4Disponible = GifDisponible = MostrarPanelClip = false;
    }

    private async Task CrearClipAsync(FormatoClip formato)
    {
        if (_clipService == null || CreandoClip || _clipInicio is not double inicio || _clipFin is not double fin) return;
        if (ArgumentosClip.RangoValido(inicio, fin, formato) is not { } rango) return;

        var subtitulos = SubtitulosParaClip(out bool soloDeImagen);
        var solicitud = new SolicitudClip(_rutaVideo, rango.InicioSeg, rango.DuracionSeg, formato, Motor?.IndicePistaAudioActual ?? -1, subtitulos,
            Path.Combine(_clipService.Carpeta, ArgumentosClip.NombreArchivo(TituloAnime, _episodio, rango.InicioSeg, formato)));

        ReiniciarClip();
        CreandoClip = true;
        AvisarEnReproductor(LocalizationService.T("Player_ClipTitulo"), LocalizationService.T("Player_ClipCreando"), "ContentCut", "#2196F3");

        // Token propio, no el del reproductor: si se cierra mientras tanto, el clip termina igual y queda en la carpeta.
        string? ruta = await _clipService.CrearAsync(solicitud, CancellationToken.None);
        CreandoClip = false;
        if (_disposeHecho) return;

        if (ruta == null)
        {
            AvisarEnReproductor(LocalizationService.T("Player_ClipTitulo"), LocalizationService.T("Player_ClipError"), "AlertCircleOutline", "#EF4444");
            return;
        }

        CopiarAlPortapapeles(ruta);
        string mensaje = string.Format(LocalizationService.T("Player_ClipListoFormato"), ruta);
        if (soloDeImagen) mensaje += "\n" + LocalizationService.T("Player_ClipSinSubsImagen");
        AvisarEnReproductor(LocalizationService.T("Player_ClipTitulo"), mensaje, "ContentCut", "#4CAF50");
    }

    /// <summary>Los subtítulos que se ven en pantalla. Los de imagen (PGS, VobSub) no los dibuja el filtro de ffmpeg.</summary>
    private SubtitulosClip? SubtitulosParaClip(out bool soloDeImagen)
    {
        soloDeImagen = false;
        if (!SubtitulosHabilitados || _pistaTextoActual is not { } pista) return null;
        if (pista.EsImagen)
        {
            soloDeImagen = true;
            return null;
        }
        if (pista.RutaExterna != null) return new SubtitulosClip(pista.RutaExterna, null);
        return new SubtitulosClip(_rutaVideo, ArgumentosClip.IndiceRelativoSubtitulos(Motor?.PistasSubtitulos ?? Array.Empty<PistaSubtitulosMotor>(), pista));
    }

    /// <summary>Como archivo (no como ruta de texto): así Ctrl+V en Discord o en el Explorador pega el video.</summary>
    private static void CopiarAlPortapapeles(string ruta)
    {
        try
        {
            Clipboard.SetFileDropList(new StringCollection { ruta });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ThreadStateException)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudo copiar el clip al portapapeles: {ex.Message}");
        }
    }
}
