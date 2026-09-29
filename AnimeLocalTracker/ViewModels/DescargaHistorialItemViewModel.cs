using System;
using System.Globalization;
using System.IO;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Fila del historial de descargas (completada o fallida) con los textos ya formateados para la vista.</summary>
public partial class DescargaHistorialItemViewModel : ObservableObject
{
    public int Id { get; }
    public int AniListId { get; }
    public string AnimeTitulo { get; }
    public int NumeroEpisodio { get; }
    public bool Completada { get; }
    public string? Error { get; }
    public string RutaArchivo { get; }
    public string CarpetaDestino { get; }
    public string TitulosAlternativos { get; }
    public DateTime FechaLocal { get; }
    public string TamanoTexto { get; }
    public string HoraTexto { get; }

    /// <summary>"Episodio 5 · 350 MB · 22:09" (sin separadores sobrantes cuando falta el tamaño).</summary>
    public string DetalleTexto { get; }

    /// <summary>Grupo temporal (Hoy / Ayer / fecha larga) para las cabeceras de la lista.</summary>
    public string GrupoTemporal { get; }

    public string TituloEpisodio => string.Format(LocalizationService.T("Act_EpisodioFormato"), NumeroEpisodio);
    public bool EsFallida => !Completada;

    /// <summary>Un opening/ending de AnimeThemes en vez de un episodio.</summary>
    public bool EsMusica { get; }

    /// <summary>Solo música: tipo|slug|versión del tema (para reintentarlo o reproducirlo).</summary>
    public string TemaClave { get; }

    /// <summary>Solo música: "OP1 · We Are!".</summary>
    public string TemaTitulo { get; }

    /// <summary>False si el archivo ya no está en disco (se comprueba fuera del hilo de UI, nunca en un getter).</summary>
    [ObservableProperty]
    private bool _archivoExiste = true;

    public DescargaHistorialItemViewModel(DescargaHistorial d, DateTime? ahoraLocal = null)
    {
        Id = d.Id;
        AniListId = d.AniListId;
        AnimeTitulo = d.AnimeTitulo;
        NumeroEpisodio = d.NumeroEpisodio;
        Completada = d.Completada;
        Error = d.Error;
        RutaArchivo = d.RutaArchivo;
        CarpetaDestino = d.CarpetaDestino;
        TitulosAlternativos = d.TitulosAlternativos;
        EsMusica = d.Tipo == DescargaHistorial.TipoMusica;
        TemaClave = d.TemaClave ?? string.Empty;
        TemaTitulo = d.TemaTitulo ?? string.Empty;

        // sqlite-net devuelve Kind=Unspecified: siempre es UTC.
        FechaLocal = d.FechaUtc.Kind == DateTimeKind.Local ? d.FechaUtc : DateTime.SpecifyKind(d.FechaUtc, DateTimeKind.Utc).ToLocalTime();
        var hoy = (ahoraLocal ?? DateTime.Now).Date;
        GrupoTemporal = FechaLocal.Date == hoy ? LocalizationService.T("Hist_FechaHoy")
            : FechaLocal.Date == hoy.AddDays(-1) ? LocalizationService.T("Hist_FechaAyer")
            : FechaLocal.ToString("D", LocalizationService.Cultura);
        HoraTexto = FechaLocal.ToString("t", LocalizationService.Cultura);
        TamanoTexto = d.Completada && d.TamanoBytes > 0 ? FormatearTamano(d.TamanoBytes) : string.Empty;
        string queEs = EsMusica ? TemaTitulo : TituloEpisodio;
        DetalleTexto = string.Join("  ·  ", new[] { queEs, TamanoTexto, HoraTexto }.Where(t => !string.IsNullOrEmpty(t)));
    }

    public static string FormatearTamano(long bytes)
    {
        double mb = bytes / 1048576.0;
        // Un opening/ending pesa 2-3 MB: sin decimal se leería "2 MB" para 2,4 MB. Los episodios (cientos de MB) siguen igual.
        return mb >= 1024 ? (mb / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : mb < 10 ? mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : mb.ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    public bool ComprobarArchivo() => Completada && !string.IsNullOrWhiteSpace(RutaArchivo) && File.Exists(RutaArchivo);
}
