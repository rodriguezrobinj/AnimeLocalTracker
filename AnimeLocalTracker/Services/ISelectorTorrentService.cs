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

    public CandidatoTorrentItem(CandidatoTorrent original)
    {
        Original = original;
        TamanoTexto = FormatearTamano(original.TamanoBytes);
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
