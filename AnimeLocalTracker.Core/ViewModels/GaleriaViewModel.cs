using AnimeLocalTracker.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;


using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Core.Models;
using AnimeLocalTracker.Core.Services;
using AnimeLocalTracker.Core.Messages;

namespace AnimeLocalTracker.Core.ViewModels;

public partial class GaleriaViewModel : ObservableObject, 
    IRecipient<UsuarioLogeadoMensaje>, 
    IRecipient<AnimeAñadidoMensaje>, 
    IRecipient<UsuarioDesconectadoMensaje>,
    IRecipient<EpisodioActualizadoMensaje>
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IAuthService _authService;
    private readonly IDialogService _dialogService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IImageCacheService _imageCacheService;
    
    public bool BibliotecaVacia => BibliotecaLocales.Count == 0;

    public bool SinResultados => BibliotecaLocales.Count > 0 && (BibliotecaFiltrada?.Count == 0);

    public int TotalAnimesBiblioteca => BibliotecaLocales.Count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BibliotecaVacia))]
    [NotifyPropertyChangedFor(nameof(SinResultados))]
    [NotifyPropertyChangedFor(nameof(TotalAnimesBiblioteca))]
    private ObservableCollection<AnimeItem> _bibliotecaLocales = [];
    
    public System.Collections.ObjectModel.ObservableCollection<AnimeLocalTracker.Core.Models.AnimeItem> BibliotecaFiltrada { get; private set; } = new();

    [ObservableProperty]
    private string _textoBusqueda = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsFiltroTodos))]
    [NotifyPropertyChangedFor(nameof(EsFiltroViendo))]
    [NotifyPropertyChangedFor(nameof(EsFiltroCompletados))]
    [NotifyPropertyChangedFor(nameof(EsFiltroPlaneando))]
    private string _filtroEstado = "Todos"; // Todos, Viendo, Completados, Planeando

    public bool EsFiltroTodos => FiltroEstado == "Todos";
    public bool EsFiltroViendo => FiltroEstado == "Viendo";
    public bool EsFiltroCompletados => FiltroEstado == "Completados";
    public bool EsFiltroPlaneando => FiltroEstado == "Planeando";

    public double UltimoScrollOffset { get; set; } = 0;

    partial void OnTextoBusquedaChanged(string value)
    {
        AplicarFiltros();
        OnPropertyChanged(nameof(SinResultados));
    }
    
    [RelayCommand]
    private void CambiarFiltroEstado(string nuevoFiltro)
    {
        FiltroEstado = nuevoFiltro;
        AplicarFiltros();
        OnPropertyChanged(nameof(SinResultados));
    }
    
    [ObservableProperty] private bool _estaConectado;
    [ObservableProperty] private string _nombreUsuarioAniList = "Usuario";
    [ObservableProperty] private string? _avatarUsuarioAniList;
    
    [ObservableProperty] private bool _estaActualizando;
    [ObservableProperty] private int _progresoTotal;
    [ObservableProperty] private int _progresoActual;
    [ObservableProperty] private string _textoProgreso = string.Empty;

    // --- PROPIEDADES DE SELECCIÓN MÚLTIPLE ---
    [ObservableProperty] private bool _modoSeleccion;

    public GaleriaViewModel(
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService, 
        IAuthService authService, 
        IDialogService dialogService, 
        IHttpClientFactory httpClientFactory,
        IImageCacheService imageCacheService)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _dialogService = dialogService;
        _httpClientFactory = httpClientFactory;
        _imageCacheService = imageCacheService;
        
        WeakReferenceMessenger.Default.Register<UsuarioLogeadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<AnimeAñadidoMensaje>(this);
        WeakReferenceMessenger.Default.Register<UsuarioDesconectadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<EpisodioActualizadoMensaje>(this);
        
        _ = CargarBibliotecaAsync();
    }
    
    public void Receive(UsuarioLogeadoMensaje message)
    {
        _ = CargarPerfilUsuarioAsync();
    }

    public void Receive(UsuarioDesconectadoMensaje message)
    {
        EstaConectado = false;
        NombreUsuarioAniList = "Usuario";
        AvatarUsuarioAniList = null;
    }

    public void Receive(AnimeAñadidoMensaje message)
    {
        // Los handlers del messenger NUNCA deben lanzar: una excepción aquí aborta
        // la entrega del mensaje al resto de receptores suscritos.
        try
        {
            if (!BibliotecaLocales.Any(a => a.AniListId == message.NuevoAnime.AniListId))
            {
                message.NuevoAnime.PortadaImagen = _imageCacheService.ObtenerPortada(message.NuevoAnime.AniListId, message.NuevoAnime.UrlPortada);
                BibliotecaLocales.Add(message.NuevoAnime);
                OnPropertyChanged(nameof(BibliotecaVacia));
                OnPropertyChanged(nameof(TotalAnimesBiblioteca));

                if (message.NuevoAnime.PortadaImagen == null && !string.IsNullOrWhiteSpace(message.NuevoAnime.UrlPortada))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var img = await _imageCacheService.ObtenerPortadaAsync(message.NuevoAnime.AniListId, message.NuevoAnime.UrlPortada);
                            if (img != null)
                            {
                                AnimeLocalTracker.Core.Services.CoreDispatcher.Invoke(() => message.NuevoAnime.PortadaImagen = img);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug("GaleriaViewModel", $"Error cargando portada de nuevo anime: {ex.Message}");
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("GaleriaViewModel", "Error al procesar AnimeAñadidoMensaje", ex);
        }
    }

    public async void Receive(EpisodioActualizadoMensaje message)
    {
        try
        {
            var anime = BibliotecaLocales.FirstOrDefault(a => a.AniListId == message.AnimeId);
            if (anime != null)
            {
                var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
                AnimeLocalTracker.Core.Services.CoreDispatcher.Invoke(() =>
                {
                    anime.EpisodiosVistos = registros.Count(r => r.VistoLocal);
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("GaleriaViewModel", "Error al actualizar episodio visto en galería", ex);
        }
    }
    
    private async Task CargarBibliotecaAsync()
    {
        try
        {
            var animes = await _databaseService.ObtenerTodosLosAnimesAsync() ?? new List<Models.AnimeItem>();
            var todosRegistros = await _databaseService.ObtenerTodosLosRegistrosAsync() ?? new List<Models.RegistroEpisodio>();
            var registrosPorAnime = todosRegistros.GroupBy(r => r.AniListId)
                                                  .ToDictionary(g => g.Key, g => g.ToList());

            // MIGRACIÓN INTELIGENTE: Recuperar el estado basado en lo que realmente has visto localmente
            foreach (var a in animes)
            {
                if (registrosPorAnime.TryGetValue(a.AniListId, out var registros))
                {
                    a.EpisodiosVistos = registros.Count(r => r.VistoLocal);
                }
                else
                {
                    a.EpisodiosVistos = 0;
                }

                if (string.IsNullOrEmpty(a.EstadoUsuario) || a.EstadoUsuario == "PLANNING")
                {
                    int episodiosVistos = a.EpisodiosVistos;

                    if (episodiosVistos > 0)
                    {
                        if (a.TotalEpisodios > 0 && episodiosVistos >= a.TotalEpisodios)
                        {
                            a.EstadoUsuario = "COMPLETED";
                        }
                        else
                        {
                            a.EstadoUsuario = "CURRENT";
                        }
                        await _databaseService.ActualizarAnimeAsync(a);
                    }
                    else if (string.IsNullOrEmpty(a.EstadoUsuario))
                    {
                        a.EstadoUsuario = "PLANNING";
                        await _databaseService.ActualizarAnimeAsync(a);
                    }
                }

                // Precarga ultra-rápida desde caché en memoria o archivo local (0ms en scroll)
                a.PortadaImagen = _imageCacheService.ObtenerPortada(a.AniListId, a.UrlPortada);
            }

            BibliotecaLocales = new ObservableCollection<AnimeItem>(animes);

            AplicarFiltros();
            

            // Ordenar alfabéticamente por defecto
            
            

            OnPropertyChanged(nameof(BibliotecaFiltrada));
            OnPropertyChanged(nameof(SinResultados));

            _ = CargarPortadasFaltantesEnSegundoPlanoAsync(animes);

            await CargarPerfilUsuarioAsync();
            OnPropertyChanged(nameof(BibliotecaVacia));
        }
        catch (Exception ex)
        {
            // Sin esto, un fallo de BD al arrancar deja la galería vacía y en silencio
            AppLogger.Error("GaleriaViewModel", "Error al cargar la biblioteca", ex);
        }
    }

    private async Task CargarPortadasFaltantesEnSegundoPlanoAsync(IEnumerable<AnimeItem> animes)
    {
        var faltantes = animes.Where(a => a.PortadaImagen == null && !string.IsNullOrWhiteSpace(a.UrlPortada)).ToList();
        if (faltantes.Count == 0) return;

        foreach (var anime in faltantes)
        {
            var img = await _imageCacheService.ObtenerPortadaAsync(anime.AniListId, anime.UrlPortada);
            if (img != null)
            {
                if (true)
                {
                    AnimeLocalTracker.Core.Services.CoreDispatcher.Invoke(() => anime.PortadaImagen = img);
                }
                else
                {
                    anime.PortadaImagen = img;
                }
            }
        }
    }
    
        private void AplicarFiltros()
    {
        if (BibliotecaLocales == null || BibliotecaFiltrada == null) return;
        
        BibliotecaFiltrada.Clear();
        foreach (var anime in BibliotecaLocales)
        {
            bool matchBusqueda = string.IsNullOrWhiteSpace(TextoBusqueda) || 
                               anime.Titulo.Contains(TextoBusqueda, StringComparison.OrdinalIgnoreCase);
            
            bool matchEstado = true;
            if (FiltroEstado == "Viendo") matchEstado = anime.EstadoUsuario == "CURRENT";
            else if (FiltroEstado == "Completados") matchEstado = anime.EstadoUsuario == "COMPLETED";
            else if (FiltroEstado == "Planeando") matchEstado = anime.EstadoUsuario == "PLANNING";

            if (matchBusqueda && matchEstado)
            {
                BibliotecaFiltrada.Add(anime);
            }
        }
    }

    private async Task CargarPerfilUsuarioAsync()
    {
        var token = _authService.ObtenerTokenGuardado();
        if (!string.IsNullOrEmpty(token))
        {
            EstaConectado = true;
            var perfil = await _animeTrackingService.ObtenerPerfilUsuarioAsync(token);
            if (perfil != null)
            {
                NombreUsuarioAniList = perfil.Name ?? "Usuario";
                AvatarUsuarioAniList = perfil.Avatar?.Large;
            }
        }
        else
        {
            EstaConectado = false;
        }
    }

    [RelayCommand]
    private void AñadirAnimeManual()
    {
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_AgregarAnime());
    }

    private bool _menuUsuarioAbierto;
    public bool MenuUsuarioAbierto
    {
        get => _menuUsuarioAbierto;
        set => SetProperty(ref _menuUsuarioAbierto, value);
    }

    [RelayCommand]
    private void ToggleMenuUsuario()
    {
        MenuUsuarioAbierto = !MenuUsuarioAbierto;
    }

    [RelayCommand]
    private void CerrarMenuUsuario()
    {
        MenuUsuarioAbierto = false;
    }

    [RelayCommand]
    private async Task ConectarAniListAsync()
    {
        MenuUsuarioAbierto = false;
        bool exito = await _authService.IniciarSesionAsync();
        if (exito)
        {
            await _dialogService.MostrarDialogoAsync("Nube Activada", "¡Conectado a AniList exitosamente! Tu progreso ahora se sincronizará.", false, "CloudCheck", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync("Autenticación Cancelada", "No se pudo iniciar sesión con AniList o el proceso fue cancelado.", false, "AlertCircle", "#FF5252");
        }
    }

    [RelayCommand]
    private async Task DesconectarAniListAsync()
    {
        MenuUsuarioAbierto = false; // Cierra el menú antes de desloguear
        _authService.CerrarSesion();
        await _dialogService.MostrarDialogoAsync("Sesión Cerrada", "Te has desconectado de AniList correctamente.", false, "Logout", "#FF5252");
    }
    
    [RelayCommand]
    private async Task ActualizarBibliotecaAsync()
    {
        if (EstaActualizando) return; 
        
        var listaAnimes = BibliotecaLocales.ToList();
        if (listaAnimes.Count == 0) return;

        EstaActualizando = true;
        ProgresoTotal = listaAnimes.Count;
        ProgresoActual = 0;

        foreach (var anime in listaAnimes)
        {
            ProgresoActual++;
            TextoProgreso = $"Sincronizando: {anime.Titulo} ({ProgresoActual}/{ProgresoTotal})";

            var datosFrescos = await _animeTrackingService.ObtenerAnimePorIdAsync(anime.AniListId);
            if (datosFrescos != null)
            {
                int episodiosEmitidos = datosFrescos.NextAiringEpisode != null 
                    ? datosFrescos.NextAiringEpisode.Episode - 1 
                    : (datosFrescos.Episodes ?? 0);
                
                anime.TotalEpisodios = episodiosEmitidos;
                anime.Estado = datosFrescos.Status ?? "UNKNOWN";
                
                // Si está conectado, sincronizamos también el estado personal del usuario
                if (EstaConectado)
                {
                    var token = _authService.ObtenerTokenGuardado();
                    if (!string.IsNullOrEmpty(token))
                    {
                        var seguimiento = await _animeTrackingService.ObtenerSeguimientoUsuarioAsync(anime.AniListId, token);
                        if (seguimiento != null && !string.IsNullOrEmpty(seguimiento.Status))
                        {
                            anime.EstadoUsuario = seguimiento.Status;
                        }
                    }
                }
                
                await _databaseService.ActualizarAnimeAsync(anime);
            }
            
            await Task.Delay(250); 
        }
        
        TextoProgreso = "¡Actualización completada con éxito!";
        await Task.Delay(2000); 
        EstaActualizando = false;
    }

    [RelayCommand]
    private void AbrirDetalle(AnimeItem anime)
    {
        if (ModoSeleccion)
        {
            anime.EstaSeleccionado = !anime.EstaSeleccionado;
            return;
        }

        // Enviamos el mensaje al MainViewModel para que cambie la VistaActual
        // Como dependemos de inyección de dependencias para DetalleViewModel, 
        // pasamos el anime en el mensaje, y MainViewModel creará el ViewModel a través de DI o de una Factory.
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(anime));
    }

    [RelayCommand]
    private void ToggleModoSeleccion()
    {
        ModoSeleccion = !ModoSeleccion;
        if (!ModoSeleccion)
        {
            // Deseleccionar todos al salir del modo
            foreach (var anime in BibliotecaLocales)
            {
                anime.EstaSeleccionado = false;
            }
        }
    }

    [RelayCommand]
    private async Task CategorizarSeleccionadosAsync(string nuevoEstado)
    {
        var seleccionados = BibliotecaLocales.Where(a => a.EstaSeleccionado).ToList();
        if (seleccionados.Count == 0) return;

        foreach (var anime in seleccionados)
        {
            anime.EstadoUsuario = nuevoEstado;
            await _databaseService.ActualizarAnimeAsync(anime);
            anime.EstaSeleccionado = false;
        }

        ModoSeleccion = false;
        AplicarFiltros();
        OnPropertyChanged(nameof(SinResultados));
    }
}
