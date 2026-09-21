using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Etiqueta informativa de la ficha (nota, formato, estudio…): icono + texto + explicación al pasar el ratón.</summary>
public sealed record EtiquetaAniList(string Icono, string Texto, string Tooltip);

/// <summary>
/// Extras de la ficha: espacio en disco (con «liberar espacio» de los episodios ya vistos), etiquetas con datos de AniList
/// (nota, formato, estudio, fuente, tráiler) y las preferencias de avisos / descarga automática de episodios nuevos.
/// </summary>
public partial class DetalleViewModel
{
    private IDatosExtraService? _datosExtra;
    private IEmisionMonitorService? _monitorEmision;
    private DatosExtraAnime? _datosExtraActuales;
    private bool _cargandoPreferencias;

    // ── Espacio en disco ──

    [ObservableProperty] private bool _tieneEspacioEnDisco;
    [ObservableProperty] private string _espacioEnDiscoTexto = string.Empty;
    [ObservableProperty] private string _espacioEnDiscoTooltip = string.Empty;
    [ObservableProperty] private bool _hayEspacioLiberable;
    [ObservableProperty] private string _liberarEspacioDescripcion = string.Empty;

    private long _bytesLiberables;
    private int _episodiosLiberables;

    /// <summary>Suma el tamaño de los archivos del anime (fuera del hilo de UI: son accesos a disco).</summary>
    internal async Task CalcularEspacioEnDiscoAsync()
    {
        var anime = AnimeSeleccionado;
        var archivos = _todosLosEpisodios
            .Where(e => e.Descargado && !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .Select(e => (e.RutaCompleta, e.Visto))
            .ToList();

        var (total, vistos, nVistos, nTotal) = await Task.Run(() =>
        {
            long t = 0, v = 0;
            int nv = 0, nt = 0;
            foreach (var (ruta, visto) in archivos)
            {
                try
                {
                    var info = new FileInfo(ruta);
                    if (!info.Exists) continue;
                    t += info.Length;
                    nt++;
                    if (visto) { v += info.Length; nv++; }
                }
                catch (IOException) { /* archivo en uso o desaparecido: no cuenta */ }
            }
            return (t, v, nv, nt);
        });

        if (!ReferenceEquals(anime, AnimeSeleccionado)) return; // se cambió de ficha mientras se calculaba

        TieneEspacioEnDisco = nTotal > 0;
        EspacioEnDiscoTexto = nTotal > 0 ? EpisodioItem.FormatearTamano(total) : string.Empty;
        EspacioEnDiscoTooltip = string.Format(LocalizationService.T("Det_EspacioTooltipFormato"), nTotal, EpisodioItem.FormatearTamano(total));
        _bytesLiberables = vistos;
        _episodiosLiberables = nVistos;
        HayEspacioLiberable = nVistos > 0;
        LiberarEspacioDescripcion = nVistos > 0
            ? string.Format(LocalizationService.T("Det_LiberarEspacioDescFormato"), nVistos, EpisodioItem.FormatearTamano(vistos))
            : LocalizationService.T("Det_LiberarEspacioNadaDesc");
    }

    /// <summary>Borra del disco los episodios YA VISTOS (con confirmación). El registro de que se vieron se conserva.</summary>
    [RelayCommand]
    private async Task LiberarEspacioAsync()
    {
        var anime = AnimeSeleccionado;
        if (anime == null) return;

        var vistos = _todosLosEpisodios
            .Where(e => e.Visto && e.Descargado && !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .ToList();
        if (vistos.Count == 0) return;

        bool confirmar = await _dialogService.MostrarDialogoAsync(
            LocalizationService.T("Det_LiberarEspacioTitulo"),
            string.Format(LocalizationService.T("Det_LiberarEspacioConfirmacionFormato"), vistos.Count, EpisodioItem.FormatearTamano(_bytesLiberables)),
            true, "DeleteSweepOutline", "#EF4444");
        if (!confirmar) return;

        long liberados = 0;
        int borrados = 0;
        foreach (var episodio in vistos)
        {
            string ruta = episodio.RutaCompleta;
            long tamano = 0;
            bool ok = await Task.Run(() =>
            {
                // Lo único que decide si el episodio "ya no está" es el archivo de VIDEO.
                try
                {
                    if (File.Exists(ruta))
                    {
                        tamano = new FileInfo(ruta).Length;
                        File.Delete(ruta);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("DetalleViewModel", $"No se pudo borrar el episodio {episodio.NumeroEpisodio}: {ex.Message}");
                    return false;
                }

                // La miniatura es opcional: la propia ficha la tiene abierta (imagen en pantalla) y Windows puede negar el
                // borrado. Si su fallo se tratara como fallo del episodio, el video ya borrado seguiría apareciendo en la
                // lista hasta recargar la pestaña.
                try
                {
                    string miniatura = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(ruta);
                    if (File.Exists(miniatura)) File.Delete(miniatura);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("DetalleViewModel", $"No se pudo borrar la miniatura del episodio {episodio.NumeroEpisodio}: {ex.Message}");
                }
                return true;
            });
            if (!ok) continue;

            liberados += tamano;
            borrados++;

            // El historial es permanente: borrar el archivo NO borra que se vio el episodio.
            try { await _databaseService.ConservarRegistroTrasEliminarArchivoAsync(anime.AniListId, episodio.NumeroEpisodio); }
            catch (Exception ex) { AppLogger.Debug("DetalleViewModel", $"No se pudo conservar el registro del episodio: {ex.Message}"); }

            episodio.Descargado = false;
            episodio.RutaCompleta = string.Empty;
            episodio.RutaMiniatura = null;
            episodio.TamanoArchivoFormateado = string.Empty;
            episodio.Resolucion = string.Empty;
            episodio.CodecVideo = string.Empty;
            episodio.Fps = string.Empty;
            episodio.Es10Bit = false;
        }

        AplicarFiltrosYOrdenamiento();
        await CalcularEspacioEnDiscoAsync();

        _dialogService.MostrarToast(
            LocalizationService.T("Det_LiberarEspacioTitulo"),
            string.Format(LocalizationService.T("Det_LiberarEspacioHechoFormato"), borrados, EpisodioItem.FormatearTamano(liberados)),
            "CheckCircleOutline", "#4CAF50");
    }

    // ── Etiquetas con datos de AniList ──

    public ObservableCollection<EtiquetaAniList> EtiquetasAniList { get; } = [];

    [ObservableProperty] private bool _tieneTrailer;

    internal async Task CargarDatosExtraAsync()
    {
        var servicio = _datosExtra;
        var anime = AnimeSeleccionado;
        if (servicio == null || anime == null) return;

        try
        {
            var datos = await servicio.ObtenerAsync(anime.AniListId);
            if (!ReferenceEquals(anime, AnimeSeleccionado)) return;
            AplicarDatosExtra(datos);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudieron cargar los datos extra: {ex.Message}");
        }
    }

    internal void AplicarDatosExtra(DatosExtraAnime? datos)
    {
        _datosExtraActuales = datos;
        EtiquetasAniList.Clear();
        TieneTrailer = DatosExtraService.UrlTrailer(datos) != null;
        if (datos == null) return;

        if (datos.NotaMedia > 0)
            EtiquetasAniList.Add(new EtiquetaAniList("StarOutline", $"{datos.NotaMedia}%", LocalizationService.T("Det_TagNotaTip")));

        string formato = TraducirValor("Fmt_", datos.Formato);
        string duracion = datos.DuracionMin > 0 ? string.Format(LocalizationService.T("Det_TagMinutosFormato"), datos.DuracionMin) : string.Empty;
        string formatoTexto = string.Join(" · ", new[] { formato, duracion }.Where(t => !string.IsNullOrEmpty(t)));
        if (formatoTexto.Length > 0)
            EtiquetasAniList.Add(new EtiquetaAniList("PlayBoxOutline", formatoTexto, LocalizationService.T("Det_TagFormatoTip")));

        if (!string.IsNullOrWhiteSpace(datos.Estudio))
            EtiquetasAniList.Add(new EtiquetaAniList("OfficeBuildingOutline", datos.Estudio, LocalizationService.T("Det_TagEstudioTip")));

        string fuente = TraducirValor("Src_", datos.Fuente);
        if (fuente.Length > 0)
            EtiquetasAniList.Add(new EtiquetaAniList("BookOpenPageVariantOutline", fuente, LocalizationService.T("Det_TagFuenteTip")));
    }

    /// <summary>Texto localizado de un valor de AniList (p. ej. LIGHT_NOVEL → «Novela ligera»); si no hay traducción, lo deja legible.</summary>
    internal static string TraducirValor(string prefijo, string valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return string.Empty;
        string clave = prefijo + valor.ToUpperInvariant();
        string texto = LocalizationService.T(clave);
        return texto != clave ? texto : valor.Replace('_', ' ');
    }

    [RelayCommand]
    private void AbrirTrailer()
    {
        string? url = DatosExtraService.UrlTrailer(_datosExtraActuales);
        if (url == null) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudo abrir el tráiler: {ex.Message}");
        }
    }

    // ── Avisos y descarga automática de episodios nuevos ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneAvisosActivos))]
    private bool _avisarEpisodioNuevo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TieneAvisosActivos))]
    private bool _descargarAutomaticamente;

    public bool TieneAvisosActivos => AvisarEpisodioNuevo || DescargarAutomaticamente;

    partial void OnAvisarEpisodioNuevoChanged(bool value)
    {
        if (!_cargandoPreferencias) _ = GuardarPreferenciasEmisionAsync();
    }

    partial void OnDescargarAutomaticamenteChanged(bool value)
    {
        if (!_cargandoPreferencias) _ = GuardarPreferenciasEmisionAsync();
    }

    internal async Task CargarPreferenciasEmisionAsync()
    {
        var anime = AnimeSeleccionado;
        if (anime == null) return;

        try
        {
            var pref = await _databaseService.ObtenerPreferenciaEmisionAsync(anime.AniListId);
            if (!ReferenceEquals(anime, AnimeSeleccionado)) return;

            _cargandoPreferencias = true;
            AvisarEpisodioNuevo = pref?.Avisar ?? false;
            DescargarAutomaticamente = pref?.AutoDescargar ?? false;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("DetalleViewModel", $"No se pudieron cargar las preferencias de emisión: {ex.Message}");
        }
        finally
        {
            _cargandoPreferencias = false;
        }
    }

    internal async Task GuardarPreferenciasEmisionAsync()
    {
        var anime = AnimeSeleccionado;
        if (anime == null) return;

        if (DescargarAutomaticamente && string.IsNullOrWhiteSpace(anime.RutaCarpeta))
        {
            // Sin carpeta no hay dónde guardar los episodios: se revierte el interruptor y se explica.
            _cargandoPreferencias = true;
            DescargarAutomaticamente = false;
            _cargandoPreferencias = false;
            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Det_AutoDescargaSinCarpetaTitulo"),
                LocalizationService.T("Det_AutoDescargaSinCarpetaMsj"),
                false, "FolderAlertOutline", "#F59E0B");
        }

        try
        {
            var pref = await _databaseService.ObtenerPreferenciaEmisionAsync(anime.AniListId) ?? new PreferenciaEmision { AniListId = anime.AniListId };

            // Punto de partida: solo cuentan los episodios que salgan DESPUÉS de activar cada opción.
            int emitido = _monitorEmision?.UltimoEmitido(_proximaEmisionActual, anime, DateTime.UtcNow) ?? 0;
            if (AvisarEpisodioNuevo && !pref.Avisar) pref.UltimoAvisado = emitido;
            if (DescargarAutomaticamente && !pref.AutoDescargar) pref.UltimoDescargado = emitido;

            pref.Avisar = AvisarEpisodioNuevo;
            pref.AutoDescargar = DescargarAutomaticamente;
            await _databaseService.GuardarPreferenciaEmisionAsync(pref);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DetalleViewModel", $"No se pudieron guardar las preferencias de emisión: {ex.Message}");
        }
    }

    /// <summary>Estado limpio al cargar otro anime (sin datos ni interruptores de la ficha anterior).</summary>
    private void ReiniciarExtras()
    {
        _datosExtraActuales = null;
        EtiquetasAniList.Clear();
        TieneTrailer = false;
        TieneEspacioEnDisco = false;
        EspacioEnDiscoTexto = string.Empty;
        HayEspacioLiberable = false;
        _cargandoPreferencias = true;
        AvisarEpisodioNuevo = false;
        DescargarAutomaticamente = false;
        _cargandoPreferencias = false;
    }
}
