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
/// (completadas y fallidas persistidas, con reproducir / abrir carpeta / reintentar). Incluye también los openings/endings
/// descargados desde la ficha (AnimeThemes): son descargas como las demás, aunque sin pausa ni prioridad (duran segundos).
/// </summary>
public partial class DescargasViewModel : ObservableObject, IRecipient<DescargaProgresoMensaje>, IRecipient<DescargaHistorialActualizadoMensaje>,
    IRecipient<DescargaMusicaProgresoMensaje>
{
    public const string FiltroTodas = "Todas";
    public const string FiltroCompletadas = "Completadas";
    public const string FiltroFallidas = "Fallidas";

    private readonly IDownloadService _downloadService;
    private readonly IDatabaseService? _database;
    private readonly IAnimeThemesDownloadService? _descargasMusica;
    private readonly IAnimeThemesService? _catalogoMusica;
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
    private bool _tieneDescargas;

    /// <summary>Hay alguna descarga de episodio (las de música no se pausan): solo entonces tiene sentido "Pausar todas".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarPausarTodas))]
    private bool _tienePausables;

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

    public bool MostrarPausarTodas => EsPestanaActivas && TienePausables;
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

    public DescargasViewModel(IDownloadService downloadService, IDatabaseService? database = null,
        IAnimeThemesDownloadService? descargasMusica = null, IAnimeThemesService? catalogoMusica = null)
    {
        _downloadService = downloadService;
        _database = database;
        _descargasMusica = descargasMusica;
        _catalogoMusica = catalogoMusica;
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(this);
        WeakReferenceMessenger.Default.Register<DescargaHistorialActualizadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<DescargaMusicaProgresoMensaje>(this);

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
        foreach (var m in _descargasMusica?.ObtenerDescargasMusicaActivas() ?? [])
        {
            m.VelocidadDescarga = m.VelocidadBps > 0 ? FormatearVelocidad(m.VelocidadBps) : string.Empty;
            ColaDescargas.Add(m);
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

        // Solo se recrean los chips si algo cambió: ActualizarConteo se llama en cada tick de progreso y, si se
        // regeneraran los botones varias veces por segundo, el clic (bajada + subida) caía sobre un botón ya
        // sustituido y "no hacía nada" hasta insistir.
        var pestanas = new (string Clave, string Texto, bool Activo)[]
        {
            ("Activas", Etiqueta("Desc_TabActivas", ColaDescargas.Count), EsPestanaActivas),
            ("Historial", Etiqueta("Desc_TabHistorial", TotalHistorial), EsPestanaHistorial)
        };
        var filtros = new (string Clave, string Texto, bool Activo)[]
        {
            (FiltroTodas, Etiqueta("Desc_FiltroTodas", TotalHistorial), FiltroHistorial == FiltroTodas),
            (FiltroCompletadas, Etiqueta("Desc_FiltroCompletadas", TotalCompletadas), FiltroHistorial == FiltroCompletadas),
            (FiltroFallidas, Etiqueta("Desc_FiltroFallidas", TotalFallidas), FiltroHistorial == FiltroFallidas)
        };

        if (!MismosChips(Pestanas, pestanas)) Pestanas = new ObservableCollection<FiltroChip>(pestanas.Select(c => new FiltroChip(c.Clave, c.Texto, c.Activo)));
        if (!MismosChips(Filtros, filtros)) Filtros = new ObservableCollection<FiltroChip>(filtros.Select(c => new FiltroChip(c.Clave, c.Texto, c.Activo)));
    }

    private static bool MismosChips(ObservableCollection<FiltroChip> actuales, (string Clave, string Texto, bool Activo)[] nuevos) =>
        actuales.Count == nuevos.Length &&
        actuales.Zip(nuevos).All(p => p.First.Clave == p.Second.Clave && p.First.Etiqueta == p.Second.Texto && p.First.EsActivo == p.Second.Activo);

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
                i.TitulosAlternativos.Contains(texto, StringComparison.CurrentCultureIgnoreCase) ||
                i.TemaTitulo.Contains(texto, StringComparison.CurrentCultureIgnoreCase));
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
            item.SinConexion = message.SinConexion;
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
                RetirarTrasCompletar(item);
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
                SinConexion = message.SinConexion,
                VelocidadBps = message.VelocidadBps,
                VelocidadDescarga = message.VelocidadDescarga ?? string.Empty
            });
            ActualizarConteo();
        }
    }

    /// <summary>La fila queda un instante al 100 % y se retira; el resultado ya vive en el historial.</summary>
    private void RetirarTrasCompletar(DescargaItem item)
    {
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

    public void Receive(DescargaMusicaProgresoMensaje message)
    {
        // Sin Application (tests headless) el mensaje no se procesa, como el de los episodios.
        Application.Current?.Dispatcher.InvokeAsync(() => AplicarMensajeMusica(message));
    }

    /// <summary>Una descarga de música (opening/ending) avanzó, terminó, falló o se canceló.</summary>
    internal void AplicarMensajeMusica(DescargaMusicaProgresoMensaje m)
    {
        var item = ColaDescargas.FirstOrDefault(d => d.EsMusica && d.AniListId == m.AniListId && d.TemaClave == m.TemaClave);

        if (m.Terminada)
        {
            if (item == null) return;
            if (m.Completada)
            {
                item.Convirtiendo = false;
                item.Progreso = 100;
                item.IsCompleted = true;
                item.RutaArchivo = m.RutaArchivo ?? string.Empty;
                RetirarTrasCompletar(item);
            }
            else
            {
                ColaDescargas.Remove(item); // fallida (ya está en el historial) o cancelada
            }
            ActualizarConteo();
            return;
        }

        if (item == null)
        {
            item = new DescargaItem
            {
                EsMusica = true,
                AniListId = m.AniListId,
                AnimeTitulo = m.AnimeTitulo,
                TemaClave = m.TemaClave,
                TemaTitulo = m.TemaTitulo,
                Fuente = "AnimeThemes",
                IsDownloading = true
            };
            ColaDescargas.Add(item);
        }

        item.Progreso = m.Progreso;
        item.Convirtiendo = m.Convirtiendo;
        item.VelocidadBps = m.VelocidadBps;
        item.VelocidadDescarga = m.VelocidadBps > 0 ? FormatearVelocidad(m.VelocidadBps) : string.Empty;
        ActualizarConteo();
    }

    private void ActualizarConteo()
    {
        ConteoActivas = ColaDescargas.Count(d => d.IsDownloading && !d.IsPaused);
        ConteoEnCola = ColaDescargas.Count(d => d.EnCola && !d.IsPaused);
        TieneDescargas = ColaDescargas.Count > 0;
        // Las de música no se pausan: "todas en pausa" mira solo los episodios.
        var pausables = ColaDescargas.Where(d => !d.EsMusica).ToList();
        TienePausables = pausables.Count > 0;
        TodasPausadas = pausables.Count > 0 && pausables.All(d => d.IsPaused);

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
        if (item.EsMusica) _descargasMusica?.CancelarDescargaMusica(item.AniListId, item.TemaClave);
        else _downloadService.CancelarDescarga(item.AniListId, item.NumeroEpisodio);
        ColaDescargas.Remove(item);
        ActualizarConteo();
    }

    [RelayCommand]
    private void AlternarPausaDescarga(DescargaItem item)
    {
        if (item == null || item.EsMusica) return;
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
        if (item == null || !item.EnCola || item.EsMusica) return;
        if (!_downloadService.PriorizarDescarga(item.AniListId, item.NumeroEpisodio)) return;

        int idx = ColaDescargas.IndexOf(item);
        int destino = ColaDescargas.TakeWhile(d => !d.EnCola).Count();
        if (idx > destino) ColaDescargas.Move(idx, destino);
    }

    [RelayCommand]
    private void AlternarPausaTodas()
    {
        bool pausar = ColaDescargas.Any(d => !d.EsMusica && !d.IsPaused);
        foreach (var d in ColaDescargas.Where(d => !d.EsMusica))
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
        _descargasMusica?.CancelarTodasMusica();
        ColaDescargas.Clear();
        ActualizarConteo();
    }

    // ── Comandos del historial ──

    [RelayCommand]
    private async Task ReproducirAsync(DescargaHistorialItemViewModel item)
    {
        if (item == null || !item.ArchivoExiste) return;

        if (item.EsMusica)
        {
            await ReproducirMusicaAsync(item);
            return;
        }
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(item.RutaArchivo, item.AniListId, item.AnimeTitulo, item.NumeroEpisodio));
    }

    /// <summary>
    /// Un opening/ending se escucha en su sitio: la ficha del anime, con el panel de música abierto y ese tema sonando. Si el
    /// anime ya no está en la biblioteca, se abre con el reproductor de música de Windows.
    /// </summary>
    private async Task ReproducirMusicaAsync(DescargaHistorialItemViewModel item)
    {
        try
        {
            var anime = _database == null ? null : await _database.ObtenerAnimePorIdAsync(item.AniListId);
            if (anime != null)
            {
                WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(anime, item.TemaClave));
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = item.RutaArchivo, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DescargasViewModel", $"No se pudo reproducir '{item.TemaTitulo}': {ex.Message}");
        }
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

        if (item.EsMusica)
        {
            if (!await ReintentarMusicaAsync(item)) return;
            await EliminarFilaAsync(item);
            CargarDescargas();
            PestanaActual = "Activas";
            return;
        }

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
            if (item.EsMusica)
            {
                if (await ReintentarMusicaAsync(item)) await EliminarFilaAsync(item);
                continue;
            }
            var titulos = item.TitulosAlternativos.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await _downloadService.IniciarDescargaEpisodioAsync(item.AniListId, item.AnimeTitulo, item.CarpetaDestino, item.NumeroEpisodio, titulos);
            await EliminarFilaAsync(item);
        }

        CargarDescargas();
        PestanaActual = "Activas";
    }

    /// <summary>
    /// Vuelve a pedir un opening/ending: busca el tema en la lista de AnimeThemes (guardada o de la red) por su clave y lo
    /// descarga igual que desde la ficha. False si ya no existe o no hay servicio.
    /// </summary>
    private async Task<bool> ReintentarMusicaAsync(DescargaHistorialItemViewModel item)
    {
        if (_descargasMusica == null || _catalogoMusica == null || string.IsNullOrWhiteSpace(item.TemaClave)) return false;

        try
        {
            var catalogo = await _catalogoMusica.ObtenerTemasAsync(item.AniListId);
            var tema = catalogo.FirstOrDefault(t => t.ClaveEstable() == item.TemaClave && !string.IsNullOrWhiteSpace(t.AudioUrlOgg));
            if (tema == null)
            {
                AppLogger.Debug("DescargasViewModel", $"'{item.TemaTitulo}' ya no está en AnimeThemes (o no hay conexión): no se puede reintentar.");
                return false;
            }

            // Se registra al instante (antes de su primera espera), así que CargarDescargas ya la ve en "Activas".
            _ = _descargasMusica.DescargarYConvertirAsync(item.AniListId, tema, null, System.Threading.CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DescargasViewModel", $"No se pudo reintentar '{item.TemaTitulo}': {ex.Message}");
            return false;
        }
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
