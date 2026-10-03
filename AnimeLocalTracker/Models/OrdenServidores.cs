using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Models;

/// <summary>
/// Una lista que el usuario ordena en Configuración y que se guarda como texto separado por comas ("Voe,MP4Upload,Mega"): los
/// elementos listados van primero y en ese orden, y el resto después en el orden por defecto. Así un valor antiguo de un solo
/// elemento ("Voe") sigue significando "Voe primero".
/// </summary>
internal static class ListaOrdenable
{
    /// <summary>Orden completo que enseña la pantalla a partir del ajuste guardado (nombres desconocidos y repetidos se ignoran).</summary>
    public static List<string> DesdeAjuste(string? ajuste, IReadOnlyList<string> predeterminado)
    {
        var orden = new List<string>();
        foreach (var nombre in Dividir(ajuste))
        {
            string? conocido = predeterminado.FirstOrDefault(p => p.Equals(nombre, StringComparison.OrdinalIgnoreCase));
            if (conocido != null && !orden.Contains(conocido)) orden.Add(conocido);
        }
        orden.AddRange(predeterminado.Where(p => !orden.Contains(p)));
        return orden;
    }

    /// <summary>Texto que se guarda. Nulo si es el orden por defecto, para que un cambio futuro del predeterminado también le llegue.</summary>
    public static string? ParaAjuste(IEnumerable<string> orden, IReadOnlyList<string> predeterminado)
    {
        var lista = orden.ToList();
        return lista.SequenceEqual(predeterminado) ? null : string.Join(",", lista);
    }

    /// <summary>Nombres de la lista guardada, sin vacíos ni espacios.</summary>
    public static IEnumerable<string> Dividir(string? ajuste) =>
        (ajuste ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// Orden en que se prueban los servidores de video al descargar un episodio. Se guarda en
/// <see cref="AppSettings.ServidorPreferidoAnimeAv1"/>. Solo se ordenan los que resuelven hoy; HLS, UPNShare y Byse siguen detrás
/// (ver AnimeAv1HtmlParser.OrdenarEmbedsPorPreferencia).
/// </summary>
public static class OrdenServidores
{
    /// <summary>Servidores que se pueden ordenar, en el orden por defecto: los de 1080p primero (Mediafire y Vidhide solo existen en JKAnime), Voe (720p) y Streamwish (HLS 720p, lento) al final.</summary>
    public static IReadOnlyList<string> Predeterminado { get; } =
        [ServidorPreferidoValores.Mp4Upload, ServidorPreferidoValores.TransferIt, ServidorPreferidoValores.Mega, ServidorPreferidoValores.Mediafire,
         ServidorPreferidoValores.Vidhide, ServidorPreferidoValores.Voe, ServidorPreferidoValores.Streamwish];

    public static List<string> DesdeAjuste(string? ajuste) => ListaOrdenable.DesdeAjuste(ajuste, Predeterminado);

    public static string? ParaAjuste(IEnumerable<string> orden) => ListaOrdenable.ParaAjuste(orden, Predeterminado);

    internal static IEnumerable<string> Dividir(string? ajuste) => ListaOrdenable.Dividir(ajuste);
}

/// <summary>
/// Orden en que se prueban los sitios de video (AnimeAV1, JKAnime). Se guarda en <see cref="AppSettings.OrdenProveedoresVideo"/>.
/// El orquestador lo aplica por el nombre de cada proveedor; los que no figuran (plugins) van después, como siempre.
/// </summary>
public static class OrdenProveedores
{
    public static IReadOnlyList<string> Predeterminado { get; } = ["AnimeAV1", "JKAnime"];

    public static List<string> DesdeAjuste(string? ajuste) => ListaOrdenable.DesdeAjuste(ajuste, Predeterminado);

    public static string? ParaAjuste(IEnumerable<string> orden) => ListaOrdenable.ParaAjuste(orden, Predeterminado);

    internal static IEnumerable<string> Dividir(string? ajuste) => ListaOrdenable.Dividir(ajuste);
}
