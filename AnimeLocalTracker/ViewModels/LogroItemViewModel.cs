using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Logros;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Una marca de nivel en la tarjeta (●●●○○): conseguido o no, con el color de su dificultad.</summary>
public sealed record NivelPip(bool Conseguido, string Color, string Nombre);

/// <summary>Cuántos niveles se llevan de una dificultad (Bronce…Diamante), para el resumen.</summary>
public sealed record ContadorNivel(string Nombre, string Color, int Cantidad);

/// <summary>Presentación de una familia de logros: textos ya localizados y datos listos para enlazar.</summary>
public sealed class LogroItemViewModel : ObservableObject
{
    // Solo colores ya presentes en la paleta de la app.
    private static readonly string[] ColoresNivel = { "#FB923C", "#94A3B8", "#FBBF24", "#34D399", "#818CF8" };
    private const string ColorNivelPendiente = "#262D38";
    private const string ColorSecreto = "#94A3B8";

    public static string ColorDeNivel(int nivel) => ColoresNivel[Math.Clamp(nivel, 1, 5) - 1];

    public LogroEstado Estado { get; }

    public string Titulo { get; }
    public string Descripcion { get; }
    public string Icono { get; }
    public string Color { get; }
    public CategoriaLogro Categoria => Estado.Definicion.Categoria;

    public bool Oculto => Estado.Oculto;
    public bool Desbloqueado => Estado.Desbloqueado;
    public bool Completo => Estado.Completo;

    public string NivelNombre { get; }
    public string NivelColor { get; }
    public IReadOnlyList<NivelPip> Niveles { get; }

    /// <summary>0–100, para el ProgressBar hacia el siguiente nivel.</summary>
    public double ProgresoPorcentaje => Estado.Progreso * 100.0;
    public bool MostrarProgreso => !Oculto && !Completo;
    public string ProgresoTexto { get; }

    public string PuntosTexto { get; }
    public string FechaTexto { get; }
    public bool TieneFecha => FechaTexto.Length > 0;

    public LogroItemViewModel(LogroEstado estado)
    {
        Estado = estado;
        var definicion = estado.Definicion;

        if (estado.Oculto)
        {
            Titulo = LocalizationService.T("Logro_Secreto_Titulo");
            Descripcion = LocalizationService.T("Logro_Secreto_Desc");
            Icono = "LockQuestion";
            Color = ColorSecreto;
        }
        else
        {
            // La descripción habla del objetivo del SIGUIENTE nivel (o del último si ya está completo).
            double objetivo = estado.SiguienteUmbral ?? definicion.Umbrales[^1];
            Titulo = LocalizationService.T(definicion.ClaveTitulo);
            Descripcion = string.Format(LocalizationService.T(definicion.ClaveDescripcion), FormatoNumero(objetivo));
            Icono = definicion.Icono;
            Color = definicion.Color;
        }

        NivelNombre = estado.Desbloqueado
            ? LocalizationService.T($"Logro_Nivel_{estado.NivelActual}")
            : LocalizationService.T("Logro_Nivel_0");
        NivelColor = estado.Desbloqueado ? ColorDeNivel(estado.NivelActual) : ColorNivelPendiente;

        Niveles = Enumerable.Range(1, definicion.NivelesTotales)
            .Select(n => new NivelPip(n <= estado.NivelActual, n <= estado.NivelActual ? ColorDeNivel(n) : ColorNivelPendiente,
                LocalizationService.T($"Logro_Nivel_{n}")))
            .ToList();

        // Un secreto sin desbloquear no debe delatar su meta ("8 / 12") ni cuántos puntos vale.
        ProgresoTexto = estado.Oculto
            ? string.Empty
            : estado.SiguienteUmbral is double siguiente
                ? $"{FormatoNumero(estado.Valor)} / {FormatoNumero(siguiente)}"
                : LocalizationService.T("Logro_Maximo");

        PuntosTexto = estado.Oculto
            ? string.Empty
            : string.Format(LocalizationService.T("Logro_PuntosFormato"), estado.Puntos, estado.PuntosMaximos);

        FechaTexto = estado.FechaUltimoNivelUtc is DateTime fecha
            ? string.Format(
                LocalizationService.T("Logro_DesbloqueadoElFormato"),
                MotorLogros.ALocal(fecha).ToString("d MMM yyyy", LocalizationService.Cultura))
            : string.Empty;
    }

    /// <summary>Enteros sin decimales; fraccionarios (horas) con uno si son pequeños.</summary>
    internal static string FormatoNumero(double valor)
    {
        if (Math.Abs(valor - Math.Round(valor)) < 1e-9 || valor >= 10) return Math.Round(valor).ToString("0", LocalizationService.Cultura);
        return valor.ToString("0.0", LocalizationService.Cultura);
    }
}
