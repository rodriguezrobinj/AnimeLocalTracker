using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimeLocalTracker.Services;

/// <summary>Valores de los ajustes de "Rendimiento de video" (Configuración → Reproducción), tal como se guardan en settings.json.</summary>
public static class ModosRendimientoVideo
{
    public const string Automatico = "Automatico";
    public const string Exactos = "Exactos";
    public const string Rapidos = "Rapidos";
    public const string Activado = "Activado";
    public const string Desactivado = "Desactivado";
}

/// <summary>Cómo se resuelven los saltos del reproductor (flechas, ±N s, saltar opening, clic en la barra).</summary>
public enum EstrategiaSaltos
{
    /// <summary>Siempre al segundo exacto (en equipos que decodifican rápido, no se nota la espera).</summary>
    Exactos,
    /// <summary>Fotograma clave cercano si lo hay (instantáneo), si no exacto: lo que conviene a un equipo modesto.</summary>
    Equilibrados,
    /// <summary>Siempre a un fotograma clave (instantáneo), aunque caiga unos segundos antes o después.</summary>
    Rapidos
}

public sealed record TarjetaGrafica(string Nombre, FabricanteGrafica Fabricante, long MemoriaMb);

/// <summary>Lo que se detectó en el último video abierto: se muestra en Configuración para que cada usuario vea por qué su equipo va así.</summary>
public sealed record InformeVideo(string Codec, bool PorHardware, string? Tarjeta, EstrategiaSaltos Saltos, bool Escalado, DateTime MomentoUtc);

/// <summary>
/// Lo que comparte toda la app sobre el video: el informe del último episodio abierto y la política del modo automático (qué saltos
/// usar y si activar el escalado inteligente según el equipo).
/// </summary>
public static class MotorVideo
{
    private static readonly object Candado = new();

    private static InformeVideo? _ultimoInforme;
    public static InformeVideo? UltimoInforme
    {
        get { lock (Candado) return _ultimoInforme; }
    }

    public static void PublicarInforme(InformeVideo informe)
    {
        lock (Candado) _ultimoInforme = informe;
    }

    /// <summary>
    /// El motor pide la tarjeta gráfica y, si no soporta el formato, suele caer solo al procesador. Pero puede quedarse con la tarjeta
    /// elegida sin que llegue ni un fotograma (visto con Tokyo Revengers S4 en una Intel HD 620). Con la tarjeta elegida y 0 fotogramas
    /// tras la espera, se reabre por procesador (una vez por episodio).
    /// </summary>
    public static bool DebeReintentarPorProcesador(bool porHardware, long fotogramas, bool yaReintentado) =>
        porHardware && fotogramas == 0 && !yaReintentado;

    // === Modo automático ===

    /// <summary>
    /// Automático: saltos exactos si el video se decodifica por la tarjeta gráfica o el procesador tiene 12 hilos o más (decodifica los
    /// hasta ~10 s entre fotogramas clave de un AV1 en muy poco); si no, equilibrados. Medido en un i5-7300U (4 hilos, AV1 por software):
    /// exacto 0,7-1 s de espera, fotograma clave ~0,1 s.
    /// </summary>
    public static EstrategiaSaltos ElegirEstrategiaSaltos(string? modo, bool decodificacionPorHardware, int hilosProcesador) => modo switch
    {
        ModosRendimientoVideo.Exactos => EstrategiaSaltos.Exactos,
        ModosRendimientoVideo.Rapidos => EstrategiaSaltos.Rapidos,
        _ => decodificacionPorHardware || hilosProcesador >= 12 ? EstrategiaSaltos.Exactos : EstrategiaSaltos.Equilibrados
    };

    /// <summary>
    /// Escalado inteligente (Anime4K: sombreadores que funcionan en cualquier tarjeta). En automático se enciende siempre y el
    /// reproductor lo apaga en ese episodio si pierde fotogramas (<see cref="VigilanteEscalado"/>).
    /// </summary>
    public static bool UsarEscaladoInteligente(string? modo) => modo != ModosRendimientoVideo.Desactivado;

    /// <summary>
    /// El nivel de Anime4K que suele aguantar cada tarjeta. Medido en una Intel HD 620 a 1080p: el ligero no pierde fotogramas y el
    /// medio pierde 9 por segundo. No se distingue una AMD integrada de una dedicada: si el medio no aguanta, el automático lo apaga.
    /// </summary>
    public static NivelEscalado NivelAnime4K(FabricanteGrafica fabricante) =>
        fabricante is FabricanteGrafica.Nvidia or FabricanteGrafica.Amd ? NivelEscalado.Medio : NivelEscalado.Ligero;

    /// <summary>
    /// Los shaders de un nivel, en el orden en que se aplican: recortar brillos, restaurar líneas y ampliar. El de ampliar se salta
    /// solo cuando el video no se agranda al menos un 20 % en pantalla (lo lleva escrito en su cabecera).
    /// </summary>
    public static IReadOnlyList<string> SombreadoresAnime4K(NivelEscalado nivel)
    {
        string variante = nivel == NivelEscalado.Medio ? "M" : "S";
        return ["Anime4K_Clamp_Highlights.glsl", $"Anime4K_Restore_CNN_{variante}.glsl", $"Anime4K_Upscale_CNN_x2_{variante}.glsl"];
    }

    /// <summary>Fabricante a partir del texto que da el controlador ("Intel", "NVIDIA Corporation", "ATI Technologies Inc.", "AMD Radeon…").</summary>
    internal static FabricanteGrafica FabricantePorNombre(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? FabricanteGrafica.Desconocido
        : texto.Contains("intel", StringComparison.OrdinalIgnoreCase) ? FabricanteGrafica.Intel
        : texto.Contains("nvidia", StringComparison.OrdinalIgnoreCase) || texto.Contains("geforce", StringComparison.OrdinalIgnoreCase) ? FabricanteGrafica.Nvidia
        : texto.Contains("amd", StringComparison.OrdinalIgnoreCase) || texto.Contains("radeon", StringComparison.OrdinalIgnoreCase)
          || texto.Contains("ati ", StringComparison.OrdinalIgnoreCase) ? FabricanteGrafica.Amd
        : FabricanteGrafica.Desconocido;
}
