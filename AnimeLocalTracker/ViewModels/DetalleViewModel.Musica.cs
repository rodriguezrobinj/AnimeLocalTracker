using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Un opening/ending en la lista de la Ficha, con su estado de descarga/reproducción local.</summary>
public partial class TemaAnimeItem : ObservableObject
{
    public required AnimeThemeInfo Info { get; init; }

    public string Slug => Info.Slug;
    public string Tipo => Info.Tipo;
    public string TituloCancion => string.IsNullOrWhiteSpace(Info.TituloCancion) ? Slug : Info.TituloCancion;
    public string Artistas => Info.Artistas;
    public string RangoTexto => string.IsNullOrWhiteSpace(Info.RangoEpisodios)
        ? LocalizationService.T("Det_MusicaTodosLosEpisodios")
        : string.Format(LocalizationService.T("Det_MusicaEpisodiosFormato"), Info.RangoEpisodios);

    [ObservableProperty] private bool _descargado;
    [ObservableProperty] private bool _descargando;
    [ObservableProperty] private bool _reproduciendo;
}

/// <summary>
/// Openings/endings del anime (AnimeThemes.moe): catálogo, descarga+conversión a mp3 y reproducción
/// de vista previa. Los mp3 ya descargados también alimentan la detección de saltos de OP/ED por
/// audio de referencia en SkipTimesCoordinator — descargar aquí "activa" ese salto más preciso.
/// </summary>
public partial class DetalleViewModel
{
    private readonly IAnimeThemesService? _animeThemesService;
    private readonly IAnimeThemesDownloadService? _animeThemesDownload;
    private MediaPlayer? _reproductorPreview;
    private TemaAnimeItem? _temaEnReproduccion;

    public ObservableCollection<TemaAnimeItem> TemasMusicales { get; } = [];

    [ObservableProperty] private bool _cargandoTemasMusicales;
    [ObservableProperty] private bool _mostrandoPanelMusica;

    public bool TieneTemasMusicales => TemasMusicales.Count > 0;

    [RelayCommand]
    private void ToggleMusica() => MostrandoPanelMusica = !MostrandoPanelMusica;

    internal async Task CargarTemasMusicalesAsync(CancellationToken cancellationToken = default)
    {
        var servicio = _animeThemesService;
        var descargas = _animeThemesDownload;
        var anime = AnimeSeleccionado;
        if (servicio == null || descargas == null || anime == null || cancellationToken.IsCancellationRequested) return;

        CargandoTemasMusicales = true;
        try
        {
            var temas = await servicio.ObtenerTemasAsync(anime.AniListId, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(anime, AnimeSeleccionado)) return;

            TemasMusicales.Clear();
            foreach (var tema in temas)
            {
                var item = new TemaAnimeItem { Info = tema };
                item.Descargado = descargas.EstaDescargado(anime.AniListId, tema);
                TemasMusicales.Add(item);
            }
            OnPropertyChanged(nameof(TieneTemasMusicales));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudieron cargar los openings/endings: {ex.Message}");
        }
        finally
        {
            CargandoTemasMusicales = false;
        }
    }

    [RelayCommand]
    private async Task DescargarTemaAsync(TemaAnimeItem? tema)
    {
        if (tema == null || tema.Descargando || _animeThemesDownload == null) return;
        var anime = AnimeSeleccionado;
        if (anime == null) return;

        tema.Descargando = true;
        try
        {
            string? ruta = await _animeThemesDownload.DescargarYConvertirAsync(anime.AniListId, tema.Info);
            tema.Descargado = ruta != null;
            if (ruta == null)
            {
                _dialogService.MostrarToast(
                    LocalizationService.T("Det_MusicaErrorDescargaTitulo"),
                    string.Format(LocalizationService.T("Det_MusicaErrorDescargaMsjFormato"), tema.TituloCancion),
                    "AlertCircleOutline", "#EF4444");
            }
        }
        finally
        {
            tema.Descargando = false;
        }
    }

    [RelayCommand]
    private void ReproducirTema(TemaAnimeItem? tema)
    {
        var anime = AnimeSeleccionado;
        if (tema == null || anime == null || !tema.Descargado || _animeThemesDownload == null) return;

        // Un solo preview a la vez: si ya sonaba este, actúa como Pausa/Play; si sonaba otro, lo corta.
        if (ReferenceEquals(_temaEnReproduccion, tema) && tema.Reproduciendo)
        {
            _reproductorPreview?.Pause();
            tema.Reproduciendo = false;
            return;
        }

        if (_temaEnReproduccion != null)
        {
            _temaEnReproduccion.Reproduciendo = false;
        }

        string ruta = _animeThemesDownload.ObtenerRutaLocalEsperada(anime.AniListId, tema.Info);

        try
        {
            if (_reproductorPreview == null)
            {
                _reproductorPreview = new MediaPlayer();
                _reproductorPreview.MediaEnded += (_, _) =>
                {
                    if (_temaEnReproduccion != null) _temaEnReproduccion.Reproduciendo = false;
                };
            }

            _reproductorPreview.Open(new Uri(ruta, UriKind.Absolute));
            _reproductorPreview.Play();
            _temaEnReproduccion = tema;
            tema.Reproduciendo = true;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudo reproducir '{tema.Slug}': {ex.Message}");
        }
    }

    [RelayCommand]
    private void EliminarTema(TemaAnimeItem? tema)
    {
        var anime = AnimeSeleccionado;
        if (tema == null || anime == null || _animeThemesDownload == null) return;

        if (ReferenceEquals(_temaEnReproduccion, tema))
        {
            _reproductorPreview?.Stop();
            tema.Reproduciendo = false;
            _temaEnReproduccion = null;
        }

        _animeThemesDownload.Eliminar(anime.AniListId, tema.Info);
        tema.Descargado = false;
    }

    /// <summary>Estado limpio al cargar otro anime: corta cualquier preview en curso.</summary>
    private void ReiniciarMusica()
    {
        _reproductorPreview?.Stop();
        _temaEnReproduccion = null;
        TemasMusicales.Clear();
        MostrandoPanelMusica = false;
        OnPropertyChanged(nameof(TieneTemasMusicales));
    }
}
