namespace AnimeLocalTracker.Messages;

/// <summary>
/// Aviso de episodios nuevos detectados en las carpetas de la biblioteca.
/// Lo consume MainViewModel para mostrar un toast en la UI.
/// </summary>
public class NuevosEpisodiosMensaje
{
    public int Cantidad { get; }
    public string Resumen { get; }

    public NuevosEpisodiosMensaje(int cantidad, string resumen)
    {
        Cantidad = cantidad;
        Resumen = resumen;
    }
}
