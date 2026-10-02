using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Colección observable cuyo contenido se sustituye de una vez. Con <see cref="ObservableCollection{T}"/> a secas, rehacer
/// una lista es vaciarla y añadir fila a fila: un aviso por elemento (1.180 en One Piece) y, aunque el resultado sea
/// idéntico al que ya había, la lista en pantalla vuelve arriba y pierde la selección.
/// </summary>
public sealed class ColeccionReemplazable<T> : ObservableCollection<T>
{
    /// <summary>
    /// Deja la colección con exactamente <paramref name="nuevos"/>. Si ya contenía esos mismos elementos en ese orden no hace
    /// nada (devuelve false); si no, la sustituye con un único aviso de cambio.
    /// </summary>
    public bool ReemplazarSiCambia(IReadOnlyList<T> nuevos)
    {
        if (MismoContenido(nuevos)) return false;

        CheckReentrancy();
        Items.Clear();
        foreach (var elemento in nuevos) Items.Add(elemento);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }

    private bool MismoContenido(IReadOnlyList<T> nuevos)
    {
        if (nuevos.Count != Count) return false;

        var comparador = EqualityComparer<T>.Default;
        for (int i = 0; i < nuevos.Count; i++)
        {
            if (!comparador.Equals(Items[i], nuevos[i])) return false;
        }
        return true;
    }
}
