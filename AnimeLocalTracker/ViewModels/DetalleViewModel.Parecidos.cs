using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Services.Recomendaciones;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// "Parecidos en tu biblioteca": animes pendientes que se parecen al de la ficha (ver docs/plan-parecidos-en-biblioteca.md).
/// Se calculan en segundo plano después de abrir la ficha; si no hay ninguno, la sección no se ve.
/// </summary>
public partial class DetalleViewModel
{
    private readonly IParecidosService? _parecidos;

    public ObservableCollection<Parecido> Parecidos { get; } = new();

    [ObservableProperty] private bool _hayParecidos;

    /// <summary>La última carga lanzada (para que las pruebas puedan esperarla).</summary>
    internal Task CargaParecidos { get; private set; } = Task.CompletedTask;

    private void ReiniciarParecidos()
    {
        Parecidos.Clear();
        HayParecidos = false;
    }

    private async Task CargarParecidosAsync(Models.AnimeItem anime, CancellationToken ct)
    {
        if (_parecidos == null) return;
        var lista = await _parecidos.BuscarAsync(anime.AniListId, ct);

        // Se abrió otra ficha (o se salió) mientras se calculaba: estos parecidos ya no son de lo que se ve.
        if (ct.IsCancellationRequested || _liberado || !ReferenceEquals(AnimeSeleccionado, anime)) return;
        foreach (var parecido in lista) Parecidos.Add(parecido);
        HayParecidos = Parecidos.Count > 0;
    }

    [RelayCommand]
    private void AbrirParecido(Parecido? parecido)
    {
        if (parecido != null) WeakReferenceMessenger.Default.Send(new NavegarMensaje_Detalle(parecido.Anime));
    }
}
