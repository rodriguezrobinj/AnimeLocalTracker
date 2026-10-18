using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.Services.Torrents;

/// <summary>
/// Cuadro "Descargar torrent" (ver docs/plan-torrents-arrastrar-magnet.md). Mismo patrón que <see cref="SelectorTorrentService"/>:
/// estado observable que <c>MainWindow.xaml</c> dibuja encima de toda la ventana.
/// </summary>
public interface IImportarTorrentService : INotifyPropertyChanged
{
    bool Visible { get; }
    bool ObteniendoInformacion { get; }
    /// <summary>Hay un cuadro o una espera de magnet abiertos: las entradas nuevas se ignoran.</summary>
    bool Ocupado { get; }
    string NombreTorrent { get; }
    IReadOnlyList<AnimeItem> Animes { get; }
    AnimeItem? AnimeElegido { get; set; }
    string TextoEpisodios { get; }
    string TextoDetalle { get; }
    string TextoCarpeta { get; }
    bool PuedeDescargar { get; }
    IAsyncRelayCommand DescargarCommand { get; }
    IRelayCommand CancelarCommand { get; }
    Task ImportarArchivoAsync(string ruta, int ignorados = 0);
    Task ImportarMagnetAsync(string enlace);
}

public partial class ImportarTorrentService : ObservableObject, IImportarTorrentService
{
    private readonly ImportadorTorrent _importador;
    private readonly IDatabaseService _db;
    private readonly IDownloadService _descargas;
    private readonly IDialogService _dialogos;
    private TorrentImportado? _torrent;
    private RepartoEpisodios? _reparto;
    private CancellationTokenSource? _ctsMagnet;
    private int _version;

    public ImportarTorrentService(ImportadorTorrent importador, IDatabaseService db, IDownloadService descargas, IDialogService dialogos)
    {
        _importador = importador;
        _db = db;
        _descargas = descargas;
        _dialogos = dialogos;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Ocupado))] private bool _visible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Ocupado))] private bool _obteniendoInformacion;
    [ObservableProperty] private string _nombreTorrent = string.Empty;
    [ObservableProperty] private IReadOnlyList<AnimeItem> _animes = Array.Empty<AnimeItem>();
    [ObservableProperty] private AnimeItem? _animeElegido;
    [ObservableProperty] private string _textoEpisodios = string.Empty;
    [ObservableProperty] private string _textoDetalle = string.Empty;
    [ObservableProperty] private string _textoCarpeta = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DescargarCommand))] private bool _puedeDescargar;

    public bool Ocupado => Visible || ObteniendoInformacion;

    /// <summary>El último recálculo de episodios (para que las pruebas puedan esperarlo).</summary>
    internal Task Recalculo { get; private set; } = Task.CompletedTask;

    public async Task ImportarArchivoAsync(string ruta, int ignorados = 0)
    {
        if (Ocupado) return;
        try
        {
            await MostrarAsync(await _importador.DesdeArchivoAsync(ruta, CancellationToken.None));
            if (ignorados > 0) _dialogos.MostrarToast(LocalizationService.T("Desc_ImportarTitulo"), LocalizationService.T("Desc_ImportarVariosArchivos"));
        }
        catch (ImportacionTorrentException ex)
        {
            AvisarError(ex.Message);
        }
        catch (Exception ex)
        {
            AvisarErrorInesperado(ex);
        }
    }

    public async Task ImportarMagnetAsync(string enlace)
    {
        if (Ocupado) return;
        ObteniendoInformacion = true;
        Core.Cancelacion.Reemplazar(ref _ctsMagnet);
        try
        {
            var torrent = await _importador.DesdeMagnetAsync(enlace, _ctsMagnet.Token);
            ObteniendoInformacion = false;
            await MostrarAsync(torrent);
        }
        catch (ImportacionTorrentException ex)
        {
            // Cancelado por el usuario: sin aviso.
            if (_ctsMagnet?.IsCancellationRequested != true) AvisarError(ex.Message);
        }
        catch (Exception ex)
        {
            AvisarErrorInesperado(ex);
        }
        finally
        {
            ObteniendoInformacion = false;
        }
    }

    private async Task MostrarAsync(TorrentImportado torrent)
    {
        if (AnalisisTorrentImportado.Repartir(torrent.Archivos, _ => false).Videos == 0)
        {
            AvisarError(LocalizationService.T("Desc_ImportarSinVideos"));
            return;
        }
        var animes = (await _db.ObtenerTodosLosAnimesAsync() ?? new List<AnimeItem>())
            .OrderBy(a => a.Titulo, StringComparer.CurrentCultureIgnoreCase).ToList();
        _torrent = torrent;
        NombreTorrent = torrent.Nombre;
        Animes = animes;
        AnimeElegido = AnalisisTorrentImportado.ReconocerAnime(torrent.Nombre, animes);
        Recalculo = RecalcularAsync(); // también si AnimeElegido no cambió (null → null)
        Visible = true;
        await Recalculo;
    }

    partial void OnAnimeElegidoChanged(AnimeItem? value) => Recalculo = RecalcularAsync();

    private async Task RecalcularAsync()
    {
        int version = ++_version;
        var anime = AnimeElegido;
        var torrent = _torrent;
        PuedeDescargar = false;
        if (anime == null || torrent == null)
        {
            _reparto = null;
            TextoEpisodios = TextoDetalle = TextoCarpeta = string.Empty;
            return;
        }

        var registros = await _db.ObtenerRegistrosPorAnimeAsync(anime.AniListId) ?? new List<RegistroEpisodio>();
        string carpeta = anime.RutaCarpeta ?? string.Empty;
        var reparto = await Task.Run(() =>
        {
            var enDisco = registros.Where(r => !string.IsNullOrEmpty(r.RutaArchivo) && File.Exists(r.RutaArchivo))
                .Select(r => r.NumeroEpisodio).ToHashSet();
            var enCarpeta = Directory.Exists(carpeta)
                ? Directory.EnumerateFiles(carpeta, "Episodio *.*").Where(AnalisisTorrentImportado.EsVideo)
                    .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f)["Episodio ".Length..], out int n) ? n : 0).ToHashSet()
                : new HashSet<int>();
            return AnalisisTorrentImportado.Repartir(torrent.Archivos,
                n => enDisco.Contains(n) || enCarpeta.Contains(n) || _descargas.EstaDescargando(anime.AniListId, n, out _));
        });
        if (version != _version) return;

        _reparto = reparto;
        TextoEpisodios = string.Format(LocalizationService.T("Desc_ImportarEpisodiosFormato"),
            AnalisisTorrentImportado.FormatearRangos(reparto.Todos), reparto.Videos);
        var detalle = new List<string>();
        if (reparto.YaEstan > 0) detalle.Add(string.Format(LocalizationService.T("Desc_ImportarYaTienesFormato"), reparto.YaEstan));
        if (reparto.SinNumero > 0) detalle.Add(string.Format(LocalizationService.T("Desc_ImportarSinNumeroFormato"), reparto.SinNumero));
        if (reparto.Nuevos.Count == 0) detalle.Add(LocalizationService.T("Desc_ImportarNingunoNuevo"));
        if (carpeta.Length == 0) detalle.Add(LocalizationService.T("Desc_ImportarSinCarpeta"));
        TextoDetalle = string.Join(" · ", detalle);
        TextoCarpeta = carpeta.Length == 0 ? string.Empty : string.Format(LocalizationService.T("Desc_ImportarCarpetaFormato"), carpeta);
        PuedeDescargar = carpeta.Length > 0 && reparto.Nuevos.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(PuedeDescargar))]
    private async Task DescargarAsync()
    {
        var anime = AnimeElegido;
        var torrent = _torrent;
        var reparto = _reparto;
        if (anime == null || torrent == null || reparto == null || !PuedeDescargar) return;

        PuedeDescargar = false;
        var candidato = new CandidatoTorrent(torrent.Nombre, torrent.Url, torrent.Hash, 0, torrent.Tamano, EsBatch: reparto.Videos > 1);
        var titulos = AnalisisTorrentImportado.TitulosDe(anime);
        foreach (int episodio in reparto.Nuevos)
            await _descargas.IniciarDescargaTorrentManualAsync(anime.AniListId, anime.Titulo, anime.RutaCarpeta, episodio, candidato, titulos);

        Cerrar();
        _dialogos.MostrarToast(LocalizationService.T("Desc_ImportarAnadidosTitulo"),
            string.Format(LocalizationService.T("Desc_ImportarAnadidosFormato"), reparto.Nuevos.Count), "CheckCircleOutline", "#4CAF50");
    }

    [RelayCommand]
    private void Cancelar()
    {
        _ctsMagnet?.Cancel();
        Cerrar();
    }

    private void Cerrar()
    {
        _version++;
        Visible = false;
        _torrent = null;
        _reparto = null;
    }

    /// <summary>Un fallo que no es del torrent (la base, un título raro de la biblioteca): al registro y al usuario, nunca en silencio.</summary>
    private void AvisarErrorInesperado(Exception ex)
    {
        AppLogger.Error("ImportarTorrentService", "Error inesperado al importar un torrent", ex);
        Cerrar();
        AvisarError(LocalizationService.T("Desc_ImportarErrorInesperado"));
    }

    private void AvisarError(string mensaje) =>
        _dialogos.MostrarToast(LocalizationService.T("Desc_ImportarErrorTitulo"), mensaje, "AlertCircleOutline", "#EF4444");
}
