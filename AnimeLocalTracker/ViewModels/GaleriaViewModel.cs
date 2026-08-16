using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BibliotecaVacia))]
    private ObservableCollection<AnimeItem> _bibliotecaLocales = [];
    
    [ObservableProperty] private bool _estaConectado;
    [ObservableProperty] private string _nombreUsuarioAniList = "Usuario";
    [ObservableProperty] private string? _avatarUsuarioAniList;
    
    [ObservableProperty] private bool _estaActualizando;
    [ObservableProperty] private int _progresoTotal;
    [ObservableProperty] private int _progresoActual;
    [ObservableProperty] private string _textoProgreso = string.Empty;

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
        BibliotecaLocales = new ObservableCollection<AnimeItem>(animes);
        
        foreach (var anime in animes)
        {
            _ = DescargarPortadaSiNoExisteAsync(anime);
        }
        
        await CargarPerfilUsuarioAsync();
        OnPropertyChanged(nameof(BibliotecaVacia));
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
        // Enviamos el mensaje al MainViewModel para que cambie la VistaActual
        // Como dependemos de inyección de dependencias para DetalleViewModel, 
        // pasamos el anime en el mensaje, y MainViewModel creará el ViewModel a través de DI o de una Factory.
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(anime));
    }
}
