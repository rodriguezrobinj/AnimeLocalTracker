using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

public enum FiltroHistorial
{
    Todos,
    EnProgreso,
    Completados
}

public partial class HistorialViewModel : ObservableObject, IRecipient<EpisodioActualizadoMensaje>, IRecipient<IdiomaCambiadoMensaje>
{
    /// <summary>Feed simple: solo los N capítulos reproducidos más recientes.</summary>
    private const int LimiteHistorial = 60;

    private readonly IDatabaseService _databaseService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly IDialogService _dialogService;
    private readonly IFileScannerService _fileScannerService;

    [ObservableProperty]
    private ObservableCollection<HistorialItemViewModel> _itemsHistorial = [];

    [ObservableProperty]
    private ObservableCollection<HistorialItemViewModel> _itemsFiltrados = [];

    /// <summary>Lista agrupada para la UI: alterna cabeceras de fecha y tarjetas.</summary>
    [ObservableProperty]
    private ObservableCollection<object> _itemsAgrupados = [];

    [ObservableProperty]
    private bool _estaCargando;

    [ObservableProperty]
    private FiltroHistorial _filtroActual = FiltroHistorial.Todos;

    [ObservableProperty]
    private int _totalEnProgreso;

    [ObservableProperty]
    private int _totalCompletados;

    [ObservableProperty]
    private int _totalElementos;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneElementos))]
    [NotifyPropertyChangedFor(nameof(EstaVacio))]
    [NotifyPropertyChangedFor(nameof(SinResultadosBusqueda))]
    private string _textoBusqueda = string.Empty;

    /// <summary>Chips de filtro con contador ("En progreso · 3").</summary>
    [ObservableProperty]
    private ObservableCollection<FiltroChip> _filtros = [];

    /// <summary>Tiempo total visto (suma de lo reproducido) en formato "12 h 30 min".</summary>
    [ObservableProperty]
    private string _tiempoVistoTexto = "0 min";

    public bool EsFiltroTodos => FiltroActual == FiltroHistorial.Todos;
    public bool EsFiltroEnProgreso => FiltroActual == FiltroHistorial.EnProgreso;
    public bool EsFiltroCompletados => FiltroActual == FiltroHistorial.Completados;

    public bool TieneElementos => ItemsFiltrados.Count > 0;
    public bool EstaVacio => !EstaCargando && ItemsHistorial.Count == 0;
    public bool SinResultadosBusqueda => !EstaCargando && ItemsHistorial.Count > 0 && ItemsFiltrados.Count == 0;

    /// <summary>NAV-01: evita re-consultar la BD en cada visita a la pestaña — los items ya
    /// se mantienen al día en vivo vía <see cref="Receive"/>, así que solo hace falta recargar
    /// del todo si nunca se cargó o si pasó el cooldown (por si algo se perdió).</summary>
    public bool NecesitaRecargar() =>
        ItemsHistorial.Count == 0 || (DateTime.UtcNow - _ultimaRecargaUtc) >= CooldownRecarga;

    public HistorialViewModel(
        IDatabaseService databaseService,
        IPlaybackStateService playbackStateService,
        IDialogService dialogService,
        IFileScannerService fileScannerService)
    {
        _databaseService = databaseService;
        _playbackStateService = playbackStateService;
        _dialogService = dialogService;
        _fileScannerService = fileScannerService;

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    partial void OnTextoBusquedaChanged(string value)
    {
        AplicarFiltro();
    }

    partial void OnFiltroActualChanged(FiltroHistorial value)
    {
        OnPropertyChanged(nameof(EsFiltroTodos));
        OnPropertyChanged(nameof(EsFiltroEnProgreso));
        OnPropertyChanged(nameof(EsFiltroCompletados));
        ActualizarFiltros();
        AplicarFiltro();
    }

    [RelayCommand]
    public async Task CargarHistorialAsync()
    {
        if (EstaCargando) return;

        try
        {
            EstaCargando = true;
            OnPropertyChanged(nameof(EstaVacio));
            OnPropertyChanged(nameof(SinResultadosBusqueda));
            _ultimaRecargaUtc = DateTime.UtcNow;

            var registros = await _databaseService.ObtenerHistorialEpisodiosAsync(LimiteHistorial);
            var animes = await _databaseService.ObtenerAnimesLigerosAsync();
            var dicAnimes = animes.ToDictionary(a => a.AniListId);

            var lista = new List<HistorialItemViewModel>();

            foreach (var reg in registros)
            {
                dicAnimes.TryGetValue(reg.AniListId, out var anime);

                string tituloAnime = anime?.Titulo ?? $"Anime #{reg.AniListId}";
                string? rutaPortada = anime?.PortadaVisible;

                // Ruta de imagen resuelta UNA vez (miniatura si existe en disco; si no, portada).
                string? rutaImagen = !string.IsNullOrWhiteSpace(reg.RutaMiniatura) && File.Exists(reg.RutaMiniatura)
                    ? reg.RutaMiniatura
                    : rutaPortada;

                var item = new HistorialItemViewModel
                {
                    AniListId = reg.AniListId,
                    NumeroEpisodio = reg.NumeroEpisodio,
                    TituloAnime = tituloAnime,
                    TituloEpisodio = string.Format(LocalizationService.T("Act_EpisodioFormato"), reg.NumeroEpisodio),
                    RutaArchivo = reg.RutaArchivo,
                    RutaMiniatura = reg.RutaMiniatura,
                    RutaPortada = rutaPortada,
                    RutaImagenMostrar = rutaImagen,
                    Resolucion = reg.Resolucion,
                    ProgresoSegundos = reg.ProgresoSegundos,
                    TotalSegundos = reg.TotalSegundos,
                    VistoLocal = reg.VistoLocal,
                    UltimaReproduccion = reg.UltimaReproduccion
                };

                lista.Add(item);
            }

            ItemsHistorial = new ObservableCollection<HistorialItemViewModel>(lista);
            ActualizarContadores();
            AplicarFiltro();
        }
        catch (Exception ex)
        {
            AppLogger.Error("HistorialViewModel", "Error al cargar historial de reproducción", ex);
        }
        finally
        {
            EstaCargando = false;
            NotificarEstados();
        }
    }

    [RelayCommand]
    public void CambiarFiltro(string filtroStr)
    {
        if (Enum.TryParse<FiltroHistorial>(filtroStr, true, out var nuevoFiltro))
        {
            FiltroActual = nuevoFiltro;
        }
    }

    [RelayCommand]
    public async Task ReanudarAsync(HistorialItemViewModel? item)
    {
        if (item == null) return;

        if (!item.ExisteArchivoLocal)
        {
            _ = _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Hist_Titulo"),
                LocalizationService.T("Hist_ArchivoNoEncontrado"),
                esConfirmacion: false,
                icono: "AlertCircle",
                color: "#F44336");
            return;
        }

        // NAV-03: el Historial solo guarda "los últimos N capítulos vistos", no la lista
        // completa de episodios del anime — sin escanear la carpeta aquí, el reproductor
        // abría el capítulo creyendo que no había anterior/siguiente (a diferencia de la
        // Ficha, que ya tiene todos los episodios en memoria).
        List<EpisodioItem> episodiosDisponibles = new();
        try
        {
            var anime = await _databaseService.ObtenerAnimePorIdAsync(item.AniListId);
            if (anime != null && !string.IsNullOrWhiteSpace(anime.RutaCarpeta))
            {
                episodiosDisponibles = await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta)
                    ?? new List<EpisodioItem>();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("HistorialViewModel", $"No se pudo armar la lista de episodios para navegación de '{item.TituloAnime}': {ex.Message}");
        }

        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
            item.RutaArchivo,
            item.AniListId,
            item.TituloAnime,
            item.NumeroEpisodio,
            EpisodiosDisponibles: episodiosDisponibles,
            RutaPortada: item.RutaPortada));
    }

    [RelayCommand]
    public async Task NavegarDetalleAsync(HistorialItemViewModel? item)
    {
        if (item == null) return;

        var anime = await _databaseService.ObtenerAnimePorIdAsync(item.AniListId);
        if (anime != null)
        {
            WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(anime));
        }
        else
        {
            // El anime ya no está en la biblioteca local: avisar (en vez de fallar en silencio).
            _ = _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Hist_Titulo"),
                string.Format(LocalizationService.T("Hist_NoEnBiblioteca"), item.TituloAnime),
                esConfirmacion: false,
                icono: "BookOpenPageVariantOutline",
                color: "#60A5FA");
        }
    }

    [RelayCommand]
    public void ExplorarGaleria()
    {
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());
    }

    public void Receive(EpisodioActualizadoMensaje message)
    {
        // El mensaje puede llegar desde un hilo de fondo (guardados de reproducción):
        // marshalling al hilo de UI antes de tocar colecciones observables.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => RecibirEpisodioActualizado(message));
            return;
        }

        RecibirEpisodioActualizado(message);
    }

    private void RecibirEpisodioActualizado(EpisodioActualizadoMensaje message)
    {
        var item = ItemsHistorial.FirstOrDefault(i =>
            i.AniListId == message.AnimeId && i.NumeroEpisodio == message.NumeroEpisodio);

        // Los mensajes del reproductor traen progreso/duración; los del marcado manual (Detalle)
        // no — y el marcado manual no debe fabricar una fecha de visionado (ver persistence.md #5).
        bool esReproduccionReal = message.ProgresoSegundos > 0 || message.TotalSegundos > 0;

        if (item != null)
        {
            // Cada guardado de progreso del reproductor ya estampa UltimaReproduccion = ahora en
            // la BD (PlaybackStateService), así que reflejarlo en memoria deja la lista igual que
            // tras una recarga: la fila sube al principio y pasa de "Ayer" a "Hoy" en vivo.
            if (esReproduccionReal || (message.VistoLocal && !item.VistoLocal))
            {
                item.UltimaReproduccion = DateTime.UtcNow;

                int indice = ItemsHistorial.IndexOf(item);
                if (indice > 0) ItemsHistorial.Move(indice, 0);
            }
            item.VistoLocal = message.VistoLocal;
            item.ProgresoSegundos = message.ProgresoSegundos;
            if (message.TotalSegundos > 0) item.TotalSegundos = message.TotalSegundos;
            ActualizarContadores();
            AplicarFiltro();
        }
        else if (esReproduccionReal)
        {
            // Episodio que aún no estaba en la lista (primera vez que se reproduce): recargar YA,
            // sin esperar el cooldown de 30 s — pero una sola vez por episodio, para no volver al
            // bucle de SELECT+rebuild en cada guardado de progreso (cada ~3 s).
            var clave = (message.AnimeId, message.NumeroEpisodio);
            if (clave != _ultimaClaveRecargada && !EstaCargando)
            {
                _ultimaClaveRecargada = clave;
                _ = CargarHistorialAsync();
            }
        }
        else
        {
            RecargarConCooldown();
        }
    }

    private (int AnimeId, int NumeroEpisodio) _ultimaClaveRecargada;

    // FIX: los guardados de progreso llegan cada ~5 s durante la reproducción; si el
    // episodio aún no está en la lista (anime nuevo), recargar en cada mensaje era un
    // bucle de SELECT+rebuild continuo. Se recarga como mucho una vez cada 30 s.
    // NAV-01: el mismo cooldown también evita recargar en cada visita a la pestaña.
    private static readonly TimeSpan CooldownRecarga = TimeSpan.FromSeconds(30);
    private DateTime _ultimaRecargaUtc = DateTime.MinValue;

    private void RecargarConCooldown()
    {
        if (EstaCargando) return;
        if ((DateTime.UtcNow - _ultimaRecargaUtc) < CooldownRecarga) return;
        _ = CargarHistorialAsync();
    }

    private void AplicarFiltro()
    {
        IEnumerable<HistorialItemViewModel> query = ItemsHistorial;

        // Filtro de estado
        switch (FiltroActual)
        {
            case FiltroHistorial.EnProgreso:
                query = query.Where(i => i.EnProgreso);
                break;
            case FiltroHistorial.Completados:
                query = query.Where(i => i.VistoLocal);
                break;
        }

        // Filtro de texto de búsqueda
        if (!string.IsNullOrWhiteSpace(TextoBusqueda))
        {
            string busqueda = TextoBusqueda.Trim();
            query = query.Where(i =>
                i.TituloAnime.Contains(busqueda, StringComparison.OrdinalIgnoreCase) ||
                i.TituloEpisodio.Contains(busqueda, StringComparison.OrdinalIgnoreCase));
        }

        ItemsFiltrados = new ObservableCollection<HistorialItemViewModel>(query);
        ItemsAgrupados = AgruparPorFecha(query);
        NotificarEstados();
    }

    /// <summary>
    /// Agrupa los episodios por fecha (Hoy / Ayer / fecha…) manteniendo el orden
    /// cronológico descendente y produciendo la lista que alterna cabeceras y tarjetas.
    /// </summary>
    private static ObservableCollection<object> AgruparPorFecha(IEnumerable<HistorialItemViewModel> items)
    {
        var salida = new List<object>();
        foreach (var grupo in items.GroupBy(i => i.GrupoTemporal))
        {
            salida.Add(grupo.Key);
            salida.AddRange(grupo);
        }
        return new ObservableCollection<object>(salida);
    }

    private void ActualizarContadores()
    {
        TotalElementos = ItemsHistorial.Count;
        TotalEnProgreso = ItemsHistorial.Count(i => i.EnProgreso);
        TotalCompletados = ItemsHistorial.Count(i => i.VistoLocal);

        // Tiempo visto: capítulo completo si está visto; si no, lo reproducido hasta ahora.
        double segundos = ItemsHistorial.Sum(i => i.VistoLocal && i.TotalSegundos > 0 ? i.TotalSegundos : i.ProgresoSegundos);
        var t = TimeSpan.FromSeconds(Math.Max(0, segundos));
        TiempoVistoTexto = t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{(int)t.TotalMinutes} min";

        ActualizarFiltros();
    }

    private void ActualizarFiltros()
    {
        string Etiqueta(string claveLoc, int n) =>
            string.Format(LocalizationService.T("Act_FiltroFormato"), LocalizationService.T(claveLoc), n);

        Filtros = new ObservableCollection<FiltroChip>
        {
            new(nameof(FiltroHistorial.Todos), Etiqueta("Hist_FiltroTodos", TotalElementos), FiltroActual == FiltroHistorial.Todos),
            new(nameof(FiltroHistorial.EnProgreso), Etiqueta("Hist_FiltroEnProgreso", TotalEnProgreso), FiltroActual == FiltroHistorial.EnProgreso),
            new(nameof(FiltroHistorial.Completados), Etiqueta("Hist_FiltroCompletados", TotalCompletados), FiltroActual == FiltroHistorial.Completados),
        };
    }

    public void Receive(IdiomaCambiadoMensaje message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefrescarTextos);
            return;
        }
        RefrescarTextos();
    }

    private void RefrescarTextos()
    {
        foreach (var i in ItemsHistorial) i.RefrescarTextos();
        ActualizarFiltros();
        AplicarFiltro();
    }

    private void NotificarEstados()
    {
        OnPropertyChanged(nameof(TieneElementos));
        OnPropertyChanged(nameof(EstaVacio));
        OnPropertyChanged(nameof(SinResultadosBusqueda));
    }
}
