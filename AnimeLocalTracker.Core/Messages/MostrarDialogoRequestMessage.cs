using CommunityToolkit.Mvvm.Messaging.Messages;

namespace AnimeLocalTracker.Core.Messages;

public class MostrarDialogoRequestMessage : AsyncRequestMessage<bool>
{
    public string Titulo { get; }
    public string Mensaje { get; }
    public bool EsConfirmacion { get; }
    public string Icono { get; }
    public string Color { get; }

    public MostrarDialogoRequestMessage(string titulo, string mensaje, bool esConfirmacion, string icono, string color)
    {
        Titulo = titulo;
        Mensaje = mensaje;
        EsConfirmacion = esConfirmacion;
        Icono = icono;
        Color = color;
    }
}
