using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Subtítulos del reproductor: la pista elegida y el estilo. Los dibuja el motor dentro del video, también los de texto.</summary>
public partial class ReproductorViewModel
{
    /// <summary>La pista elegida es ASS/SSA: el menú de subtítulos ofrece "Usar mi estilo".</summary>
    [ObservableProperty] private bool _pistaActualEsAss;

    private PistaSubtitulosMotor? _pistaTextoActual;

    /// <summary>Olvida la pista elegida (episodio nuevo): las del archivo anterior no valen para este.</summary>
    private void OlvidarPistaSubtitulos()
    {
        _pistaTextoActual = null;
        PistaActualEsAss = false;
    }

    /// <summary>Muestra la pista elegida (sola al abrir el episodio, o por el usuario en el menú).</summary>
    private void AbrirPistaSubtitulos(PistaSubtitulosMotor pista)
    {
        _pistaTextoActual = pista;
        PistaActualEsAss = pista.EsAss;
        _subtitleCoordinator.SeleccionarPista(Motor, pista);
    }

    private bool _usarMiEstiloEnAss;
    /// <summary>Con una pista ASS, verla con el estilo de Configuración en vez del original del subtítulo. Se guarda como preferencia.</summary>
    public bool UsarMiEstiloEnAss
    {
        get => _usarMiEstiloEnAss;
        set
        {
            if (!SetProperty(ref _usarMiEstiloEnAss, value)) return;

            GuardarUsarMiEstiloEnAss(value);
            AplicarEstiloSubtitulosAlMotor();
        }
    }

    [RelayCommand]
    private void ToggleUsarMiEstiloEnAss() => UsarMiEstiloEnAss = !UsarMiEstiloEnAss;

    /// <summary>Al motor: el estilo de Configuración y si debe usarlo también en las pistas ASS.</summary>
    private void AplicarEstiloSubtitulosAlMotor() => Motor?.AplicarEstiloSubtitulos(EstiloSubtitulos, UsarMiEstiloEnAss);

    partial void OnEstiloSubtitulosChanged(EstiloSubtitulos value) => AplicarEstiloSubtitulosAlMotor();

    private void GuardarUsarMiEstiloEnAss(bool valor)
    {
        if (_settingsService == null) return;
        var config = _settingsService.ObtenerConfiguracion();
        if (config == null || config.UsarMiEstiloEnAss == valor) return;
        config.UsarMiEstiloEnAss = valor;
        _ = _settingsService.GuardarConfiguracionAsync(config);
    }
}
