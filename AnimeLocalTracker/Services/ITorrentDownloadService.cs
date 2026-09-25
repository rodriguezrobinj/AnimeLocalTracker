using System;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>Resultado final de una descarga por torrent (Fase 1b, MVP).</summary>
public readonly record struct ResultadoTorrent(bool Exito, string? RutaArchivo, string? Error);

/// <summary>
/// Motor de descarga por BitTorrent (MonoTorrent). Por defecto no sigue sembrando tras
/// completar; con <c>seguirSembrando</c> (Fase 2c) el <c>TorrentManager</c> queda activo
/// sirviendo piezas a otros peers hasta que se llama <see cref="DetenerTodoElSeedingAsync"/>
/// o se cierra la app.
/// </summary>
public interface ITorrentDownloadService
{
    /// <summary>
    /// Descarga el archivo de video del episodio pedido dentro del torrent (el resto
    /// de archivos, si los hay, se excluyen con Priority.DoNotDownload) y lo mueve a
    /// <paramref name="rutaDestinoEsperada"/> al terminar. Sirve tanto para releases de
    /// un solo episodio como para batches (Fase 2a): primero intenta identificar el
    /// archivo del episodio pedido por su nombre; si el torrent no tiene más que un
    /// video (caso normal de un release suelto), cae a "el archivo de video más
    /// grande" — ver <see cref="SeleccionArchivoTorrent"/>.
    /// </summary>
    /// <param name="torrentUrl">URL https del archivo .torrent (no magnet — tener el
    /// archivo completo da la lista de archivos sin depender de peers).</param>
    /// <param name="carpetaTemporal">Carpeta de trabajo de MonoTorrent para este
    /// torrent (se borra al terminar, con éxito o sin él).</param>
    /// <param name="rutaDestinoEsperada">Ruta final del archivo de video.</param>
    /// <param name="numeroEpisodio">Episodio pedido — usado para elegir el archivo
    /// correcto dentro de un torrent con varios videos (batch).</param>
    /// <param name="seguirSembrando">Fase 2c (AppSettings.SeguirSembrandoTorrents): si es
    /// true, al completar el archivo se COPIA (no se mueve) a
    /// <paramref name="rutaDestinoEsperada"/> y el torrent se deja sembrando desde su
    /// carpeta temporal — moverlo rompería el sembrado, porque MonoTorrent sigue sirviendo
    /// piezas desde ahí. Si es false (por defecto), se mueve y se detiene de inmediato.</param>
    Task<ResultadoTorrent> DescargarAsync(
        string torrentUrl,
        string carpetaTemporal,
        string rutaDestinoEsperada,
        int numeroEpisodio,
        bool seguirSembrando = false,
        IProgress<(double Progreso, double VelocidadBps)>? progress = null,
        CancellationToken ct = default);

    /// <summary>Detiene y quita del motor todos los torrents que quedaron sembrando
    /// (<paramref name="seguirSembrando"/> de descargas anteriores). Se llama al cerrar
    /// la app — no hay sembrado persistente entre sesiones en este alcance.</summary>
    Task DetenerTodoElSeedingAsync();
}
