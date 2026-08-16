using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

public partial class GaleriaViewModel : ObservableObject, IRecipient<UsuarioLogeadoMensaje>, IRecipient<AnimeAñadidoMensaje>
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IAuthService _authService;
    private readonly IDialogService _dialogService;
    
    public bool BibliotecaVacia => BibliotecaLocales.Count == 0;

    public bool SinResultados => BibliotecaLocales.Count > 0 && (BibliotecaFiltrada?.IsEmpty ?? false);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BibliotecaVacia))]
    [NotifyPropertyChangedFor(nameof(SinResultados))]
    private ObservableCollection<AnimeItem> _bibliotecaLocales = [];
    
    public ICollectionView? BibliotecaFiltrada { get; private set; }

    [ObservableProperty]
    private string _textoBusqueda = string.Empty;

    [ObservableProperty]
    private string _filtroEstado = "Todos"; // Todos, Viendo, Completados, Planeando

    partial void OnTextoBusquedaChanged(string value)
    {
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
    }
    
    [RelayCommand]
    private void CambiarFiltroEstado(string nuevoFiltro)
    {
        FiltroEstado = nuevoFiltro;
        BibliotecaFiltrada?.Refresh();
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

    public GaleriaViewModel(IAnimeTrackingService animeTrackingService, IDatabaseService databaseService, IAuthService authService, IDialogService dialogService)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _dialogService = dialogService;
        
        WeakReferenceMessenger.Default.Register<UsuarioLogeadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<AnimeAñadidoMensaje>(this);
        
        _ = CargarBibliotecaAsync();
    }
    
    public void Receive(UsuarioLogeadoMensaje message)
    {
        _ = CargarPerfilUsuarioAsync();
    }

    public void Receive(AnimeAñadidoMensaje message)
    {
        if (!BibliotecaLocales.Any(a => a.AniListId == message.NuevoAnime.AniListId))
        {
            BibliotecaLocales.Add(message.NuevoAnime);
            OnPropertyChanged(nameof(BibliotecaVacia));
            _ = DescargarPortadaSiNoExisteAsync(message.NuevoAnime);
        }
    }
    
    private async Task CargarBibliotecaAsync()
    {
        var animes = await _databaseService.ObtenerTodosLosAnimesAsync();
        
        // MIGRACIÓN INTELIGENTE: Recuperar el estado basado en lo que realmente has visto localmente
        foreach (var a in animes)
        {
            if (string.IsNullOrEmpty(a.EstadoUsuario) || a.EstadoUsuario == "PLANNING")
            {
                var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(a.AniListId);
                int episodiosVistos = registros.Count(r => r.VistoLocal);
                
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
        }
        
        BibliotecaLocales = new ObservableCollection<AnimeItem>(animes);
        
        BibliotecaFiltrada = CollectionViewSource.GetDefaultView(BibliotecaLocales);
        BibliotecaFiltrada.Filter = FiltrarAnime;
        
        // Ordenar alfabéticamente por defecto
        BibliotecaFiltrada.SortDescriptions.Clear();
        BibliotecaFiltrada.SortDescriptions.Add(new SortDescription("Titulo", ListSortDirection.Ascending));
        
        OnPropertyChanged(nameof(BibliotecaFiltrada));
        OnPropertyChanged(nameof(SinResultados));
        
        foreach (var anime in animes)
        {
            _ = DescargarPortadaSiNoExisteAsync(anime);
        }
        
        await CargarPerfilUsuarioAsync();
        OnPropertyChanged(nameof(BibliotecaVacia));
    }
    
    private bool FiltrarAnime(object obj)
    {
        if (obj is not AnimeItem anime) return false;

        // 1. Filtro por texto
        if (!string.IsNullOrWhiteSpace(TextoBusqueda))
        {
            if (!anime.Titulo.Contains(TextoBusqueda, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 2. Filtro por estado
        if (FiltroEstado != "Todos")
        {
            string estadoEsperado = FiltroEstado switch
            {
                "Viendo" => "CURRENT",
                "Completados" => "COMPLETED",
                "Planeando" => "PLANNING",
                _ => ""
            };
            
            if (!string.IsNullOrEmpty(estadoEsperado) && anime.EstadoUsuario != estadoEsperado)
            {
                return false;
            }
        }

        return true;
    }
    
    private async Task DescargarPortadaSiNoExisteAsync(AnimeItem anime)
    {
        if (string.IsNullOrWhiteSpace(anime.UrlPortada)) return;
        
        string directory = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "AnimeLocalTracker", "Covers");
        string localPath = System.IO.Path.Combine(directory, $"{anime.AniListId}.jpg");
        
        if (!System.IO.File.Exists(localPath))
        {
            try 
            {
                System.IO.Directory.CreateDirectory(directory);
                using var client = new System.Net.Http.HttpClient();
                var bytes = await client.GetByteArrayAsync(anime.UrlPortada);
                await System.IO.File.WriteAllBytesAsync(localPath, bytes);
                
                System.Windows.Application.Current.Dispatcher.Invoke(() => 
                {
                    anime.NotificarPortadaActualizada();
                });
            }
            catch { /* Ignorar falla de red */ }
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
        // Enviar mensaje al MainViewModel para que abra el diálogo de búsqueda
        WeakReferenceMessenger.Default.Send(new AbrirBuscadorMensaje());
    }

    [RelayCommand]
    private async Task ConectarAniListAsync()
    {
        bool exito = await _authService.IniciarSesionAsync();
        if (exito)
        {
            await _dialogService.MostrarDialogoAsync("Nube Activada", "¡Conectado a AniList exitosamente! Tu progreso ahora se sincronizará.", false, "CloudCheck", "#4CAF50");
        }
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
        BibliotecaFiltrada?.Refresh();
        OnPropertyChanged(nameof(SinResultados));
    }
}
