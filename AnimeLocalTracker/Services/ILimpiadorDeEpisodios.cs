using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>"Eliminar tras ver": borra el video de los episodios ya vistos según el modo elegido en Configuración.</summary>
public interface ILimpiadorDeEpisodios
{
    /// <summary>
    /// Se llama cuando se terminó de ver <paramref name="episodioTerminado"/> reproduciéndolo de verdad. Decide y ejecuta el
    /// borrado según el modo; con el modo apagado no hace nada. Nunca lanza: un fallo se registra y el episodio se queda como estaba.
    /// </summary>
    Task AplicarTrasVerAsync(int aniListId, int episodioTerminado);
}
