using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using MaterialDesignThemes.Wpf;

namespace AnimeLocalTracker.ViewModels;

public partial class MainViewModel : ObservableObject 
{
    private readonly IAniListService _aniListService;
    private readonly IDatabaseService _databaseService;
    
    public bool MostrandoGaleria => AnimeSeleccionado == null;
    public bool MostrandoDetalle => AnimeSeleccionado != null;
    public bool BibliotecaVacia => BibliotecaLocales.Count == 0;

    public ObservableCollection<AnimeItem> BibliotecaLocales { get; } = [];
    
    // Nuevas dependencias para OAuth
    private readonly AuthService _authService = new();
    
    [ObservableProperty]
    private bool _estaConectadoANube;
    
    // === VARIABLES PARA LA BARRA DE PROGRESO ===
    [ObservableProperty]
    private bool _estaActualizando;
    
    [ObservableProperty]
    private int _progresoTotal;
    
    [ObservableProperty]
    private int _progresoActual;
    
    [ObservableProperty]
    private string _textoProgreso = string.Empty;
    
    // === SISTEMA DE DIÁLOGOS CUSTOM ===
    [ObservableProperty] private bool _isDialogOpen; // Para el buscador
    [ObservableProperty] private bool _dialogoVisible;
    [ObservableProperty] private string _dialogoTitulo = "";
    [ObservableProperty] private string _dialogoMensaje = "";
    [ObservableProperty] private bool _dialogoEsConfirmacion;
    [ObservableProperty] private string _dialogoIcono = "InformationOutline";
    [ObservableProperty] private string _dialogoColor = "#3F51B5";
    
    private TaskCompletionSource<bool>? _dialogTcs;

    public async Task<bool> MostrarDialogoAsync(string titulo, string mensaje, bool esConfirmacion = false, string icono = "InformationOutline", string color = "#3F51B5")
    {
        DialogoTitulo = titulo;
        DialogoMensaje = mensaje;
        DialogoEsConfirmacion = esConfirmacion;
        DialogoIcono = icono;
        DialogoColor = color;
        
        DialogoVisible = true;
        
        _dialogTcs = new TaskCompletionSource<bool>();
        return await _dialogTcs.Task;
    }

    [RelayCommand]
    private void AceptarDialogo()
    {
        DialogoVisible = false;
        _dialogTcs?.TrySetResult(true);
    }

    [RelayCommand]
    private void CancelarDialogo()
    {
        DialogoVisible = false;
        _dialogTcs?.TrySetResult(false);
    }
    // === VARIABLES DEL EDITOR DE SEGUIMIENTO (ANILIST) ===
    [ObservableProperty] private bool _mostrandoEditorSeguimiento;
    [ObservableProperty] private string _editEstado = "CURRENT";
    [ObservableProperty] private int _editProgreso;
    [ObservableProperty] private float _editPuntaje;
    [ObservableProperty] private DateTime? _editFechaInicio;
    [ObservableProperty] private DateTime? _editFechaFin;
    // === VARIABLES DEL PERFIL DE USUARIO ===
    [ObservableProperty] private bool _estaConectado;
    [ObservableProperty] private string _nombreUsuarioAniList = "Usuario";
    [ObservableProperty] private string? _avatarUsuarioAniList;
    
    // === BUSCADOR DINÁMICO ===
    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<AniListMedia> _resultadosBusqueda = [];
    
    private System.Threading.CancellationTokenSource? _searchCts;
    private string _textoBusqueda = string.Empty;
    public string TextoBusqueda
    {
        get => _textoBusqueda;
        set
        {
            SetProperty(ref _textoBusqueda, value);
            EjecutarBusquedaEnVivoAsync(value); // ¡El disparador automático!
        }
    }

    // Los 5 estados exactos que exige la API de AniList
    public List<string> OpcionesEstado { get; } = ["CURRENT", "COMPLETED", "PAUSED", "DROPPED", "PLANNING"];

    public MainViewModel(IAniListService aniListService, IDatabaseService databaseService)
    {
        _aniListService = aniListService;
        _databaseService = databaseService;
        
        // Comprobamos si el token existe en el disco
        EstaConectadoANube = _authService.EstaAutenticado();
        
        _ = CargarBibliotecaAsync(); 
    }
    
    [RelayCommand]
    private async Task ConectarAniListAsync()
    {
        // 1. Iniciamos el servidor y pedimos permisos en el navegador
        bool exito = await _authService.IniciarSesionAsync();
        
        if (exito)
        {
            await MostrarDialogoAsync("Nube Activada", "¡Conectado a AniList exitosamente! Tu progreso ahora se sincronizará.", false, "CloudCheck", "#4CAF50");
            
            // 2. ¡LA LÍNEA CLAVE! Carga la foto y cambia "EstaConectado" a true para que el XAML reaccione
            await CargarPerfilUsuarioAsync(); 
        }
    }
    
    private async Task CargarBibliotecaAsync()
    {
        // === LA CURA PARA LA DUPLICACIÓN VISUAL ===
        BibliotecaLocales.Clear(); 
        
        var animes = await _databaseService.ObtenerTodosLosAnimesAsync();
        foreach (var anime in animes)
        {
            BibliotecaLocales.Add(anime);
        }
        
        await CargarPerfilUsuarioAsync();
        OnPropertyChanged(nameof(BibliotecaVacia));
    }

    [RelayCommand]
    private void AñadirAnimeManual()
    {
        // 1. Limpiamos cualquier búsqueda anterior
        TextoBusqueda = string.Empty;
        ResultadosBusqueda.Clear();
        
        // 2. Abrimos el Dialog activando la propiedad IsOpen
        IsDialogOpen = true;
    }

    [RelayCommand]
    private void CerrarDialogoBusqueda()
    {
        IsDialogOpen = false;
    }
    
    // ==============================================================
    // SISTEMA DE NAVEGACIÓN
    // ==============================================================
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MostrandoGaleria))]
    [NotifyPropertyChangedFor(nameof(MostrandoDetalle))]
    private AnimeItem? _animeSeleccionado;

    private List<EpisodioItem> _todosLosEpisodios = new();

    [ObservableProperty]
    private bool _ordenAscendente = true;

    [ObservableProperty]
    private string _filtroEpisodios = "Todos";

    public string[] OpcionesFiltro { get; } = ["Todos", "Vistos", "No Vistos", "Favoritos"];

    partial void OnOrdenAscendenteChanged(bool value) => AplicarFiltrosYOrdenamiento();
    partial void OnFiltroEpisodiosChanged(string value) => AplicarFiltrosYOrdenamiento();

    private void AplicarFiltrosYOrdenamiento()
    {
        if (_todosLosEpisodios == null || _todosLosEpisodios.Count == 0) return;

        var query = _todosLosEpisodios.AsEnumerable();

        switch (FiltroEpisodios)
        {
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
    }

    public ObservableCollection<EpisodioItem> EpisodiosDelAnime { get; } = [];
    
    [RelayCommand]
    private async Task AbrirDetalle(AnimeItem anime)
    {
        AnimeSeleccionado = anime; 
        EpisodiosDelAnime.Clear(); 
        _todosLosEpisodios.Clear();
        
        OrdenAscendente = true;
        FiltroEpisodios = "Todos";
        
        var scanner = new FileScannerService();
        var encontrados = await scanner.EscanearEpisodiosAsync(anime.RutaCarpeta);
        var registrosGuardados = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
        
        // Si AniList dijo que tiene 12, mostramos 12. 
        // Si devolvió 0 (en emisión), mostramos hasta el último que tengas descargado.
        int maxEpisodio = anime.TotalEpisodios > 0 ? anime.TotalEpisodios : 
            (encontrados.Count > 0 ? encontrados.Max(e => e.NumeroEpisodio) : 12);

        // Generamos la lista del 1 al Total
        for (int i = 1; i <= maxEpisodio; i++)
        {
            var archivoLocal = encontrados.FirstOrDefault(e => e.NumeroEpisodio == i);
            var memoria = registrosGuardados.FirstOrDefault(r => r.NumeroEpisodio == i);
            
            var ep = new EpisodioItem
            {
                NumeroEpisodio = i,
                Descargado = archivoLocal != null, // Si lo encontró el escáner, es true
                RutaCompleta = archivoLocal?.RutaCompleta ?? string.Empty,
                Visto = memoria != null && memoria.VistoLocal,
                Favorito = memoria != null && memoria.FavoritoLocal
            };
            
            _todosLosEpisodios.Add(ep);
        }
        
        AplicarFiltrosYOrdenamiento();
    }

    [RelayCommand]
    private void VolverAGaleria()
    {
        AnimeSeleccionado = null; // Volvemos a la pantalla principal
    }
    
    [RelayCommand]
    private async Task EliminarAnimeActualAsync()
    {
        if (AnimeSeleccionado == null) return;

        bool confirmacion = await MostrarDialogoAsync("Confirmar Eliminación", $"¿Estás seguro de que deseas eliminar '{AnimeSeleccionado.Titulo}' de tu biblioteca?", true, "AlertCircleOutline", "#E53935");
        if (confirmacion)
        {
            // 1. Lo borramos del disco duro
            await _databaseService.EliminarAnimeAsync(AnimeSeleccionado);
            
            // 2. Lo borramos de la interfaz gráfica
            BibliotecaLocales.Remove(AnimeSeleccionado);
            OnPropertyChanged(nameof(BibliotecaVacia));
            
            // 3. Volvemos a la pantalla principal automáticamente
            VolverAGaleria();
        }
    }
    
    // ==============================================================
    // REPRODUCTOR DE VIDEO
    // ==============================================================
    [RelayCommand]
    private async Task ReproducirEpisodio(EpisodioItem episodio)
    {
        if (episodio == null || AnimeSeleccionado == null) return;
        
        // === LA NUEVA LÓGICA DE DESCARGA ===
        if (!episodio.Descargado || !File.Exists(episodio.RutaCompleta))
        {
            await MostrarDialogoAsync("Episodio no encontrado", $"Archivo no encontrado para el episodio {episodio.NumeroEpisodio}.\nBuscando opciones de descarga en el navegador web...", false, "InformationOutline", "#FFC107");

            // Formateamos el número a dos dígitos (ej: "03" en vez de "3") porque así lo buscan los trackers
            string numeroEp = episodio.NumeroEpisodio.ToString("D2"); 
            string busqueda = $"{AnimeSeleccionado.Titulo} {numeroEp}";
            
            // Armamos la URL de Nyaa.si (puedes cambiarla por la de Google si prefieres)
            string url = $"https://nyaa.si/?f=0&c=0_0&q={Uri.EscapeDataString(busqueda)}";
            
            // Le ordenamos a Windows que abra tu navegador predeterminado
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo 
            { 
                FileName = url, 
                UseShellExecute = true 
            });
            
            return; // Salimos para no intentar abrir PotPlayer
        }

        try
        {
            // 1. Lanzamos el video usando el motor nativo de Windows (Fire and Forget)
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = episodio.RutaCompleta,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(startInfo);

            // 2. Le damos 2 segundos al reproductor para que arranque y se registre en la RAM
            await Task.Delay(2000);

            // 3. Cazamos el proceso REAL de PotPlayer por su nombre
            // Revisamos las tres formas comunes en las que PotPlayer se llama a sí mismo
            var procesos = System.Diagnostics.Process.GetProcessesByName("PotPlayer64");
            if (procesos.Length == 0) procesos = System.Diagnostics.Process.GetProcessesByName("PotPlayerMini64");
            if (procesos.Length == 0) procesos = System.Diagnostics.Process.GetProcessesByName("PotPlayerMini");

            // === LA MAGIA DEL TRACKING HÍBRIDO ===
            if (procesos.Length > 0)
            {
                var reproductor = procesos[0]; 
                reproductor.EnableRaisingEvents = true;
                
                await reproductor.WaitForExitAsync();

                if (AnimeSeleccionado != null)
                {
                    // 1. Fase Local (Single Source of Truth)
                    var nuevoRegistro = new RegistroEpisodio
                    {
                        AniListId = AnimeSeleccionado.AniListId,
                        NumeroEpisodio = episodio.NumeroEpisodio,
                        RutaArchivo = episodio.RutaCompleta,
                        VistoLocal = true,
                        FavoritoLocal = episodio.Favorito,
                        SincronizadoEnNube = false 
                    };
                    
                    // Guardamos rápidamente en el disco duro mecánico o SSD
                    await _databaseService.GuardarRegistroEpisodioAsync(nuevoRegistro);

                    // 2. Fase Nube (Sincronización Eventual)
                    if (EstaConectadoANube)
                    {
                        string token = _authService.ObtenerTokenGuardado();
                        bool exitoEnNube = await _aniListService.ActualizarProgresoAsync(AnimeSeleccionado.AniListId, episodio.NumeroEpisodio, token);
                        
                        if (exitoEnNube)
                        {
                            nuevoRegistro.SincronizadoEnNube = true;
                            // Actualizamos el registro en SQLite para marcar que la nube ya lo sabe
                            await _databaseService.GuardarRegistroEpisodioAsync(nuevoRegistro); 
                        }
                    }
                }

                // 3. Reacción de la Interfaz
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    episodio.Visto = true;
                });
            }
        }
        catch (System.Exception ex)
        {
            await MostrarDialogoAsync("Error", $"Error al intentar reproducir: {ex.Message}", false, "AlertCircleOutline", "#E53935");
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
            SincronizadoEnNube = false // Lo marcamos como no sincronizado por si implementamos favoritos en nube
        };
        
        await _databaseService.GuardarRegistroEpisodioAsync(registro);
        
        if (FiltroEpisodios == "Favoritos")
        {
            AplicarFiltrosYOrdenamiento();
        }
    }
    
    [RelayCommand]
    private async Task ActualizarBibliotecaAsync()
    {
        // 1. Evitamos que el usuario presione el botón múltiples veces
        if (EstaActualizando) return; 
        
        var listaAnimes = BibliotecaLocales.ToList();
        if (listaAnimes.Count == 0) return;

        // 2. Encendemos la barra de progreso en la interfaz
        EstaActualizando = true;
        ProgresoTotal = listaAnimes.Count;
        ProgresoActual = 0;

        foreach (var anime in listaAnimes)
        {
            ProgresoActual++;
            TextoProgreso = $"Sincronizando: {anime.Titulo} ({ProgresoActual}/{ProgresoTotal})";

            var datosFrescos = await _aniListService.ObtenerAnimePorIdAsync(anime.AniListId);
            
            if (datosFrescos != null)
            {
                int episodiosEmitidos = datosFrescos.NextAiringEpisode != null 
                    ? datosFrescos.NextAiringEpisode.Episode - 1 
                    : (datosFrescos.Episodes ?? 0);
                
                anime.TotalEpisodios = episodiosEmitidos;
                anime.Estado = datosFrescos.Status ?? "UNKNOWN";
                
                await _databaseService.ActualizarAnimeAsync(anime);
            }
            
            // 3. RATE LIMITING: Pausa de seguridad de 250ms para no saturar los servidores de AniList
            await Task.Delay(250); 
        }

        
        // 4. Mostramos el éxito y apagamos la barra tras 2 segundos
        TextoProgreso = "¡Actualización completada con éxito!";
        await Task.Delay(2000); 
        EstaActualizando = false;
    }
    
    [RelayCommand]
    private async Task AbrirEditorSeguimientoAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        // 1. Reseteamos los campos y mostramos el panel de inmediato (UX fluida)
        EditEstado = "CURRENT";
        EditProgreso = 0;
        EditPuntaje = 0;
        EditFechaInicio = null;
        EditFechaFin = null;
        MostrandoEditorSeguimiento = true;

        // 2. Traemos tu token y consultamos la nube
        var token = _authService.ObtenerTokenGuardado();
        if (string.IsNullOrEmpty(token)) return;

        var datos = await _aniListService.ObtenerSeguimientoUsuarioAsync(AnimeSeleccionado.AniListId, token);
        if (datos != null)
        {
            // 3. Rellenamos los campos con lo que haya en internet
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
    private async Task GuardarSeguimientoAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        var token = _authService.ObtenerTokenGuardado();
        if (!EstaConectado) 
        {
            await MostrarDialogoAsync("Error de Autenticación", "Debes conectar tu cuenta de AniList primero.", false, "AlertCircleOutline", "#E53935");
            return;
        }

        // Enviamos la mutación a los servidores de Japón
        string estadoEnIngles = ConvertirEstadoAIngles(EditEstadoVisual);
        bool exito = await _aniListService.GuardarSeguimientoUsuarioAsync(
            AnimeSeleccionado.AniListId, estadoEnIngles, EditProgreso, EditPuntaje, EditFechaInicio, EditFechaFin, token);
            
        if (exito)
        {
            // 1. Cerramos el panel
            MostrandoEditorSeguimiento = false;
            
            // 2. SINCRONIZACIÓN REACTIVA: Actualizamos la memoria local inmediatamente
            AnimeSeleccionado.Estado = estadoEnIngles;
            
            // 3. Guardamos el nuevo estado en tu base de datos SQLite
            await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
            
            // 4. Feedback visual absoluto para ti
            await MostrarDialogoAsync("Nube Sincronizada", "¡Seguimiento actualizado en AniList con éxito!", false, "CloudCheck", "#4CAF50");
        }
        else
        {
            // Mantenemos el error genérico por si falla la conexión
            await MostrarDialogoAsync("Error de Sincronización", "Hubo un error de comunicación al intentar guardar tus datos en AniList.", false, "AlertCircleOutline", "#E53935");
        }
    }
    
    [RelayCommand]
    private void CerrarEditorSeguimiento()
    {
        MostrandoEditorSeguimiento = false;
    }
    
    // === TRADUCTOR VISUAL PARA EL PANEL ===
    // Lo que ve el usuario (Español)
    public List<string> OpcionesEstadoVisual { get; } = ["Viendo", "Finalizado", "En Pausa", "Abandonado", "Planeando"];
    
    [ObservableProperty] private string _editEstadoVisual = "Viendo";
    
    // El motor traductor privado
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
    private async Task MarcarVistosAsync(System.Collections.IList episodiosSeleccionados)
    {
        if (episodiosSeleccionados == null || episodiosSeleccionados.Count == 0 || AnimeSeleccionado == null) return;

        var episodios = episodiosSeleccionados.Cast<EpisodioItem>().ToList();

        foreach (var ep in episodios)
        {
            ep.Visto = true;
            
            var registro = new RegistroEpisodio 
            {
                AniListId = AnimeSeleccionado.AniListId,
                NumeroEpisodio = ep.NumeroEpisodio,
                VistoLocal = true,
                RutaArchivo = ep.RutaCompleta ?? string.Empty
            };
            
            await _databaseService.GuardarRegistroEpisodioAsync(registro); 
        }
    }

    [RelayCommand]
    private async Task MarcarNoVistosAsync(System.Collections.IList episodiosSeleccionados)
    {
        if (episodiosSeleccionados == null || episodiosSeleccionados.Count == 0 || AnimeSeleccionado == null) return;

        var episodios = episodiosSeleccionados.Cast<EpisodioItem>().ToList();

        foreach (var ep in episodios)
        {
            ep.Visto = false; // Actualiza la UI
            
            var registro = new RegistroEpisodio 
            {
                AniListId = AnimeSeleccionado.AniListId,
                NumeroEpisodio = ep.NumeroEpisodio,
                VistoLocal = false,
                RutaArchivo = ep.RutaCompleta ?? string.Empty
            };
            
            await _databaseService.GuardarRegistroEpisodioAsync(registro); 
        }
    }
    
    public async Task CargarPerfilUsuarioAsync()
    {
        var token = _authService.ObtenerTokenGuardado();
        if (!string.IsNullOrEmpty(token))
        {
            EstaConectado = true;
            var perfil = await _aniListService.ObtenerPerfilUsuarioAsync(token);
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
    
    private async void EjecutarBusquedaEnVivoAsync(string busqueda)
    {
        // Si borras el texto o es muy corto, limpiamos los resultados
        if (string.IsNullOrWhiteSpace(busqueda) || busqueda.Length < 3)
        {
            ResultadosBusqueda.Clear();
            return;
        }

        // Cancelamos la búsqueda anterior si sigues tecleando rápido (DEBOUNCING)
        _searchCts?.Cancel();
        _searchCts = new System.Threading.CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            // Esperamos 500 milisegundos a que dejes de teclear
            await Task.Delay(500, token); 
            
            if (!token.IsCancellationRequested)
            {
                var resultados = await _aniListService.BuscarAnimesEnVivoAsync(busqueda);
                ResultadosBusqueda.Clear();
                foreach (var r in resultados) ResultadosBusqueda.Add(r);
            }
        }
        catch (TaskCanceledException) { /* Se ignora porque significa que el usuario siguió tecleando */ }
    }

    [RelayCommand]
    private async Task SeleccionarYCrearAnimeAsync(AniListMedia animeAPI)
    {
        if (animeAPI?.Title?.Romaji == null) return;

        // 1. Limpiamos el nombre de caracteres prohibidos por Windows (:, /, \, *, ?, etc)
        string nombreSeguro = string.Join("_", animeAPI.Title.Romaji.Split(System.IO.Path.GetInvalidFileNameChars()));
        
        // 2. Definimos la ruta base (Ej: C:\Users\HP\Videos\Anime)
        string rutaBaseVideos = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Anime");
        string nuevaRutaCarpeta = System.IO.Path.Combine(rutaBaseVideos, nombreSeguro);

        // 3. CREACIÓN AUTOMÁTICA DE CARPETA
        if (!System.IO.Directory.Exists(nuevaRutaCarpeta))
        {
            System.IO.Directory.CreateDirectory(nuevaRutaCarpeta);
        }

        // 4. Lo convertimos a nuestro modelo local
        int episodiosEmitidos = animeAPI.NextAiringEpisode != null 
            ? animeAPI.NextAiringEpisode.Episode - 1 
            : (animeAPI.Episodes ?? 12);
            
        var nuevoAnimeLocal = new AnimeItem
        {
            AniListId = animeAPI.Id,
            Titulo = animeAPI.Title.Romaji,
            UrlPortada = animeAPI.CoverImage?.ExtraLarge ?? animeAPI.CoverImage?.Large ?? "",
            RutaCarpeta = nuevaRutaCarpeta,
            Estado = animeAPI.Status ?? "UNKNOWN",
            TotalEpisodios = episodiosEmitidos,
            Generos = animeAPI.Genres != null ? string.Join(", ", animeAPI.Genres) : "",
            Sinopsis = animeAPI.Description ?? ""
        };

        // 5. Guardamos en SQLite y lo mostramos en la Galería
        await _databaseService.GuardarAnimeAsync(nuevoAnimeLocal);
        BibliotecaLocales.Add(nuevoAnimeLocal);
        OnPropertyChanged(nameof(BibliotecaVacia));

        // 6. Cerramos el buscador y limpiamos
        IsDialogOpen = false;
        TextoBusqueda = string.Empty;
        ResultadosBusqueda.Clear();
        
        await MostrarDialogoAsync("Anime Añadido Exitosamente", $"Carpeta creada automáticamente en:\n{nuevaRutaCarpeta}", false, "FolderPlusOutline", "#4CAF50");
    }
    
    [RelayCommand]
    private async Task ActualizarAnimeActualAsync()
    {
        if (AnimeSeleccionado == null) return;
        
        var datosFrescos = await _aniListService.ObtenerAnimePorIdAsync(AnimeSeleccionado.AniListId);
        if (datosFrescos != null)
        {
            int episodiosEmitidos = datosFrescos.NextAiringEpisode != null 
                ? datosFrescos.NextAiringEpisode.Episode - 1 
                : (datosFrescos.Episodes ?? AnimeSeleccionado.TotalEpisodios);
            
            if (episodiosEmitidos == 0) episodiosEmitidos = 12; // Fallback
            
            if (episodiosEmitidos > 0)
            {
                AnimeSeleccionado.TotalEpisodios = episodiosEmitidos;
                AnimeSeleccionado.Estado = datosFrescos.Status ?? "UNKNOWN";
                AnimeSeleccionado.Generos = datosFrescos.Genres != null ? string.Join(", ", datosFrescos.Genres) : "";
                AnimeSeleccionado.UrlPortada = datosFrescos.CoverImage?.ExtraLarge ?? datosFrescos.CoverImage?.Large ?? AnimeSeleccionado.UrlPortada;
                
                await _databaseService.ActualizarAnimeAsync(AnimeSeleccionado);
                
                // Refrescar la vista actual para que aparezcan los nuevos episodios
                await AbrirDetalle(AnimeSeleccionado);
                
                await MostrarDialogoAsync("Actualizado", $"Anime actualizado. Total de episodios emitidos hasta ahora: {episodiosEmitidos}", false, "CheckCircleOutline", "#4CAF50");
            }
        }
        else
        {
            await MostrarDialogoAsync("Error", "Error al conectar con AniList para actualizar.", false, "AlertCircleOutline", "#E53935");
        }
    }
}