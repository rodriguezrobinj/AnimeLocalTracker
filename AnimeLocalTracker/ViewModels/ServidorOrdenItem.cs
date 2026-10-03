using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// Una fila de una lista que el usuario ordena en Configuración (servidores de descarga, sitios de video): nombre, puesto y si puede
/// subir o bajar. Lleva sus propios botones, así una sola plantilla de la vista sirve para todas las listas.
/// </summary>
public partial class ServidorOrdenItem : ObservableObject
{
    /// <param name="mover">Lo que hace la lista dueña al pedir subir (-1) o bajar (+1) esta fila.</param>
    public ServidorOrdenItem(string nombre, Action<ServidorOrdenItem, int> mover)
    {
        Nombre = nombre;
        SubirCommand = new RelayCommand(() => mover(this, -1));
        BajarCommand = new RelayCommand(() => mover(this, 1));
    }

    public string Nombre { get; }

    public IRelayCommand SubirCommand { get; }
    public IRelayCommand BajarCommand { get; }

    [ObservableProperty] private int _posicion;
    [ObservableProperty] private bool _puedeSubir;
    [ObservableProperty] private bool _puedeBajar;
}
