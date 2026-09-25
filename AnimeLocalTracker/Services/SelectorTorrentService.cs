using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.Services;

/// <summary>Mismo patrón que <see cref="DialogService"/>: singleton compartido, estado
/// observable que <c>MainWindow.xaml</c> muestra como overlay, resuelto vía
/// <see cref="TaskCompletionSource{TResult}"/> cuando el usuario elige o cancela.</summary>
public partial class SelectorTorrentService : ObservableObject, ISelectorTorrentService
{
    [ObservableProperty] private bool _selectorVisible;
    [ObservableProperty] private string _tituloEpisodio = "";

    public ObservableCollection<CandidatoTorrentItem> Candidatos { get; } = new();

    private TaskCompletionSource<CandidatoTorrent?>? _tcs;

    public Task<CandidatoTorrent?> MostrarSelectorAsync(string tituloEpisodio, IReadOnlyList<CandidatoTorrent> candidatos)
    {
        if (System.Windows.Application.Current?.Dispatcher != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            return System.Windows.Application.Current.Dispatcher.Invoke(() => MostrarSelectorInterno(tituloEpisodio, candidatos));
        }
        return MostrarSelectorInterno(tituloEpisodio, candidatos);
    }

    private Task<CandidatoTorrent?> MostrarSelectorInterno(string tituloEpisodio, IReadOnlyList<CandidatoTorrent> candidatos)
    {
        TituloEpisodio = tituloEpisodio;
        Candidatos.Clear();
        foreach (var c in candidatos) Candidatos.Add(new CandidatoTorrentItem(c));

        SelectorVisible = true;

        _tcs = new TaskCompletionSource<CandidatoTorrent?>();
        return _tcs.Task;
    }

    [RelayCommand]
    private void Seleccionar(CandidatoTorrentItem? item)
    {
        SelectorVisible = false;
        _tcs?.TrySetResult(item?.Original);
    }

    [RelayCommand]
    private void CancelarSelector()
    {
        SelectorVisible = false;
        _tcs?.TrySetResult(null);
    }
}
