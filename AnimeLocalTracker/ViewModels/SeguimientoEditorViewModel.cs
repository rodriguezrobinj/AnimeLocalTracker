using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Editor de seguimiento de AniList de la ficha (estado, episodios vistos, puntuación y fechas), con su selector de fecha.
/// Abre al instante con lo guardado en local, se pone al día con AniList si hay conexión y guarda siempre en local primero.
/// Es una pieza de la ficha (<see cref="DetalleViewModel.Seguimiento"/>); su ventana es <c>EditorSeguimientoView</c>.
/// </summary>
public sealed partial class SeguimientoEditorViewModel : ObservableObject
{
    private readonly IAnimeTrackingService _animeTrackingService;
    private readonly IDatabaseService _databaseService;
    private readonly IAuthService _authService;
    private readonly IDialogService _dialogService;
    private readonly Func<int> _filasDeEpisodios;

    /// <param name="filasDeEpisodios">Cuántas filas tiene la lista de episodios de la ficha: tope del progreso cuando AniList
    /// no indica el total.</param>
    public SeguimientoEditorViewModel(
        IAnimeTrackingService animeTrackingService,
        IDatabaseService databaseService,
        IAuthService authService,
        IDialogService dialogService,
        Func<int>? filasDeEpisodios = null)
    {
        _animeTrackingService = animeTrackingService;
        _databaseService = databaseService;
        _authService = authService;
        _dialogService = dialogService;
        _filasDeEpisodios = filasDeEpisodios ?? (() => 0);
    }

    /// <summary>El anime de la ficha. Lo pone la ficha.</summary>
    [ObservableProperty] private AnimeItem? _anime;

    // === EDITOR DE SEGUIMIENTO ===
    [ObservableProperty] private bool _mostrandoEditorSeguimiento;
    [ObservableProperty] private string _editEstado = "CURRENT";
    [ObservableProperty] private int _editProgreso;
    [ObservableProperty] private string _editProgresoTexto = "0";

    partial void OnEditProgresoTextoChanged(string value)
    {
        ProcesarProgresoTexto(value);
    }

    private void ProcesarProgresoTexto(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            EditProgreso = 0;
            return;
        }

        string soloDigitos = new string(value.Where(char.IsDigit).ToArray());
        if (string.IsNullOrEmpty(soloDigitos))
        {
            EditProgreso = 0;
            EditProgresoTexto = "0";
            return;
        }

        if (int.TryParse(soloDigitos, out int num))
        {
            int max = ObtenerMaximoEpisodiosEmitidos();
            if (num < 0) num = 0;
            if (max > 0 && num > max) num = max;

            EditProgreso = num;
            if (num.ToString() != value)
            {
                EditProgresoTexto = num.ToString();
            }
        }
    }

    public int ObtenerMaximoEpisodiosEmitidos()
    {
        if (Anime == null) return 9999;
        if (Anime.TotalEpisodios > 0) return Anime.TotalEpisodios;
        int filas = _filasDeEpisodios();
        if (filas > 0) return filas;
        return 9999;
    }

    [ObservableProperty] private float _editPuntaje;
    [ObservableProperty] private DateTime? _editFechaInicio;
    [ObservableProperty] private DateTime? _editFechaFin;
    [ObservableProperty] private string _editEstadoVisual = LocalizationService.T("Estado_Viendo");

    /// <summary>Lo último que se puso en el editor desde código: si el usuario no lo tocó, lo de AniList puede reemplazarlo.</summary>
    private (string Estado, int Progreso, float Puntaje, DateTime? Inicio, DateTime? Fin)? _editorAplicado;

    private void AplicarAlEditor(string estado, int progreso, float puntaje, DateTime? inicio, DateTime? fin)
    {
        int max = ObtenerMaximoEpisodiosEmitidos();
        EditProgreso = Math.Clamp(progreso, 0, max > 0 ? max : 9999);
        EditProgresoTexto = EditProgreso.ToString();
        EditEstadoVisual = ConvertirEstadoAEspanol(estado);
        EditPuntaje = puntaje;
        EditFechaInicio = inicio;
        EditFechaFin = fin;
        NotificarDerivadosEditor();
        _editorAplicado = ValoresDelEditor();
    }

    private (string, int, float, DateTime?, DateTime?) ValoresDelEditor() =>
        (ConvertirEstadoAIngles(EditEstadoVisual), EditProgreso, EditPuntaje, EditFechaInicio, EditFechaFin);

    private static DateTime? DesdeFechaAniList(AniListFuzzyDate? fecha) =>
        fecha?.Year is int anio ? new DateTime(anio, fecha.Month ?? 1, fecha.Day ?? 1) : null;

    [RelayCommand]
    private async Task AbrirEditorSeguimientoAsync()
    {
        if (Anime == null) return;
        var anime = Anime;

        // 1) Al instante, lo guardado en local. Antes abría con "Viendo, 0 pts, sin fechas" y solo se corregía si AniList
        //    respondía: sin conexión mostraba datos falsos.
        SeguimientoLocal? local = null;
        try { local = await _databaseService.ObtenerSeguimientoLocalAsync(anime.AniListId); }
        catch (Exception ex) { AppLogger.Debug("SeguimientoEditorViewModel", $"No se pudo leer el seguimiento local de {anime.AniListId}: {ex.Message}"); }
        if (!ReferenceEquals(anime, Anime)) return;

        string estadoLocal = local?.Estado ?? (string.IsNullOrWhiteSpace(anime.EstadoUsuario) ? "CURRENT" : anime.EstadoUsuario);
        AplicarAlEditor(estadoLocal, local?.Progreso ?? anime.EpisodiosVistos, local?.Puntaje ?? 0, local?.FechaInicio, local?.FechaFin);
        MostrandoEditorSeguimiento = true;

        // 2) Con conexión, lo de AniList (que puede venir de otro dispositivo). Un cambio local aún sin enviar manda sobre él.
        if (local?.Pendiente == true) return;
        var token = _authService.ObtenerTokenGuardado();
        if (string.IsNullOrEmpty(token)) return;

        var datos = await _animeTrackingService.ObtenerSeguimientoUsuarioAsync(anime.AniListId, token);
        if (datos == null || !ReferenceEquals(anime, Anime)) return;

        var remoto = new SeguimientoLocal
        {
            AniListId = anime.AniListId,
            Estado = datos.Status ?? "CURRENT",
            Progreso = datos.Progress,
            Puntaje = datos.Score,
            FechaInicio = DesdeFechaAniList(datos.StartedAt),
            FechaFin = DesdeFechaAniList(datos.CompletedAt),
            ModificadoUtc = DateTime.UtcNow
        };
        try { await _databaseService.GuardarSeguimientoLocalAsync(remoto); }
        catch (Exception ex) { AppLogger.Debug("SeguimientoEditorViewModel", $"No se pudo guardar el seguimiento local de {anime.AniListId}: {ex.Message}"); }

        // Si el usuario ya empezó a editar mientras llegaba la respuesta, no se le pisa lo que escribió.
        if (MostrandoEditorSeguimiento && _editorAplicado == ValoresDelEditor())
            AplicarAlEditor(remoto.Estado, remoto.Progreso, remoto.Puntaje, remoto.FechaInicio, remoto.FechaFin);
    }

    [RelayCommand]
    private async Task GuardarEditorSeguimientoAsync()
    {
        if (Anime == null) return;
        var anime = Anime;

        int max = ObtenerMaximoEpisodiosEmitidos();
        int progresoFinal = Math.Clamp(EditProgreso, 0, max > 0 ? max : 9999);
        string estadoEnIngles = ConvertirEstadoAIngles(EditEstadoVisual);
        var token = _authService.ObtenerTokenGuardado();
        bool conCuenta = !string.IsNullOrEmpty(token);

        // 1) Siempre en local: sin conexión (o sin cuenta de AniList) el cambio ya no se pierde. Pendiente solo con cuenta: sin ella
        //    no hay a dónde enviarlo, y subirlo al conectar una cuenta días después podría pisar lo que haya en AniList.
        var seguimiento = new SeguimientoLocal
        {
            AniListId = anime.AniListId,
            Estado = estadoEnIngles,
            Progreso = progresoFinal,
            Puntaje = EditPuntaje,
            FechaInicio = EditFechaInicio,
            FechaFin = EditFechaFin,
            Pendiente = conCuenta,
            ModificadoUtc = DateTime.UtcNow
        };
        anime.EstadoUsuario = estadoEnIngles;
        anime.EpisodiosVistos = progresoFinal;
        await _databaseService.ActualizarAnimeAsync(anime);
        await _databaseService.GuardarSeguimientoLocalAsync(seguimiento);
        MostrandoEditorSeguimiento = false;

        if (!conCuenta)
        {
            _dialogService.MostrarToast(LocalizationService.T("Det_SeguimientoLocalTitulo"), LocalizationService.T("Det_SeguimientoLocalMsj"), "ContentSaveOutline", "#60A5FA");
            return;
        }

        // 2) A AniList; si no se puede ahora, lo envía la sincronización cuando vuelva la conexión.
        bool exito = await _animeTrackingService.GuardarSeguimientoUsuarioAsync(
            anime.AniListId, estadoEnIngles, progresoFinal, EditPuntaje, EditFechaInicio, EditFechaFin, token!);

        if (exito)
        {
            seguimiento.Pendiente = false;
            await _databaseService.GuardarSeguimientoLocalAsync(seguimiento);
            await _dialogService.MostrarDialogoAsync(LocalizationService.T("Det_NubeSincronizadaTitulo"), LocalizationService.T("Det_NubeSincronizadaMsj"), false, "CloudCheck", "#4CAF50");
        }
        else
        {
            _dialogService.MostrarToast(LocalizationService.T("Det_SeguimientoPendienteTitulo"), LocalizationService.T("Det_SeguimientoPendienteMsj"), "CloudOffOutline", "#60A5FA");
        }
    }
    
    [RelayCommand]
    private void CerrarEditorSeguimiento()
    {
        MostrandoEditorSeguimiento = false;
    }
    
    private static string ConvertirEstadoAIngles(string estadoVisual)
    {
        if (estadoVisual == LocalizationService.T("Estado_Viendo")) return "CURRENT";
        if (estadoVisual == LocalizationService.T("Estado_Finalizado")) return "COMPLETED";
        if (estadoVisual == LocalizationService.T("Estado_EnPausa")) return "PAUSED";
        if (estadoVisual == LocalizationService.T("Estado_Abandonado")) return "DROPPED";
        if (estadoVisual == LocalizationService.T("Estado_Planeando")) return "PLANNING";
        return "CURRENT";
    }

    private static string ConvertirEstadoAEspanol(string estadoIngles) => estadoIngles switch
    {
        "CURRENT" => LocalizationService.T("Estado_Viendo"),
        "COMPLETED" => LocalizationService.T("Estado_Finalizado"),
        "PAUSED" => LocalizationService.T("Estado_EnPausa"),
        "DROPPED" => LocalizationService.T("Estado_Abandonado"),
        "PLANNING" => LocalizationService.T("Estado_Planeando"),
        _ => LocalizationService.T("Estado_Viendo")
    };

    /// <summary>Estados como chips de un clic (Viendo / Finalizado / En pausa / Abandonado / Planeando). Clave = texto localizado.</summary>
    public ObservableCollection<FiltroChip> EstadosChips { get; } = CrearChipsEstado();

    private static ObservableCollection<FiltroChip> CrearChipsEstado()
    {
        string[] claves = ["Estado_Viendo", "Estado_Finalizado", "Estado_EnPausa", "Estado_Abandonado", "Estado_Planeando"];
        string inicial = LocalizationService.T("Estado_Viendo");
        return new ObservableCollection<FiltroChip>(claves.Select(k =>
        {
            string texto = LocalizationService.T(k);
            return new FiltroChip(texto, texto, texto == inicial);
        }));
    }

    partial void OnEditEstadoVisualChanged(string value)
    {
        foreach (var chip in EstadosChips) chip.EsActivo = chip.Clave == value;
    }

    /// <summary>Total de episodios conocido (0 si AniList no lo indica: serie en emisión sin fin anunciado).</summary>
    public int EditTotalEpisodios => Anime?.TotalEpisodios ?? 0;
    public bool TieneTotalEpisodios => EditTotalEpisodios > 0;
    public string EditTotalEpisodiosTexto => TieneTotalEpisodios ? $"/ {EditTotalEpisodios}" : string.Empty;


    /// <summary>"85" o "—" cuando no hay puntuación (0 en AniList = sin puntuar).</summary>
    public string EditPuntajeTexto => EditPuntaje > 0 ? $"{EditPuntaje:F0}" : "—";

    partial void OnEditPuntajeChanged(float value)
    {
        OnPropertyChanged(nameof(EditPuntajeTexto));
    }

    /// <summary>Se llama al abrir el editor y cuando cambia el anime/total, para refrescar los textos derivados.</summary>
    private void NotificarDerivadosEditor()
    {
        OnPropertyChanged(nameof(EditTotalEpisodios));
        OnPropertyChanged(nameof(TieneTotalEpisodios));
        OnPropertyChanged(nameof(EditTotalEpisodiosTexto));
        OnPropertyChanged(nameof(EditPuntajeTexto));
    }

    /// <summary>
    /// Elige un estado con un clic. Como en la web de AniList: "Finalizado" completa el progreso (si se conoce el total)
    /// y pone la fecha de fin; "Viendo" pone la fecha de inicio si aún no tiene. Lo ya escrito no se pisa.
    /// </summary>
    [RelayCommand]
    private void SeleccionarEstado(string estado)
    {
        if (string.IsNullOrEmpty(estado)) return;
        EditEstadoVisual = estado;

        if (estado == LocalizationService.T("Estado_Finalizado"))
        {
            if (TieneTotalEpisodios) EditProgresoTexto = EditTotalEpisodios.ToString();
            EditFechaFin ??= DateTime.Today;
        }
        else if (estado == LocalizationService.T("Estado_Viendo"))
        {
            EditFechaInicio ??= DateTime.Today;
        }
    }

    // ── Selector de fecha como tarjeta superpuesta (igual que el editor), en vez de un popup ──

    [ObservableProperty] private bool _mostrandoCalendarioFecha;
    [ObservableProperty] private string _calendarioTitulo = string.Empty;
    [ObservableProperty] private bool _calendarioTieneFecha;
    private bool _calendarioEsInicio;

    /// <summary>Fecha con la que se abre el calendario (la ya elegida o, si no hay, hoy).</summary>
    public DateTime CalendarioFechaInicial { get; private set; } = DateTime.Today;

    public string EditFechaInicioTexto => FormatearFecha(EditFechaInicio);
    public string EditFechaFinTexto => FormatearFecha(EditFechaFin);

    private static string FormatearFecha(DateTime? fecha) => fecha.HasValue ? fecha.Value.ToString("d", LocalizationService.Cultura) : "—";

    partial void OnEditFechaInicioChanged(DateTime? value) => OnPropertyChanged(nameof(EditFechaInicioTexto));
    partial void OnEditFechaFinChanged(DateTime? value) => OnPropertyChanged(nameof(EditFechaFinTexto));

    [RelayCommand]
    private void AbrirCalendarioInicio() => AbrirCalendario(esInicio: true);

    [RelayCommand]
    private void AbrirCalendarioFin() => AbrirCalendario(esInicio: false);

    private void AbrirCalendario(bool esInicio)
    {
        _calendarioEsInicio = esInicio;
        var actual = esInicio ? EditFechaInicio : EditFechaFin;
        CalendarioTitulo = LocalizationService.T(esInicio ? "Det_FechaInicio" : "Det_FechaFin");
        CalendarioTieneFecha = actual.HasValue;
        CalendarioFechaInicial = actual ?? DateTime.Today;
        MostrandoCalendarioFecha = true;
    }

    /// <summary>Un clic en un día lo aplica al campo y cierra el calendario.</summary>
    [RelayCommand]
    private void ElegirFechaCalendario(DateTime fecha)
    {
        if (_calendarioEsInicio) EditFechaInicio = fecha.Date;
        else EditFechaFin = fecha.Date;
        MostrandoCalendarioFecha = false;
    }

    [RelayCommand]
    private void QuitarFechaCalendario()
    {
        if (_calendarioEsInicio) EditFechaInicio = null;
        else EditFechaFin = null;
        MostrandoCalendarioFecha = false;
    }

    [RelayCommand]
    private void CerrarCalendarioFecha() => MostrandoCalendarioFecha = false;

    [RelayCommand]
    private void IncrementarProgreso() => EditProgresoTexto = (EditProgreso + 1).ToString();

    [RelayCommand]
    private void DecrementarProgreso() => EditProgresoTexto = Math.Max(0, EditProgreso - 1).ToString();
}
