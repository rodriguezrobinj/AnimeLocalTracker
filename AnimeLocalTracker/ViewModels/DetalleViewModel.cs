using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Messages;

namespace AnimeLocalTracker.ViewModels;

public partial class DetalleViewModel : ObservableObject, 
    IRecipient<UsuarioLogeadoMensaje>, 
    IRecipient<UsuarioDesconectadoMensaje>, 
    IRecipient<EpisodioActualizadoMensaje>,
    IRecipient<DescargaProgresoMensaje>
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IAuthService _authService;
    private readonly IFileScannerService _fileScannerService;
    private readonly IDialogService _dialogService;
    private readonly IDownloadService _downloadService;
    
    [ObservableProperty]
    private AnimeItem? _animeSeleccionado;

    private List<EpisodioItem> _todosLosEpisodios = new();
    
    public ObservableCollection<EpisodioItem> EpisodiosDelAnime { get; } = [];

    [ObservableProperty]
    private bool _ordenAscendente = false;

    [ObservableProperty]
    private string _filtroEpisodios = "Todos";

    public string[] OpcionesFiltro { get; } = ["Todos", "Descargados", "Vistos", "No Vistos", "Favoritos"];

    [ObservableProperty] private string _mensajeSinEpisodios = "No hay episodios para mostrar";
    [ObservableProperty] private string _subtituloSinEpisodios = "El filtro actual no encontró coincidencias.";

    // === EDITOR DE SEGUIMIENTO ===
    [ObservableProperty] private bool _mostrandoEditorSeguimiento;
    [ObservableProperty] private string _editEstado = "CURRENT";
    [ObservableProperty] private int _editProgreso;
    [ObservableProperty] private float _editPuntaje;
    [ObservableProperty] private DateTime? _editFechaInicio;
    [ObservableProperty] private DateTime? _editFechaFin;
    [ObservableProperty] private string _editEstadoVisual = "Viendo";
    public List<string> OpcionesEstadoVisual { get; } = ["Viendo", "Finalizado", "En Pausa", "Abandonado", "Planeando"];

    [ObservableProperty] private bool _estaConectado;

    public DetalleViewModel(
        IAnimeTrackingService animeTrackingService, 
        IDatabaseService databaseService, 
        IAuthService authService, 
        IFileScannerService fileScannerService,
        IDialogService dialogService,
        IDownloadService downloadService)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _fileScannerService = fileScannerService;
        _dialogService = dialogService;
        _downloadService = downloadService;
        
        WeakReferenceMessenger.Default.Register<UsuarioLogeadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<UsuarioDesconectadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<EpisodioActualizadoMensaje>(this);
        WeakReferenceMessenger.Default.Register<DescargaProgresoMensaje>(this);
        EstaConectado = _authService.EstaAutenticado();
    }

    public void Receive(UsuarioLogeadoMensaje message) => EstaConectado = true;
    public void Receive(UsuarioDesconectadoMensaje message) => EstaConectado = false;
    public void Receive(EpisodioActualizadoMensaje message)
    {
        if (AnimeSeleccionado == null || AnimeSeleccionado.AniListId != message.AnimeId) return;

        var episodio = _todosLosEpisodios.FirstOrDefault(e => e.NumeroEpisodio == message.NumeroEpisodio);
        if (episodio != null)
        {
            // Ejecutar en el hilo principal de la UI
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.Invoke(() =>
                {
                    episodio.Visto = message.VistoLocal;
                    episodio.ProgresoSegundos = message.ProgresoSegundos;
                    if (message.TotalSegundos > 0)
                    {
                        episodio.TotalSegundos = message.TotalSegundos;
                    }
                    AplicarFiltrosYOrdenamiento();
                });
            }
            else
            {
                episodio.Visto = message.VistoLocal;
                episodio.ProgresoSegundos = message.ProgresoSegundos;
                if (message.TotalSegundos > 0)
                {
                    episodio.TotalSegundos = message.TotalSegundos;
                }
            }
        }
    }

    public void Receive(DescargaProgresoMensaje message)
    {
        if (AnimeSeleccionado == null || AnimeSeleccionado.AniListId != message.AniListId) return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var episodio = _todosLosEpisodios.FirstOrDefault(e => e.NumeroEpisodio == message.NumeroEpisodio);
            if (episodio != null)
            {
                episodio.IsDownloading = message.IsDownloading;
                episodio.DownloadProgress = message.Progreso;

                if (message.IsCompleted)
                {
                    episodio.Descargado = true;
                    episodio.RutaCompleta = message.RutaArchivo;
                    episodio.CalcularTamanoArchivo();
                    _dialogService.MostrarDialogoAsync("Descarga Completada", 
                        $"El episodio {episodio.NumeroEpisodio} se ha descargado exitosamente.", 
                        false, "CheckCircleOutline", "#4CAF50");
                    AplicarFiltrosYOrdenamiento();
                }
                else if (!string.IsNullOrEmpty(message.Error))
                {
                    _dialogService.MostrarDialogoAsync("Error de descarga", 
                        $"Error al descargar el episodio {episodio.NumeroEpisodio}:\n{message.Error}", 
                        false, "AlertCircleOutline", "#E53935");
                }
            }
        });
    }

    public async Task InicializarAsync(AnimeItem anime)
    {
        AnimeSeleccionado = anime;
        _todosLosEpisodios.Clear();
        EpisodiosDelAnime.Clear();

        // 1. CARGA RÁPIDA DE BASE DE DATOS Y DISCO (SIN INTERNET)
        var registrosGuardados = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
        
        // Escaneamos la carpeta local si existe
        List<EpisodioItem> encontrados = new();
        if (!string.IsNullOrEmpty(anime.RutaCarpeta))
        {
            encontrados = await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta);
        }

        int maxEpisodio = 0;
        if (encontrados.Count > 0)
            maxEpisodio = encontrados.Max(e => e.NumeroEpisodio);
        if (anime.TotalEpisodios > maxEpisodio)
            maxEpisodio = anime.TotalEpisodios;
        if (anime.EpisodiosVistos > maxEpisodio)
            maxEpisodio = anime.EpisodiosVistos;

        // Límite de seguridad para prevenir asignaciones anómalas de memoria (máx 3000)
        const int LimiteSeguridadEpisodios = 3000;
        int episodiosACargar = Math.Min(maxEpisodio, LimiteSeguridadEpisodios);

        // USAMOS TASK.RUN PARA NO CONGELAR LA UI
        var episodiosGenerados = await Task.Run(() => 
        {
            var archivosPorEp = encontrados.GroupBy(e => e.NumeroEpisodio)
                                           .ToDictionary(g => g.Key, g => g.First());
            var registrosPorEp = registrosGuardados.GroupBy(r => r.NumeroEpisodio)
                                                   .ToDictionary(g => g.Key, g => g.First());

            var temp = new List<EpisodioItem>(episodiosACargar);
            for (int i = 1; i <= episodiosACargar; i++)
            {
                archivosPorEp.TryGetValue(i, out var archivoLocal);
                registrosPorEp.TryGetValue(i, out var memoria);
                
                bool estaDescargando = _downloadService.EstaDescargando(anime.AniListId, i, out double prog);

                temp.Add(new EpisodioItem
                {
                    NumeroEpisodio = i,
                    Descargado = archivoLocal != null,
                    RutaCompleta = archivoLocal?.RutaCompleta ?? string.Empty,
                    TamanoArchivoFormateado = archivoLocal?.TamanoArchivoFormateado ?? string.Empty,
                    Visto = memoria != null && memoria.VistoLocal,
                    Favorito = memoria != null && memoria.FavoritoLocal,
                    ProgresoSegundos = memoria?.ProgresoSegundos ?? 0,
                    TotalSegundos = memoria?.TotalSegundos ?? 0,
                    IsDownloading = estaDescargando,
                    DownloadProgress = prog
                });
            }
            return temp;
        });

        _todosLosEpisodios.AddRange(episodiosGenerados);
        AplicarFiltrosYOrdenamiento();
    }

    partial void OnOrdenAscendenteChanged(bool value) => AplicarFiltrosYOrdenamiento();
    partial void OnFiltroEpisodiosChanged(string value) => AplicarFiltrosYOrdenamiento();

    private void AplicarFiltrosYOrdenamiento()
    {
        if (_todosLosEpisodios == null || _todosLosEpisodios.Count == 0)
        {
            EpisodiosDelAnime.Clear();
            if (AnimeSeleccionado != null && (AnimeSeleccionado.Estado == "NOT_YET_RELEASED" || AnimeSeleccionado.TotalEpisodios == 0))
            {
                MensajeSinEpisodios = "Anime aún no estrenado";
                SubtituloSinEpisodios = "Este anime aún no cuenta con episodios emitidos.";
            }
            else
            {
                MensajeSinEpisodios = "No hay episodios para mostrar";
                SubtituloSinEpisodios = "No se encontraron episodios para este anime.";
            }
            return;
        }

        var query = _todosLosEpisodios.AsEnumerable();

        switch (FiltroEpisodios)
        {
            case "Descargados":
                query = query.Where(e => e.Descargado);
                break;
            case "Vistos":
                query = query.Where(e => e.Visto);
                break;
            case "No Vistos":
                query = query.Where(e => !e.Visto);
                break;
            case "Favoritos":
                query = query.Where(e => e.Favorito);
                break;
        }

        query = OrdenAscendente ? query.OrderBy(e => e.NumeroEpisodio) : query.OrderByDescending(e => e.NumeroEpisodio);

        EpisodiosDelAnime.Clear();
        foreach (var ep in query) EpisodiosDelAnime.Add(ep);

        if (EpisodiosDelAnime.Count == 0)
        {
            MensajeSinEpisodios = "No hay episodios con este filtro";
            SubtituloSinEpisodios = $"No se encontraron episodios en la categoría '{FiltroEpisodios}'.";
        }
    }

    [RelayCommand]
    private void VolverAGaleria()
    {
        WeakReferenceMessenger.Default.Send(new NavegarMensaje_Galeria());
    }
    
    [RelayCommand]
    private async Task EliminarAnimeActualAsync()
    {
        if (AnimeSeleccionado == null) return;

        bool confirmacion = await _dialogService.MostrarDialogoAsync("Confirmar Eliminación", $"¿Estás seguro de que deseas eliminar '{AnimeSeleccionado.Titulo}' de tu biblioteca?", true, "AlertCircleOutline", "#E53935");
        if (confirmacion)
        {
            await _databaseService.EliminarAnimeAsync(AnimeSeleccionado);
            VolverAGaleria();
        }
    }
    
    [RelayCommand]
    private async Task ReproducirEpisodio(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        
        if (!episodio.Descargado || !File.Exists(episodio.RutaCompleta))
        {
            await _dialogService.MostrarDialogoAsync("Episodio no encontrado", $"Archivo no encontrado para el episodio {episodio.NumeroEpisodio}.\nBuscando opciones de descarga en el navegador web...", false, "InformationOutline", "#FFC107");

            string numeroEp = episodio.NumeroEpisodio.ToString("D2"); 
            string busqueda = $"{AnimeSeleccionado.Titulo} {numeroEp}";
            string url = $"https://nyaa.si/?f=0&c=0_0&q={Uri.EscapeDataString(busqueda)}";
            
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            return; 
        }

        try
        {
            // Enviamos un mensaje a la aplicación principal para que abra nuestra nueva ventana de reproductor
            var episodiosDisponibles = _todosLosEpisodios.Where(e => e.Descargado && File.Exists(e.RutaCompleta)).ToList();
            WeakReferenceMessenger.Default.Send(new NavegarMensaje_Reproductor(
                episodio.RutaCompleta,
                AnimeSeleccionado.AniListId,
                AnimeSeleccionado.Titulo,
                episodio.NumeroEpisodio,
                EpisodiosDisponibles: episodiosDisponibles
            ));
        }
        catch (System.Exception ex)
        {
            await _dialogService.MostrarDialogoAsync("Error", $"Error al intentar iniciar el reproductor: {ex.Message}", false, "AlertCircleOutline", "#E53935");
        }
    }
    
    [RelayCommand]
    private async Task AlternarFavoritoEpisodioAsync(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        
        episodio.Favorito = !episodio.Favorito;
        
        var registro = new RegistroEpisodio
        {
            AniListId = AnimeSeleccionado.AniListId,
            NumeroEpisodio = episodio.NumeroEpisodio,
            RutaArchivo = episodio.RutaCompleta,
            VistoLocal = episodio.Visto,
            FavoritoLocal = episodio.Favorito,
            SincronizadoEnNube = false 
        };
        
        await _databaseService.GuardarRegistroEpisodioAsync(registro);
        
        if (FiltroEpisodios == "Favoritos")
        {
            AplicarFiltrosYOrdenamiento();
        }
    }
    
    [RelayCommand]
    private async Task MarcarVistosAsync(System.Collections.IList episodiosSeleccionados)
    {
        if (episodiosSeleccionados == null || episodiosSeleccionados.Count == 0 || AnimeSeleccionado == null) return;

        var episodios = episodiosSeleccionados.Cast<EpisodioItem>().ToList();
        var listaRegistros = new List<RegistroEpisodio>(episodios.Count);
        foreach (var ep in episodios)
        {
            ep.Visto = true;
            listaRegistros.Add(new RegistroEpisodio 
            {
                AniListId = AnimeSeleccionado.AniListId,
                NumeroEpisodio = ep.NumeroEpisodio,
                VistoLocal = true,
                RutaArchivo = ep.RutaCompleta ?? string.Empty
            });
        }
        await _databaseService.GuardarRegistrosEpisodioBulkAsync(listaRegistros);
    }

    [RelayCommand]
    private async Task MarcarNoVistosAsync(System.Collections.IList episodiosSeleccionados)
    {
        if (episodiosSeleccionados == null || episodiosSeleccionados.Count == 0 || AnimeSeleccionado == null) return;

        var episodios = episodiosSeleccionados.Cast<EpisodioItem>().ToList();
        var listaRegistros = new List<RegistroEpisodio>(episodios.Count);
        foreach (var ep in episodios)
        {
            ep.Visto = false;
            listaRegistros.Add(new RegistroEpisodio 
            {
                AniListId = AnimeSeleccionado.AniListId,
                NumeroEpisodio = ep.NumeroEpisodio,
                VistoLocal = false,
                RutaArchivo = ep.RutaCompleta ?? string.Empty
            });
        }
        await _databaseService.GuardarRegistrosEpisodioBulkAsync(listaRegistros);
    }

    [RelayCommand]
    private async Task ActualizarAnimeActualAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        var datosFrescos = await _animeTrackingService.ObtenerAnimePorIdAsync(AnimeSeleccionado.AniListId);
        if (datosFrescos != null)
        {
            string estadoFresco = datosFrescos.Status?.ToUpperInvariant() ?? "UNKNOWN";
            int episodiosEmitidos = 0;

            if (estadoFresco == "NOT_YET_RELEASED")
            {
                episodiosEmitidos = 0;
            }
            else if (estadoFresco == "RELEASING")
            {
                if (datosFrescos.NextAiringEpisode != null)
                {
                    episodiosEmitidos = Math.Max(0, datosFrescos.NextAiringEpisode.Episode - 1);
                }
                else
                {
                    episodiosEmitidos = datosFrescos.Episodes ?? 0;
                }
            }
            else
            {
                episodiosEmitidos = datosFrescos.Episodes ?? AnimeSeleccionado.TotalEpisodios;
            }

            var titulosAlt = new List<string>();
            if (!string.IsNullOrWhiteSpace(datosFrescos.Title.English)) titulosAlt.Add(datosFrescos.Title.English);
            if (!string.IsNullOrWhiteSpace(datosFrescos.Title.UserPreferred) && datosFrescos.Title.UserPreferred != datosFrescos.Title.Romaji) titulosAlt.Add(datosFrescos.Title.UserPreferred);
            if (datosFrescos.Synonyms != null) titulosAlt.AddRange(datosFrescos.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s)));
            AnimeSeleccionado.NombresAlternativos = string.Join(" | ", titulosAlt.Distinct());

            AnimeSeleccionado.TotalEpisodios = episodiosEmitidos;
            AnimeSeleccionado.Estado = datosFrescos.Status ?? "UNKNOWN";
            AnimeSeleccionado.Generos = datosFrescos.Genres != null ? string.Join(", ", datosFrescos.Genres) : "";
            AnimeSeleccionado.UrlPortada = datosFrescos.CoverImage?.ExtraLarge ?? datosFrescos.CoverImage?.Large ?? AnimeSeleccionado.UrlPortada;
            
            await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
            
            await InicializarAsync(AnimeSeleccionado);
            
            await _dialogService.MostrarDialogoAsync("Actualizado", $"Anime actualizado. Total de episodios emitidos: {episodiosEmitidos}", false, "CheckCircleOutline", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync("Error", "Error al conectar con AniList para actualizar.", false, "AlertCircleOutline", "#E53935");
        }
    }
    
    [RelayCommand]
    private async Task AbrirEditorSeguimientoAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        EditEstado = "CURRENT";
        EditProgreso = 0;
        EditPuntaje = 0;
        EditFechaInicio = null;
        EditFechaFin = null;
        MostrandoEditorSeguimiento = true;

        var token = _authService.ObtenerTokenGuardado();
        if (string.IsNullOrEmpty(token)) return;

        var datos = await _animeTrackingService.ObtenerSeguimientoUsuarioAsync(AnimeSeleccionado.AniListId, token);
        if (datos != null)
        {
            EditEstadoVisual = ConvertirEstadoAEspanol(datos.Status ?? "CURRENT");
            EditProgreso = datos.Progress;
            EditPuntaje = datos.Score;
            
            if (datos.StartedAt != null && datos.StartedAt.Year.HasValue)
                EditFechaInicio = new DateTime(datos.StartedAt.Year.Value, datos.StartedAt.Month ?? 1, datos.StartedAt.Day ?? 1);
            
            if (datos.CompletedAt != null && datos.CompletedAt.Year.HasValue)
                EditFechaFin = new DateTime(datos.CompletedAt.Year.Value, datos.CompletedAt.Month ?? 1, datos.CompletedAt.Day ?? 1);
        }
    }

    [RelayCommand]
    private async Task GuardarEditorSeguimientoAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        var token = _authService.ObtenerTokenGuardado();
        if (string.IsNullOrEmpty(token)) 
        {
            await _dialogService.MostrarDialogoAsync("Error de Autenticación", "Debes conectar tu cuenta de AniList primero.", false, "AlertCircleOutline", "#E53935");
            return;
        }

        string estadoEnIngles = ConvertirEstadoAIngles(EditEstadoVisual);
        bool exito = await _animeTrackingService.GuardarSeguimientoUsuarioAsync(
            AnimeSeleccionado.AniListId, estadoEnIngles, EditProgreso, EditPuntaje, EditFechaInicio, EditFechaFin, token);
            
        if (exito)
        {
            MostrandoEditorSeguimiento = false;
            AnimeSeleccionado.EstadoUsuario = estadoEnIngles;
            await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
            await _dialogService.MostrarDialogoAsync("Nube Sincronizada", "¡Seguimiento actualizado en AniList con éxito!", false, "CloudCheck", "#4CAF50");
        }
        else
        {
            await _dialogService.MostrarDialogoAsync("Error de Sincronización", "Hubo un error de comunicación al intentar guardar tus datos en AniList.", false, "AlertCircleOutline", "#E53935");
        }
    }
    
    [RelayCommand]
    private void CerrarEditorSeguimiento()
    {
        MostrandoEditorSeguimiento = false;
    }
    
    private static string ConvertirEstadoAIngles(string estadoVisual) => estadoVisual switch
    {
        "Viendo" => "CURRENT",
        "Finalizado" => "COMPLETED",
        "En Pausa" => "PAUSED",
        "Abandonado" => "DROPPED",
        "Planeando" => "PLANNING",
        _ => "CURRENT"
    };

    private static string ConvertirEstadoAEspanol(string estadoIngles) => estadoIngles switch
    {
        "CURRENT" => "Viendo",
        "COMPLETED" => "Finalizado",
        "PAUSED" => "En Pausa",
        "DROPPED" => "Abandonado",
        "PLANNING" => "Planeando",
        _ => "Viendo"
    };

    [RelayCommand]
    private async Task DescargarEpisodioAsync(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        if (episodio.IsDownloading) return;

        episodio.IsDownloading = true;
        episodio.DownloadProgress = 0;

        var titulosCandidatos = new List<string>();
        if (!string.IsNullOrWhiteSpace(AnimeSeleccionado.NombresAlternativos))
        {
            titulosCandidatos.AddRange(AnimeSeleccionado.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries));
        }

        await _downloadService.IniciarDescargaEpisodioAsync(
            AnimeSeleccionado.AniListId, 
            AnimeSeleccionado.Titulo, 
            AnimeSeleccionado.RutaCarpeta, 
            episodio.NumeroEpisodio,
            titulosCandidatos);
    }
}
