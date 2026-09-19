using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

public enum ResultadoIntegridad
{
    Ok,
    Corrupto,
    NoSePudoVerificar
}

/// <summary>
/// "Doctor de integridad de video": detecta contenedores rotos (típicamente descargas
/// interrumpidas) sin decodificar el archivo entero — más rápido, suficiente para el caso
/// real (moov atom faltante, cabecera truncada, stream ilegible).
/// </summary>
public interface IVideoIntegrityService
{
    Task<ResultadoIntegridad> VerificarArchivoAsync(string rutaArchivo, CancellationToken ct = default);
}
