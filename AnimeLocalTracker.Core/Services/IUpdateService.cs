using AnimeLocalTracker.Core.Services;
using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Core.Models;
using Velopack;

namespace AnimeLocalTracker.Core.Services;

public interface IUpdateService
{
    /// <summary>
    /// Obtiene la versión semántica actual instalada de la aplicación.
    /// </summary>
    string ObtenerVersionActual();

    /// <summary>
    /// Indica si la aplicación está ejecutándose dentro de una instalación administrada por Velopack.
    /// </summary>
    bool EstaInstaladoPorVelopack();

    /// <summary>
    /// Consulta si existen nuevas versiones disponibles en GitHub Releases.
    /// </summary>
    Task<UpdateInfo?> ComprobarActualizacionesAsync(bool esManual = false);

    /// <summary>
    /// Descarga los paquetes de la actualización en segundo plano reportando progreso opcional.
    /// </summary>
    Task<bool> DescargarActualizacionAsync(UpdateInfo updateInfo, Action<int>? onProgreso = null);

    /// <summary>
    /// Aplica la actualización descargada y reinicia la aplicación inmediatamente.
    /// </summary>
    void AplicarActualizacionYReiniciar(UpdateInfo updateInfo);

    /// <summary>
    /// Inicia el bucle de verificación automática y silenciosa en segundo plano.
    /// </summary>
    void IniciarVerificacionSegundoPlano(TimeSpan intervalo);

    /// <summary>
    /// Obtiene la información y novedades de la versión (con caché local para no consultar a GitHub repetitivamente).
    /// </summary>
    Task<ReleaseInfo> ObtenerInfoUltimaVersionAsync(bool forzarActualizacion = false);
}
