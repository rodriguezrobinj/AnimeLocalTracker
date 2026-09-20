using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Controls;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Franquicias;
using AnimeLocalTracker.Services.Logros;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Estadísticas personales estilo MAL: resumen general, actividad de los últimos 7 días,
/// estado de la lista, top de animes más vistos, episodios por año y por género.
/// </summary>
public partial class EstadisticasViewModel : ObservableObject, IRecipient<IdiomaCambiadoMensaje>
{
    private readonly IDatabaseService _databaseService;
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IAuthService _authService;
    private readonly IDialogService _dialogService;
    private readonly ILogrosService? _logrosService;
    private readonly IFranquiciaService? _franquiciaService;

    private static readonly IReadOnlyDictionary<int, int> SinFranquicias = new Dictionary<int, int>();

    public EstadisticasViewModel(IDatabaseService databaseService, IAnimeTrackingService animeTrackingService,
        IAuthService authService, IDialogService dialogService, ILogrosService? logrosService = null,
        IFranquiciaService? franquiciaService = null)
    {
        _databaseService = databaseService;
        _animeTrackingService = animeTrackingService;
        _authService = authService;
        _dialogService = dialogService;
        _logrosService = logrosService;
        _franquiciaService = franquiciaService;

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    /// <summary>
    /// Los géneros, rangos y textos se traducen al CALCULAR las estadísticas: al cambiar de idioma hay que
    /// recalcularlas la próxima vez que se abra la pestaña (si no, quedan en el idioma anterior hasta 30 s).
    /// </summary>
    public void Receive(IdiomaCambiadoMensaje message) => _ultimaCargaUtc = DateTime.MinValue;

    // === RESUMEN ===
    [ObservableProperty] private int _totalAnimes;
    [ObservableProperty] private int _totalEpisodiosVistos;
    [ObservableProperty] private string _horasVistasTexto = "0 h";
    [ObservableProperty] private double _porcentajeCompletado;
    /// <summary>Texto del porcentaje de biblioteca completada (evita StringFormat en XAML).</summary>
    public string PorcentajeCompletadoTexto => $"{PorcentajeCompletado:F0}%";

    partial void OnPorcentajeCompletadoChanged(double value) => OnPropertyChanged(nameof(PorcentajeCompletadoTexto));
    [ObservableProperty] private int _animesEnProceso;
    [ObservableProperty] private int _totalFavoritos;
    [ObservableProperty] private int _totalDescargados;
    [ObservableProperty] private string _duracionPromedioTexto = "—";

    // === ACTIVIDAD RECIENTE ===
    [ObservableProperty] private List<BarraDato> _actividadSemana = new();
    [ObservableProperty] private string _promedioDiarioTexto = "0";

    // === ESTADO DE LA LISTA ===
    [ObservableProperty] private List<BarraDato> _listaPorEstado = new();

    // === DONUTS ===
    [ObservableProperty] private List<DonutDato> _donutEstado = new();
    [ObservableProperty] private List<DonutDato> _donutGeneros = new();
    [ObservableProperty] private string _donutEstadoCentro = "0";
    [ObservableProperty] private string _donutGenerosCentro = "—";
    [ObservableProperty] private string _donutGenerosSubcentro = "";

    // === INSIGHTS DEL ANALISTA ===
    [ObservableProperty] private string _generoFavorito = "—";
    [ObservableProperty] private string _generoFavoritoDetalle = "";
    [ObservableProperty] private string _animeMasVisto = "—";
    [ObservableProperty] private string _animeMasVistoDetalle = "";
    [ObservableProperty] private string _mejorAnio = "—";
    [ObservableProperty] private string _mejorAnioDetalle = "";
    [ObservableProperty] private string _horasPorMes = "0";
    [ObservableProperty] private string _rachaMaxima = string.Format(LocalizationService.T("Stats_DiasFormato"), 0);
    [ObservableProperty] private string _rachaActual = string.Format(LocalizationService.T("Stats_DiasFormato"), 0);

    // === TOP ANIMES ===
    [ObservableProperty] private List<TopAnime> _topAnimes = new();

    // === LOGROS: solo un resumen compacto; el catálogo completo vive en la pestaña Logros ===
    [ObservableProperty] private bool _hayLogros;
    [ObservableProperty] private string _logrosNivelesTexto = string.Empty;
    [ObservableProperty] private string _logrosRangoNombre = string.Empty;
    [ObservableProperty] private string _logrosPuntosTexto = string.Empty;
    [ObservableProperty] private double _logrosProgresoRango;
    [ObservableProperty] private string _logrosSiguienteRangoTexto = string.Empty;
    [ObservableProperty] private List<ContadorNivel> _logrosContadores = new();
    [ObservableProperty] private List<LogroItemViewModel> _logrosProximos = new();

    [RelayCommand]
    private void VerTodosLosLogros() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Logros());

    // === TARJETA WRAPPED ===
    [ObservableProperty] private bool _generandoWrapped;

    // === DESGLOSES ===
    [ObservableProperty] private List<BarraDato> _vistosPorAnio = new();
    [ObservableProperty] private List<BarraDato> _vistosPorGenero = new();

    // === ERRORES ===
    [ObservableProperty] private bool _hayError;
    [ObservableProperty] private string _mensajeError = "";

    // NAV-01: evita releer y reagregar toda la biblioteca en cada visita a la pestaña —
    // se recarga como mucho una vez cada 30 s (mismo cooldown que Historial/Actualizaciones).
    private static readonly TimeSpan CooldownRecarga = TimeSpan.FromSeconds(30);
    private DateTime _ultimaCargaUtc = DateTime.MinValue;
    public bool NecesitaRecargar() =>
        HayError || (DateTime.UtcNow - _ultimaCargaUtc) >= CooldownRecarga;

    public async Task CargarEstadisticasAsync()
    {
        try
        {
            HayError = false;
            MensajeError = "";
            _ultimaCargaUtc = DateTime.UtcNow;
            // PER-02: la carga y la agregación corren fuera del hilo de UI — si la
            // continuación reanudara en la UI, bibliotecas grandes congelarían la ventana.
            var datos = await Task.Run(async () =>
            {
                var animes = await _databaseService.ObtenerTodosLosAnimesAsync() ?? new List<Models.AnimeItem>();
                var registros = await _databaseService.ObtenerTodosLosRegistrosAsync() ?? new List<Models.RegistroEpisodio>();
                // Franquicias YA conocidas (sin red): el top se pinta de inmediato; lo que falte se sincroniza aparte.
                var franquicias = _franquiciaService != null
                    ? await _franquiciaService.ObtenerMapaAsync(animes.Select(a => a.AniListId))
                    : SinFranquicias;
                return (animes, registros, franquicias);
            });
            await Task.Run(() => CalcularEstadisticas(datos.animes, datos.registros, datos.franquicias));
            await ActualizarResumenLogrosAsync(datos.animes, datos.registros);
            _sincronizacionFranquicias = SincronizarFranquiciasAsync(datos.animes, datos.registros);
        }
        catch (Exception ex)
        {
            // EST-03: si la DB está bloqueada/corrompida, mostrar un estado de error
            // visible en vez de dejar el panel con placeholders vacíos sin explicación.
            AppLogger.Error("EstadisticasViewModel", "Error cargando estadísticas", ex);
            HayError = true;
            MensajeError = LocalizationService.T("Stats_ErrorCargaMsj");
        }
    }

    private Task _sincronizacionFranquicias = Task.CompletedTask;

    /// <summary>Solo para pruebas: espera a que termine la sincronización de franquicias en segundo plano.</summary>
    internal Task SincronizacionFranquicias => _sincronizacionFranquicias;

    /// <summary>
    /// Top de lo más visto por TIEMPO, sumando la franquicia completa (temporadas, películas, spin-offs).
    /// También fija la tarjeta "Anime más visto", que ahora habla de la misma franquicia.
    /// </summary>
    private void ActualizarTop(IReadOnlyList<Models.AnimeItem> animes, IReadOnlyCollection<Models.RegistroEpisodio> vistos,
        IReadOnlyDictionary<int, int> franquicias)
    {
        var top = CalculadorTop.Calcular(animes, vistos, franquicias);
        double maximo = Math.Max(1, top.Count > 0 ? top[0].Segundos : 1);

        TopAnimes = top
            .Select((f, i) => new TopAnime(i + 1, f.Titulo, f.Segundos, f.Episodios, f.Titulos)
            {
                AnchoBarra = f.Segundos / maximo * 420.0
            })
            .ToList();

        if (top.Count > 0)
        {
            var lider = top[0];
            AnimeMasVisto = lider.Titulo;
            AnimeMasVistoDetalle = $"{TopAnime.FormatoTiempo(lider.Segundos)} · {TopAnime.DetalleDe(lider.Episodios, lider.Titulos)}";
        }
    }

    /// <summary>
    /// Trae de AniList las relaciones que falten (primera vez, series nuevas, o caducadas) y, si hubo datos nuevos,
    /// recalcula el top con las franquicias completas. Corre aparte: la pestaña ya se pintó con lo que había.
    /// </summary>
    private async Task SincronizarFranquiciasAsync(List<Models.AnimeItem> animes, List<Models.RegistroEpisodio> registros)
    {
        if (_franquiciaService == null) return;

        try
        {
            var ids = animes.Select(a => a.AniListId).ToList();
            if (!await _franquiciaService.SincronizarAsync(ids)) return;

            var franquicias = await _franquiciaService.ObtenerMapaAsync(ids);
            var vistos = registros.Where(r => r.VistoLocal).ToList();
            await Task.Run(() => ActualizarTop(animes, vistos, franquicias));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("EstadisticasViewModel", $"No se pudo actualizar el top con las franquicias: {ex.Message}");
        }
    }

    /// <summary>
    /// Evalúa los logros con los datos ya cargados (avisa de los nuevos) y publica el resumen. Un fallo
    /// aquí no debe marcar como fallida toda la pestaña de estadísticas: solo se registra.
    /// </summary>
    private async Task ActualizarResumenLogrosAsync(List<Models.AnimeItem> animes, List<Models.RegistroEpisodio> registros)
    {
        if (_logrosService == null) return;

        try
        {
            var resumen = await _logrosService.EvaluarAsync(animes, registros);

            LogrosNivelesTexto = string.Format(LocalizationService.T("Logro_NivelesFormato"), resumen.NivelesDesbloqueados, resumen.NivelesTotales);
            LogrosRangoNombre = LocalizationService.T(resumen.Rango.ClaveNombre);
            LogrosPuntosTexto = string.Format(LocalizationService.T("Logro_PuntosTotalFormato"), resumen.Puntos);
            LogrosProgresoRango = resumen.Rango.ProgresoHaciaSiguiente(resumen.Puntos) * 100.0;
            LogrosSiguienteRangoTexto = resumen.Rango.PuntosSiguiente is int siguiente
                ? string.Format(LocalizationService.T("Logro_SiguienteRangoFormato"), siguiente - resumen.Puntos,
                    LocalizationService.T($"Logro_Rango_{resumen.Rango.Indice + 1}"))
                : LocalizationService.T("Logro_RangoMaximo");
            LogrosContadores = Enum.GetValues<NivelLogro>()
                .Select(n => new ContadorNivel(
                    LocalizationService.T($"Logro_Nivel_{(int)n}"),
                    LogroItemViewModel.ColorDeNivel((int)n),
                    resumen.PorNivel.GetValueOrDefault(n)))
                .ToList();
            LogrosProximos = resumen.Proximos.Select(l => new LogroItemViewModel(l)).ToList();
            HayLogros = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("EstadisticasViewModel", "Error evaluando los logros", ex);
        }
    }

    /// <summary>
    /// Agregación pura de las estadísticas. PER-01: los registros se indexan UNA sola vez
    /// (lookup por AniListId y por fecha) — los barridos O(A×R) originales son O(A+R).
    /// WPF enlaza cambios de propiedad desde cualquier hilo, así que puede correr en un
    /// hilo de fondo y solo se publica el resultado final.
    /// </summary>
    private void CalcularEstadisticas(List<Models.AnimeItem> animes, List<Models.RegistroEpisodio> registros,
        IReadOnlyDictionary<int, int> franquicias)
    {
        var registrosPorAnime = registros.ToLookup(r => r.AniListId);

        var vistos = registros.Where(r => r.VistoLocal).ToList();
        var porDia = vistos.ToLookup(r => r.UltimaReproduccion?.Date);

        // === RESUMEN ===
        TotalAnimes = animes.Count;
        TotalEpisodiosVistos = vistos.Count;
        TotalFavoritos = registros.Count(r => r.FavoritoLocal);
        TotalDescargados = registros.Count(r => !string.IsNullOrWhiteSpace(r.RutaArchivo));

        // Los episodios marcados/importados sin duración se estiman (ver EstimadorDuracion): igual que Logros.
        double segundosVistos = EstimadorDuracion.SegundosVistos(vistos);
        double horas = segundosVistos / 3600.0;
        HorasVistasTexto = horas >= 10 ? $"{horas:F0} h" : $"{horas:F1} h";

        var conDuracion = vistos.Where(r => r.TotalSegundos > 0).ToList();
        DuracionPromedioTexto = conDuracion.Count > 0 ? $"{conDuracion.Average(r => r.TotalSegundos) / 60.0:F0} min" : "—";

        int totalCapacidad = animes.Sum(a => Math.Max(a.TotalEpisodios, 0));
        PorcentajeCompletado = totalCapacidad > 0
            ? Math.Min(100.0, TotalEpisodiosVistos * 100.0 / totalCapacidad)
            : 0;

        AnimesEnProceso = animes.Count(a =>
        {
            int vistosAnime = registrosPorAnime[a.AniListId].Count(r => r.VistoLocal);
            return vistosAnime > 0 && vistosAnime < Math.Max(a.TotalEpisodios, 0);
        });

        // === ACTIVIDAD: últimos 7 días ===
        var actividad = new List<(string Etiqueta, int Valor)>();
        int maxDia = 1;
        for (int i = 6; i >= 0; i--)
        {
            var dia = DateTime.Today.AddDays(-i);
            int n = porDia[dia].Count();
            actividad.Add((dia.ToString("ddd d"), n));
            maxDia = Math.Max(maxDia, n);
        }
        ActividadSemana = actividad
            .Select(a => new BarraDato(a.Etiqueta, a.Valor, (a.Valor / (double)maxDia) * 420.0))
            .ToList();

        int semana = porDia.Where(g => g.Key.HasValue && g.Key.Value >= DateTime.Today.AddDays(-6)).Sum(g => g.Count());
        PromedioDiarioTexto = $"{semana / 7.0:F1}";

        // === ESTADO DE LA LISTA (EstadoUsuario) ===
        var porEstado = animes
            .GroupBy(a => string.IsNullOrWhiteSpace(a.EstadoUsuario) ? "SIN_ESTADO" : a.EstadoUsuario.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.Count());

        var ordenEstados = new (string Clave, string Etiqueta, string Color)[]
        {
            ("COMPLETED", "Completados", "#34D399"),
            ("CURRENT", "En curso", "#60A5FA"),
            ("PLANNING", "Planeados", "#A78BFA"),
            ("PAUSED", "En pausa", "#FBBF24"),
            ("DROPPED", "Abandonados", "#F87171"),
            ("SIN_ESTADO", "Sin estado", "#6B7280")
        };

        int maxEstado = Math.Max(1, ordenEstados.Max(o => porEstado.GetValueOrDefault(o.Clave)));
        ListaPorEstado = ordenEstados
            .Select(o => new BarraDato(o.Etiqueta, porEstado.GetValueOrDefault(o.Clave), (porEstado.GetValueOrDefault(o.Clave) / (double)maxEstado) * 420.0))
            .ToList();

        DonutEstado = ordenEstados
            .Where(o => porEstado.GetValueOrDefault(o.Clave) > 0)
            .Select(o => new DonutDato(o.Etiqueta, porEstado.GetValueOrDefault(o.Clave), o.Color))
            .ToList();
        DonutEstadoCentro = TotalAnimes.ToString();

        // === TOP 5 MÁS VISTOS: por TIEMPO y sumando toda la franquicia (también fija "Anime más visto") ===
        ActualizarTop(animes, vistos, franquicias);

        // === POR AÑO ===
        var porAnio = vistos
            .Where(r => r.UltimaReproduccion.HasValue)
            .GroupBy(r => r.UltimaReproduccion!.Value.Year)
            .OrderByDescending(g => g.Key)
            .Take(6)
            .ToList();
        int maxAnio = porAnio.Count > 0 ? porAnio.Max(g => g.Count()) : 1;
        VistosPorAnio = porAnio
            .Select(g => new BarraDato(g.Key.ToString(), g.Count(), (g.Count() / (double)maxAnio) * 420.0))
            .ToList();

        // === POR GÉNERO ===
        var porGenero = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var anime in animes)
        {
            if (string.IsNullOrWhiteSpace(anime.Generos)) continue;
            if (!registrosPorAnime[anime.AniListId].Any(r => r.VistoLocal)) continue;

            foreach (var genero in anime.Generos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                porGenero[genero] = porGenero.GetValueOrDefault(genero) + 1;
            }
        }

        var generosTop = porGenero.OrderByDescending(kv => kv.Value).Take(6).ToList();
        int maxGenero = generosTop.Count > 0 ? generosTop[0].Value : 1;
        VistosPorGenero = generosTop
            .Select(kv => new BarraDato(LocalizationService.TraducirGenero(kv.Key), kv.Value, (kv.Value / (double)maxGenero) * 420.0))
            .ToList();

        // === DONUT DE GÉNEROS (top 6 + "Otros") ===
        var paletaGeneros = new[] { "#A78BFA", "#60A5FA", "#34D399", "#FBBF24", "#F472B6", "#38BDF8" };
        var generosParaDonut = porGenero.OrderByDescending(kv => kv.Value).Take(6).ToList();
        int resto = porGenero.Where(kv => !generosParaDonut.Any(g => g.Key == kv.Key)).Sum(kv => kv.Value);
        var donutGeneros = generosParaDonut
            .Select((kv, i) => new DonutDato(LocalizationService.TraducirGenero(kv.Key), kv.Value, paletaGeneros[i % paletaGeneros.Length]))
            .ToList();
        if (resto > 0) donutGeneros.Add(new DonutDato(LocalizationService.T("Stats_Otros"), resto, "#4B5563"));
        DonutGeneros = donutGeneros;

        // === INSIGHTS DEL ANALISTA ===
        // Género favorito (por animes con episodios vistos)
        var generoFav = porGenero.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (generoFav.Key != null)
        {
            GeneroFavorito = LocalizationService.TraducirGenero(generoFav.Key);
            double pct = TotalAnimes > 0 ? generoFav.Value * 100.0 / TotalAnimes : 0;
            GeneroFavoritoDetalle = string.Format(LocalizationService.T("Stats_GeneroFavoritoDetalleFormato"), generoFav.Value, pct);
            DonutGenerosCentro = LocalizationService.TraducirGenero(generoFav.Key);
            DonutGenerosSubcentro = string.Format(LocalizationService.T("Stats_FavoritoPorcentaje"), pct);
        }

        // Mejor año
        var mejorAnio = vistos
            .Where(r => r.UltimaReproduccion.HasValue)
            .GroupBy(r => r.UltimaReproduccion!.Value.Year)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (mejorAnio != null)
        {
            MejorAnio = mejorAnio.Key.ToString();
            MejorAnioDetalle = string.Format(LocalizationService.T("Stats_EpisodiosEnAnioFormato"), mejorAnio.Count(), mejorAnio.Key);
        }

        // Horas por mes (desde el primer registro)
        var primerRegistro = registros
            .Where(r => r.UltimaReproduccion.HasValue)
            .Select(r => r.UltimaReproduccion!.Value)
            .DefaultIfEmpty(DateTime.Today)
            .Min();
        int meses = Math.Max(1, ((DateTime.Today.Year - primerRegistro.Year) * 12) + DateTime.Today.Month - primerRegistro.Month + 1);
        HorasPorMes = $"{horas / meses:F1} h";

        // Rachas (días consecutivos con al menos 1 episodio visto)
        var diasConActividad = vistos
            .Where(r => r.UltimaReproduccion.HasValue)
            .Select(r => r.UltimaReproduccion!.Value.Date)
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        int rachaMax = 0, rachaActual = 0;
        if (diasConActividad.Count > 0)
        {
            int consecutivos = 1;
            for (int i = 1; i < diasConActividad.Count; i++)
            {
                if ((diasConActividad[i] - diasConActividad[i - 1]).Days == 1) consecutivos++;
                else consecutivos = 1;
                rachaMax = Math.Max(rachaMax, consecutivos);
            }
            rachaMax = Math.Max(rachaMax, consecutivos);

            // Racha actual: hacia atrás desde hoy (o ayer si hoy no hay actividad)
            var fin = diasConActividad.Contains(DateTime.Today) ? DateTime.Today : DateTime.Today.AddDays(-1);
            rachaActual = 0;
            var cursor = fin;
            while (diasConActividad.Contains(cursor))
            {
                rachaActual++;
                cursor = cursor.AddDays(-1);
            }
        }
        RachaMaxima = string.Format(LocalizationService.T("Stats_DiasFormato"), rachaMax);
        RachaActual = string.Format(LocalizationService.T("Stats_DiasFormato"), rachaActual);
    }

    /// <summary>
    /// Compone la tarjeta "Anime Wrapped" renderizando AnimeWrappedCardView fuera de pantalla
    /// (RenderTargetBitmap) — reutiliza los datos ya agregados por CalcularEstadisticas, sin
    /// volver a tocar la base de datos. Guarda el PNG en Imágenes\AnimeLocalTracker y lo copia
    /// al portapapeles para pegarlo directo en Discord/redes.
    /// </summary>
    [RelayCommand]
    private async Task GenerarTarjetaResumenAsync()
    {
        if (GenerandoWrapped) return;
        GenerandoWrapped = true;
        try
        {
            var datos = await ConstruirDatosWrappedAsync();
            byte[] png = RenderizarTarjetaWrapped(datos);

            string carpeta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AnimeLocalTracker");
            Directory.CreateDirectory(carpeta);
            string ruta = Path.Combine(carpeta, $"AnimeWrapped_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await File.WriteAllBytesAsync(ruta, png);

            CopiarPngAlPortapapeles(png);

            _dialogService.MostrarToast(
                LocalizationService.T("Stats_Wrapped"),
                string.Format(LocalizationService.T("Stats_WrappedListoFormato"), ruta),
                "ImageMultiple", "#8B5CF6");
        }
        catch (Exception ex)
        {
            AppLogger.Error("EstadisticasViewModel", "Error generando tarjeta Wrapped", ex);
            _dialogService.MostrarToast(
                LocalizationService.T("Stats_Wrapped"),
                LocalizationService.T("Stats_WrappedErrorMsj"),
                "AlertCircle", "#F87171");
        }
        finally
        {
            GenerandoWrapped = false;
        }
    }

    private async Task<Models.WrappedCardData> ConstruirDatosWrappedAsync()
    {
        string nombreUsuario = LocalizationService.T("Gal_UsuarioDefault");
        ImageSource? avatar = null;

        string token = _authService.ObtenerTokenGuardado();
        if (!string.IsNullOrEmpty(token))
        {
            var perfil = await _animeTrackingService.ObtenerPerfilUsuarioAsync(token);
            if (perfil != null)
            {
                nombreUsuario = perfil.Name ?? nombreUsuario;
                if (!string.IsNullOrWhiteSpace(perfil.Avatar?.Large))
                {
                    avatar = await CargarAvatarAsync(perfil.Avatar.Large);
                }
            }
        }

        return new Models.WrappedCardData
        {
            TituloCard = LocalizationService.T("Wrapped_Titulo"),
            NombreUsuario = nombreUsuario,
            Avatar = avatar,
            HorasVistasTexto = HorasVistasTexto,
            HorasLabel = LocalizationService.T("Wrapped_Horas"),
            EpisodiosVistosTexto = TotalEpisodiosVistos.ToString(),
            EpisodiosLabel = LocalizationService.T("Wrapped_Episodios"),
            GeneroFavorito = GeneroFavorito,
            GeneroLabel = LocalizationService.T("Wrapped_GeneroFavorito"),
            RachaMaximaTexto = RachaMaxima,
            RachaLabel = LocalizationService.T("Wrapped_RachaMaxima"),
            TopAnimesLabel = LocalizationService.T("Wrapped_TopAnimes"),
            TopAnimesTitulos = TopAnimes.Take(3).Select((t, i) => $"{i + 1}. {t.Titulo}").ToList(),
            Footer = LocalizationService.T("Wrapped_Footer"),
        };
    }

    // CacheOption.OnLoad fuerza la descarga+decodificación completa dentro de EndInit(), y
    // Freeze() quita la afinidad de hilo — necesario porque esto corre en un hilo de fondo
    // (Task.Run) para no bloquear la UI mientras se descarga el avatar de AniList.
    private static async Task<ImageSource?> CargarAvatarAsync(string url)
    {
        try
        {
            return await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(url);
                bmp.EndInit();
                bmp.Freeze();
                return (ImageSource)bmp;
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("EstadisticasViewModel", "Error descargando avatar para tarjeta Wrapped", ex);
            return null;
        }
    }

    private static byte[] RenderizarTarjetaWrapped(Models.WrappedCardData datos)
    {
        const int ancho = 1080, alto = 1350;
        var vista = new Views.AnimeWrappedCardView { DataContext = datos };
        vista.Measure(new Size(ancho, alto));
        vista.Arrange(new Rect(0, 0, ancho, alto));
        vista.UpdateLayout();

        var rtb = new RenderTargetBitmap(ancho, alto, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(vista);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static void CopiarPngAlPortapapeles(byte[] png)
    {
        using var ms = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Clipboard.SetImage(decoder.Frames[0]);
    }
}

/// <summary>Fila de una barra de estadísticas (clase real para bindings WPF).</summary>
public class BarraDato
{
    public string Etiqueta { get; }
    public int Valor { get; }
    public double AnchoBarra { get; set; }

    public BarraDato(string etiqueta, int valor, double anchoBarra)
    {
        Etiqueta = etiqueta;
        Valor = valor;
        AnchoBarra = anchoBarra;
    }
}

/// <summary>
/// Entrada del top de lo más visto: una franquicia completa (o un anime suelto) ordenada por el TIEMPO que le has
/// dedicado — no por el número de episodios, que favorecía siempre a las series larguísimas.
/// </summary>
public class TopAnime
{
    public int Posicion { get; }
    public string Titulo { get; }
    public double Segundos { get; }
    public int EpisodiosVistos { get; }

    /// <summary>Cuántos títulos (temporadas, películas, especiales…) de la franquicia has visto.</summary>
    public int Titulos { get; }

    public double AnchoBarra { get; set; }

    public string TiempoTexto => FormatoTiempo(Segundos);
    public string DetalleTexto => DetalleDe(EpisodiosVistos, Titulos);

    public TopAnime(int posicion, string titulo, double segundos, int episodiosVistos, int titulos)
    {
        Posicion = posicion;
        Titulo = titulo;
        Segundos = segundos;
        EpisodiosVistos = episodiosVistos;
        Titulos = titulos;
    }

    internal static string FormatoTiempo(double segundos)
    {
        double horas = segundos / 3600.0;
        return horas >= 10 ? $"{horas:F0} h" : $"{horas:F1} h";
    }

    internal static string DetalleDe(int episodios, int titulos) => titulos > 1
        ? string.Format(LocalizationService.T("Stats_TopDetalleFranquiciaFormato"), episodios, titulos)
        : string.Format(LocalizationService.T("Stats_EpisodiosVistosFormato"), episodios);
}

