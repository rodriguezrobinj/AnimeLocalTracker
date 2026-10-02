using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

public enum EstadoMinijuego
{
    Inicio,
    Jugando,
    Resumen
}

/// <summary>Una de las 4 opciones de respuesta; su color (acierto/fallo) lo decide la vista con estos flags.</summary>
public sealed partial class OpcionRespuesta : ObservableObject
{
    public int Indice { get; }
    public int Numero => Indice + 1;
    public string Titulo { get; }
    public string NombreAccesible => string.Format(LocalizationService.T("Mini_OpcionAccFormato"), Numero, Titulo);

    /// <summary>Se marca en la respuesta correcta cuando la ronda termina (haya acertado o no el jugador).</summary>
    [ObservableProperty] private bool _esCorrecta;

    /// <summary>Se marca solo en la opción errónea que eligió el jugador.</summary>
    [ObservableProperty] private bool _esIncorrecta;

    public OpcionRespuesta(int indice, string titulo)
    {
        Indice = indice;
        Titulo = titulo;
    }

    public void RefrescarTextos() => OnPropertyChanged(nameof(NombreAccesible));
}

/// <summary>Una ronda ya jugada, para el resumen final: cuál era la respuesta, si se acertó y cuántos puntos dio.</summary>
public sealed record RondaResumen(int Numero, string Respuesta, bool Acierto, int Puntos);

/// <summary>Pista ya localizada, lista para mostrar.</summary>
public sealed record PistaItem(string Icono, string Etiqueta, string Texto);
