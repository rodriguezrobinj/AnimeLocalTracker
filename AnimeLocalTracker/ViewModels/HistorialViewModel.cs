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

public partial class HistorialViewModel : ObservableObject, IRecipient<EpisodioActualizadoMensaje>
{
    /// <summary>Feed simple: solo los N capítulos reproducidos más recientes.</summary>
    private const int LimiteHistorial = 60;

    private readonly IDatabaseService _databaseService;
    private readonly IPlaybackStateService _playbackStateService;
    private readonly IDialogService _dialogService;

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

    public bool EsFiltroTodos => FiltroActual == FiltroHistorial.Todos;
    public bool EsFiltroEnProgreso => FiltroActual == FiltroHistorial.EnProgreso;
    public bool EsFiltroCompletados => FiltroActual == FiltroHistorial.Completados;

    public bool TieneElementos => ItemsFiltrados.Count > 0;
    public bool EstaVacio => !EstaCargando && ItemsHistorial.Count == 0;
    public bool SinResultadosBusqueda => !EstaCargando && ItemsHistorial.Count > 0 && ItemsFiltrados.Count == 0;

    public HistorialViewModel(
        IDatabaseService databaseService,
        IPlaybackStateService playbackStateService,
        IDialogService dialogService)
    {
        _databaseService = databaseService;
        _playbackStateService = playbackStateService;
        _dialogService = dialogService;

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
            _ultimaRecargaPorMensaje = DateTime.UtcNow;

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
                    TituloEpisodio = $"Episodio {reg.NumeroEpisodio}",
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
    public void Reanudar(HistorialItemViewModel? item)
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

        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
            item.RutaArchivo,
            item.AniListId,
            item.TituloAnime,
            item.NumeroEpisodio));
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
    public async Task AlternarVistoAsync(HistorialItemViewModel? item)
    {
        if (item == null) return;

        try
        {
            bool nuevoVisto = !item.VistoLocal;
            item.VistoLocal = nuevoVisto;

            if (nuevoVisto)
            {
                item.ProgresoSegundos = 0;
                // Marcar "visto" desde el historial NO registra fecha de reproducción:
                // el historial solo muestra visionado real.
                await _playbackStateService.MarcarComoVistoYSincronizarAsync(
                    item.AniListId,
                    item.NumeroEpisodio,
                    item.RutaArchivo,
                    item.TotalSegundos,
                    registrarReproduccion: false);
            }
            else
            {
                var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(item.AniListId);
                var reg = registros.FirstOrDefault(r => r.NumeroEpisodio == item.NumeroEpisodio);
                if (reg != null)
                {
                    reg.VistoLocal = false;
                    await _databaseService.GuardarRegistroEpisodioAsync(reg);
                }
            }

            ActualizarContadores();
            AplicarFiltro();
        }
        catch (Exception ex)
        {
            AppLogger.Error("HistorialViewModel", $"Error al alternar estado visto para ep {item.NumeroEpisodio}", ex);
        }
    }

    [RelayCommand]
    public async Task EliminarItemAsync(HistorialItemViewModel? item)
    {
        if (item == null) return;

        try
        {
            await _databaseService.LimpiarRegistroHistorialAsync(item.AniListId, item.NumeroEpisodio);
            ItemsHistorial.Remove(item);
            ItemsFiltrados.Remove(item);
            ActualizarContadores();
            NotificarEstados();
        }
        catch (Exception ex)
        {
            AppLogger.Error("HistorialViewModel", $"Error al eliminar ep {item.NumeroEpisodio} del historial", ex);
        }
    }

    [RelayCommand]
    public async Task LimpiarHistorialAsync()
    {
        if (ItemsHistorial.Count == 0) return;

        bool confirmar = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Hist_LimpiarTitulo"),
            LocalizationService.T("Hist_LimpiarMensaje"),
            esConfirmacion: true,
            icono: "DeleteSweepOutline",
            color: "#F44336");

        if (!confirmar) return;

        try
        {
            await _databaseService.LimpiarTodoElHistorialAsync();
            ItemsHistorial.Clear();
            ItemsFiltrados.Clear();
            ActualizarContadores();
            NotificarEstados();
        }
        catch (Exception ex)
        {
            AppLogger.Error("HistorialViewModel", "Error al vaciar el historial", ex);
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

        if (item != null)
        {
            // La fecha NO se pisa en cada guardado de progreso (mostraba la hora del
            // guardado en vez de la hora real en la que se vio el episodio): solo se
            // actualiza cuando el episodio pasa a "visto" (momento real del visionado).
            if (message.VistoLocal && !item.VistoLocal)
            {
                item.UltimaReproduccion = DateTime.UtcNow;
            }
            item.VistoLocal = message.VistoLocal;
            item.ProgresoSegundos = message.ProgresoSegundos;
            if (message.TotalSegundos > 0) item.TotalSegundos = message.TotalSegundos;
            ActualizarContadores();
            AplicarFiltro();
        }
        else
        {
            RecargarConCooldown();
        }
    }

    // FIX: los guardados de progreso llegan cada ~5 s durante la reproducción; si el
    // episodio aún no está en la lista (anime nuevo), recargar en cada mensaje era un
    // bucle de SELECT+rebuild continuo. Se recarga como mucho una vez cada 30 s.
    private DateTime _ultimaRecargaPorMensaje = DateTime.MinValue;

    private void RecargarConCooldown()
    {
        if (EstaCargando) return;
        var ahora = DateTime.UtcNow;
        if ((ahora - _ultimaRecargaPorMensaje).TotalSeconds < 30) return;
        _ultimaRecargaPorMensaje = ahora;
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
    }

    private void NotificarEstados()
    {
        OnPropertyChanged(nameof(TieneElementos));
        OnPropertyChanged(nameof(EstaVacio));
        OnPropertyChanged(nameof(SinResultadosBusqueda));
    }
}
