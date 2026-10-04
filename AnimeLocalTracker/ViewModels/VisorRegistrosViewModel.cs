using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Un archivo de registro en el selector, con su nombre legible.</summary>
public sealed record ArchivoRegistroOpcion(ArchivoRegistro Archivo, string Etiqueta)
{
    /// <summary>Lo que leen el selector y los lectores de pantalla (sin esto, el texto del record entero).</summary>
    public override string ToString() => Etiqueta;
}

/// <summary>Una entrada del visor. <see cref="Expandido"/> muestra la traza completa.</summary>
public sealed partial class EntradaRegistroItem : ObservableObject
{
    public EntradaRegistro Entrada { get; }
    public string HoraTexto { get; }
    public string Nivel => Entrada.Nivel;
    public string Fuente => Entrada.Fuente;
    public string Mensaje => Entrada.Mensaje;
    public string? Detalle => Entrada.Detalle;
    public bool TieneDetalle => !string.IsNullOrEmpty(Entrada.Detalle);
    public int LineasDetalle { get; }
    public bool EsContexto => Entrada.EsContexto;
    public bool EsCabecera => Entrada.EsCabecera;

    [ObservableProperty] private bool _expandido;

    /// <param name="conFecha">errores.log y el registro antiguo juntan varios días: se muestra también la fecha.</param>
    public EntradaRegistroItem(EntradaRegistro entrada, bool conFecha)
    {
        Entrada = entrada;
        HoraTexto = entrada.Fecha is DateTime f ? f.ToString(conFecha ? "dd/MM HH:mm:ss" : "HH:mm:ss.fff", LocalizationService.Cultura) : "";
        LineasDetalle = TieneDetalle ? entrada.Detalle!.Count(c => c == '\n') + 1 : 0;
    }

    /// <summary>La entrada como texto plano (para copiar), con la traza sangrada.</summary>
    public string ComoTexto()
    {
        if (EsCabecera) return $"==== {Mensaje} ====";
        var sb = new StringBuilder();
        sb.Append(HoraTexto).Append(' ').Append(Nivel.PadRight(5)).Append(EsContexto ? " · " : " ").Append('[').Append(Fuente).Append("] ").Append(Mensaje);
        if (TieneDetalle)
        {
            foreach (var linea in Detalle!.Split('\n')) sb.AppendLine().Append("    ").Append(linea);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Visor de registros dentro de la app (Configuración → Registro de diagnóstico → Ver registros): elige una
/// sesión, errores.log o el registro antiguo; filtra por nivel, fuente y texto; lo más reciente arriba. La
/// sesión actual se actualiza sola mientras la vista está abierta (<see cref="RefrescarEnVivoAsync"/>,
/// leyendo solo lo añadido). Los archivos se leen y filtran fuera del hilo de la interfaz.
/// </summary>
public partial class VisorRegistrosViewModel : ObservableObject, IRecipient<IdiomaCambiadoMensaje>
{
    /// <summary>Entradas que se copian como mucho (las más recientes de lo visible).</summary>
    internal const int MaxEntradasCopiadas = 3000;

    private static readonly TimeSpan RetrasoBusqueda = TimeSpan.FromMilliseconds(250);

    private readonly IDialogService? _dialogService;
    private readonly string _carpeta;
    private readonly string _rutaSesionActual;

    /// <summary>Todas las entradas del archivo elegido, la más reciente primero.</summary>
    private List<EntradaRegistroItem> _todas = new();
    private long _leidoHasta;
    private int _versionCarga;
    private int _versionFiltro;
    private bool _suprimirCarga;
    private bool _suprimirFiltro;
    private CancellationTokenSource? _retrasoBusqueda;

    public ObservableCollection<ArchivoRegistroOpcion> Archivos { get; } = new();
    public ObservableCollection<string> Fuentes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsSesionActual))]
    private ArchivoRegistroOpcion? _archivoSeleccionado;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneEntradas))]
    [NotifyPropertyChangedFor(nameof(SinResultados))]
    [NotifyPropertyChangedFor(nameof(TextoResumen))]
    private IReadOnlyList<EntradaRegistroItem> _entradasVisibles = Array.Empty<EntradaRegistroItem>();

    [ObservableProperty] private string? _fuenteSeleccionada;
    [ObservableProperty] private bool _mostrarDebug = true;
    [ObservableProperty] private bool _mostrarInfo = true;
    [ObservableProperty] private bool _mostrarAvisos = true;
    [ObservableProperty] private bool _mostrarErrores = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneBusqueda))]
    private string _textoBusqueda = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchivoVacio))]
    [NotifyPropertyChangedFor(nameof(SinResultados))]
    private bool _estaCargando;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArchivoVacio))]
    [NotifyPropertyChangedFor(nameof(SinResultados))]
    [NotifyPropertyChangedFor(nameof(TextoResumen))]
    private int _totalEntradas;

    [ObservableProperty] private int _numErrores;
    [ObservableProperty] private int _numAvisos;
    [ObservableProperty] private int _numInfo;
    [ObservableProperty] private int _numDebug;

    public bool EsSesionActual => ArchivoSeleccionado?.Archivo.Tipo == TipoArchivoRegistro.SesionActual;

    /// <summary>La sesión actual ya está leída y en pantalla: al volver a la vista basta con añadir lo nuevo.</summary>
    public bool SesionActualYaCargada => EsSesionActual && !EstaCargando && _leidoHasta > 0;
    public bool TieneEntradas => EntradasVisibles.Count > 0;
    public bool ArchivoVacio => !EstaCargando && TotalEntradas == 0;
    public bool SinResultados => !EstaCargando && TotalEntradas > 0 && EntradasVisibles.Count == 0;
    public bool TieneBusqueda => TextoBusqueda.Length > 0;
    // El separador de sesión no es una entrada: no cuenta ni en lo visible ni en el total.
    public string TextoResumen => string.Format(LocalizationService.T("Reg_Resumen"), EntradasVisibles.Count(e => !e.EsCabecera), TotalEntradas);

    /// <param name="carpeta">Carpeta de registros (tests: una temporal). Por defecto la de <see cref="AppLogger"/>.</param>
    /// <param name="rutaSesionActual">Archivo de la sesión en curso. Por defecto <see cref="AppLogger.RutaSesion"/>.</param>
    public VisorRegistrosViewModel(IDialogService? dialogService = null, string? carpeta = null, string? rutaSesionActual = null)
    {
        _dialogService = dialogService;
        _carpeta = carpeta ?? AppLogger.Carpeta;
        _rutaSesionActual = rutaSesionActual ?? AppLogger.RutaSesion;
        WeakReferenceMessenger.Default.Register<IdiomaCambiadoMensaje>(this);
    }

    public void Receive(IdiomaCambiadoMensaje message)
    {
        var seleccionada = ArchivoSeleccionado?.Archivo.Ruta;
        _suprimirCarga = true;
        RellenarArchivos(Archivos.Select(a => a.Archivo).ToList(), seleccionada);
        _suprimirCarga = false;
        ReconstruirFuentes();
        OnPropertyChanged(nameof(TextoResumen));
    }

    /// <summary>Al entrar en la vista: lista los archivos y abre la sesión actual (o conserva la elegida).</summary>
    public async Task CargarAsync()
    {
        AppLogger.Flush(); // que lo último ya esté en el archivo
        var seleccionada = ArchivoSeleccionado?.Archivo.Ruta;
        List<ArchivoRegistro> archivos;
        try
        {
            archivos = await Task.Run(() => LectorRegistros.ListarArchivos(_carpeta, _rutaSesionActual));
        }
        catch (Exception ex)
        {
            AppLogger.Error("VisorRegistrosViewModel", "No se pudo listar la carpeta de registros", ex);
            archivos = new List<ArchivoRegistro>();
        }

        _suprimirCarga = true;
        RellenarArchivos(archivos, seleccionada);
        _suprimirCarga = false;
        await CargarArchivoAsync();
    }

    private void RellenarArchivos(List<ArchivoRegistro> archivos, string? rutaSeleccionada)
    {
        Archivos.Clear();
        foreach (var a in archivos) Archivos.Add(new ArchivoRegistroOpcion(a, Etiqueta(a)));
        ArchivoSeleccionado = Archivos.FirstOrDefault(a => string.Equals(a.Archivo.Ruta, rutaSeleccionada, StringComparison.OrdinalIgnoreCase))
                              ?? Archivos.FirstOrDefault();
    }

    /// <summary>"Sesión actual (desde 00:40)", "Hoy 00:40 · 12 KB", "Solo avisos y errores (todas las sesiones)"…</summary>
    internal static string Etiqueta(ArchivoRegistro a)
    {
        var cultura = LocalizationService.Cultura;
        string tamano = Core.Formato.Tamano(a.Bytes);
        switch (a.Tipo)
        {
            case TipoArchivoRegistro.SesionActual:
                return string.Format(LocalizationService.T("Reg_SesionActual"), a.Inicio?.ToString("HH:mm", cultura) ?? "");
            case TipoArchivoRegistro.Errores:
                return $"{LocalizationService.T("Reg_SoloErrores")} · {tamano}";
            case TipoArchivoRegistro.Antiguo:
                return $"{LocalizationService.T("Reg_Antiguo")} · {tamano}";
            default:
                var inicio = a.Inicio ?? DateTime.MinValue;
                string dia = inicio.Date == DateTime.Today ? LocalizationService.T("Reg_Hoy")
                    : inicio.Date == DateTime.Today.AddDays(-1) ? LocalizationService.T("Reg_Ayer")
                    : inicio.ToString("d", cultura);
                return $"{dia} {inicio.ToString("HH:mm", cultura)} · {tamano}";
        }
    }

    partial void OnArchivoSeleccionadoChanged(ArchivoRegistroOpcion? value)
    {
        if (!_suprimirCarga && value != null) _ = CargarArchivoAsync();
    }

    private async Task CargarArchivoAsync()
    {
        var opcion = ArchivoSeleccionado;
        if (opcion == null)
        {
            _todas = new List<EntradaRegistroItem>();
            RecalcularContadores();
            ReconstruirFuentes();
            EntradasVisibles = Array.Empty<EntradaRegistroItem>();
            return;
        }

        int version = ++_versionCarga;
        EstaCargando = true;
        try
        {
            var archivo = opcion.Archivo;
            bool conFecha = archivo.Tipo is TipoArchivoRegistro.Errores or TipoArchivoRegistro.Antiguo;
            var (entradas, hasta) = await Task.Run(() =>
            {
                var (texto, siguiente) = LectorRegistros.LeerDesde(archivo.Ruta, 0);
                var items = LectorRegistros.Analizar(texto, archivo.Inicio)
                    .Select(e => new EntradaRegistroItem(e, conFecha))
                    .Reverse()
                    .ToList();
                return (items, siguiente);
            });
            if (version != _versionCarga) return; // se eligió otro archivo mientras tanto

            _todas = entradas;
            _leidoHasta = hasta;
            RecalcularContadores();
            ReconstruirFuentes();
            await AplicarFiltrosAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("VisorRegistrosViewModel", $"No se pudo leer {Path.GetFileName(opcion.Archivo.Ruta)}", ex);
            _todas = new List<EntradaRegistroItem>();
            RecalcularContadores();
            EntradasVisibles = Array.Empty<EntradaRegistroItem>();
        }
        finally
        {
            if (version == _versionCarga) EstaCargando = false;
        }
    }

    /// <summary>
    /// Añade lo que se escribió en la sesión actual desde la última lectura (la vista lo llama cada pocos
    /// segundos mientras está abierta). Solo lee los bytes nuevos.
    /// </summary>
    public async Task RefrescarEnVivoAsync()
    {
        if (!EsSesionActual || EstaCargando || ArchivoSeleccionado == null) return;
        string ruta = ArchivoSeleccionado.Archivo.Ruta;
        var inicio = ArchivoSeleccionado.Archivo.Inicio;
        long desde = _leidoHasta;
        int version = _versionCarga;
        try
        {
            var (nuevas, hasta) = await Task.Run(() =>
            {
                var (texto, siguiente) = LectorRegistros.LeerDesde(ruta, desde);
                return (LectorRegistros.Analizar(texto, inicio).Select(e => new EntradaRegistroItem(e, false)).Reverse().ToList(), siguiente);
            });
            if (version != _versionCarga || nuevas.Count == 0)
            {
                if (version == _versionCarga) _leidoHasta = hasta;
                return;
            }

            // El archivo se rotó (volvió a empezar): se sustituye en vez de añadir.
            bool rotado = hasta < desde || desde == 0;
            _todas = rotado ? nuevas : nuevas.Concat(_todas).ToList();
            _leidoHasta = hasta;
            RecalcularContadores();
            AnadirFuentesNuevas(nuevas);

            // Lo normal: unas pocas líneas nuevas sobre una lista ya en pantalla. Se insertan arriba en la misma lista en vez
            // de sustituirla entera (sustituirla obligaba a redibujar todas las filas visibles cada pocos segundos y en
            // cada visita). Con el archivo rotado o un filtro a medio aplicar, se rehace como siempre.
            if (!rotado && _filtrosEnCurso == 0 && EntradasVisibles is ObservableCollection<EntradaRegistroItem> enPantalla)
            {
                var (niveles, fuente, texto) = FiltroActual();
                var visiblesNuevas = Filtrar(nuevas, niveles, fuente, texto);
                for (int i = 0; i < visiblesNuevas.Count; i++) enPantalla.Insert(i, visiblesNuevas[i]);
                OnPropertyChanged(nameof(TieneEntradas));
                OnPropertyChanged(nameof(SinResultados));
                OnPropertyChanged(nameof(TextoResumen));
            }
            else
            {
                await AplicarFiltrosAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("VisorRegistrosViewModel", $"No se pudo actualizar la sesión actual: {ex.Message}");
        }
    }

    private void RecalcularContadores()
    {
        int errores = 0, avisos = 0, info = 0, debug = 0;
        foreach (var e in _todas)
        {
            if (e.EsCabecera) continue;
            switch (e.Nivel)
            {
                case "ERROR": errores++; break;
                case "WARN": avisos++; break;
                case "INFO": info++; break;
                default: debug++; break;
            }
        }
        NumErrores = errores;
        NumAvisos = avisos;
        NumInfo = info;
        NumDebug = debug;
        TotalEntradas = errores + avisos + info + debug;
    }

    /// <summary>Fuentes del archivo, las que más escriben primero, tras "Todas".</summary>
    private void ReconstruirFuentes()
    {
        string? elegida = EsTodas(FuenteSeleccionada) ? null : FuenteSeleccionada;
        _suprimirFiltro = true;
        Fuentes.Clear();
        Fuentes.Add(LocalizationService.T("Reg_TodasFuentes"));
        foreach (var f in _todas.Where(e => !e.EsCabecera && e.Fuente.Length > 0)
                                .GroupBy(e => e.Fuente).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key))
        {
            Fuentes.Add(f);
        }
        FuenteSeleccionada = elegida != null && Fuentes.Contains(elegida) ? elegida : Fuentes[0];
        _suprimirFiltro = false;
    }

    private void AnadirFuentesNuevas(IEnumerable<EntradaRegistroItem> nuevas)
    {
        foreach (var f in nuevas.Where(e => !e.EsCabecera && e.Fuente.Length > 0).Select(e => e.Fuente).Distinct())
        {
            if (!Fuentes.Contains(f)) Fuentes.Add(f);
        }
    }

    private bool EsTodas(string? fuente) => fuente == null || Fuentes.Count == 0 || fuente == Fuentes[0];

    partial void OnFuenteSeleccionadaChanged(string? value) { if (!_suprimirFiltro) _ = AplicarFiltrosAsync(); }
    partial void OnMostrarDebugChanged(bool value) { if (!_suprimirFiltro) _ = AplicarFiltrosAsync(); }
    partial void OnMostrarInfoChanged(bool value) { if (!_suprimirFiltro) _ = AplicarFiltrosAsync(); }
    partial void OnMostrarAvisosChanged(bool value) { if (!_suprimirFiltro) _ = AplicarFiltrosAsync(); }
    partial void OnMostrarErroresChanged(bool value) { if (!_suprimirFiltro) _ = AplicarFiltrosAsync(); }

    partial void OnTextoBusquedaChanged(string value)
    {
        if (_suprimirFiltro) return;
        _retrasoBusqueda?.Cancel();
        var cts = _retrasoBusqueda = new CancellationTokenSource();
        _ = BuscarConRetrasoAsync(cts.Token);
    }

    /// <summary>Espera a que se deje de escribir un momento antes de filtrar (no refiltra en cada tecla).</summary>
    private async Task BuscarConRetrasoAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(RetrasoBusqueda, ct);
            await AplicarFiltrosAsync();
        }
        catch (OperationCanceledException)
        {
            // Se siguió escribiendo: filtra la búsqueda siguiente.
        }
    }

    /// <summary>Filtra fuera del hilo de la interfaz y publica el resultado de una sola vez.</summary>
    internal async Task AplicarFiltrosAsync()
    {
        int version = ++_versionFiltro;
        var todas = _todas;
        var (niveles, fuente, texto) = FiltroActual();

        List<EntradaRegistroItem> visibles;
        _filtrosEnCurso++;
        try
        {
            visibles = await Task.Run(() => Filtrar(todas, niveles, fuente, texto));
        }
        finally
        {
            _filtrosEnCurso--;
        }
        if (version != _versionFiltro) return; // llegó un filtro más nuevo
        // Colección observable: la actualización en vivo le añade las líneas nuevas sin sustituirla (ver RefrescarEnVivoAsync).
        EntradasVisibles = new ObservableCollection<EntradaRegistroItem>(visibles);
    }

    /// <summary>Filtros que se están aplicando fuera del hilo de la interfaz (solo se toca desde ese hilo).</summary>
    private int _filtrosEnCurso;

    private (HashSet<string> Niveles, string? Fuente, string Texto) FiltroActual()
    {
        var niveles = new HashSet<string>(StringComparer.Ordinal);
        if (MostrarDebug) niveles.Add("DEBUG");
        if (MostrarInfo) niveles.Add("INFO");
        if (MostrarAvisos) niveles.Add("WARN");
        if (MostrarErrores) niveles.Add("ERROR");
        return (niveles, EsTodas(FuenteSeleccionada) ? null : FuenteSeleccionada, TextoBusqueda.Trim());
    }

    internal static List<EntradaRegistroItem> Filtrar(IEnumerable<EntradaRegistroItem> todas, ISet<string> niveles, string? fuente, string texto)
    {
        return todas.Where(e =>
        {
            if (e.EsCabecera) return fuente == null && texto.Length == 0;
            if (!niveles.Contains(e.Nivel)) return false;
            if (fuente != null && !string.Equals(e.Fuente, fuente, StringComparison.Ordinal)) return false;
            if (texto.Length == 0) return true;
            return e.Mensaje.Contains(texto, StringComparison.OrdinalIgnoreCase)
                || e.Fuente.Contains(texto, StringComparison.OrdinalIgnoreCase)
                || (e.Detalle?.Contains(texto, StringComparison.OrdinalIgnoreCase) ?? false);
        }).ToList();
    }

    /// <summary>Lo visible en orden cronológico (como se lee un registro), las <see cref="MaxEntradasCopiadas"/> más recientes.</summary>
    internal string TextoParaCopiar()
    {
        var entradas = EntradasVisibles.Take(MaxEntradasCopiadas).Reverse();
        var sb = new StringBuilder();
        if (ArchivoSeleccionado != null) sb.AppendLine($"# {ArchivoSeleccionado.Etiqueta} — {Path.GetFileName(ArchivoSeleccionado.Archivo.Ruta)}");
        foreach (var e in entradas) sb.AppendLine(e.ComoTexto());
        return sb.ToString();
    }

    [RelayCommand]
    private Task Actualizar() => CargarAsync();

    [RelayCommand]
    private void CopiarVisibles()
    {
        if (EntradasVisibles.Count == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(TextoParaCopiar());
            int copiadas = Math.Min(EntradasVisibles.Count, MaxEntradasCopiadas);
            _dialogService?.MostrarToast(LocalizationService.T("Reg_Titulo"),
                string.Format(LocalizationService.T("Reg_Copiadas"), copiadas), "ContentCopy", "#2563EB");
        }
        catch (Exception ex)
        {
            // El portapapeles puede estar ocupado por otra aplicación un instante.
            AppLogger.Warn("VisorRegistrosViewModel", $"No se pudo copiar al portapapeles: {ex.Message}");
            _dialogService?.MostrarToast(LocalizationService.T("Reg_Titulo"), LocalizationService.T("Reg_ErrorCopiar"), "AlertCircleOutline", "#F59E0B");
        }
    }

    [RelayCommand]
    private void CopiarEntrada(EntradaRegistroItem? item)
    {
        if (item == null) return;
        try
        {
            System.Windows.Clipboard.SetText(item.ComoTexto());
            _dialogService?.MostrarToast(LocalizationService.T("Reg_Titulo"), LocalizationService.T("Reg_EntradaCopiada"), "ContentCopy", "#2563EB");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("VisorRegistrosViewModel", $"No se pudo copiar al portapapeles: {ex.Message}");
        }
    }

    [RelayCommand]
    private void AbrirCarpeta()
    {
        try
        {
            AppLogger.Flush();
            Directory.CreateDirectory(_carpeta);
            Core.Shell.Abrir(_carpeta);
        }
        catch (Exception ex)
        {
            AppLogger.Error("VisorRegistrosViewModel", "Error abriendo la carpeta de registros", ex);
        }
    }

    [RelayCommand]
    private void Volver() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Configuracion());

    [RelayCommand]
    private void AlternarDetalle(EntradaRegistroItem? item)
    {
        if (item?.TieneDetalle == true) item.Expandido = !item.Expandido;
    }

    /// <summary>Solo avisos y errores (lo primero que se mira cuando algo falla).</summary>
    [RelayCommand]
    private Task SoloProblemas()
    {
        _suprimirFiltro = true;
        MostrarDebug = false;
        MostrarInfo = false;
        MostrarAvisos = true;
        MostrarErrores = true;
        _suprimirFiltro = false;
        return AplicarFiltrosAsync();
    }

    [RelayCommand]
    private Task LimpiarFiltros()
    {
        _suprimirFiltro = true;
        MostrarDebug = MostrarInfo = MostrarAvisos = MostrarErrores = true;
        FuenteSeleccionada = Fuentes.FirstOrDefault();
        TextoBusqueda = "";
        _suprimirFiltro = false;
        _retrasoBusqueda?.Cancel();
        return AplicarFiltrosAsync();
    }
}
