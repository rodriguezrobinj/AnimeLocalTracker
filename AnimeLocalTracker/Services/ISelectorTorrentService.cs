using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Envoltorio de <see cref="CandidatoTorrent"/> para mostrar en el selector (Fase 2d):
/// campos ya formateados (tamaño legible, etc.) — la vista no hace formateo.
/// </summary>
public sealed class CandidatoTorrentItem
{
    public CandidatoTorrent Original { get; }
    public string Titulo => Original.Titulo;
    public int Seeders => Original.Seeders;
    public bool EsBatch => Original.EsBatch;
    public string TamanoTexto { get; }

    /// <summary>Grupo de fansub/release ("Erai-raws"), si el título empieza con "[Grupo]"; si no, null.</summary>
    public string? Grupo { get; }
    public bool TieneGrupo => Grupo != null;

    /// <summary>Resolución anunciada en el título ("1080p"), si la hay; si no, null.</summary>
    public string? Resolucion { get; }
    public bool TieneResolucion => Resolucion != null;

    public CandidatoTorrentItem(CandidatoTorrent original)
    {
        Original = original;
        TamanoTexto = FormatearTamano(original.TamanoBytes);
        Grupo = ExtraerGrupo(original.Titulo);
        Resolucion = ExtraerResolucion(original.Titulo);
    }

    private static readonly System.Text.RegularExpressions.Regex GrupoRegex =
        new(@"^\s*\[(?<g>[^\]]{1,40})\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ResolucionRegex =
        new(@"(?<![0-9])(?<r>2160|1440|1080|720|480)p\b", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"[Erai-raws] Anime - 22 [720p]" → "Erai-raws". Solo el corchete inicial: los del final son códigos (CRC, calidad).</summary>
    internal static string? ExtraerGrupo(string titulo)
    {
        var m = GrupoRegex.Match(titulo ?? "");
        if (!m.Success) return null;
        var grupo = m.Groups["g"].Value.Trim();
        return grupo.Length == 0 ? null : grupo;
    }

    /// <summary>"... 1080p CR WEB-DL ..." → "1080p" (siempre en minúscula).</summary>
    internal static string? ExtraerResolucion(string titulo)
    {
        var m = ResolucionRegex.Match(titulo ?? "");
        return m.Success ? m.Groups["r"].Value + "p" : null;
    }

    private static string FormatearTamano(long bytes)
    {
        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
        if (gb >= 1.0) return $"{gb:F1} GB";
        double mb = bytes / 1024.0 / 1024.0;
        return $"{mb:F0} MB";
    }
}

/// <summary>
/// Overlay modal (Fase 2d) para elegir a mano entre varios candidatos de torrent de
/// Nyaa.si, en vez de que la app elija sola el de más semillas. Mismo patrón que
/// <see cref="IDialogService"/>: estado observable + comandos, renderizado una sola
/// vez en <c>MainWindow.xaml</c> como overlay compartido de toda la app.
/// </summary>
public interface ISelectorTorrentService : INotifyPropertyChanged
{
    bool SelectorVisible { get; }
    string TituloEpisodio { get; }
    ObservableCollection<CandidatoTorrentItem> Candidatos { get; }

    CommunityToolkit.Mvvm.Input.IRelayCommand<CandidatoTorrentItem> SeleccionarCommand { get; }
    CommunityToolkit.Mvvm.Input.IRelayCommand CancelarSelectorCommand { get; }

    /// <summary>Muestra el overlay con los candidatos dados y espera a que el usuario
    /// elija uno o cancele. Null si cancela.</summary>
    Task<CandidatoTorrent?> MostrarSelectorAsync(string tituloEpisodio, IReadOnlyList<CandidatoTorrent> candidatos);
}
