namespace AnimeLocalTracker.Messages;

/// <summary>
/// Avance de la descarga de un opening/ending (AnimeThemes) para la pestaña Descargas. Es un mensaje aparte de
/// <see cref="DescargaProgresoMensaje"/> a propósito: ese va por (anime, número de episodio) y lo escuchan la ficha,
/// Actualizaciones y la descarga automática, que tomarían una canción por un "episodio 0".
/// </summary>
/// <param name="Progreso">De 0 a 100.</param>
/// <param name="Terminada">Ya no sigue: completada, fallida o cancelada (la fila se retira de las activas).</param>
public sealed record DescargaMusicaProgresoMensaje(
    int AniListId,
    string AnimeTitulo,
    string TemaClave,
    string TemaTitulo,
    double Progreso,
    double VelocidadBps,
    bool Convirtiendo,
    bool Terminada,
    bool Completada,
    bool Cancelada,
    string? RutaArchivo,
    string? Error);
