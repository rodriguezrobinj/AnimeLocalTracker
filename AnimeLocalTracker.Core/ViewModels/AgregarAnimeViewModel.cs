using AnimeLocalTracker.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Core.Messages;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;

namespace AnimeLocalTracker.Core.ViewModels;

public partial class AgregarAnimeViewModel : ObservableObject,
    IRecipient<AnimeAñadidoMensaje>
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly ISettingsService _settingsService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private ObservableCollection<AnimeBusquedaItem> _resultados = [];

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _busquedaSinResultados;

    [ObservableProperty]
    private bool _mostrandoTendencias = true;

    [ObservableProperty]
    private string _tituloSeccion = "Tendencias de la temporada";

    private string _textoBusqueda = string.Empty;
    public string TextoBusqueda
    {
        get => _textoBusqueda;
        set
        {
            if (SetProperty(ref _textoBusqueda, value))
            {
                OnPropertyChanged(nameof(TieneTextoBusqueda));
                BusquedaSinResultados = false;
                EjecutarBusquedaEnVivo(value);
            }
        }
    }

    public bool TieneTextoBusqueda => !string.IsNullOrWhiteSpace(TextoBusqueda);

    private CancellationTokenSource? _searchCts;
    private HashSet<int> _animesEnBibliotecaIds = [];

    public AgregarAnimeViewModel(
        IAnimeTrackingService animeTrackingService,
        IDatabaseService databaseService,
        ISettingsService settingsService,
        IDialogService dialogService)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _settingsService = settingsService;
        _dialogService = dialogService;

        // Registro vía IRecipient<AnimeAñadidoMensaje>: una sola suscripción.
        // (Antes había un lambda + Receive() duplicando la misma lógica.)
        WeakReferenceMessenger.Default.Register<AnimeAñadidoMensaje>(this);

        _ = CargarInicialAsync();
    }

    public async Task CargarInicialAsync()
    {
        await ActualizarCacheBibliotecaAsync();
        await CargarTendenciasAsync();
    }

    public async Task ActualizarCacheBibliotecaAsync()
    {
        try
        {
            var animes = await _databaseService.ObtenerTodosLosAnimesAsync();
            _animesEnBibliotecaIds = new HashSet<int>(animes.Select(a => a.AniListId));
            ActualizarEstadoVisualBiblioteca();
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", "Error al actualizar caché de biblioteca", ex);
        }
    }

    private void ActualizarEstadoVisualBiblioteca()
    {
        foreach (var item in Resultados)
        {
            item.EstaEnBiblioteca = _animesEnBibliotecaIds.Contains(item.Media.Id);
        }
    }

    [RelayCommand]
    public async Task CargarTendenciasAsync()
    {
        CancelarBusquedaPendiente();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            IsSearching = true;
            MostrandoTendencias = true;
            TituloSeccion = "Tendencias de la temporada";
            BusquedaSinResultados = false;

            var tendencias = await _animeTrackingService.ObtenerAnimesTendenciaAsync(cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            Resultados.Clear();
            foreach (var media in tendencias)
            {
                Resultados.Add(new AnimeBusquedaItem
                {
                    Media = media,
                    EstaEnBiblioteca = _animesEnBibliotecaIds.Contains(media.Id)
                });
            }

            BusquedaSinResultados = Resultados.Count == 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", "Error cargando tendencias", ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
            }
            cts.Dispose();
        }
    }

    private async void EjecutarBusquedaEnVivo(string busqueda)
    {
        if (string.IsNullOrWhiteSpace(busqueda) || busqueda.Trim().Length < 2)
        {
            _ = CargarTendenciasAsync();
            return;
        }

        CancelarBusquedaPendiente();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        try
        {
            IsSearching = true;
            MostrandoTendencias = false;
            TituloSeccion = $"Resultados para \"{busqueda.Trim()}\"";
            BusquedaSinResultados = false;

            await Task.Delay(350, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            var resultados = await _animeTrackingService.BuscarAnimesEnVivoAsync(busqueda.Trim(), cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            Resultados.Clear();
            foreach (var media in resultados)
            {
                Resultados.Add(new AnimeBusquedaItem
                {
                    Media = media,
                    EstaEnBiblioteca = _animesEnBibliotecaIds.Contains(media.Id)
                });
            }

            BusquedaSinResultados = Resultados.Count == 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", $"Error buscando animes para '{busqueda}'", ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
            }
            cts.Dispose();
        }
    }

    [RelayCommand]
    public void LimpiarBusqueda()
    {
        TextoBusqueda = string.Empty;
    }

    [RelayCommand]
    public async Task AñadirAnimeAsync(AnimeBusquedaItem? item)
    {
        if (item?.Media == null || item.EstaGuardando) return;

        var animeAPI = item.Media;
        string titulo = item.TituloPrincipal;

        try
        {
            item.EstaGuardando = true;

            // Validar si ya existe en la biblioteca
            var animesGuardados = await _databaseService.ObtenerTodosLosAnimesAsync();
            var animeExistente = animesGuardados.FirstOrDefault(a => a.AniListId == animeAPI.Id);
            if (animeExistente != null)
            {
                item.EstaEnBiblioteca = true;
                _animesEnBibliotecaIds.Add(animeAPI.Id);
                await _dialogService.MostrarDialogoAsync(
                    "Anime Existente",
                    $"El anime '{titulo}' ya se encuentra en tu biblioteca.",
                    false,
                    "InformationOutline",
                    "#FF9800");
                return;
            }

            string nombreSeguro = string.Join("_", titulo.Split(Path.GetInvalidFileNameChars()));
            string rutaBaseVideos = _settingsService.ObtenerRutaBaseAnimes();
            string nuevaRutaCarpeta = Path.Combine(rutaBaseVideos, nombreSeguro);

            if (!Directory.Exists(nuevaRutaCarpeta))
            {
                Directory.CreateDirectory(nuevaRutaCarpeta);
            }

            int episodiosEmitidos = 0;
            string estadoAnime = animeAPI.Status?.ToUpperInvariant() ?? "UNKNOWN";

            if (estadoAnime == "NOT_YET_RELEASED")
            {
                episodiosEmitidos = 0;
            }
            else if (estadoAnime == "RELEASING")
            {
                if (animeAPI.NextAiringEpisode != null)
                {
                    episodiosEmitidos = Math.Max(0, animeAPI.NextAiringEpisode.Episode - 1);
                }
                else
                {
                    episodiosEmitidos = animeAPI.Episodes ?? 0;
                }
            }
            else // FINISHED, etc.
            {
                episodiosEmitidos = animeAPI.Episodes ?? 0;
            }

            var titulosAlt = new List<string>();
            if (!string.IsNullOrWhiteSpace(animeAPI.Title?.English)) titulosAlt.Add(animeAPI.Title.English);
            if (!string.IsNullOrWhiteSpace(animeAPI.Title?.UserPreferred) && animeAPI.Title.UserPreferred != titulo) titulosAlt.Add(animeAPI.Title.UserPreferred);
            if (animeAPI.Synonyms != null) titulosAlt.AddRange(animeAPI.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));

            var nuevoAnime = new AnimeItem
            {
                AniListId = animeAPI.Id,
                MalId = animeAPI.IdMal,
                Titulo = titulo,
                NombresAlternativos = string.Join(" | ", titulosAlt.Distinct()),
                Sinopsis = animeAPI.Description ?? string.Empty,
                Generos = animeAPI.Genres != null ? string.Join(", ", animeAPI.Genres) : string.Empty,
                Estado = animeAPI.Status ?? "UNKNOWN",
                TotalEpisodios = episodiosEmitidos,
                UrlPortada = animeAPI.CoverImage?.ExtraLarge ?? string.Empty,
                RutaCarpeta = nuevaRutaCarpeta
            };

            await _databaseService.GuardarAnimeAsync(nuevoAnime);

            item.EstaEnBiblioteca = true;
            _animesEnBibliotecaIds.Add(animeAPI.Id);

            // Notificar a la biblioteca y al resto de la aplicación
            WeakReferenceMessenger.Default.Send(new AnimeAñadidoMensaje(nuevoAnime));

            await _dialogService.MostrarDialogoAsync(
                "¡Anime Añadido!",
                $"'{titulo}' se ha añadido a tu biblioteca correctamente.",
                false,
                "CheckCircle",
                "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", $"Error al añadir anime '{titulo}'", ex);
            await _dialogService.MostrarDialogoAsync(
                "Error al Añadir",
                $"Ocurrió un error al añadir '{titulo}': {ex.Message}",
                false,
                "AlertCircle",
                "#FF5252");
        }
        finally
        {
            item.EstaGuardando = false;
        }
    }

    [RelayCommand]
    public async Task VerEnBibliotecaAsync(AnimeBusquedaItem? item)
    {
        if (item?.Media == null) return;

        try
        {
            var animes = await _databaseService.ObtenerTodosLosAnimesAsync();
            var animeLocal = animes.FirstOrDefault(a => a.AniListId == item.Media.Id);
            if (animeLocal != null)
            {
                WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(animeLocal));
            }
            else
            {
                WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", "Error navegando a la biblioteca", ex);
        }
    }

    public void Receive(AnimeAñadidoMensaje message)
    {
        // Los handlers del messenger NUNCA deben lanzar: una excepción aquí aborta
        // la entrega del mensaje al resto de receptores suscritos.
        try
        {
            if (message.NuevoAnime != null)
            {
                _animesEnBibliotecaIds.Add(message.NuevoAnime.AniListId);
                ActualizarEstadoVisualBiblioteca();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", "Error al procesar AnimeAñadidoMensaje", ex);
        }
    }

    private void CancelarBusquedaPendiente()
    {
        try
        {
            _searchCts?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }
}
