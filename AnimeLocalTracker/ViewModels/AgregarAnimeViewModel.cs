using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

public partial class AgregarAnimeViewModel : ObservableObject,
    IRecipient<AnimeAñadidoMensaje>,
    IRecipient<IdiomaCambiadoMensaje>
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly AnimeLibraryService _animeLibraryService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private ObservableCollection<AnimeBusquedaItem> _resultados = [];

    /// <summary>Vista filtrada de <see cref="Resultados"/> por temporada/año (la lista se pinta desde aquí).</summary>
    public ICollectionView ResultadosFiltrados { get; }

    // --- FILTROS DE TEMPORADA Y AÑO (sobre resultados de búsqueda/tendencias) ---
    public static string TodasLasTemporadas => LocalizationService.T("Gal_TodasLasTemporadas");
    public static string TodosLosAños => LocalizationService.T("Gal_TodosLosAnios");

    [ObservableProperty]
    private ObservableCollection<string> _temporadasDisponibles = [TodasLasTemporadas];

    [ObservableProperty]
    private string _temporadaSeleccionada = TodasLasTemporadas;

    [ObservableProperty]
    private ObservableCollection<string> _aniosDisponibles = [TodosLosAños];

    [ObservableProperty]
    private string _anioSeleccionado = TodosLosAños;

    // Reutiliza la misma clave que GaleriaViewModel para "todos los géneros" (igual que ya se
    // reutilizan Gal_TodasLasTemporadas/Gal_TodosLosAnios): es un texto genérico, no específico de Galería.
    public static string TodosLosGeneros => LocalizationService.T("Gal_TodosLosGeneros");

    [ObservableProperty]
    private ObservableCollection<string> _generosDisponibles = [TodosLosGeneros];

    [ObservableProperty]
    private string _generoSeleccionado = TodosLosGeneros;

    public bool HayFiltrosActivos =>
        TemporadaSeleccionada != TodasLasTemporadas ||
        AnioSeleccionado != TodosLosAños ||
        GeneroSeleccionado != TodosLosGeneros;

    /// <summary>Sin coincidencias por temporada/año/género, aunque la búsqueda en sí trajo resultados.</summary>
    public bool SinResultadosFiltrados => Resultados.Count > 0 && (ResultadosFiltrados?.IsEmpty ?? false);

    public bool MostrarSinResultados => BusquedaSinResultados || SinResultadosFiltrados;

    private void RefrescarFiltroTemporada()
    {
        ResultadosFiltrados.Refresh();
        OnPropertyChanged(nameof(HayFiltrosActivos));
        OnPropertyChanged(nameof(SinResultadosFiltrados));
        OnPropertyChanged(nameof(MostrarSinResultados));
    }

    partial void OnTemporadaSeleccionadaChanged(string value) => RefrescarFiltroTemporada();

    partial void OnAnioSeleccionadoChanged(string value) => RefrescarFiltroTemporada();

    partial void OnGeneroSeleccionadoChanged(string value) => RefrescarFiltroTemporada();

    [RelayCommand]
    private void LimpiarFiltros()
    {
        TemporadaSeleccionada = TodasLasTemporadas;
        AnioSeleccionado = TodosLosAños;
        GeneroSeleccionado = TodosLosGeneros;
    }

    private bool FiltrarResultado(object obj)
    {
        if (obj is not AnimeBusquedaItem item) return true;

        if (TemporadaSeleccionada != TodasLasTemporadas &&
            item.TemporadaTexto != TemporadaSeleccionada)
        {
            return false;
        }

        if (AnioSeleccionado != TodosLosAños &&
            item.AñoTexto != AnioSeleccionado)
        {
            return false;
        }

        if (GeneroSeleccionado != TodosLosGeneros &&
            (item.Media.Genres == null || !item.Media.Genres.Any(g => g.Equals(GeneroSeleccionado, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        return true;
    }

    private static string SeasonATexto(string codigo) => codigo switch
    {
        "WINTER" => LocalizationService.T("Temporada_Invierno"),
        "SPRING" => LocalizationService.T("Temporada_Primavera"),
        "SUMMER" => LocalizationService.T("Temporada_Verano"),
        "FALL" => LocalizationService.T("Temporada_Otonio"),
        _ => codigo
    };

    private void ActualizarTemporadasYAniosDisponibles()
    {
        var ordenTemporadas = new[] { "WINTER", "SPRING", "SUMMER", "FALL" };
        var temporadasUnicas = Resultados
            .Select(r => r.Media?.Season)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nuevaListaTemporadas = new List<string> { TodasLasTemporadas };
        nuevaListaTemporadas.AddRange(
            ordenTemporadas.Where(t => temporadasUnicas.Contains(t, StringComparer.OrdinalIgnoreCase))
                           .Select(SeasonATexto));

        string prevTemporada = TemporadaSeleccionada;
        TemporadasDisponibles = new ObservableCollection<string>(nuevaListaTemporadas);
        TemporadaSeleccionada = nuevaListaTemporadas.Contains(prevTemporada) ? prevTemporada : TodasLasTemporadas;

        var aniosUnicos = Resultados
            .Select(r => r.Media?.StartDate?.Year)
            .Where(y => y.HasValue)
            .Select(y => y!.Value)
            .Distinct()
            .OrderByDescending(y => y)
            .Select(y => y.ToString())
            .ToList();

        var nuevaListaAnios = new List<string> { TodosLosAños };
        nuevaListaAnios.AddRange(aniosUnicos);

        string prevAnio = AnioSeleccionado;
        AniosDisponibles = new ObservableCollection<string>(nuevaListaAnios);
        AnioSeleccionado = nuevaListaAnios.Contains(prevAnio) ? prevAnio : TodosLosAños;
    }

    /// <summary>Igual que <see cref="ActualizarTemporadasYAniosDisponibles"/> pero para género: el valor
    /// real se guarda en inglés (tal cual lo entrega AniList) — el ComboBox solo TRADUCE lo que se
    /// muestra vía el converter GeneroTraducido, mismo patrón que GaleriaViewModel.</summary>
    private void ActualizarGenerosDisponibles()
    {
        var generosUnicos = Resultados
            .SelectMany(r => r.Media?.Genres ?? [])
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g)
            .ToList();

        var nuevaLista = new List<string> { TodosLosGeneros };
        nuevaLista.AddRange(generosUnicos);

        string prevGenero = GeneroSeleccionado;
        GenerosDisponibles = new ObservableCollection<string>(nuevaLista);
        GeneroSeleccionado = nuevaLista.Contains(prevGenero) ? prevGenero : TodosLosGeneros;
    }

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarSinResultados))]
    private bool _busquedaSinResultados;

    [ObservableProperty]
    private bool _mostrandoTendencias = true;

    [ObservableProperty]
    private string _tituloSeccion = LocalizationService.T("Add_TendenciasTemporada");

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
                _ = EjecutarBusquedaEnVivoAsync(value);
            }
        }
    }

    public bool TieneTextoBusqueda => !string.IsNullOrWhiteSpace(TextoBusqueda);

    private CancellationTokenSource? _searchCts;
    private HashSet<int> _animesEnBibliotecaIds = [];

    public AgregarAnimeViewModel(
        IAnimeTrackingService animeTrackingService,
        IDatabaseService databaseService,
        AnimeLibraryService animeLibraryService,
        IDialogService dialogService)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _animeLibraryService = animeLibraryService;
        _dialogService = dialogService;

        // Registro vía IRecipient<AnimeAñadidoMensaje>: una sola suscripción.
        // (Antes había un lambda + Receive() duplicando la misma lógica.)
        WeakReferenceMessenger.Default.Register<AnimeAñadidoMensaje>(this);
        WeakReferenceMessenger.Default.Register<IdiomaCambiadoMensaje>(this);

        // Resultados es una única instancia estable durante toda la vida del VM (solo se
        // Clear()+Add() en cada búsqueda): la vista filtrada se crea una sola vez aquí.
        ResultadosFiltrados = CollectionViewSource.GetDefaultView(Resultados);
        ResultadosFiltrados.Filter = FiltrarResultado;

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
            TituloSeccion = LocalizationService.T("Add_TendenciasTemporada");
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
            ActualizarTemporadasYAniosDisponibles();
            ActualizarGenerosDisponibles();
            RefrescarFiltroTemporada();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", "Error cargando tendencias", ex);
            _dialogService.MostrarToast(
                LocalizationService.T("Add_ErrorBusquedaTitulo"),
                LocalizationService.T("Add_ErrorBusquedaMsj"),
                "AlertCircle",
                "#FF5252");
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

    private async Task EjecutarBusquedaEnVivoAsync(string busqueda)
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
            TituloSeccion = string.Format(LocalizationService.T("Add_ResultadosParaFormato"), busqueda.Trim());
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
            ActualizarTemporadasYAniosDisponibles();
            ActualizarGenerosDisponibles();
            RefrescarFiltroTemporada();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", $"Error buscando animes para '{busqueda}'", ex);
            _dialogService.MostrarToast(
                LocalizationService.T("Add_ErrorBusquedaTitulo"),
                LocalizationService.T("Add_ErrorBusquedaMsj"),
                "AlertCircle",
                "#FF5252");
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

            // ARQ-02: la lógica de alta vive en AnimeLibraryService (un solo punto de verdad)
            var nuevoAnime = await _animeLibraryService.CrearYGuardarAnimeAsync(animeAPI, titulo);

            if (nuevoAnime == null)
            {
                item.EstaEnBiblioteca = true;
                _animesEnBibliotecaIds.Add(animeAPI.Id);
                await _dialogService.MostrarDialogoAsync(
                    LocalizationService.T("Dlg_AnimeExistente"),
                    string.Format(LocalizationService.T("Dlg_AnimeExistenteMsj"), titulo),
                    false,
                    "InformationOutline",
                    "#FF9800");
                return;
            }

            item.EstaEnBiblioteca = true;
            _animesEnBibliotecaIds.Add(animeAPI.Id);

            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Add_AnimeAnadidoTitulo"),
                string.Format(LocalizationService.T("Add_AnimeAnadidoMsj"), titulo),
                false,
                "CheckCircle",
                "#4CAF50");
        }
        catch (Exception ex)
        {
            AppLogger.Error("AgregarAnimeViewModel", $"Error al añadir anime '{titulo}'", ex);
            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Add_ErrorAlAnadirTitulo"),
                string.Format(LocalizationService.T("Add_ErrorAlAnadirMsj"), titulo, ex.Message),
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

    /// <summary>LOC-08: regenera el título de sección y los desplegables de temporada/año/género
    /// (texto plano, no se refrescan solos al cambiar de idioma; el género además necesita
    /// reconstruirse para que el converter GeneroTraducido re-evalúe la traducción mostrada).</summary>
    public void Receive(IdiomaCambiadoMensaje message)
    {
        TituloSeccion = !MostrandoTendencias && TieneTextoBusqueda
            ? string.Format(LocalizationService.T("Add_ResultadosParaFormato"), TextoBusqueda.Trim())
            : LocalizationService.T("Add_TendenciasTemporada");
        ActualizarTemporadasYAniosDisponibles();
        ActualizarGenerosDisponibles();
        RefrescarFiltroTemporada();
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
