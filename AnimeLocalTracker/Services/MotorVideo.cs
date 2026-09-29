using System;
using System.Collections.Generic;
using System.Linq;
using FlyleafLib;

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

public sealed record TarjetaGrafica(string Nombre, GPUVendor Fabricante, long MemoriaMb);

/// <summary>Lo que se detectó en el último video abierto: se muestra en Configuración para que cada usuario vea por qué su equipo va así.</summary>
public sealed record InformeVideo(string Codec, bool PorHardware, string? Tarjeta, EstrategiaSaltos Saltos, bool Escalado, DateTime MomentoUtc);

/// <summary>
/// Motor de video (Flyleaf) compartido por toda la app: arranque único, tarjetas gráficas disponibles, informe del último video y la
/// política del modo automático (qué saltos usar y si activar el escalado inteligente según el equipo).
/// </summary>
public static class MotorVideo
{
    private static readonly object Candado = new();
    private static bool _iniciado;

    /// <summary>Arranca el motor una vez. False en pruebas automáticas (Flyleaf puede colgarse sin ventana) o si falla.</summary>
    public static bool AsegurarIniciado()
    {
        if (ViewModels.ReproductorViewModel.EsEntornoPruebas()) return false;

        lock (Candado)
        {
            if (_iniciado) return true;
            try
            {
                Engine.Start(new EngineConfig { FFmpegPath = ":FFmpeg", UIRefresh = true });
                _iniciado = true;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("MotorVideo", $"No se pudo iniciar el motor Flyleaf: {ex.Message}");
            }
            return _iniciado;
        }
    }

    /// <summary>Tarjetas gráficas reales del equipo (sin el adaptador de software de Microsoft ni nombres repetidos).</summary>
    public static IReadOnlyList<TarjetaGrafica> ListarTarjetas()
    {
        if (!AsegurarIniciado()) return Array.Empty<TarjetaGrafica>();
        try
        {
            return Engine.Video.GPUAdapters.Values
                .Where(g => !string.IsNullOrWhiteSpace(g.Description) && !g.Description.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                .Select(g => new TarjetaGrafica(g.Description.Trim(), g.Vendor, (long)((ulong)g.VideoMemory / (1024 * 1024))))
                .GroupBy(t => t.Nombre, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(t => t.MemoriaMb).First())
                .OrderByDescending(t => t.MemoriaMb)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MotorVideo", $"No se pudieron listar las tarjetas gráficas: {ex.Message}");
            return Array.Empty<TarjetaGrafica>();
        }
    }

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
    /// Patrón para Config.Video.GPUAdapter: Flyleaf lo compara como expresión regular contra el nombre, así que "Intel(R) HD Graphics
    /// 620" tal cual no coincidiría consigo mismo (los paréntesis son un grupo). Null = la que elija Windows.
    /// </summary>
    public static string? PatronTarjeta(string? nombre) =>
        string.IsNullOrWhiteSpace(nombre) ? null : "^" + System.Text.RegularExpressions.Regex.Escape(nombre.Trim()) + "$";

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
    /// Escalado inteligente (RTX Video Super Resolution de NVIDIA o el de Intel; Flyleaf no lo tiene para AMD). En automático, solo con
    /// tarjeta NVIDIA o Intel dedicada (2 GB o más): en una integrada gasta batería y apenas se nota. Flyleaf además solo lo aplica si el
    /// video se amplía en pantalla y es de 8 bits (los AV1 de 10 bits no lo usan).
    /// </summary>
    public static bool UsarEscaladoInteligente(string? modo, GPUVendor fabricante, long memoriaMb) => modo switch
    {
        ModosRendimientoVideo.Activado => true,
        ModosRendimientoVideo.Desactivado => false,
        _ => fabricante is GPUVendor.Nvidia or GPUVendor.Intel && memoriaMb >= 2048
    };

    public static bool EscaladoDisponible(GPUVendor fabricante) => fabricante is GPUVendor.Nvidia or GPUVendor.Intel;
}
