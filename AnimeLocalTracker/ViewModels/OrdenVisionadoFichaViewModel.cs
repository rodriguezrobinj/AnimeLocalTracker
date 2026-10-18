using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Una fila de la ventana "Orden de visionado".</summary>
public partial class FilaOrdenVisionado : ObservableObject
{
    public FilaOrdenVisionado(ParteOrdenVisionado parte, bool esActual)
    {
        AniListId = parte.AniListId;
        NumeroTexto = parte.Numero > 0 ? $"{parte.Numero}." : string.Empty;
        Titulo = parte.Titulo ?? LocalizationService.T("Det_OrdenSinDatos");
        string formato = DetalleViewModel.TraducirValor("Fmt_", parte.Formato);
        string anio = parte.Anio > 0 ? parte.Anio.ToString(LocalizationService.Cultura) : string.Empty;
        Subtitulo = string.Join(" · ", new[] { formato, anio }.Where(t => t.Length > 0));
        UrlPortada = parte.UrlPortada;
        EsExtra = parte.EsExtra;
        EsAparte = parte.EsAparte;
        EsDeLaLineaPrincipal = !parte.EsExtra && !parte.EsAparte;
        EsActual = esActual;
        _estado = parte.Estado;
        _anime = parte.Anime;
    }

    public int AniListId { get; }
    public string NumeroTexto { get; }
    public string Titulo { get; }
    public string Subtitulo { get; }
    public string? UrlPortada { get; }
    public bool EsExtra { get; }
    public bool EsAparte { get; }
    public bool EsDeLaLineaPrincipal { get; }
    public bool EsActual { get; }

    [ObservableProperty] private bool _esSiguiente;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextoEstado), nameof(IconoEstado))]
    private EstadoParteSaga _estado;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeAnadir), nameof(PuedeAbrir))]
    private AnimeItem? _anime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeAnadir))]
    private bool _estaAnadiendo;

    public bool PuedeAnadir => Anime == null && !EstaAnadiendo;
    public bool PuedeAbrir => Anime != null && !EsActual;

    public string TextoEstado => LocalizationService.T(Estado switch
    {
        EstadoParteSaga.Vista => "Det_OrdenVista",
        EstadoParteSaga.AMedias => "Det_OrdenAMedias",
        EstadoParteSaga.Descargada => "Det_OrdenDescargada",
        EstadoParteSaga.EnBiblioteca => "Det_OrdenEnBiblioteca",
        _ => "Det_OrdenNoLaTienes",
    });

    public string IconoEstado => Estado switch
    {
        EstadoParteSaga.Vista => "CheckCircle",
        EstadoParteSaga.AMedias => "ProgressClock",
        EstadoParteSaga.Descargada => "Download",
        EstadoParteSaga.EnBiblioteca => "BookmarkOutline",
        _ => "PlusCircleOutline",
    };
}

/// <summary>
/// Ventana "Orden de visionado" de la ficha (ver docs/plan-orden-visionado-franquicia.md). Pieza de <see cref="DetalleViewModel"/>
/// como <see cref="MusicaFichaViewModel"/>: se crea con cada ficha, y su vista (<c>OrdenVisionadoView</c>) la crea la ficha la primera
/// vez que se abre.
/// </summary>
public partial class OrdenVisionadoFichaViewModel : ObservableObject, IDisposable
{
    private readonly IOrdenVisionadoService? _servicio;
    private readonly IDialogService _dialogService;
    private int _animeId;
    private int _version;
    private CancellationTokenSource? _cts;

    public OrdenVisionadoFichaViewModel(IOrdenVisionadoService? servicio, IDialogService dialogService)
    {
        _servicio = servicio;
        _dialogService = dialogService;
    }

    /// <summary>Filas <see cref="FilaOrdenVisionado"/> y, antes del grupo aparte, su cabecera (string).</summary>
    public ObservableCollection<object> Filas { get; } = new();

    [ObservableProperty] private bool _hayOrdenVisionado;
    [ObservableProperty] private bool _mostrandoOrdenVisionado;
    [ObservableProperty] private bool _sinConexion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarCargando), nameof(MostrarVacio))]
    private bool _estaCargando;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrarVacio))]
    private bool _sinPartes;

    /// <summary>El indicador solo mientras no hay nada que enseñar (lo guardado se pinta enseguida).</summary>
    public bool MostrarCargando => EstaCargando && Filas.Count == 0;

    public bool MostrarVacio => SinPartes && !EstaCargando;

    /// <summary>La vista lleva la lista hasta esta fila (la siguiente, o la de la ficha si no hay siguiente).</summary>
    public event Action<FilaOrdenVisionado>? FilaDestacadaSolicitada;

    /// <summary>La última carga lanzada (para que las pruebas puedan esperarla).</summary>
    internal Task Carga { get; private set; } = Task.CompletedTask;

    public void Preparar(int aniListId) => _animeId = aniListId;

    public async Task ComprobarDisponibleAsync(CancellationToken ct)
    {
        if (_servicio == null || _animeId <= 0) return;
        bool hay = await _servicio.TieneFranquiciaOSinConsultarAsync(_animeId);
        if (!ct.IsCancellationRequested) HayOrdenVisionado = hay;
    }

    [RelayCommand]
    private Task AbrirAsync()
    {
        if (_servicio == null || _animeId <= 0) return Task.CompletedTask;
        MostrandoOrdenVisionado = true;
        Core.Cancelacion.Reemplazar(ref _cts);
        Carga = CargarAsync(++_version, _cts!.Token);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Cerrar()
    {
        MostrandoOrdenVisionado = false;
        _version++;
        _cts?.Cancel();
        EstaCargando = false;
    }

    private async Task CargarAsync(int version, CancellationToken ct)
    {
        EstaCargando = true;
        OnPropertyChanged(nameof(MostrarCargando));
        try
        {
            var guardado = await _servicio!.LeerGuardadoAsync(_animeId);
            if (version != _version) return;
            Pintar(guardado);

            var actualizado = await _servicio.ActualizarAsync(_animeId, ct);
            if (version != _version) return;
            if (actualizado.Partes.Count > 0) Pintar(actualizado);
            SinConexion = actualizado.SinConexion;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OrdenVisionadoFichaViewModel", $"No se pudo cargar el orden de visionado: {ex.Message}");
        }
        finally
        {
            if (version == _version) EstaCargando = false;
        }
    }

    private void Pintar(OrdenVisionadoLeido leido)
    {
        Filas.Clear();
        bool cabeceraAparte = false;
        foreach (var parte in leido.Partes)
        {
            if (parte.EsAparte && !cabeceraAparte)
            {
                Filas.Add(LocalizationService.T("Det_OrdenAparte"));
                cabeceraAparte = true;
            }
            Filas.Add(new FilaOrdenVisionado(parte, esActual: parte.AniListId == _animeId));
        }
        MarcarSiguiente();
        SinPartes = leido.Partes.Count <= 1;
        OnPropertyChanged(nameof(MostrarCargando));

        var filas = Filas.OfType<FilaOrdenVisionado>().ToList();
        var destacada = filas.FirstOrDefault(f => f.EsSiguiente) ?? filas.FirstOrDefault(f => f.EsActual);
        if (destacada != null) FilaDestacadaSolicitada?.Invoke(destacada);
    }

    private void MarcarSiguiente()
    {
        var filas = Filas.OfType<FilaOrdenVisionado>().ToList();
        var siguiente = filas.FirstOrDefault(f => f.EsDeLaLineaPrincipal && f.Estado != EstadoParteSaga.Vista);
        foreach (var fila in filas) fila.EsSiguiente = ReferenceEquals(fila, siguiente);
    }

    [RelayCommand]
    private void AbrirFila(FilaOrdenVisionado? fila)
    {
        if (fila?.Anime == null || fila.EsActual) return;
        Cerrar();
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(fila.Anime));
    }

    // AllowConcurrentExecutions: sin él el comando descartaría por su cuenta la segunda pulsación y la prueba
    // Anadir_MientrasAnade_NoRepite no comprobaría la guarda de la fila (PuedeAnadir), que es la que protege cada fila por separado.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AnadirAsync(FilaOrdenVisionado? fila)
    {
        if (_servicio == null || fila == null || !fila.PuedeAnadir) return;
        fila.EstaAnadiendo = true;
        try
        {
            var anime = await _servicio.AnadirABibliotecaAsync(fila.AniListId);
            if (anime == null)
            {
                // Mismo color que los demás avisos de error (AvisarSinVideo del reproductor).
                _dialogService.MostrarToast(LocalizationService.T("Det_OrdenAnadirErrorTitulo"), LocalizationService.T("Det_OrdenAnadirErrorMsj"),
                    "AlertCircleOutline", "#EF4444");
                return;
            }
            fila.Anime = anime;
            fila.Estado = EstadoParteSaga.EnBiblioteca;
            MarcarSiguiente();
        }
        finally
        {
            fila.EstaAnadiendo = false;
        }
    }

    public void Dispose()
    {
        _version++;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
