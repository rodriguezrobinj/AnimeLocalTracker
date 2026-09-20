using System;
using System.Collections.ObjectModel;
using System.Linq;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Ayudas del editor de seguimiento de AniList: estados como chips, stepper de episodios, atajos de fecha.</summary>
public partial class DetalleViewModel
{
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
    public int EditTotalEpisodios => AnimeSeleccionado?.TotalEpisodios ?? 0;
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
