using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Motor de mentira: la prueba decide el estado y dispara los avisos; el motor apunta lo que se le pidió. Se comporta como el de
/// verdad en lo que el reproductor da por hecho: abrir deja el estado en "abriendo", reproducir en "reproduciendo", saltar mueve la posición
/// y pinta fotogramas nuevos.
/// </summary>
public sealed class MotorDePrueba : IMotorVideo
{
    public List<string> Abiertos { get; } = [];
    public List<(double Segundos, bool Exacto)> Saltos { get; } = [];
    public int VecesDetenido { get; private set; }
    public int VecesDesechado { get; private set; }
    public int VecesReproducir { get; private set; }
    public int VecesPausar { get; private set; }
    public PistaAudioMotor? AudioElegido { get; private set; }
    public PistaSubtitulosMotor? SubtitulosDelMotor { get; private set; }
    public int VecesSubtitulosApagados { get; private set; }
    public IReadOnlyList<FiltroAudio> Filtros { get; private set; } = [];
    public List<(string Id, string Clave, string Valor)> CambiosEnVivo { get; } = [];
    public bool AdmiteCambiosEnVivo { get; set; } = true;

    public bool Desechado { get; private set; }
    public EstadoMotor Estado { get; set; } = EstadoMotor.Detenido;
    public TimeSpan Posicion { get; set; }
    public TimeSpan Duracion { get; set; }
    public event EventHandler<AperturaTerminada>? AperturaTerminada;

    public double VelocidadPuesta { get; set; } = 1;
    public int VolumenPuesto { get; set; } = -1;
    public bool SilencioPuesto { get; set; }
    public bool AceleracionPuesta { get; set; } = true;
    public double Velocidad { set => VelocidadPuesta = value; }
    public int Volumen { set => VolumenPuesto = value; }
    public bool Silencio { set => SilencioPuesto = value; }
    public int DesfaseSubtitulosPuesto { get; set; }
    public int DesfaseAudioPuesto { get; set; }
    public int DesfaseSubtitulosMs { set => DesfaseSubtitulosPuesto = value; }
    public int DesfaseAudioMs { set => DesfaseAudioPuesto = value; }
    public bool AceleracionPorHardware { set => AceleracionPuesta = value; }

    private long _fotogramas;
    public long FotogramasMostrados { get => Interlocked.Read(ref _fotogramas); set => Interlocked.Exchange(ref _fotogramas, value); }
    public bool DecodificaPorHardware { get; set; }
    public string? CodecVideo { get; set; } = "h264";
    public (int Ancho, int Alto) TamanoVideo { get; set; } = (1920, 1080);
    public double ProporcionVideo { get; set; } = 16 / 9.0;
    public event EventHandler? ProporcionVideoCambiada;
    public TarjetaGrafica? TarjetaEnUso { get; set; }
    public bool EscaladoInteligente { get; set; }
    public long FotogramasPerdidos { get; set; }
    public BitmapSource? Captura { get; set; }
    public BitmapSource? CapturarFotograma() => Captura;

    public List<PistaAudioMotor> Audio { get; } = [];
    public List<PistaSubtitulosMotor> Subtitulos { get; } = [];
    public IReadOnlyList<PistaAudioMotor> PistasAudio => Audio;
    public int IndicePistaAudioActual { get; set; } = -1;
    public IReadOnlyList<PistaSubtitulosMotor> PistasSubtitulos => Subtitulos;
    public int IndicePistaSubtitulosDelMotor => SubtitulosDelMotor?.Indice ?? -1;

    public void Abrir(string ruta) { Abiertos.Add(ruta); Estado = EstadoMotor.Abriendo; FotogramasMostrados = 0; }
    public void Reproducir() { VecesReproducir++; Estado = EstadoMotor.Reproduciendo; }
    public void Pausar() { VecesPausar++; Estado = EstadoMotor.EnPausa; }
    public void Detener() { VecesDetenido++; Estado = EstadoMotor.Detenido; }

    public void Saltar(TimeSpan destino, bool exacto)
    {
        Saltos.Add((destino.TotalSeconds, exacto));
        Posicion = destino;
        Interlocked.Add(ref _fotogramas, 2); // tras un salto el motor pinta el fotograma nuevo
    }

    public void ElegirAudio(PistaAudioMotor pista) { AudioElegido = pista; IndicePistaAudioActual = pista.Indice; }
    public void MostrarSubtitulosDelMotor(PistaSubtitulosMotor pista) => SubtitulosDelMotor = pista;
    public void ApagarSubtitulosDelMotor() { VecesSubtitulosApagados++; SubtitulosDelMotor = null; }
    public (AnimeLocalTracker.Models.EstiloSubtitulos Estilo, bool TambienEnAss)? EstiloPuesto { get; private set; }
    public void AplicarEstiloSubtitulos(AnimeLocalTracker.Models.EstiloSubtitulos estilo, bool tambienEnAss) => EstiloPuesto = (estilo, tambienEnAss);
    public IReadOnlyList<FiltroAudio>? FiltrosRecordados { get; private set; }
    public void RecordarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros) => FiltrosRecordados = filtros;
    public bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros) { Filtros = filtros; return true; }
    public bool CambiarFiltroEnVivo(string id, string clave, string valor) { CambiosEnVivo.Add((id, clave, valor)); return AdmiteCambiosEnVivo; }
    public void Dispose() { VecesDesechado++; Desechado = true; }

    /// <summary>El aviso que da el motor al terminar de abrir el archivo (o una pista de audio suya).</summary>
    public void TerminarApertura(bool exito = true, string? error = null, ClaseApertura clase = ClaseApertura.Video) =>
        AperturaTerminada?.Invoke(this, new AperturaTerminada(clase, exito, error));

    public void CambiarProporcion(double proporcion) { ProporcionVideo = proporcion; ProporcionVideoCambiada?.Invoke(this, EventArgs.Empty); }
}
