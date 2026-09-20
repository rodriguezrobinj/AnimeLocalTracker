using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Logros;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Botón de filtro (categoría o estado) con su etiqueta ya localizada.</summary>
public sealed partial class FiltroChip : ObservableObject
{
    public string Clave { get; }
    public string Etiqueta { get; }

    [ObservableProperty] private bool _esActivo;

    public FiltroChip(string clave, string etiqueta, bool esActivo)
    {
        Clave = clave;
        Etiqueta = etiqueta;
        _esActivo = esActivo;
    }
}

/// <summary>
/// Pestaña "Logros": catálogo completo con filtros. Es una lista virtualizada agrupada por
/// categoría (cabeceras de tipo string + tarjetas <see cref="LogroItemViewModel"/>), ver ui-wpf-vistas.md.
/// </summary>
public partial class LogrosViewModel : ObservableObject, IRecipient<IdiomaCambiadoMensaje>
{
    public const string TodasLasCategorias = "Todas";
    public const string EstadoTodos = "Todos";
    public const string EstadoEnProgreso = "EnProgreso";
    public const string EstadoDesbloqueados = "Desbloqueados";
    public const string EstadoBloqueados = "Bloqueados";

    // NAV-01: como Historial/Estadísticas, no reevalúa en cada visita a la pestaña.
    private static readonly TimeSpan CooldownRecarga = TimeSpan.FromSeconds(30);

    private readonly ILogrosService _logrosService;
    private ResumenLogros _resumen = ResumenLogros.Vacio;
    private List<LogroItemViewModel> _todos = new();
    private DateTime _ultimaCargaUtc = DateTime.MinValue;

    [ObservableProperty] private bool _estaCargando;
    [ObservableProperty] private ObservableCollection<object> _itemsAgrupados = new();
    [ObservableProperty] private ObservableCollection<FiltroChip> _categorias = new();
    [ObservableProperty] private ObservableCollection<FiltroChip> _estados = new();

    // Resumen (rango, puntos y niveles por dificultad)
    [ObservableProperty] private string _rangoNombre = string.Empty;
    [ObservableProperty] private string _puntosTexto = string.Empty;
    [ObservableProperty] private string _siguienteRangoTexto = string.Empty;
    [ObservableProperty] private double _progresoRango;
    [ObservableProperty] private string _nivelesTexto = string.Empty;
    [ObservableProperty] private double _progresoTotal;
    [ObservableProperty] private List<ContadorNivel> _contadores = new();

    private string _categoriaActual = TodasLasCategorias;
    private string _estadoActual = EstadoTodos;

    public bool TieneElementos => ItemsAgrupados.Count > 0;
    public bool EstaVacio => !EstaCargando && _todos.Count == 0;
    public bool SinResultados => !EstaCargando && _todos.Count > 0 && ItemsAgrupados.Count == 0;

    public LogrosViewModel(ILogrosService logrosService)
    {
        _logrosService = logrosService;
        ConstruirFiltros();
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public bool NecesitaRecargar() =>
        _todos.Count == 0 || (DateTime.UtcNow - _ultimaCargaUtc) >= CooldownRecarga;

    [RelayCommand]
    public async Task CargarAsync()
    {
        if (EstaCargando) return;

        try
        {
            EstaCargando = true;
            NotificarEstados();
            _ultimaCargaUtc = DateTime.UtcNow;

            _resumen = await _logrosService.EvaluarAsync();
            Reconstruir();
        }
        catch (Exception ex)
        {
            AppLogger.Error("LogrosViewModel", "Error al cargar los logros", ex);
        }
        finally
        {
            EstaCargando = false;
            NotificarEstados();
        }
    }

    [RelayCommand]
    private void SeleccionarCategoria(string? clave)
    {
        _categoriaActual = clave ?? TodasLasCategorias;
        foreach (var chip in Categorias) chip.EsActivo = chip.Clave == _categoriaActual;
        AplicarFiltros();
    }

    [RelayCommand]
    private void SeleccionarEstado(string? clave)
    {
        _estadoActual = clave ?? EstadoTodos;
        foreach (var chip in Estados) chip.EsActivo = chip.Clave == _estadoActual;
        AplicarFiltros();
    }

    public void Receive(IdiomaCambiadoMensaje message)
    {
        // Los textos de tarjetas, chips y resumen se construyen con LocalizationService.T(): rehacerlos.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Reconstruir);
            return;
        }
        Reconstruir();
    }

    private void Reconstruir()
    {
        ConstruirFiltros();
        _todos = _resumen.Logros.Select(l => new LogroItemViewModel(l)).ToList();

        NivelesTexto = string.Format(LocalizationService.T("Logro_NivelesFormato"), _resumen.NivelesDesbloqueados, _resumen.NivelesTotales);
        ProgresoTotal = _resumen.NivelesTotales > 0 ? _resumen.NivelesDesbloqueados * 100.0 / _resumen.NivelesTotales : 0;

        RangoNombre = LocalizationService.T(_resumen.Rango.ClaveNombre);
        PuntosTexto = string.Format(LocalizationService.T("Logro_PuntosTotalFormato"), _resumen.Puntos);
        ProgresoRango = _resumen.Rango.ProgresoHaciaSiguiente(_resumen.Puntos) * 100.0;
        SiguienteRangoTexto = _resumen.Rango.PuntosSiguiente is int siguiente
            ? string.Format(LocalizationService.T("Logro_SiguienteRangoFormato"), siguiente - _resumen.Puntos,
                LocalizationService.T($"Logro_Rango_{_resumen.Rango.Indice + 1}"))
            : LocalizationService.T("Logro_RangoMaximo");

        Contadores = Enum.GetValues<NivelLogro>()
            .Select(n => new ContadorNivel(
                LocalizationService.T($"Logro_Nivel_{(int)n}"),
                LogroItemViewModel.ColorDeNivel((int)n),
                _resumen.PorNivel.GetValueOrDefault(n)))
            .ToList();

        AplicarFiltros();
    }

    private void ConstruirFiltros()
    {
        Categorias = new ObservableCollection<FiltroChip>(
            new[] { new FiltroChip(TodasLasCategorias, LocalizationService.T("Logro_Cat_Todas"), _categoriaActual == TodasLasCategorias) }
                .Concat(Enum.GetValues<CategoriaLogro>().Select(c =>
                    new FiltroChip(c.ToString(), LocalizationService.T($"Logro_Cat_{c}"), _categoriaActual == c.ToString()))));

        Estados = new ObservableCollection<FiltroChip>(new[]
        {
            new FiltroChip(EstadoTodos, LocalizationService.T("Logro_Est_Todos"), _estadoActual == EstadoTodos),
            new FiltroChip(EstadoEnProgreso, LocalizationService.T("Logro_Est_EnProgreso"), _estadoActual == EstadoEnProgreso),
            new FiltroChip(EstadoDesbloqueados, LocalizationService.T("Logro_Est_Desbloqueados"), _estadoActual == EstadoDesbloqueados),
            new FiltroChip(EstadoBloqueados, LocalizationService.T("Logro_Est_Bloqueados"), _estadoActual == EstadoBloqueados),
        });
    }

    private void AplicarFiltros()
    {
        IEnumerable<LogroItemViewModel> consulta = _todos;

        if (_categoriaActual != TodasLasCategorias)
        {
            consulta = consulta.Where(l => l.Categoria.ToString() == _categoriaActual);
        }

        consulta = _estadoActual switch
        {
            EstadoEnProgreso => consulta.Where(l => !l.Completo && !l.Oculto && l.ProgresoPorcentaje > 0),
            EstadoDesbloqueados => consulta.Where(l => l.Desbloqueado),
            EstadoBloqueados => consulta.Where(l => !l.Desbloqueado),
            _ => consulta
        };

        // Cabecera de categoría + sus tarjetas, conservando el orden del catálogo.
        var salida = new List<object>();
        foreach (var grupo in consulta.GroupBy(l => l.Categoria))
        {
            salida.Add(LocalizationService.T($"Logro_Cat_{grupo.Key}"));
            salida.AddRange(grupo);
        }

        ItemsAgrupados = new ObservableCollection<object>(salida);
        NotificarEstados();
    }

    private void NotificarEstados()
    {
        OnPropertyChanged(nameof(TieneElementos));
        OnPropertyChanged(nameof(EstaVacio));
        OnPropertyChanged(nameof(SinResultados));
    }
}
