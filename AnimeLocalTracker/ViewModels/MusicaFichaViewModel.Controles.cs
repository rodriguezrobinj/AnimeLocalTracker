using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Lo que la ventana de música ofrece sobre la lista entera: buscador, contador de guardados, la barra de "sonando ahora"
/// (anterior, siguiente, aleatorio, repetir) y el estado sin conexión.
/// </summary>
public sealed partial class MusicaFichaViewModel : IRecipient<EstadoConexionMensaje>
{
    // === Buscador ===

    /// <summary>Con menos temas la lista se ve entera: el buscador solo estorbaría.</summary>
    internal const int MinimoTemasParaBuscar = 8;

    /// <summary>Texto del buscador: filtra por título, artista o etiqueta ("OP3", "ED12 v2"), sin distinguir mayúsculas ni acentos.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayBusquedaTemas))]
    private string _busquedaTemas = string.Empty;

    public bool HayBusquedaTemas => !string.IsNullOrWhiteSpace(BusquedaTemas);

    public bool MostrarBuscadorTemas => TemasMusicales.Count >= MinimoTemasParaBuscar;

    /// <summary>Con buscador, la lista conserva un alto mínimo: sin él, la ventana encogía y saltaba con cada letra escrita.</summary>
    public double AltoMinimoListaTemas => MostrarBuscadorTemas ? 300 : 0;

    /// <summary>Hay temas, pero ninguno pasa el filtro o la búsqueda.</summary>
    public bool SinResultadosDeTemas => TemasMusicales.Count > 0 && TemasMusicalesVisibles.Count == 0;

    partial void OnBusquedaTemasChanged(string value) => AplicarFiltroTemas();

    [RelayCommand]
    private void LimpiarBusquedaTemas() => BusquedaTemas = string.Empty;

    private bool CoincideConBusqueda(TemaAnimeItem tema)
    {
        string texto = BusquedaTemas.Trim();
        if (texto.Length == 0) return true;

        // Un tema tapado por spoiler solo se encuentra por su etiqueta: buscar por título lo destaparía sin querer.
        if (Contiene(tema.EtiquetaSlug, texto)) return true;
        return !tema.OcultoPorSpoiler && (Contiene(tema.TituloCancion, texto) || Contiene(tema.Artistas, texto));
    }

    private static bool Contiene(string? donde, string texto) =>
        !string.IsNullOrEmpty(donde) &&
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(donde, texto, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    // === Contador de guardados ===

    /// <summary>Cuántos temas de este anime están ya en la carpeta de música ("29 / 75 guardados").</summary>
    [ObservableProperty] private int _temasGuardados;

    public int TotalTemas => TemasMusicales.Count;

    public bool EsFiltroTemasGuardados => FiltroTemasMusicales == "Guardados";

    private void AlCambiarLaListaDeTemas(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (TemaAnimeItem tema in e.OldItems) tema.PropertyChanged -= AlCambiarUnTema;
        if (e.NewItems != null)
        {
            foreach (TemaAnimeItem tema in e.NewItems)
            {
                tema.SinConexion = SinConexion;
                tema.PropertyChanged += AlCambiarUnTema;
            }
        }

        ActualizarContadorTemas();
        OnPropertyChanged(nameof(TotalTemas));
        OnPropertyChanged(nameof(MostrarBuscadorTemas));
        OnPropertyChanged(nameof(AltoMinimoListaTemas));
    }

    private void AlCambiarUnTema(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TemaAnimeItem.Descargado)) ActualizarContadorTemas();
    }

    private void ActualizarContadorTemas() => TemasGuardados = TemasMusicales.Count(t => t.Descargado);

    // === Sonando ahora ===

    /// <summary>El tema cargado en el reproductor (sonando o en pausa): alimenta la barra de abajo de la ventana.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayTemaActual))]
    private TemaAnimeItem? _temaActual;

    public bool HayTemaActual => TemaActual != null;

    /// <summary>El siguiente tema se elige al azar (sin repetir hasta que hayan sonado todos).</summary>
    [ObservableProperty] private bool _aleatorioMusica;

    /// <summary>Al terminar, el mismo tema vuelve a empezar.</summary>
    [ObservableProperty] private bool _repetirMusica;

    /// <summary>La ventana debe llevar la lista hasta este tema (se pulsó el título en la barra de "sonando ahora").</summary>
    public event Action<TemaAnimeItem>? TemaActualSolicitado;

    /// <summary>Solo para pruebas: el azar del modo aleatorio.</summary>
    internal Random Azar { get; set; } = Random.Shared;

    /// <summary>Por encima de estos segundos, "anterior" vuelve al principio del tema en vez de cambiar de tema.</summary>
    internal const double SegundosParaReiniciarTema = 3;

    /// <summary>Lo que ha ido sonando, en orden: "anterior" en modo aleatorio vuelve por aquí.</summary>
    private readonly List<TemaAnimeItem> _historialTemas = [];
    private readonly HashSet<TemaAnimeItem> _sonadosEnAleatorio = [];
    private bool _volviendoAlAnterior;

    partial void OnTemaActualChanged(TemaAnimeItem? value)
    {
        if (value == null) return;

        _sonadosEnAleatorio.Add(value);
        if (!_volviendoAlAnterior && !ReferenceEquals(_historialTemas.LastOrDefault(), value)) _historialTemas.Add(value);
    }

    partial void OnAleatorioMusicaChanged(bool value)
    {
        _sonadosEnAleatorio.Clear();
        if (TemaActual != null) _sonadosEnAleatorio.Add(TemaActual);
        if (!_aplicandoAjustesMusicaGuardados) ProgramarGuardadoAjustesMusica(conRetraso: false);
    }

    partial void OnRepetirMusicaChanged(bool value)
    {
        if (!_aplicandoAjustesMusicaGuardados) ProgramarGuardadoAjustesMusica(conRetraso: false);
    }

    [RelayCommand]
    private void AlternarAleatorioMusica() => AleatorioMusica = !AleatorioMusica;

    [RelayCommand]
    private void AlternarRepetirMusica() => RepetirMusica = !RepetirMusica;

    /// <summary>Play/pausa del tema de la barra.</summary>
    [RelayCommand]
    private void AlternarTemaActual()
    {
        if (TemaActual != null) ReproducirTema(TemaActual);
    }

    /// <summary>Pasa al siguiente tema que se pueda escuchar; al llegar al final, vuelve al primero.</summary>
    [RelayCommand]
    private void TemaSiguiente()
    {
        var siguiente = SiguienteEscuchable(TemaActual, darLaVuelta: true);
        if (siguiente != null) ReproducirTema(siguiente);
    }

    /// <summary>
    /// Si el tema lleva sonando más de unos segundos, vuelve a su principio; si no, pasa al anterior (en modo aleatorio, al que
    /// sonó antes).
    /// </summary>
    [RelayCommand]
    private void TemaAnterior()
    {
        var actual = TemaActual;
        if (actual == null) return;

        if (actual.PosicionSegundos > SegundosParaReiniciarTema)
        {
            BuscarTema(actual, 0);
            return;
        }

        var anterior = AnteriorEscuchable(actual);
        if (anterior == null)
        {
            BuscarTema(actual, 0);
            return;
        }

        _volviendoAlAnterior = true;
        try { ReproducirTema(anterior); }
        finally { _volviendoAlAnterior = false; }
    }

    /// <summary>Quita el filtro y la búsqueda si tapan el tema que suena, y pide a la ventana que lo muestre.</summary>
    [RelayCommand]
    private void MostrarTemaActual()
    {
        var actual = TemaActual;
        if (actual == null) return;

        if (!TemasMusicalesVisibles.Contains(actual))
        {
            BusquedaTemas = string.Empty;
            FiltroTemasMusicales = "Todos";
        }
        TemaActualSolicitado?.Invoke(actual);
    }

    /// <summary>
    /// El siguiente tema con archivo (guardado o vista previa). Recorre TODOS los temas, estén filtrados o no: el filtro y la
    /// búsqueda son para mirar la lista, no cambian qué suena.
    /// </summary>
    private TemaAnimeItem? SiguienteEscuchable(TemaAnimeItem? desde, bool darLaVuelta)
    {
        if (AleatorioMusica)
        {
            var otros = TemasMusicales.Where(t => t.PuedeReproducir && !ReferenceEquals(t, desde)).ToList();
            if (otros.Count == 0) return null;

            var sinSonar = otros.Where(t => !_sonadosEnAleatorio.Contains(t)).ToList();
            if (sinSonar.Count == 0)
            {
                if (!darLaVuelta) return null; // ya sonaron todos: la reproducción continua se detiene, igual que al final de la lista
                _sonadosEnAleatorio.Clear();
                sinSonar = otros;
            }
            return sinSonar[Azar.Next(sinSonar.Count)];
        }

        int indice = desde == null ? -1 : TemasMusicales.IndexOf(desde);
        for (int i = indice + 1; i < TemasMusicales.Count; i++)
            if (TemasMusicales[i].PuedeReproducir) return TemasMusicales[i];

        if (!darLaVuelta) return null;
        return TemasMusicales.FirstOrDefault(t => t.PuedeReproducir && !ReferenceEquals(t, desde));
    }

    private TemaAnimeItem? AnteriorEscuchable(TemaAnimeItem actual)
    {
        if (AleatorioMusica)
        {
            // El último del historial es el propio tema actual: se descarta y se vuelve al que sonó antes.
            if (_historialTemas.Count > 0 && ReferenceEquals(_historialTemas[^1], actual)) _historialTemas.RemoveAt(_historialTemas.Count - 1);
            while (_historialTemas.Count > 0)
            {
                var candidato = _historialTemas[^1];
                if (candidato.PuedeReproducir && TemasMusicales.Contains(candidato)) return candidato;
                _historialTemas.RemoveAt(_historialTemas.Count - 1);
            }
            return null;
        }

        for (int i = TemasMusicales.IndexOf(actual) - 1; i >= 0; i--)
            if (TemasMusicales[i].PuedeReproducir) return TemasMusicales[i];
        return null;
    }

    private void OlvidarHistorialDeTemas()
    {
        _historialTemas.Clear();
        _sonadosEnAleatorio.Clear();
    }

    // === Seguir sonando fuera de la ficha ===

    /// <summary>
    /// Opción del usuario (también está en Configuración): al salir de la ficha con un tema sonando, la música sigue mientras
    /// se navega por la app, con una barra pequeña en la ventana principal. Apagada, se corta al salir (como siempre).
    /// </summary>
    [ObservableProperty] private bool _seguirFueraDeLaFicha;

    partial void OnSeguirFueraDeLaFichaChanged(bool value)
    {
        if (!_aplicandoAjustesMusicaGuardados) ProgramarGuardadoAjustesMusica(conRetraso: false);
    }

    [RelayCommand]
    private void AlternarSeguirFueraDeLaFicha() => SeguirFueraDeLaFicha = !SeguirFueraDeLaFicha;

    /// <summary>Su ficha ya no está en pantalla y la música sigue sonando (la tiene <see cref="IMusicaDeFondoService"/>).</summary>
    public bool EsDeFondo { get; private set; }

    internal void PasarAFondo()
    {
        EsDeFondo = true;
        MostrandoPanelMusica = false;
        GuardarAjustesMusicaPendientesYa();
    }

    internal void VolverALaFicha(bool abrirVentana)
    {
        EsDeFondo = false;
        if (abrirVentana) MostrandoPanelMusica = true;
    }

    /// <summary>La ficha nueva que recibe esta música le dice de dónde sacar el episodio más avanzado (para los spoilers).</summary>
    internal void UsarEpisodioMasAltoVisto(Func<int>? episodioMasAltoVisto) => _episodioMasAltoVisto = episodioMasAltoVisto;

    /// <summary>La vista de la ficha deja de verse: la música se corta, salvo que se haya quedado sonando de fondo.</summary>
    public void AlOcultarLaFicha()
    {
        if (EsDeFondo) GuardarAjustesMusicaPendientesYa();
        else DetenerMusica();
    }

    // === Sin conexión ===

    private readonly IEstadoConexionService? _estadoConexion;

    /// <summary>
    /// Sin internet solo vale lo que ya está en el disco: se pueden escuchar (y guardar) los temas con archivo, pero no bajar
    /// ni preparar ninguno nuevo. Antes esos botones seguían activos y fallaban al pulsarlos.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HayConexion))]
    private bool _sinConexion;

    public bool HayConexion => !SinConexion;

    partial void OnSinConexionChanged(bool value)
    {
        lock (_candadoTemas)
            foreach (var tema in TemasMusicales) tema.SinConexion = value;
    }

    public void Receive(EstadoConexionMensaje message)
    {
        // Sin el servicio de conexión (las pruebas) no se escucha el aviso global: lo mandan otras pruebas en paralelo.
        if (_estadoConexion == null) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => SinConexion = message.SinConexion));
            return;
        }
        SinConexion = message.SinConexion;
    }

    private void AvisarSinConexion() => _dialogService.MostrarToast(
        LocalizationService.T("Det_MusicaSinConexionTitulo"), LocalizationService.T("Det_MusicaSinConexion"), "WifiOff", "#F59E0B");
}
