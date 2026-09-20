using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Pestaña Descargas: "Activas" (cola en vivo con prioridad, pausa y cancelación) e "Historial"
/// (completadas y fallidas persistidas, con reproducir / abrir carpeta / reintentar).
/// </summary>
public partial class DescargasViewModel : ObservableObject, IRecipient<DescargaProgresoMensaje>, IRecipient<DescargaHistorialActualizadoMensaje>
{
    public const string FiltroTodas = "Todas";
    public const string FiltroCompletadas = "Completadas";
    public const string FiltroFallidas = "Fallidas";

    private readonly IDownloadService _downloadService;
    private readonly IDatabaseService? _database;
    private List<DescargaHistorialItemViewModel> _historial = [];

    public ObservableCollection<DescargaItem> ColaDescargas { get; } = [];

    /// <summary>Historial filtrado y agrupado por día: alterna cabeceras (string) y filas (item).</summary>
    [ObservableProperty]
    private ObservableCollection<object> _itemsAgrupados = [];

    [ObservableProperty]
    private int _conteoActivas;

    [ObservableProperty]
    private int _conteoEnCola;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarPausarTodas))]
    private bool _tieneDescargas;

    [ObservableProperty]
    private bool _todasPausadas;

    [ObservableProperty]
    private string _velocidadTotalTexto = "—";

    // ── Pestañas ──
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsPestanaActivas))]
    [NotifyPropertyChangedFor(nameof(EsPestanaHistorial))]
    [NotifyPropertyChangedFor(nameof(MostrarPausarTodas))]
    private string _pestanaActual = "Activas";

    /// <summary>Chips de pestaña (Activas / Historial) con su contador.</summary>
    [ObservableProperty]
    private ObservableCollection<FiltroChip> _pestanas = [];

    /// <summary>Chips de filtro del historial (Todas / Completadas / Fallidas) con su contador.</summary>
    [ObservableProperty]
    private ObservableCollection<FiltroChip> _filtros = [];

    public bool MostrarPausarTodas => EsPestanaActivas && TieneDescargas;
    public bool EsPestanaActivas => PestanaActual == "Activas";
    public bool EsPestanaHistorial => PestanaActual == "Historial";

    // ── Historial ──
    [ObservableProperty]
    private string _filtroHistorial = FiltroTodas;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayBusqueda))]
    private string _textoBusqueda = string.Empty;

    public bool HayBusqueda => !string.IsNullOrEmpty(TextoBusqueda);

    [ObservableProperty]
    private bool _estaCargando;

    [ObservableProperty]
    private int _totalHistorial;

    [ObservableProperty]
    private int _totalCompletadas;

    [ObservableProperty]
    private int _totalFallidas;

    [ObservableProperty]
    private int _completadasHoy;

    /// <summary>Hay filas en el historial (aunque el filtro/búsqueda las oculte).</summary>
    [ObservableProperty]
    private bool _tieneHistorial;

    /// <summary>El filtro o la búsqueda dejaron la lista vacía pero existe historial.</summary>
    [ObservableProperty]
    private bool _sinResultados;

    public DescargasViewModel(IDownloadService downloadService, IDatabaseService? database = null)
    {
        _downloadService = downloadService;
        _database = database;
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(this);
        WeakReferenceMessenger.Default.Register<DescargaHistorialActualizadoMensaje>(this);

        CargarDescargas();
        ActualizarChips();
        _ = CargarHistorialAsync();
    }

    /// <summary>Recarga cola e historial (se invoca al entrar en la pestaña).</summary>
    public void Refrescar()
    {
        CargarDescargas();
        _ = CargarHistorialAsync();
    }

    public void CargarDescargas()
    {
        ColaDescargas.Clear();
        foreach (var d in _downloadService.ObtenerDescargasActivas())
        {
            ColaDescargas.Add(d);
        }
        ActualizarConteo();
    }

    public async Task CargarHistorialAsync()
    {
        if (_database == null) return;

        try
        {
            EstaCargando = _historial.Count == 0;
            var filas = await _database.ObtenerDescargasHistorialAsync();
            var ahora = DateTime.Now;
            var items = filas.Select(f => new DescargaHistorialItemViewModel(f, ahora)).ToList();

            // File.Exists nunca en el hilo de UI ni en getters de binding.
            var existentes = await Task.Run(() => items.Select(i => i.ComprobarArchivo()).ToArray());
            for (int i = 0; i < items.Count; i++)
            {
                items[i].ArchivoExiste = !items[i].Completada || existentes[i];
            }

            _historial = items;
            TotalHistorial = items.Count;
            TotalCompletadas = items.Count(i => i.Completada);
            TotalFallidas = items.Count(i => !i.Completada);
            CompletadasHoy = items.Count(i => i.Completada && i.FechaLocal.Date == ahora.Date);
            TieneHistorial = items.Count > 0;
            ActualizarChips();
            AplicarFiltroHistorial();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DescargasViewModel", $"No se pudo cargar el historial de descargas: {ex.Message}");
        }
        finally
        {
            EstaCargando = false;
        }
    }

    private void ActualizarChips()
    {
        string Etiqueta(string clave, int n) =>
            string.Format(LocalizationService.T("Act_FiltroFormato"), LocalizationService.T(clave), n);

        Pestanas =
        [
            new FiltroChip("Activas", Etiqueta("Desc_TabActivas", ColaDescargas.Count), EsPestanaActivas),
            new FiltroChip("Historial", Etiqueta("Desc_TabHistorial", TotalHistorial), EsPestanaHistorial)
        ];
        Filtros =
        [
            new FiltroChip(FiltroTodas, Etiqueta("Desc_FiltroTodas", TotalHistorial), FiltroHistorial == FiltroTodas),
            new FiltroChip(FiltroCompletadas, Etiqueta("Desc_FiltroCompletadas", TotalCompletadas), FiltroHistorial == FiltroCompletadas),
            new FiltroChip(FiltroFallidas, Etiqueta("Desc_FiltroFallidas", TotalFallidas), FiltroHistorial == FiltroFallidas)
        ];
    }

    partial void OnPestanaActualChanged(string value) => ActualizarChips();

    private void AplicarFiltroHistorial()
    {
        IEnumerable<DescargaHistorialItemViewModel> query = _historial;
        if (FiltroHistorial == FiltroCompletadas) query = query.Where(i => i.Completada);
        else if (FiltroHistorial == FiltroFallidas) query = query.Where(i => !i.Completada);

        string texto = TextoBusqueda?.Trim() ?? string.Empty;
        if (texto.Length > 0)
        {
            query = query.Where(i =>
                i.AnimeTitulo.Contains(texto, StringComparison.CurrentCultureIgnoreCase) ||
                i.TitulosAlternativos.Contains(texto, StringComparison.CurrentCultureIgnoreCase));
        }

        var salida = new List<object>();
        foreach (var grupo in query.GroupBy(i => i.GrupoTemporal))
        {
            salida.Add(grupo.Key);
            salida.AddRange(grupo);
        }

        ItemsAgrupados = new ObservableCollection<object>(salida);
        SinResultados = salida.Count == 0 && _historial.Count > 0;
    }

    partial void OnFiltroHistorialChanged(string value)
    {
        ActualizarChips();
        AplicarFiltroHistorial();
    }
    partial void OnTextoBusquedaChanged(string value) => AplicarFiltroHistorial();

    private static void EnUi(Action accion)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) accion();
        else dispatcher.InvokeAsync(accion);
    }

    public void Receive(DescargaHistorialActualizadoMensaje message) => EnUi(() => _ = CargarHistorialAsync());

    public void Receive(DescargaProgresoMensaje message)
    {
        // Sin Application (tests headless) el mensaje no se procesa, como antes.
        Application.Current?.Dispatcher.InvokeAsync(() => AplicarMensaje(message));
    }

    internal void AplicarMensaje(DescargaProgresoMensaje message)
    {
        var item = ColaDescargas.FirstOrDefault(d => d.AniListId == message.AniListId && d.NumeroEpisodio == message.NumeroEpisodio);

        if (item != null)
        {
            if (!string.IsNullOrWhiteSpace(message.AnimeTitulo) && (string.IsNullOrEmpty(item.AnimeTitulo) || item.AnimeTitulo == "Descarga"))
            {
                item.AnimeTitulo = message.AnimeTitulo;
            }
            item.EnCola = message.EnCola;
            item.Reintentos = message.Reintentos;
            item.Progreso = message.Progreso;
            item.IsDownloading = message.IsDownloading;
            item.IsCompleted = message.IsCompleted;
            item.IsPaused = message.IsPaused;
            item.RutaArchivo = message.RutaArchivo;
            item.Error = message.Error;
            item.VelocidadBps = message.VelocidadBps;
            item.VelocidadDescarga = message.VelocidadDescarga ?? string.Empty;

            if (message.IsCompleted)
            {
                // La fila queda un instante al 100 % y se retira; el resultado ya vive en el historial.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500);
                        EnUi(() =>
                        {
                            ColaDescargas.Remove(item);
                            ActualizarConteo();
                        });
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("DescargasViewModel", $"Error al remover descarga completada: {ex.Message}");
                    }
                });
            }
            else if (!string.IsNullOrEmpty(message.Error))
            {
                ColaDescargas.Remove(item);
                ActualizarConteo();
            }
            else
            {
                ActualizarConteo();
            }
        }
        else if (message.IsDownloading)
        {
            ColaDescargas.Add(new DescargaItem
            {
                AniListId = message.AniListId,
                AnimeTitulo = !string.IsNullOrWhiteSpace(message.AnimeTitulo) ? message.AnimeTitulo : "Descarga",
                NumeroEpisodio = message.NumeroEpisodio,
                Progreso = message.Progreso,
                IsDownloading = true,
                IsCompleted = false,
                IsPaused = message.IsPaused,
                EnCola = message.EnCola,
                Reintentos = message.Reintentos,
                VelocidadBps = message.VelocidadBps,
                VelocidadDescarga = message.VelocidadDescarga ?? string.Empty
            });
            ActualizarConteo();
        }
    }

    private void ActualizarConteo()
    {
        ConteoActivas = ColaDescargas.Count(d => d.IsDownloading && !d.IsPaused);
        ConteoEnCola = ColaDescargas.Count(d => d.EnCola && !d.IsPaused);
        TieneDescargas = ColaDescargas.Count > 0;
        TodasPausadas = ColaDescargas.Count > 0 && ColaDescargas.All(d => d.IsPaused);

        double bps = ColaDescargas.Where(d => !d.IsPaused && !d.EnCola).Sum(d => d.VelocidadBps);
        VelocidadTotalTexto = bps > 0 ? FormatearVelocidad(bps) : "—";
        ActualizarChips();
    }

    internal static string FormatearVelocidad(double bytesPorSegundo)
    {
        double mb = bytesPorSegundo / 1048576.0;
        return mb >= 1 ? $"{mb:0.0} MB/s" : $"{bytesPorSegundo / 1024.0:0} KB/s";
    }

    // ── Comandos de la cola ──

    [RelayCommand]
    private void CambiarPestana(string pestana)
    {
        if (pestana is "Activas" or "Historial") PestanaActual = pestana;
    }

    [RelayCommand]
    private void CambiarFiltro(string filtro)
    {
        if (filtro is FiltroTodas or FiltroCompletadas or FiltroFallidas) FiltroHistorial = filtro;
    }

    [RelayCommand]
    private void LimpiarBusqueda() => TextoBusqueda = string.Empty;

    [RelayCommand]
    private void CancelarDescarga(DescargaItem item)
    {
        if (item == null) return;
        _downloadService.CancelarDescarga(item.AniListId, item.NumeroEpisodio);
        ColaDescargas.Remove(item);
        ActualizarConteo();
    }

    [RelayCommand]
    private void AlternarPausaDescarga(DescargaItem item)
    {
        if (item == null) return;
        if (item.IsPaused)
        {
            item.IsPaused = false;
            item.EnCola = true;
            _downloadService.ReanudarDescarga(item.AniListId, item.NumeroEpisodio);
        }
        else
        {
            item.IsPaused = true;
            _downloadService.PausarDescarga(item.AniListId, item.NumeroEpisodio);
        }
        ActualizarConteo();
    }

    /// <summary>Adelanta una descarga en cola para que sea la siguiente en empezar.</summary>
    [RelayCommand]
    private void Priorizar(DescargaItem item)
    {
        if (item == null || !item.EnCola) return;
        if (!_downloadService.PriorizarDescarga(item.AniListId, item.NumeroEpisodio)) return;

        int idx = ColaDescargas.IndexOf(item);
        int destino = ColaDescargas.TakeWhile(d => !d.EnCola).Count();
        if (idx > destino) ColaDescargas.Move(idx, destino);
    }

    [RelayCommand]
    private void AlternarPausaTodas()
    {
        bool pausar = ColaDescargas.Any(d => !d.IsPaused);
        foreach (var d in ColaDescargas)
        {
            d.IsPaused = pausar;
            if (!pausar) d.EnCola = true;
        }

        if (pausar) _downloadService.PausarTodas();
        else _downloadService.ReanudarTodas();
        ActualizarConteo();
    }

    [RelayCommand]
    private void CancelarTodas()
    {
        _downloadService.CancelarTodas();
        ColaDescargas.Clear();
        ActualizarConteo();
    }

    // ── Comandos del historial ──

    [RelayCommand]
    private void Reproducir(DescargaHistorialItemViewModel item)
    {
        if (item == null || !item.ArchivoExiste) return;
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(item.RutaArchivo, item.AniListId, item.AnimeTitulo, item.NumeroEpisodio));
    }

    [RelayCommand]
    private void AbrirCarpeta(DescargaHistorialItemViewModel item)
    {
        if (item == null) return;
        try
        {
            string? argumentos = null;
            if (item.ArchivoExiste && !string.IsNullOrWhiteSpace(item.RutaArchivo)) argumentos = $"/select,\"{item.RutaArchivo}\"";
            else if (Directory.Exists(item.CarpetaDestino)) argumentos = $"\"{item.CarpetaDestino}\"";
            if (argumentos == null) return;

            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = argumentos, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DescargasViewModel", $"Error abriendo carpeta de descarga: {ex.Message}");
        }
    }

    /// <summary>Vuelve a encolar una descarga (fallida o cuyo archivo se perdió) y retira la fila antigua.</summary>
    [RelayCommand]
    private async Task ReintentarAsync(DescargaHistorialItemViewModel item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.CarpetaDestino)) return;

        var titulos = item.TitulosAlternativos.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await _downloadService.IniciarDescargaEpisodioAsync(item.AniListId, item.AnimeTitulo, item.CarpetaDestino, item.NumeroEpisodio, titulos);
        await EliminarFilaAsync(item);
        CargarDescargas();
        PestanaActual = "Activas";
    }

    [RelayCommand]
    private async Task ReintentarFallidasAsync()
    {
        foreach (var item in _historial.Where(i => !i.Completada).ToList())
        {
            if (string.IsNullOrWhiteSpace(item.CarpetaDestino)) continue;
            var titulos = item.TitulosAlternativos.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await _downloadService.IniciarDescargaEpisodioAsync(item.AniListId, item.AnimeTitulo, item.CarpetaDestino, item.NumeroEpisodio, titulos);
            await EliminarFilaAsync(item);
        }

        CargarDescargas();
        PestanaActual = "Activas";
    }

    [RelayCommand]
    private async Task QuitarDelHistorialAsync(DescargaHistorialItemViewModel item)
    {
        if (item == null) return;
        await EliminarFilaAsync(item);
    }

    [RelayCommand]
    private async Task LimpiarFallidasAsync()
    {
        if (_database == null) return;
        await _database.LimpiarDescargasHistorialAsync(soloFallidas: true);
        await CargarHistorialAsync();
    }

    [RelayCommand]
    private async Task LimpiarHistorialAsync()
    {
        if (_database == null) return;
        await _database.LimpiarDescargasHistorialAsync();
        await CargarHistorialAsync();
    }

    private async Task EliminarFilaAsync(DescargaHistorialItemViewModel item)
    {
        if (_database == null) return;
        await _database.EliminarDescargaHistorialAsync(item.Id);
        await CargarHistorialAsync();
    }

    [RelayCommand]
    private void Volver()
    {
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());
    }
}
