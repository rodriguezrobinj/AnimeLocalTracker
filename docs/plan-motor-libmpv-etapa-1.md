# Plan de implementación: motor libmpv, etapa 1 (la costura)

> **Para quien lo ejecute:** SUB-SKILL OBLIGATORIO: `superpowers:executing-plans`, tarea a tarea, en esta misma sesión (en este repo no se usan subagentes salvo que el usuario lo pida). Los pasos usan casillas (`- [ ]`) para llevar la cuenta.

**Objetivo:** que el reproductor de episodios deje de hablar con Flyleaf directamente y hable con una interfaz, `IMotorVideo`, cuya única implementación de momento es Flyleaf. Sin ningún cambio visible.

**Arquitectura:** sustitución hoja a hoja. La coreografía del reproductor (arranque en silencio hasta el primer fotograma, reanudación, reintentos, fin de episodio) se queda donde está; cada llamada a `Player.X` pasa a ser `Motor.X`. Lo que no tiene significado fuera de Flyleaf (crear su `Config`, el parche de subtítulos, el contador de fotogramas por reflexión, la lectura adelantada con su `Demuxer`) se muda a `MotorFlyleaf`. La vista clásica sigue usando el `Player` de Flyleaf a través de una propiedad de paso.

**Tecnologías:** C# .NET 8, WPF, FlyleafLib 3.11.3, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-motor-libmpv.md`. Quien ejecute lee los dos documentos.

**Respecto al esbozo del contrato en el diseño (sección 4.2):** aquí queda cerrado tras recorrer cada llamada, y cambia en tres cosas. La apertura no es una tarea que se espera sino una orden más un aviso (`Abrir` + `AperturaTerminada`), que es como lo usa hoy el reproductor. El contrato es más ancho (fotogramas mostrados, decodificación, tarjeta, proporción) porque la coreografía del arranque se queda en el reproductor. Y no hay interfaz aparte para "subtítulos dibujados por la app": el reproductor ya trabaja con pistas propias (`PistaSubtitulosMotor`) y la vista clásica llega a Flyleaf por la propiedad de paso `Player`.

## Restricciones globales

- **Sin commit ni push.** Donde un plan normal diría "commit", aquí hay un paso de verificación. Sin ramas, worktrees ni subagentes.
- **Ningún cambio de comportamiento.** Mismos tiempos de espera, mismos reintentos, mismo orden de llamadas al motor. Si una sustitución obligara a cambiar algo observable, se para y se consulta.
- **`ReproductorView.xaml` y `ReproductorView.xaml.cs` no se tocan** salvo donde este plan lo dice (tarea 7): se borran en la etapa 6.
- **Sin tipos de Flyleaf en `IMotorVideo`** ni en los coordinadores. Al terminar, `using FlyleafLib` solo queda en: `Services/MotorFlyleaf.cs`, `Services/MotorVideo.cs`, `Services/ParcheSubtitulosFlyleaf.cs`, `Services/SubtitleAssRenderer.cs`, `Controls/SubtitleTextView.cs`, la vista clásica y la propiedad de paso `Player` de `ReproductorViewModel`.
- **Las pruebas siguen corriendo sin motor real.** `MotorFlyleaf.Crear` devuelve `null` bajo el ejecutor de pruebas, como hoy `CreateOptimizedPlayer`; las pruebas nuevas inyectan `MotorDePrueba`.
- **Las pruebas nunca escriben bajo `AppDataPaths`.**
- **Verificación de C#:** skill `repo-build-test` (compilar la app sola con 0 advertencias; pruebas con `--no-restore -p:BuildProjectReferences=false`; la suite completa una vez al final).
- **Verificación en la app:** skill `perfil-aislado`. Nunca más del 85 % de un episodio ni el último de un anime sin completar.
- Mensajes, comentarios y nombres en español, como el código que los rodea. Los números de línea son los del commit `a2eb985`.

## Foco de revisión

Condiciones que el diseño implica y que ninguna prueba de flujo cubre por sí sola; cada una lleva su prueba en la tarea indicada:

1. **No hay motor** (pruebas, o Flyleaf no arranca en el equipo): abrir un episodio no lanza y el progreso guardado se sigue mostrando. → Tarea 4, `SinMotor_AbrirUnEpisodioNoFalla`.
2. **Cambiar de pista de audio** hace que el motor avise de una apertura: no debe tratarse como un episodio nuevo (no vuelve a reproducir ni a elegir subtítulos). → Tarea 5, `CambiarDePistaDeAudio_NoSeTrataComoUnEpisodioNuevo`.
3. **Aviso de apertura que llega tarde**, de un episodio que ya se dejó atrás: no debe marcar como abierto al episodio nuevo antes de tiempo. Hoy no está protegido (el aviso no dice de qué archivo es); se deja documentado con una prueba que fija el comportamiento actual para que la etapa 2 lo vea. → Tarea 4, `UnAvisoDeAperturaDelEpisodioAnterior_SeComportaComoHoy`.
4. **La tarjeta gráfica no da imagen en 3 s**: se reabre una sola vez decodificando por procesador. → Tarea 4, `SiLaTarjetaNoDaImagen_ReabreUnaVezPorProcesador`.
5. **Cerrar el reproductor con la apertura a medias**: el motor se desecha una vez y nada lanza después. → Tarea 4, `CerrarConLaAperturaAMedias_DesechaElMotorSinFallar`.

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
|---|---|---|
| `AnimeLocalTracker/Services/IMotorVideo.cs` | Crear | Contrato del motor y sus tipos |
| `AnimeLocalTracker/Services/MotorFlyleaf.cs` | Crear | Flyleaf detrás del contrato |
| `AnimeLocalTracker/Services/MotorVideo.cs` | Modificar | `FabricanteGrafica` propio en vez de `GPUVendor`; contador de fotogramas como `long` |
| `AnimeLocalTracker/Services/PlaybackVolumeCoordinator.cs` + interfaz | Modificar | Recibe `IMotorVideo` |
| `AnimeLocalTracker/Services/PlaybackSeekCoordinator.cs` + interfaz | Modificar | Recibe `IMotorVideo` |
| `AnimeLocalTracker/Services/SubtitleCoordinator.cs` + interfaz | Modificar | Recibe `IMotorVideo` y `PistaSubtitulosMotor` |
| `AnimeLocalTracker/Services/FrameCaptureService.cs` + interfaz | Modificar | Recibe `IMotorVideo` |
| `AnimeLocalTracker/Services/EpisodeNavigator.cs` + interfaz | Modificar | La lectura adelantada se le pide a quien llama |
| `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs`, `.Audio.cs`, `.Subtitulos.cs` | Modificar | `Motor` en lugar de `Player` |
| `AnimeLocalTracker/Views/MiniReproductorWindow.xaml.cs` | Modificar | Proporción del video desde el motor |
| `AnimeLocalTracker/ViewModels/ConfiguracionViewModel.cs` | Modificar (si compila distinto) | Usa `TarjetaGrafica` con el fabricante propio |
| `AnimeLocalTracker.Tests/ViewModels/MotorDePrueba.cs` | Crear | Motor de mentira |
| `AnimeLocalTracker.Tests/ViewModels/ReproductorConMotorTests.cs` | Crear | El reproductor con el motor de mentira |
| `AnimeLocalTracker.Tests/Services/CoordinadoresConMotorTests.cs` | Crear | Saltos, volumen y subtítulos con el motor de mentira |

---

## Tarea 1: el contrato y el motor de mentira

**Archivos:**
- Crear: `AnimeLocalTracker/Services/IMotorVideo.cs`
- Crear: `AnimeLocalTracker.Tests/ViewModels/MotorDePrueba.cs`
- Modificar: `AnimeLocalTracker/Services/MotorVideo.cs` (registro `TarjetaGrafica`, `UsarEscaladoInteligente`, `EscaladoDisponible`, `DebeReintentarPorProcesador`, `ListarTarjetas`)

**Interfaces:**
- Produce: `IMotorVideo`, `EstadoMotor`, `ClaseApertura`, `AperturaTerminada`, `PistaAudioMotor`, `PistaSubtitulosMotor`, `AjustesInicialesMotor`, `FabricanteGrafica`, y `MotorDePrueba` para todas las tareas siguientes.

- [ ] **Paso 1: crear `Services/IMotorVideo.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;

namespace AnimeLocalTracker.Services;

public enum EstadoMotor { Detenido, Abriendo, Reproduciendo, EnPausa, Terminado, Fallido }

/// <summary>Qué terminó de abrirse: el archivo (o una pista de audio suya) o una pista de subtítulos que dibuja el propio motor.</summary>
public enum ClaseApertura { Video, Subtitulos }

public sealed record AperturaTerminada(ClaseApertura Clase, bool Exito, string? Error);

public enum FabricanteGrafica { Desconocido, Intel, Nvidia, Amd }

/// <summary>Una pista de audio del archivo abierto. <paramref name="Idioma"/> en dos letras, o null si no se sabe.</summary>
public sealed record PistaAudioMotor(int Indice, string? Idioma, string? Titulo);

/// <summary>Una pista de subtítulos. Las de imagen (PGS, VobSub) y las externas solo las sabe mostrar el motor.</summary>
public sealed record PistaSubtitulosMotor(int Indice, string? Idioma, string? Titulo, bool EsImagen, bool EsAss, string? RutaExterna);

/// <summary>Lo que el motor necesita saber antes de abrir el primer archivo.</summary>
public sealed record AjustesInicialesMotor(bool AceleracionPorHardware, string? TarjetaGrafica, IReadOnlyList<FiltroAudio> FiltrosAudio);

/// <summary>
/// El motor de video del reproductor de episodios, sin tipos de ninguna biblioteca. Es lo que el reproductor le pedía a Flyleaf, ni más
/// ni menos: la coreografía (arrancar en silencio hasta el primer fotograma, reanudar, reintentar) sigue en el reproductor.
/// Salvo que se diga otra cosa, los miembros no lanzan: si el motor no puede, no hace nada.
/// </summary>
public interface IMotorVideo : IDisposable
{
    bool Desechado { get; }
    EstadoMotor Estado { get; }
    TimeSpan Posicion { get; }
    TimeSpan Duracion { get; }

    /// <summary>Puede llegar desde un hilo del motor, no desde el de la interfaz.</summary>
    event EventHandler<AperturaTerminada>? AperturaTerminada;

    void Abrir(string ruta);
    void Reproducir();
    void Pausar();
    void Detener();
    void Saltar(TimeSpan destino, bool exacto);
    double Velocidad { set; }
    int Volumen { set; }
    bool Silencio { set; }

    /// <summary>Fotogramas presentados desde que se abrió el archivo, reproduciendo o en pausa.</summary>
    long FotogramasMostrados { get; }
    bool DecodificaPorHardware { get; }
    bool AceleracionPorHardware { set; }
    string? CodecVideo { get; }
    (int Ancho, int Alto) TamanoVideo { get; }
    /// <summary>Ancho entre alto del video abierto; 0 si aún no se sabe.</summary>
    double ProporcionVideo { get; }
    event EventHandler? ProporcionVideoCambiada;
    TarjetaGrafica? TarjetaEnUso { get; }
    bool EscaladoInteligente { get; set; }
    BitmapSource? CapturarFotograma();

    IReadOnlyList<PistaAudioMotor> PistasAudio { get; }
    int IndicePistaAudioActual { get; }
    void ElegirAudio(PistaAudioMotor pista);

    IReadOnlyList<PistaSubtitulosMotor> PistasSubtitulos { get; }
    /// <summary>Índice de la pista que el propio motor tiene abierta, o -1.</summary>
    int IndicePistaSubtitulosDelMotor { get; }
    /// <summary>La pista que corresponde a un objeto propio del motor (lo que entrega la vista clásica), o null.</summary>
    PistaSubtitulosMotor? PistaSubtitulosDe(object nativa);
    void MostrarSubtitulosDelMotor(PistaSubtitulosMotor pista);
    void ApagarSubtitulosDelMotor();

    /// <returns>False si el motor no pudo montar la cadena.</returns>
    bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros);
    /// <returns>False si ese filtro no admite el cambio sin remontar la cadena.</returns>
    bool CambiarFiltroEnVivo(string id, string clave, string valor);

    /// <summary>Adelanta la lectura de un archivo que se abrirá después. Sin garantías: si falla no pasa nada.</summary>
    void PrecalentarArchivo(string ruta, CancellationToken ct);
}
```

- [ ] **Paso 2: `MotorVideo.cs`, fabricante propio y contador largo**

  1. `public sealed record TarjetaGrafica(string Nombre, GPUVendor Fabricante, long MemoriaMb);` → `public sealed record TarjetaGrafica(string Nombre, FabricanteGrafica Fabricante, long MemoriaMb);`
  2. Añadir en `MotorVideo`:

```csharp
    internal static FabricanteGrafica FabricanteDe(GPUVendor fabricante) => fabricante switch
    {
        GPUVendor.Intel => FabricanteGrafica.Intel,
        GPUVendor.Nvidia => FabricanteGrafica.Nvidia,
        GPUVendor.ATI => FabricanteGrafica.Amd,
        _ => FabricanteGrafica.Desconocido
    };
```

     (Si `GPUVendor` no tiene `ATI`, mirar sus valores con "ir a definición" y usar el de AMD; los demás van a `Desconocido`.)
  3. En `ListarTarjetas`: `new TarjetaGrafica(g.Description.Trim(), g.Vendor, …)` → `new TarjetaGrafica(g.Description.Trim(), FabricanteDe(g.Vendor), …)`.
  4. `UsarEscaladoInteligente(string? modo, GPUVendor fabricante, long memoriaMb)` → parámetro `FabricanteGrafica fabricante`; la última rama queda `_ => fabricante is FabricanteGrafica.Nvidia or FabricanteGrafica.Intel && memoriaMb >= 2048`.
  5. `EscaladoDisponible(GPUVendor fabricante)` → `EscaladoDisponible(FabricanteGrafica fabricante) => fabricante is FabricanteGrafica.Nvidia or FabricanteGrafica.Intel;`
  6. `DebeReintentarPorProcesador(bool porHardware, uint fotogramas, bool yaReintentado)` → `long fotogramas`.
  7. Compilar; arreglar los llamadores que no compilen (pruebas de `MotorVideo` que pasen `GPUVendor.X` → `FabricanteGrafica.X`; `(uint)0` → `0`). `ConfiguracionViewModel` solo usa `Nombre` y `MemoriaMb`: no debería cambiar.

- [ ] **Paso 3: crear `AnimeLocalTracker.Tests/ViewModels/MotorDePrueba.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Motor de mentira: la prueba decide el estado y dispara los avisos; el motor apunta lo que se le pidió. Se comporta como Flyleaf
/// en lo que el reproductor da por hecho: abrir deja el estado en "abriendo", reproducir en "reproduciendo", saltar mueve la posición
/// y pinta fotogramas nuevos.
/// </summary>
public sealed class MotorDePrueba : IMotorVideo
{
    public List<string> Abiertos { get; } = [];
    public List<(double Segundos, bool Exacto)> Saltos { get; } = [];
    public List<string> Precalentados { get; } = [];
    public int VecesDetenido, VecesDesechado, VecesReproducir, VecesPausar;
    public PistaAudioMotor? AudioElegido;
    public PistaSubtitulosMotor? SubtitulosDelMotor;
    public int VecesSubtitulosApagados;
    public IReadOnlyList<FiltroAudio> Filtros = [];
    public List<(string Id, string Clave, string Valor)> CambiosEnVivo { get; } = [];
    public bool AdmiteCambiosEnVivo = true;

    public bool Desechado { get; private set; }
    public EstadoMotor Estado { get; set; } = EstadoMotor.Detenido;
    public TimeSpan Posicion { get; set; }
    public TimeSpan Duracion { get; set; }
    public event EventHandler<AperturaTerminada>? AperturaTerminada;

    public double VelocidadPuesta = 1;
    public int VolumenPuesto = -1;
    public bool SilencioPuesto;
    public bool AceleracionPuesta = true;
    public double Velocidad { set => VelocidadPuesta = value; }
    public int Volumen { set => VolumenPuesto = value; }
    public bool Silencio { set => SilencioPuesto = value; }
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
    public BitmapSource? Captura;
    public BitmapSource? CapturarFotograma() => Captura;

    public List<PistaAudioMotor> Audio { get; } = [];
    public List<PistaSubtitulosMotor> Subtitulos { get; } = [];
    public IReadOnlyList<PistaAudioMotor> PistasAudio => Audio;
    public int IndicePistaAudioActual { get; set; } = -1;
    public IReadOnlyList<PistaSubtitulosMotor> PistasSubtitulos => Subtitulos;
    public int IndicePistaSubtitulosDelMotor => SubtitulosDelMotor?.Indice ?? -1;
    public PistaSubtitulosMotor? PistaSubtitulosDe(object nativa) => nativa as PistaSubtitulosMotor;

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
    public bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros) { Filtros = filtros; return true; }
    public bool CambiarFiltroEnVivo(string id, string clave, string valor) { CambiosEnVivo.Add((id, clave, valor)); return AdmiteCambiosEnVivo; }
    public void PrecalentarArchivo(string ruta, CancellationToken ct) => Precalentados.Add(ruta);
    public void Dispose() { VecesDesechado++; Desechado = true; }

    /// <summary>El aviso que da el motor al terminar de abrir el archivo (o una pista de audio suya).</summary>
    public void TerminarApertura(bool exito = true, string? error = null, ClaseApertura clase = ClaseApertura.Video) =>
        AperturaTerminada?.Invoke(this, new AperturaTerminada(clase, exito, error));

    public void CambiarProporcion(double proporcion) { ProporcionVideo = proporcion; ProporcionVideoCambiada?.Invoke(this, EventArgs.Empty); }
}
```

- [ ] **Paso 4: verificación**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false -nodeReuse:false --filter "FullyQualifiedName~MotorVideo|FullyQualifiedName~Configuracion"
```

Esperado: 0 advertencias; todo en verde. `IMotorVideo` aún no lo usa nadie.

---

## Tarea 2: `MotorFlyleaf`

**Archivos:**
- Crear: `AnimeLocalTracker/Services/MotorFlyleaf.cs`

**Interfaces:**
- Consume: `IMotorVideo` y sus tipos (tarea 1); `MotorVideo.AsegurarIniciado`, `MotorVideo.PatronTarjeta`, `MotorVideo.FabricanteDe`, `ParcheSubtitulosFlyleaf.Aplicar`, `ReproductorViewModel.EsEntornoPruebas`.
- Produce: `MotorFlyleaf.Crear(AjustesInicialesMotor) : MotorFlyleaf?` y `MotorFlyleaf.Player` (para la vista clásica).

No se puede instanciar bajo el ejecutor de pruebas (Flyleaf se cuelga sin ventana): se verifica al compilar y en la app (tarea 8).

- [ ] **Paso 1: crear el archivo**

El cuerpo de `Crear` es el de `ReproductorViewModel.CreateOptimizedPlayer` (líneas 524–589) movido tal cual, con los ajustes que antes leía del reproductor recibidos por parámetro. El contador de fotogramas es el de las líneas 1644–1660; la lectura adelantada, la de `EpisodeNavigator.PrecargarSiguienteAsync` (líneas 107–137).

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;
using FlyleafLib;
using FlyleafLib.MediaFramework.MediaDemuxer;
using FlyleafLib.MediaFramework.MediaStream;
using FlyleafLib.MediaPlayer;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Flyleaf detrás de <see cref="IMotorVideo"/>. Aquí vive todo lo que solo tiene sentido con Flyleaf: su configuración, el parche de
/// su lector de subtítulos, sus contadores privados de fotogramas y la lectura adelantada con un demuxer aparte.
/// </summary>
public sealed class MotorFlyleaf : IMotorVideo
{
    /// <summary>El reproductor de Flyleaf, para la vista clásica (su anfitrión de video y el menú de subtítulos se enlazan a él).</summary>
    public Player Player { get; }

    private INotifyPropertyChanged? _videoObservado;

    private MotorFlyleaf(Player player)
    {
        Player = player;
        player.OpenCompleted += (_, e) => AperturaTerminada?.Invoke(this,
            new AperturaTerminada(e.IsSubtitles ? ClaseApertura.Subtitulos : ClaseApertura.Video, e.Success, e.Error));
        _videoObservado = player.Video;
        if (_videoObservado != null) _videoObservado.PropertyChanged += Video_PropertyChanged;
    }

    /// <summary>Null bajo el ejecutor de pruebas (Flyleaf puede colgarse sin ventana) o si el motor no arranca en este equipo.</summary>
    public static MotorFlyleaf? Crear(AjustesInicialesMotor ajustes)
    {
        if (ViewModels.ReproductorViewModel.EsEntornoPruebas()) return null;

        try
        {
            MotorVideo.AsegurarIniciado();

            var config = new Config();

            // 1. Seeking rápido instantáneo por Keyframe (no frame-accurate) -> 0 ms seek latency
            if (config.Player != null)
            {
                config.Player.SeekAccurate = false;
                config.Player.AutoPlay = true;

                // El hilo de reproducción es el que alimenta el audio: con la ventana sin foco y
                // otras apps compitiendo por CPU, en prioridad Normal el sonido se entrecorta.
                config.Player.ThreadPriority = ThreadPriority.Highest;
                // Contador de fotogramas mostrados: con él se sabe cuándo aparece la imagen de verdad.
                config.Player.Stats = true;
            }

            // 2. Decoder multi-hilos para decodificación suave de AV1 y HEVC 10-bit
            if (config.Decoder != null)
            {
                // Todos los hilos: el AV1 va por software en gráficas sin soporte (Intel HD 620) y un salto preciso decodifica hasta
                // ~10 s de video; con 4 hilos en vez de 2 se decodifica un ~15 % más rápido (medido con ffmpeg: 231 → 267 fps).
                config.Decoder.VideoThreads = Math.Max(2, Environment.ProcessorCount);
            }

            // 2b. Decodificación por GPU (Direct3D) explícita: si falla, FlyleafLib cae solo a software.
            if (config.Video != null)
            {
                config.Video.VideoAcceleration = ajustes.AceleracionPorHardware;

                // Tarjeta gráfica elegida en Configuración (portátiles con integrada + dedicada: Windows suele dar la integrada).
                string? patronTarjeta = MotorVideo.PatronTarjeta(ajustes.TarjetaGrafica);
                if (patronTarjeta != null) config.Video.GPUAdapter = patronTarjeta;
            }

            // 3. Buffer de Demuxer en RAM (30 segundos precargados en memoria para reproducción sin tirones)
            if (config.Demuxer != null)
            {
                config.Demuxer.BufferDuration = 300_000_000L;
            }

            // 4. Subtítulos: Flyleaf arranca siempre con los suyos apagados para que no abra (ni lea) ninguna pista por su cuenta;
            // la app elige la pista y la lee ella misma.
            if (config.Subtitles != null)
            {
                config.Subtitles.Enabled = false;
            }

            // 5. Filtros de audio guardados: ecualizador y Modo Noche
            if (config.Audio != null && ajustes.FiltrosAudio.Count > 0)
            {
                config.Audio.Filters = AFiltrosDeFlyleaf(ajustes.FiltrosAudio);
            }

            // Red de seguridad para cuando Flyleaf sí decodifica una pista de texto: su lector de estilos ASS cerraba la app.
            ParcheSubtitulosFlyleaf.Aplicar();

            return new MotorFlyleaf(new Player(config));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MotorFlyleaf", $"Player nativo no disponible en este entorno: {ex.Message}");
            return null;
        }
    }

    public event EventHandler<AperturaTerminada>? AperturaTerminada;
    public event EventHandler? ProporcionVideoCambiada;

    public bool Desechado => Player.IsDisposed;

    public EstadoMotor Estado => Player.Status switch
    {
        Status.Opening => EstadoMotor.Abriendo,
        Status.Playing => EstadoMotor.Reproduciendo,
        Status.Paused => EstadoMotor.EnPausa,
        Status.Ended => EstadoMotor.Terminado,
        Status.Failed => EstadoMotor.Fallido,
        _ => EstadoMotor.Detenido
    };

    public TimeSpan Posicion => TimeSpan.FromTicks(Player.CurTime);
    public TimeSpan Duracion => TimeSpan.FromTicks(Player.Duration);

    public void Abrir(string ruta) => Player.OpenAsync(ruta);
    public void Reproducir() => Player.Play();
    public void Pausar() => Player.Pause();
    public void Detener() => Player.Stop();

    public void Saltar(TimeSpan destino, bool exacto)
    {
        if (exacto) Player.SeekAccurate((int)Math.Round(destino.TotalMilliseconds));
        else Player.CurTime = destino.Ticks;
    }

    public double Velocidad { set => Player.Speed = (float)value; }
    public int Volumen { set { if (Player.Audio != null) Player.Audio.Volume = value; } }
    public bool Silencio { set { if (Player.Audio != null) Player.Audio.Mute = value; } }

    // Contadores internos de Flyleaf que suben con CADA fotograma presentado (reproduciendo y en pausa/salto). El público
    // Video.FramesDisplayed solo se refresca una vez por segundo: esperar a él retrasaba hasta 1 s el destape del video.
    private static readonly System.Reflection.FieldInfo? CampoFotogramasReproducidos =
        typeof(Player).GetField("framesDisplayed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? CampoFotogramasMostradosEnPausa =
        typeof(Player).GetField("showFrameCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    public long FotogramasMostrados
    {
        get
        {
            try
            {
                if (CampoFotogramasReproducidos != null && CampoFotogramasMostradosEnPausa != null)
                    return (uint)CampoFotogramasReproducidos.GetValue(Player)! + (long)(uint)CampoFotogramasMostradosEnPausa.GetValue(Player)!;
                return Player.Video?.FramesDisplayed ?? 0; // si una versión nueva de Flyleaf cambia los nombres: el contador público
            }
            catch { return 0; }
        }
    }

    public bool DecodificaPorHardware => Player.VideoDecoder?.VideoAccelerated ?? false;
    public bool AceleracionPorHardware { set => Player.Config.Video.VideoAcceleration = value; }
    public string? CodecVideo => Player.Video?.Codec;

    /// <summary>Justo al abrir, Player.Video aún vale 0x0 (se rellena con el primer fotograma): entonces se toma de la pista de video
    /// del contenedor, que ya está leída.</summary>
    public (int Ancho, int Alto) TamanoVideo
    {
        get
        {
            int ancho = Player.Video?.Width ?? 0, alto = Player.Video?.Height ?? 0;
            if (ancho > 0 && alto > 0) return (ancho, alto);

            var pista = Player.VideoDemuxer?.VideoStream ?? Player.VideoDemuxer?.VideoStreams?.FirstOrDefault();
            return pista == null ? (0, 0) : ((int)pista.Width, (int)pista.Height);
        }
    }

    public double ProporcionVideo => Player.Video?.AspectRatio.Value ?? 0;

    private void Video_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Video.AspectRatio)) ProporcionVideoCambiada?.Invoke(this, EventArgs.Empty);
    }

    public TarjetaGrafica? TarjetaEnUso
    {
        get
        {
            var tarjeta = Player.Renderer?.GPUAdapter;
            return tarjeta == null
                ? null
                : new TarjetaGrafica(tarjeta.Description, MotorVideo.FabricanteDe(tarjeta.Vendor), (long)((ulong)tarjeta.VideoMemory / (1024 * 1024)));
        }
    }

    public bool EscaladoInteligente
    {
        get => Player.Config.Video.SuperResolution;
        set { if (Player.Config.Video.SuperResolution != value) Player.Config.Video.SuperResolution = value; }
    }

    public BitmapSource? CapturarFotograma() => Player.TakeSnapshotToBitmapSource(0, 0);

    public IReadOnlyList<PistaAudioMotor> PistasAudio =>
        (Player.Audio?.Streams?.ToList() ?? []).Select(s => new PistaAudioMotor(s.StreamIndex, IdiomaDosLetras(s), s.Title)).ToList();

    public int IndicePistaAudioActual => Player.Audio?.StreamIndex ?? -1;

    public void ElegirAudio(PistaAudioMotor pista)
    {
        var nativa = Player.Audio?.Streams?.FirstOrDefault(s => s.StreamIndex == pista.Indice);
        if (nativa != null) Player.OpenAsync(nativa);
    }

    public IReadOnlyList<PistaSubtitulosMotor> PistasSubtitulos =>
        (Player.Subtitles?.Streams?.ToList() ?? []).Select(APista).ToList();

    public int IndicePistaSubtitulosDelMotor => Player.Subtitles?.StreamIndex ?? -1;

    public PistaSubtitulosMotor? PistaSubtitulosDe(object nativa) => nativa is SubtitlesStream s ? APista(s) : null;

    public void MostrarSubtitulosDelMotor(PistaSubtitulosMotor pista)
    {
        var nativa = Player.Subtitles?.Streams?.FirstOrDefault(s => s.StreamIndex == pista.Indice && s.ExternalStream?.Url == pista.RutaExterna);
        if (nativa == null) return;
        if (Player.Config?.Subtitles != null) Player.Config.Subtitles.Enabled = true;
        Player.OpenAsync(nativa);
    }

    public void ApagarSubtitulosDelMotor()
    {
        if (Player.Config?.Subtitles != null) Player.Config.Subtitles.Enabled = false;
    }

    private static PistaSubtitulosMotor APista(SubtitlesStream s) => new(
        s.StreamIndex, IdiomaDosLetras(s), s.Title, s.IsBitmap,
        s.CodecID is Flyleaf.FFmpeg.AVCodecID.Ass or Flyleaf.FFmpeg.AVCodecID.Ssa, s.ExternalStream?.Url);

    private static string? IdiomaDosLetras(StreamBase s)
    {
        try
        {
            string? idioma = s.Language?.TopCulture?.TwoLetterISOLanguageName;
            return idioma is "iv" ? null : idioma; // cultura invariante = sin idioma
        }
        catch { return null; }
    }

    private static List<Filter> AFiltrosDeFlyleaf(IReadOnlyList<FiltroAudio> filtros) =>
        filtros.Select(f => new Filter { Id = f.Id, Name = f.Nombre, Args = f.Argumentos }).ToList();

    public bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros)
    {
        if (Player.Config?.Audio == null) return true;
        Player.Config.Audio.Filters = AFiltrosDeFlyleaf(filtros);
        return Player.Config.Audio.ReloadFilters() >= 0;
    }

    public bool CambiarFiltroEnVivo(string id, string clave, string valor) =>
        Player.Config?.Audio == null || Player.Config.Audio.UpdateFilter(id, clave, valor) >= 0;

    /// <summary>
    /// Solo de Flyleaf (no está en el contrato): deja apuntada la cadena sin recargarla. El cambio en vivo solo toca el filtro que
    /// está sonando; esta lista es la que Flyleaf monta al abrir el siguiente episodio, y sin ponerla al día volvían las ganancias anteriores.
    /// </summary>
    public void RecordarFiltrosParaElSiguienteArchivo(IReadOnlyList<FiltroAudio> filtros)
    {
        if (Player.Config?.Audio != null) Player.Config.Audio.Filters = AFiltrosDeFlyleaf(filtros);
    }

    /// <summary>
    /// Abre un <see cref="Demuxer"/> APARTE del Player en reproducción contra el archivo y lo descarta enseguida: adelanta el sondeo del
    /// contenedor y calienta la caché de E/S de Windows para ese archivo. Nunca toca el Player, y cualquier fallo se descarta.
    /// </summary>
    public void PrecalentarArchivo(string ruta, CancellationToken ct) => _ = Task.Run(() =>
    {
        try
        {
            if (ct.IsCancellationRequested) return;

            var demuxer = new Demuxer(new Config().Demuxer, MediaType.Video);
            try
            {
                string error = demuxer.Open(ruta);
                if (!string.IsNullOrEmpty(error))
                    AppLogger.Debug("MotorFlyleaf", $"Precarga del siguiente episodio no pudo abrir '{ruta}': {error}");
            }
            finally
            {
                demuxer.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("MotorFlyleaf", $"Precarga del siguiente episodio falló (no afecta la reproducción actual): {ex.Message}");
        }
    }, ct);

    public void Dispose()
    {
        if (_videoObservado != null) _videoObservado.PropertyChanged -= Video_PropertyChanged;
        _videoObservado = null;
        Player.Dispose();
    }
}
```

Notas para compilar:
- Si `StreamBase` no es la clase común de `AudioStream` y `SubtitlesStream`, escribir dos sobrecargas de `IdiomaDosLetras`.
- El alias del paquete `Flyleaf.FFmpeg` (`FFmpegExtendido`) no afecta a `Flyleaf.FFmpeg.AVCodecID`: `ReproductorViewModel.Subtitulos.cs` ya lo usa así (línea 72).
- El hook `aviso_reglas.py` no tiene nada que avisar aquí (no hay `DllImport`, `async void` ni `ProcessStartInfo`).

- [ ] **Paso 2: verificación**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false
```

Esperado: 0 errores, 0 advertencias. Nadie usa `MotorFlyleaf` todavía.

---

## Tarea 3: los coordinadores y el navegador pasan al contrato

**Archivos:**
- Modificar: `Services/IPlaybackVolumeCoordinator.cs`, `PlaybackVolumeCoordinator.cs`, `IPlaybackSeekCoordinator.cs`, `PlaybackSeekCoordinator.cs`, `ISubtitleCoordinator.cs`, `SubtitleCoordinator.cs`, `IFrameCaptureService.cs`, `FrameCaptureService.cs`, `IEpisodeNavigator.cs`, `EpisodeNavigator.cs`
- Modificar (solo para que compile, sin cambiar lógica): los puntos de `ReproductorViewModel*.cs` que llaman a esos métodos pasan `Motor` (la propiedad se crea en la tarea 4; en esta tarea se hace el cambio de firmas y la tarea 4 termina de compilar). **Las tareas 3 y 4 se compilan juntas**: la verificación de la 3 son sus pruebas, al final de la 4.
- Crear: `AnimeLocalTracker.Tests/Services/CoordinadoresConMotorTests.cs`

**Interfaces:**
- Consume: `IMotorVideo`, `PistaSubtitulosMotor`, `MotorDePrueba`.
- Produce (firmas nuevas, las usa la tarea 4):
  - `IPlaybackVolumeCoordinator`: `bool AplicarVolumen(IMotorVideo? motor, int volumen, bool estabaMuteado)`, `void Desmutear(IMotorVideo? motor, int volumenActual)`, `void Mutear(IMotorVideo? motor)`
  - `IPlaybackSeekCoordinator`: `void SolicitarSeek(IMotorVideo? motor, double segundos, Func<bool> haCompletadoOpen, bool preciso = false)`
  - `ISubtitleCoordinator`: `bool DebenHabilitarsePorDefecto(IMotorVideo? motor, bool permitirSubtitulosConfig)`, `void Deshabilitar(IMotorVideo? motor)`, `void SeleccionarPista(IMotorVideo? motor, PistaSubtitulosMotor pista)`, `PistaSubtitulosMotor? PistaPorDefecto(IMotorVideo? motor, string idiomaPreferido)`. `Habilitar` desaparece: solo lo usaba `SeleccionarPista`.
  - `IFrameCaptureService`: `Task<string?> CapturarYGuardarAsync(IMotorVideo? motor)`
  - `IEpisodeNavigator`: `void ConsiderarPrecarga(double porcentaje, string? rutaSiguiente, Action<string, CancellationToken> precalentar)`

- [ ] **Paso 1: escribir las pruebas (fallarán al compilar)**

`AnimeLocalTracker.Tests/Services/CoordinadoresConMotorTests.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Tests.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Los coordinadores del reproductor contra el contrato del motor (antes recibían el Player de Flyleaf y no se podían probar).</summary>
public class CoordinadoresConMotorTests
{
    private static async Task EsperarHasta(Func<bool> condicion, int ms = 5000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue($"la condición debía cumplirse en {ms} ms");
    }

    [Fact]
    public void Salto_ConElVideoAbierto_SeAplicaEnElMomento()
    {
        var motor = new MotorDePrueba { Estado = EstadoMotor.Reproduciendo };
        using var saltos = new PlaybackSeekCoordinator();

        saltos.SolicitarSeek(motor, 120, () => true, preciso: true);

        motor.Saltos.Should().Equal((120d, true));
    }

    [Theory]
    [InlineData(EstadoMotor.Abriendo, true)]
    [InlineData(EstadoMotor.Detenido, true)]
    [InlineData(EstadoMotor.Reproduciendo, false)] // aún no terminó de abrir, aunque el estado diga otra cosa
    public void Salto_MientrasElVideoSeAbre_SeGuardaParaCuandoAbra(EstadoMotor estado, bool aperturaCompleta)
    {
        var motor = new MotorDePrueba { Estado = estado };
        using var saltos = new PlaybackSeekCoordinator();

        saltos.SolicitarSeek(motor, 300, () => aperturaCompleta);

        motor.Saltos.Should().BeEmpty("saltar mientras se crea el decodificador dejaba la pantalla en negro");
        saltos.ConsumirSeekPendienteAlAbrir().Should().Be(300);
        saltos.ConsumirSeekPendienteAlAbrir().Should().BeNull("se consume una vez");
    }

    [Fact]
    public async Task VariosSaltosSeguidos_AplicaElPrimeroYLuegoSoloElUltimo()
    {
        var motor = new MotorDePrueba { Estado = EstadoMotor.Reproduciendo };
        using var saltos = new PlaybackSeekCoordinator();

        saltos.SolicitarSeek(motor, 10, () => true);
        saltos.SolicitarSeek(motor, 20, () => true);
        saltos.SolicitarSeek(motor, 30, () => true);

        await EsperarHasta(() => motor.Saltos.Count == 2);
        motor.Saltos.Should().Equal((10d, false), (30d, false));
    }

    [Fact]
    public void Salto_SinMotorOConElMotorDesechado_NoHaceNada()
    {
        var motor = new MotorDePrueba { Estado = EstadoMotor.Reproduciendo };
        motor.Dispose();
        using var saltos = new PlaybackSeekCoordinator();

        saltos.SolicitarSeek(null, 10, () => true);
        saltos.SolicitarSeek(motor, 10, () => true);

        motor.Saltos.Should().BeEmpty();
    }

    [Fact]
    public void Volumen_AlSubirloEstandoEnSilencio_QuitaElSilencio()
    {
        var motor = new MotorDePrueba { SilencioPuesto = true };
        var volumen = new PlaybackVolumeCoordinator();

        volumen.AplicarVolumen(motor, 40, estabaMuteado: true).Should().BeTrue();
        motor.VolumenPuesto.Should().Be(40);
        motor.SilencioPuesto.Should().BeFalse();

        volumen.AplicarVolumen(motor, 0, estabaMuteado: true).Should().BeFalse("bajar a cero no quita el silencio");
        volumen.AplicarVolumen(null, 40, estabaMuteado: true).Should().BeFalse();
    }

    [Fact]
    public void Volumen_SilenciarYQuitarElSilencio()
    {
        var motor = new MotorDePrueba();
        var volumen = new PlaybackVolumeCoordinator();

        volumen.Mutear(motor);
        motor.SilencioPuesto.Should().BeTrue();

        volumen.Desmutear(motor, 70);
        motor.SilencioPuesto.Should().BeFalse();
        motor.VolumenPuesto.Should().Be(70);
    }

    [Fact]
    public void Subtitulos_PistaPorDefecto_PrefiereElIdiomaYNoEligeSiElMotorYaTieneUna()
    {
        var motor = new MotorDePrueba();
        motor.Subtitulos.Add(new PistaSubtitulosMotor(2, "en", null, false, true, null));
        motor.Subtitulos.Add(new PistaSubtitulosMotor(3, "es", "Signs & Songs", false, true, null));
        motor.Subtitulos.Add(new PistaSubtitulosMotor(4, "es", null, false, true, null));
        var subtitulos = new SubtitleCoordinator();

        subtitulos.DebenHabilitarsePorDefecto(motor, true).Should().BeTrue();
        subtitulos.DebenHabilitarsePorDefecto(motor, false).Should().BeFalse();
        subtitulos.DebenHabilitarsePorDefecto(new MotorDePrueba(), true).Should().BeFalse("sin pistas no hay nada que encender");
        subtitulos.PistaPorDefecto(motor, "es")!.Indice.Should().Be(4, "la de diálogo, no la de solo carteles");

        subtitulos.SeleccionarPista(motor, motor.Subtitulos[0]);
        motor.SubtitulosDelMotor.Should().Be(motor.Subtitulos[0]);
        subtitulos.PistaPorDefecto(motor, "es").Should().BeNull("el motor ya tiene una pista abierta");

        subtitulos.Deshabilitar(motor);
        motor.SubtitulosDelMotor.Should().BeNull();
    }

    [Fact]
    public void LecturaAdelantada_SePideUnaVezPorEpisodioYSoloCercaDelFinal()
    {
        using var navegador = new EpisodeNavigator();
        int pedidas = 0;
        void Precalentar(string ruta, CancellationToken ct) => pedidas++;

        navegador.ConsiderarPrecarga(0.50, "C:\\Anime\\Ep02.mkv", Precalentar);
        pedidas.Should().Be(0);
        navegador.ConsiderarPrecarga(0.96, "C:\\Anime\\Ep02.mkv", Precalentar);
        navegador.ConsiderarPrecarga(0.97, "C:\\Anime\\Ep02.mkv", Precalentar);
        pedidas.Should().Be(1);

        navegador.CancelarPrecargaYReiniciar();
        navegador.ConsiderarPrecarga(0.96, "C:\\Anime\\Ep03.mkv", Precalentar);
        pedidas.Should().Be(2);
    }
}
```

- [ ] **Paso 2: `PlaybackVolumeCoordinator`**

Quitar `using FlyleafLib…`. Cuerpos nuevos (los `try` y los mensajes se conservan):

```csharp
    public bool AplicarVolumen(IMotorVideo? motor, int volumen, bool estabaMuteado)
    {
        if (motor == null) return false;
        try
        {
            motor.Volumen = volumen;
            if (volumen > 0 && estabaMuteado)
            {
                motor.Silencio = false;
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("PlaybackVolumeCoordinator", $"Error asignando volumen: {ex.Message}");
        }
        return false;
    }

    public void Desmutear(IMotorVideo? motor, int volumenActual)
    {
        if (motor == null) return;
        try
        {
            motor.Silencio = false;
            motor.Volumen = volumenActual;
        }
        catch { }
    }

    public void Mutear(IMotorVideo? motor)
    {
        if (motor == null) return;
        try { motor.Silencio = true; }
        catch { }
    }
```

La interfaz, con las mismas tres firmas. (`player?.Audio == null` pasa a ser asunto del motor: `MotorFlyleaf` no hace nada si no hay audio.)

- [ ] **Paso 3: `PlaybackSeekCoordinator`**

Sustituciones, sin tocar nada más:

| Antes | Después |
|---|---|
| `SolicitarSeek(Player? player, …)` | `SolicitarSeek(IMotorVideo? motor, …)` |
| `player == null \|\| player.IsDisposed` | `motor == null \|\| motor.Desechado` |
| `AplicarSeekNativo(Player player, …)` y sus dos llamadas | `AplicarSeekNativo(IMotorVideo motor, …)` |
| `player.IsDisposed` | `motor.Desechado` |
| `player.Status == Status.Opening \|\| player.Status == Status.Stopped` | `motor.Estado is EstadoMotor.Abriendo or EstadoMotor.Detenido` |
| `if (preciso) player.SeekAccurate((int)Math.Round(segundos * 1000)); else player.CurTime = TimeSpan.FromSeconds(segundos).Ticks;` | `motor.Saltar(TimeSpan.FromSeconds(segundos), preciso);` |

- [ ] **Paso 4: `SubtitleCoordinator`**

```csharp
    public bool DebenHabilitarsePorDefecto(IMotorVideo? motor, bool permitirSubtitulosConfig) =>
        permitirSubtitulosConfig && motor != null && motor.PistasSubtitulos.Count > 0;

    public void Deshabilitar(IMotorVideo? motor) => motor?.ApagarSubtitulosDelMotor();

    public PistaSubtitulosMotor? PistaPorDefecto(IMotorVideo? motor, string idiomaPreferido)
    {
        if (motor == null || motor.IndicePistaSubtitulosDelMotor >= 0) return null;
        var pistas = motor.PistasSubtitulos;
        int i = ElegirPista(pistas.Select(s => new PistaSubtitulos(s.EsImagen, s.Idioma, s.Titulo)).ToList(), idiomaPreferido);
        return i >= 0 ? pistas[i] : null;
    }

    public void SeleccionarPista(IMotorVideo? motor, PistaSubtitulosMotor pista) => motor?.MostrarSubtitulosDelMotor(pista);
```

Se borran `Habilitar` y `IdiomaDosLetras` (el idioma ya viene en dos letras del motor). `ElegirPista`, `PistaSubtitulos`, `EsSoloCarteles` y sus pruebas no cambian.

- [ ] **Paso 5: `FrameCaptureService`**

`CapturarYGuardarAsync(Player? player)` → `CapturarYGuardarAsync(IMotorVideo? motor)`; `if (player == null) return null;` → `if (motor == null) return null;`; `var bitmap = player.TakeSnapshotToBitmapSource(0, 0);` → `var bitmap = motor.CapturarFotograma();`. El resto igual.

- [ ] **Paso 6: `EpisodeNavigator`**

```csharp
    public void ConsiderarPrecarga(double porcentaje, string? rutaSiguiente, Action<string, CancellationToken> precalentar)
    {
        if (!DebePrecargarSiguienteEpisodio(porcentaje, rutaSiguiente, _rutaPrecargada)) return;

        _rutaPrecargada = rutaSiguiente;
        precalentar(rutaSiguiente!, _precargaCts.Token);
    }
```

Se borra `PrecargarSiguienteAsync` (vive ahora en `MotorFlyleaf.PrecalentarArchivo`) y los `using FlyleafLib…`. Actualizar la firma en `IEpisodeNavigator` y su comentario. Si alguna prueba simula `ConsiderarPrecarga` con Moq, añadir `It.IsAny<Action<string, CancellationToken>>()`.

---

## Tarea 4: el reproductor habla con `Motor` (apertura, transporte y arranque)

**Archivos:**
- Modificar: `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs`
- Crear: `AnimeLocalTracker.Tests/ViewModels/ReproductorConMotorTests.cs`

**Interfaces:**
- Consume: todo lo anterior.
- Produce: `ReproductorViewModel.Motor` (`IMotorVideo?`), el parámetro de constructor `Func<AjustesInicialesMotor, IMotorVideo?>? fabricaMotor`, y `ReproductorViewModel.Player` como propiedad de paso para la vista clásica.

- [ ] **Paso 1: escribir las pruebas (fallarán al compilar)**

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// El reproductor con un motor de mentira. Antes de la costura estas rutas (arranque, reanudación, reintentos) solo se podían ver en
/// la app: bajo pruebas no había motor.
/// </summary>
public class ReproductorConMotorTests : IDisposable
{
    private readonly Mock<IPlaybackStateService> _estado = new();
    private readonly MotorDePrueba _motor = new() { Duracion = TimeSpan.FromSeconds(1420) };
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_motor_" + Guid.NewGuid().ToString("N"));
    private readonly string _ruta;

    public ReproductorConMotorTests()
    {
        Directory.CreateDirectory(_carpeta);
        _ruta = Path.Combine(_carpeta, "Ep05.mkv");
        File.WriteAllText(_ruta, "no es un video: solo tiene que existir");
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    private ReproductorViewModel CrearSut(IMotorVideo? motor) => new(
        Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
        playbackStateService: _estado.Object, fabricaMotor: _ => motor);

    private static async Task EsperarHasta(Func<bool> condicion, int ms = 8000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue($"la condición debía cumplirse en {ms} ms");
    }

    [Fact]
    public async Task AlAbrirUnEpisodio_ArrancaEnSilencioYSeDestapaConElPrimerFotograma()
    {
        using var sut = CrearSut(_motor);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        _motor.Abiertos.Should().Equal(_ruta);
        _motor.SilencioPuesto.Should().BeTrue("el audio arrancaba antes que la imagen");
        sut.OcultarVideoInicio.Should().BeTrue();

        _motor.TerminarApertura();

        _motor.Estado.Should().Be(EstadoMotor.Reproduciendo);
        sut.PlayPauseIcon.Should().Be("Pause");
        _motor.SilencioPuesto.Should().BeTrue("sigue en silencio hasta que se vea la imagen");

        _motor.FotogramasMostrados = 3;

        await EsperarHasta(() => !sut.OcultarVideoInicio);
        await EsperarHasta(() => !_motor.SilencioPuesto);
        _motor.Saltos.Should().BeEmpty("sin punto guardado no hay salto de reanudación");
    }

    [Fact]
    public async Task ConPuntoGuardado_SaltaAhiAntesDeDestaparElVideo()
    {
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, 5, It.IsAny<string?>())).ReturnsAsync((300d, 1420d));
        using var sut = CrearSut(_motor);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();
        _motor.Saltos.Should().BeEmpty("no se salta mientras el decodificador arranca: dejaba la pantalla en negro");
        _motor.FotogramasMostrados = 3;

        await EsperarHasta(() => !sut.OcultarVideoInicio);
        _motor.Saltos.Should().ContainSingle().Which.Segundos.Should().Be(300);
        sut.CurrentSeconds.Should().Be(300);
    }

    [Fact]
    public async Task SinMotor_AbrirUnEpisodioNoFalla()
    {
        _estado.Setup(e => e.ObtenerPosicionParaReanudarAsync(101, 5, It.IsAny<string?>())).ReturnsAsync((300d, 1420d));
        using var sut = CrearSut(null);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        sut.Motor.Should().BeNull();
        sut.CurrentSeconds.Should().Be(300, "el punto guardado se muestra aunque no haya motor");
        sut.TogglePlayPause();
        sut.Seek(60);
    }

    [Fact]
    public async Task SiLaAperturaFalla_ReintentaUnaVezYLuegoAvisa()
    {
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        _motor.TerminarApertura(exito: false, error: "formato no reconocido");

        await EsperarHasta(() => _motor.Abiertos.Count == 2);
        _motor.VecesDetenido.Should().Be(1);
        sut.OcultarVideoInicio.Should().BeTrue("aún se está intentando");

        _motor.TerminarApertura(exito: false, error: "formato no reconocido");

        sut.OcultarVideoInicio.Should().BeFalse();
        sut.PlayPauseIcon.Should().Be("Play");
        _motor.Abiertos.Should().HaveCount(2, "un archivo dañado de verdad no se reintenta sin fin");
    }

    [Fact]
    public async Task SiLaTarjetaNoDaImagen_ReabreUnaVezPorProcesador()
    {
        _motor.DecodificaPorHardware = true;
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        _motor.TerminarApertura(); // y no llega ni un fotograma

        await EsperarHasta(() => _motor.Abiertos.Count == 2, ms: 10000);
        _motor.AceleracionPuesta.Should().BeFalse();
        _motor.VecesDetenido.Should().Be(1);

        _motor.TerminarApertura(); // la reapertura tampoco pinta nada: no hay tercera
        await EsperarHasta(() => !sut.OcultarVideoInicio, ms: 15000); // se deja de esperar: mejor ver el fallo que una tapa eterna
        _motor.Abiertos.Should().HaveCount(2);
    }

    [Fact]
    public async Task UnAvisoDeAperturaDelEpisodioAnterior_SeComportaComoHoy()
    {
        // El aviso del motor no dice de qué archivo es. Si llega el del episodio que se dejó atrás, el reproductor lo toma por el
        // del nuevo: se fija aquí para que la etapa 2 (libmpv sí sabe de qué archivo avisa) decida si lo corrige.
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        string otra = Path.Combine(_carpeta, "Ep06.mkv");
        File.WriteAllText(otra, "x");
        await sut.CargarVideoAsync(otra, 101, "Frieren", 6);

        _motor.TerminarApertura(); // el del 5, tardío

        _motor.Abiertos.Should().Equal(_ruta, otra);
        _motor.VecesReproducir.Should().Be(1);
    }

    [Fact]
    public async Task CerrarConLaAperturaAMedias_DesechaElMotorSinFallar()
    {
        var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);

        sut.Dispose();
        _motor.TerminarApertura(); // el aviso llega con el reproductor ya cerrado
        await Task.Delay(300);

        _motor.VecesDesechado.Should().Be(1);
        sut.Motor.Should().BeNull();
    }

    [Fact]
    public async Task PausarYReanudar_PasanPorElMotor_YAlTerminarSeVuelveAEmpezar()
    {
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.TogglePlayPause();
        _motor.Estado.Should().Be(EstadoMotor.EnPausa);
        sut.PlayPauseIcon.Should().Be("Play");

        sut.TogglePlayPause();
        _motor.Estado.Should().Be(EstadoMotor.Reproduciendo);

        _motor.Estado = EstadoMotor.Terminado;
        sut.TogglePlayPause();
        _motor.Saltos.Should().Contain((0d, false), "reproducir un episodio terminado lo reinicia");
        _motor.Estado.Should().Be(EstadoMotor.Reproduciendo);
    }
}
```

- [ ] **Paso 2: campo, propiedades y fábrica**

  1. Quitar `using FlyleafLib.MediaFramework.MediaDecoder;`. Dejar `using FlyleafLib.MediaPlayer;` solo para la propiedad de paso.
  2. Sustituir (líneas 56–57):

```csharp
    [ObservableProperty]
    private Player _player = null!;
```

     por:

```csharp
    /// <summary>El motor de video. Null bajo pruebas sin motor inyectado, o si el motor no arranca en este equipo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Player))]
    private IMotorVideo? _motor;

    /// <summary>Solo para la vista clásica, que se enlaza al reproductor de Flyleaf (su anfitrión de video y el menú de subtítulos).
    /// La lógica del reproductor usa <see cref="Motor"/>.</summary>
    public Player? Player => (Motor as MotorFlyleaf)?.Player;

    private readonly Func<AjustesInicialesMotor, IMotorVideo?> _fabricaMotor;
```

  3. Constructor: añadir al final `Func<AjustesInicialesMotor, IMotorVideo?>? fabricaMotor = null` y, en el cuerpo, `_fabricaMotor = fabricaMotor ?? MotorFlyleaf.Crear;`.
  4. Sustituir `AsegurarPlayerInicializado` y `CreateOptimizedPlayer` enteros (líneas 509–672) por:

```csharp
    public void AsegurarMotorInicializado()
    {
        if (Motor != null && !Motor.Desechado) return;

        var ajustes = _settingsService?.ObtenerConfiguracion();
        var motor = _fabricaMotor(new AjustesInicialesMotor(
            ajustes?.AceleracionHardwareVideo ?? true,
            ajustes?.TarjetaGraficaVideo,
            ModoNocheActivo || EcualizadorActivo ? ConstruirFiltrosAudio() : Array.Empty<Core.FiltroAudio>()));
        if (motor == null)
        {
            Motor = null;
            return;
        }

        // Prioridad del proceso solo mientras hay un video abierto (se restaura en Dispose).
        if (!_prioridadElevada)
        {
            PrioridadReproduccion.Elevar();
            _prioridadElevada = true;
        }

        motor.AperturaTerminada += AlTerminarApertura;
        Motor = motor;
    }

    private void AlTerminarApertura(object? sender, AperturaTerminada e)
    {
        if (sender is not IMotorVideo motor) return;

        // Que el motor abra una pista de subtítulos también avisa: solo hay que extraer la pista nueva. Antes se reevaluaba
        // "subtítulos por defecto" y, con esa opción apagada, se apagaba en el acto la pista que el usuario acababa de elegir en el menú.
        if (e.Clase == ClaseApertura.Subtitulos)
        {
            if (!e.Exito) AppLogger.Warn("ReproductorViewModel", $"No se pudo abrir la pista de subtítulos: {e.Error}");
            CargarCuesDePistaDelMotor();
            return;
        }

        if (_cambiandoPistaAudio)
        {
            // Cambio de pista de audio desde el menú: el video sigue donde estaba; solo se restaura volumen/silencio.
            _cambiandoPistaAudio = false;
            if (!e.Exito) AppLogger.Warn("ReproductorViewModel", $"No se pudo abrir la pista de audio: {e.Error}");
            try { motor.Volumen = Volumen; motor.Silencio = IsMuted; } catch { }
            return;
        }

        if (!e.Exito)
        {
            if (ReintentarAperturaSiCorresponde(e.Error)) return;
            NotificarFalloAlAbrir(e.Error);
            return;
        }

        _haCompletadoOpen = true;
        AppLogger.Debug("ReproductorViewModel", $"[Arranque] OpenCompleted a los {_relojArranque.ElapsedMilliseconds} ms (estado {motor.Estado}).");
        AppLogger.Debug("ReproductorViewModel", $"Pistas de subtítulos disponibles: {motor.PistasSubtitulos.Count}.");
        EvaluarSubtitulosPorDefecto();

        try
        {
            if (motor.Estado != EstadoMotor.Reproduciendo)
            {
                motor.Reproducir();
            }
            PlayPauseIcon = "Pause";
            _smtc?.ActualizarEstadoReproduccion(true);
            IniciarProteccionPantallaSiCorresponde();
        }
        catch { }

        try
        {
            // Sigue en silencio hasta que se vea la imagen (y, al reanudar, hasta que el salto al punto guardado se
            // asiente): lo quita VigilarArranqueAsync.
            motor.Volumen = Volumen;
            motor.Silencio = true;
        }
        catch { }

        ActualizarPistasAudio(aplicarPreferencia: true);
        _ = VigilarArranqueAsync(motor, _trackingCts?.Token ?? CancellationToken.None);

        // IMPORTANTE: NO saltar aquí (ni el diferido-al-abrir del coordinador ni la reanudación). En este punto el decodificador
        // de video aún está creando su contexto y un salto inmediato lo interrumpe en algunos archivos (HEVC/VFR), dejando la
        // pantalla en negro con audio avanzando. El bucle de progreso aplica el salto cuando el video ya reproduce de verdad.
    }
```

     Renombrar el otro llamador: `ViewModels/NavigationService.cs:185` (`viewModel.AsegurarPlayerInicializado();` → `viewModel.AsegurarMotorInicializado();`).

- [ ] **Paso 3: sustituciones hoja a hoja en `ReproductorViewModel.cs`**

| Líneas | Antes | Después |
|---|---|---|
| 423, 445, 454 | `_volumeCoordinator.X(Player, …)` | `_volumeCoordinator.X(Motor, …)` |
| 677–686 | `if (Player == null) return;` · `Player.Status == Status.Playing` | `if (Motor == null) return;` · `Motor.Estado == EstadoMotor.Reproduciendo` |
| 691–693 | `Player == null \|\| Player.Status != Status.Playing` · `Player.Pause()` | `Motor == null \|\| Motor.Estado != EstadoMotor.Reproduciendo` · `Motor.Pausar()` |
| 702–710 | `Player == null \|\| Player.Status == Status.Playing` · `Player.Status == Status.Ended` · `Player.CurTime = 0` · `Player.Play()` | `Motor == null \|\| Motor.Estado == EstadoMotor.Reproduciendo` · `Motor.Estado == EstadoMotor.Terminado` · `Motor.Saltar(TimeSpan.Zero, exacto: false)` · `Motor.Reproducir()` |
| 843, 876, 2119 | `_seekCoordinator.SolicitarSeek(Player, …)` | `_seekCoordinator.SolicitarSeek(Motor, …)` |
| 855–857 | `Player != null && !Player.IsDisposed` · `TimeSpan.FromTicks(Player.CurTime).TotalSeconds` | `Motor != null && !Motor.Desechado` · `Motor.Posicion.TotalSeconds` |
| 1341 | `AsegurarPlayerInicializado();` | `AsegurarMotorInicializado();` |
| 1360 | `if (Player != null)` | `if (Motor != null)` |
| 1370 | `Player.Config.Video.VideoAcceleration = …` | `Motor.AceleracionPorHardware = …` |
| 1376 | `try { if (Player.Audio != null) Player.Audio.Mute = true; } catch { }` | `try { Motor.Silencio = true; } catch { }` |
| 1378 | `Player.OpenAsync(rutaVideo);` | `Motor.Abrir(rutaVideo);` |
| 1386 | `Player.Speed = (float)Math.Clamp(velocidad, 0.5, 2.0);` | `Motor.Velocidad = Math.Clamp(velocidad, 0.5, 2.0);` |
| 1493 | `VigilarArranqueAsync(Player player, …)` | `VigilarArranqueAsync(IMotorVideo motor, …)` |
| 1499, 1503, 1527 | `player.IsDisposed` | `motor.Desechado` |
| 1499, 1504, 1521, 1530 | `FotogramasMostrados(player)` | `motor.FotogramasMostrados` (la variable `uint fotogramasAntes` pasa a `long`) |
| 1504 | `player.VideoDecoder?.VideoAccelerated ?? false` | `motor.DecodificaPorHardware` |
| 1508 | `ReabrirPorProcesador(player);` | `ReabrirPorProcesador(motor);` |
| 1517 | `TimeSpan.FromTicks(player.Duration).TotalSeconds` | `motor.Duracion.TotalSeconds` |
| 1529, 1548 | `TimeSpan.FromTicks(player.CurTime).TotalSeconds` | `motor.Posicion.TotalSeconds` |
| 1566 | `AplicarRendimientoSegunEquipo(player);` | `AplicarRendimientoSegunEquipo(motor);` |
| 1567 | `try { if (!player.IsDisposed && player.Audio != null && !_cambiandoPistaAudio) player.Audio.Mute = IsMuted; } catch { }` | `try { if (!motor.Desechado && !_cambiandoPistaAudio) motor.Silencio = IsMuted; } catch { }` |
| 1576–1589 | `ReabrirPorProcesador(Player player)` · `player.Video?.Codec` · `player.Config.Video.VideoAcceleration = false;` · `player.Stop(); player.OpenAsync(ruta);` | `ReabrirPorProcesador(IMotorVideo motor)` · `motor.CodecVideo` · `motor.AceleracionPorHardware = false;` · `motor.Detener(); motor.Abrir(ruta);` |
| 1620–1636 | cuerpo de `AplicarRendimientoSegunEquipo(Player player)` | ver bloque de abajo |
| 1644–1660 | los dos `FieldInfo` y `FotogramasMostrados(Player)` | se borran (viven en `MotorFlyleaf`) |
| 1699–1713 | `var player = Player;` · `player.IsDisposed` · `player.Stop()` · `player.OpenAsync(ruta)` | `var motor = Motor;` · `motor.Desechado` · `motor.Detener()` · `motor.Abrir(ruta)` |
| 1724 | `Player != null && !Player.IsDisposed && Player.Status == Status.Playing` | `Motor != null && !Motor.Desechado && Motor.Estado == EstadoMotor.Reproduciendo` |
| 1734 | `try { if (Player?.Audio != null) Player.Audio.Mute = IsMuted; } catch { }` | `try { if (Motor != null) Motor.Silencio = IsMuted; } catch { }` |
| 1800 | `CapturarYGuardarAsync(Player)` | `CapturarYGuardarAsync(Motor)` |
| 1838–1841 | `Player != null && !Player.IsDisposed` · `FromTicks(Player.CurTime)` · `FromTicks(Player.Duration)` | `Motor != null && !Motor.Desechado` · `Motor.Posicion.TotalSeconds` · `Motor.Duracion.TotalSeconds` |
| 1994 | `while (Player != null && !Player.IsDisposed && …)` | `while (Motor != null && !Motor.Desechado && …)` |
| 2007, 2069 | `Player.Status == Status.Playing` / `Player?.Status == Status.Playing` | `Motor.Estado == EstadoMotor.Reproduciendo` / `Motor?.Estado == EstadoMotor.Reproduciendo` |
| 2012, 2014, 2015 | `TimeSpan.FromTicks(Player.Duration/CurTime)` | `Motor.Duracion` / `Motor.Posicion.TotalSeconds` / `Motor.Duracion.TotalSeconds` |
| 2042–2044 | `_episodeNavigator.ConsiderarPrecarga(porcentaje, _siguienteEpisodioCache?.RutaCompleta);` | `_episodeNavigator.ConsiderarPrecarga(porcentaje, _siguienteEpisodioCache?.RutaCompleta, (ruta, ct) => Motor?.PrecalentarArchivo(ruta, ct));` |
| 2060 | `Player?.Status == Status.Ended` | `Motor?.Estado == EstadoMotor.Terminado` |
| 2276 | `Player?.CurTime` · `Player?.Status` · `Player?.Video…` (línea de registro) | `Motor?.Posicion.TotalSeconds` · `Motor?.Estado` · `Motor?.FotogramasMostrados` |
| 2362–2376 | bloque de cierre con `Player` | ver bloque de abajo |

`AplicarRendimientoSegunEquipo`:

```csharp
    private void AplicarRendimientoSegunEquipo(IMotorVideo motor)
    {
        try
        {
            var ajustes = _settingsService?.ObtenerConfiguracion();
            bool porHardware = motor.DecodificaPorHardware;
            string codec = motor.CodecVideo ?? "?";
            var tarjeta = motor.TarjetaEnUso;

            _estrategiaSaltos = MotorVideo.ElegirEstrategiaSaltos(ajustes?.ModoSaltosVideo, porHardware, Environment.ProcessorCount);

            bool escalado = tarjeta != null && MotorVideo.UsarEscaladoInteligente(ajustes?.EscaladoInteligenteVideo, tarjeta.Fabricante, tarjeta.MemoriaMb);
            motor.EscaladoInteligente = escalado;

            AppLogger.Info("ReproductorViewModel", $"Video: {codec} por {(porHardware ? "tarjeta gráfica" : "procesador")} · tarjeta {tarjeta?.Nombre ?? "?"} · " +
                $"{Environment.ProcessorCount} hilos · saltos {_estrategiaSaltos} · escalado {(escalado ? "sí" : "no")}.");
            MotorVideo.PublicarInforme(new InformeVideo(codec, porHardware, tarjeta?.Nombre, _estrategiaSaltos, escalado, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ReproductorViewModel", $"No se pudo evaluar el rendimiento del video: {ex.Message}");
        }
    }
```

Cierre (líneas 2362–2376):

```csharp
        if (Motor != null)
        {
            try
            {
                Motor.AperturaTerminada -= AlTerminarApertura;
                if (Motor.Estado == EstadoMotor.Reproduciendo)
                {
                    Motor.Pausar();
                }
                Motor.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ReproductorViewModel", $"Player dispose cleanup: {ex.Message}");
            }
            Motor = null;
        }
```

Los comentarios que nombran a Flyleaf para explicar *por qué* existe un rodeo se conservan tal cual.

- [ ] **Paso 4: no compilar todavía.** Los otros dos archivos parciales aún usan `Player` como si fuera el de antes; se compila al final de la tarea 6.

---

## Tarea 5: audio (pistas y filtros)

**Archivos:**
- Modificar: `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` (pistas de audio, líneas 968–1022) y `ReproductorViewModel.Audio.cs`
- Modificar: `AnimeLocalTracker.Tests/ViewModels/ReproductorConMotorTests.cs`

**Interfaces:**
- Consume: `IMotorVideo.PistasAudio`, `IndicePistaAudioActual`, `ElegirAudio`, `AplicarFiltrosAudio`, `CambiarFiltroEnVivo`.
- Produce: `ConstruirFiltrosAudio() : IReadOnlyList<Core.FiltroAudio>` (lo usa `AsegurarMotorInicializado`).

- [ ] **Paso 1: añadir las pruebas a `ReproductorConMotorTests`**

```csharp
    [Fact]
    public async Task CambiarDePistaDeAudio_NoSeTrataComoUnEpisodioNuevo()
    {
        _motor.Audio.Add(new PistaAudioMotor(1, "ja", null));
        _motor.Audio.Add(new PistaAudioMotor(2, "es", "Latino"));
        _motor.IndicePistaAudioActual = 1;
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();
        _motor.FotogramasMostrados = 3;
        await EsperarHasta(() => !sut.OcultarVideoInicio);
        sut.PistasAudio.Should().HaveCount(2);
        sut.HayVariasPistasAudio.Should().BeTrue();

        sut.SeleccionarPistaAudioCommand.Execute(sut.PistasAudio[1]);
        _motor.AudioElegido.Should().Be(new PistaAudioMotor(2, "es", "Latino"));
        _motor.SilencioPuesto = true; // el motor reabre el audio y vuelve a avisar
        _motor.TerminarApertura();

        _motor.Abiertos.Should().HaveCount(1, "no se reabre el episodio");
        _motor.VecesReproducir.Should().Be(1, "ni se vuelve a dar a reproducir");
        _motor.SilencioPuesto.Should().BeFalse("se restaura el silencio que tenía el usuario");
        _motor.VolumenPuesto.Should().Be(sut.Volumen);
        sut.PistasAudio[1].EsActual.Should().BeTrue();
    }

    [Fact]
    public async Task ElEcualizador_MontaSusFiltrosEnElMotor_YUnaBandaSeCambiaEnVivo()
    {
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.BandasEcualizador[0].Ganancia = 6; // mover una banda con el ecualizador apagado lo enciende y monta la cadena
        sut.EcualizadorActivo.Should().BeTrue();
        _motor.Filtros.Should().NotBeEmpty();

        sut.BandasEcualizador[1].Ganancia = 3; // ya encendido: solo se toca el filtro que está sonando
        _motor.CambiosEnVivo.Should().Contain(c => c.Id == AnimeLocalTracker.Core.EcualizadorAudio.IdBanda(1) && c.Clave == "g");
    }
```

- [ ] **Paso 2: pistas de audio en `ReproductorViewModel.cs`**

```csharp
    private void ActualizarPistasAudio(bool aplicarPreferencia)
    {
        var pistas = Motor?.PistasAudio ?? Array.Empty<PistaAudioMotor>();
        int actual = Motor?.IndicePistaAudioActual ?? -1;
        var opciones = pistas
            .Select((p, i) => new OpcionPistaAudio(p, OpcionPistaAudio.ConstruirNombre(p.Idioma, p.Titulo, i + 1), p.Idioma, p.Indice == actual))
            .ToList();
```

El resto del método no cambia. En `CambiarPistaAudio`: `if (Player == null || opcion.Pista is not FlyleafLib.MediaFramework.MediaStream.AudioStream pista) return;` → `if (Motor == null || opcion.Pista is not PistaAudioMotor pista) return;`, y `Player.OpenAsync(pista);` → `Motor.ElegirAudio(pista);`. La comparación `o.Pista == opcion.Pista` pasa a `Equals(o.Pista, opcion.Pista)` (los registros se comparan por valor).

- [ ] **Paso 3: `ReproductorViewModel.Audio.cs`**

  1. Quitar los tres `using FlyleafLib…`.
  2. `ConstruirFiltrosAudio`:

```csharp
    /// <summary>Cadena de filtros de audio actual (ecualizador y Modo Noche).</summary>
    private IReadOnlyList<Core.FiltroAudio> ConstruirFiltrosAudio() =>
        Core.EcualizadorAudio.ConstruirFiltros(EcualizadorActivo, BandasEcualizador.Select(b => b.Ganancia).ToList(), ModoNocheActivo, ArgumentosModoNoche).ToList();
```

  3. `AplicarFiltrosAudio`:

```csharp
    private void AplicarFiltrosAudio(bool videoAbriendose = false)
    {
        if (Motor == null) return;
        try
        {
            bool montados = Motor.AplicarFiltrosAudio(ConstruirFiltrosAudio());
            if (!montados && !videoAbriendose) AppLogger.Warn("ReproductorViewModel", "El motor no pudo montar los filtros de audio.");
        }
```

     (el `catch` se conserva).
  4. `ActualizarFiltroBanda`:

```csharp
        if (!EcualizadorActivo || Motor == null) return;
        try
        {
            bool r1 = Motor.CambiarFiltroEnVivo(Core.EcualizadorAudio.IdBanda(banda.Indice), "g", Core.EcualizadorAudio.TextoGanancia(banda.Ganancia));
            double pre = Core.EcualizadorAudio.Preamplificacion(BandasEcualizador.Select(b => b.Ganancia).ToList());
            bool r2 = Motor.CambiarFiltroEnVivo(Core.EcualizadorAudio.IdPreamplificador, "volume", Core.EcualizadorAudio.TextoVolumen(pre));
            if (!r1 || !r2) AplicarFiltrosAudio(); // si el filtro no admite el cambio en vivo, se monta la cadena de nuevo
        }
```

     **Una diferencia que hay que conservar:** hoy, antes de los dos cambios en vivo, se hace `audio.Filters = ConstruirFiltrosAudio()` *sin* recargar, para que el siguiente episodio no vuelva a las ganancias anteriores. `AplicarFiltrosAudio` del contrato sí recarga (un corte de milisegundos al mover una banda), así que no vale para eso. Como primera línea del `try`, antes de los cambios en vivo:

```csharp
            (Motor as MotorFlyleaf)?.RecordarFiltrosParaElSiguienteArchivo(ConstruirFiltrosAudio());
```

     Con otro motor no hace falta: no guarda una lista aparte para el siguiente archivo.

- [ ] **Paso 4:** seguir con la tarea 6 antes de compilar.

---

## Tarea 6: subtítulos

**Archivos:**
- Modificar: `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` (líneas 940–948 y 1030–1070) y `ReproductorViewModel.Subtitulos.cs`
- Modificar: `AnimeLocalTracker.Tests/ViewModels/ReproductorConMotorTests.cs`

**Interfaces:**
- Consume: `PistaSubtitulosMotor`, `IMotorVideo.PistasSubtitulos`, `IndicePistaSubtitulosDelMotor`, `PistaSubtitulosDe`, `TamanoVideo`; `ISubtitleCoordinator` con las firmas de la tarea 3.

- [ ] **Paso 1: añadir las pruebas**

```csharp
    [Fact]
    public async Task UnaPistaDeTexto_LaLeeLaApp_YNoSeLePideAlMotor()
    {
        // El lector de subtítulos ASS de Flyleaf cerraba la app con ciertas etiquetas: las pistas de texto las lee la app.
        var extractor = new Mock<ISubtitleCuesExtractorService>();
        IReadOnlyList<AnimeLocalTracker.Models.SubtitleCue> lineas = [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "Hola")];
        extractor.Setup(x => x.ExtraerAsync(_ruta, 2, null, It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(lineas);
        _motor.Subtitulos.Add(new PistaSubtitulosMotor(2, LocalizationService.Cultura.TwoLetterISOLanguageName, null, false, false, null));
        using var sut = new ReproductorViewModel(
            Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
            playbackStateService: _estado.Object, subtitleCuesExtractorService: extractor.Object, fabricaMotor: _ => _motor);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        await EsperarHasta(() => sut.SubtitulosDobleLineaActivo);
        _motor.SubtitulosDelMotor.Should().BeNull();
        sut.SubtitulosHabilitados.Should().BeTrue();
    }

    [Fact]
    public async Task SiLaAppNoPuedeLeerUnaPistaDeTexto_SeLaDejaAlMotor()
    {
        var extractor = new Mock<ISubtitleCuesExtractorService>();
        extractor.Setup(x => x.ExtraerAsync(_ruta, 2, null, It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AnimeLocalTracker.Models.SubtitleCue>)Array.Empty<AnimeLocalTracker.Models.SubtitleCue>());
        var texto = new PistaSubtitulosMotor(2, LocalizationService.Cultura.TwoLetterISOLanguageName, null, false, false, null);
        _motor.Subtitulos.Add(texto);
        using var sut = new ReproductorViewModel(
            Mock.Of<IDatabaseService>(), Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
            playbackStateService: _estado.Object, subtitleCuesExtractorService: extractor.Object, fabricaMotor: _ => _motor);

        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        await EsperarHasta(() => _motor.SubtitulosDelMotor == texto); // mejor los del motor que quedarse sin subtítulos
    }

    [Fact]
    public async Task UnaPistaDeImagen_SeLePideAlMotor_YSuAvisoNoReiniciaElEpisodio()
    {
        var imagen = new PistaSubtitulosMotor(4, "es", null, EsImagen: true, EsAss: false, RutaExterna: null);
        _motor.Subtitulos.Add(imagen);
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        _motor.SubtitulosDelMotor.Should().Be(imagen, "la app no sabe dibujar subtítulos de imagen");

        _motor.TerminarApertura(clase: ClaseApertura.Subtitulos);

        _motor.VecesReproducir.Should().Be(1);
        _motor.Abiertos.Should().HaveCount(1);
    }

    [Fact]
    public async Task ApagarLosSubtitulos_SeLoDiceAlMotor()
    {
        _motor.Subtitulos.Add(new PistaSubtitulosMotor(4, "es", null, true, false, null));
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.TurnOffSubtitles();

        _motor.SubtitulosDelMotor.Should().BeNull();
        sut.SubtitulosHabilitados.Should().BeFalse();
    }
```

(Añadir `using System.Collections.Generic;` al archivo de pruebas.)

- [ ] **Paso 2: `ReproductorViewModel.cs`**

```csharp
    [RelayCommand]
    public void SelectSubtitleStream(object stream)
    {
        // La vista clásica entrega la pista de Flyleaf; la nueva entregará ya la del contrato.
        var pista = stream as PistaSubtitulosMotor ?? Motor?.PistaSubtitulosDe(stream);
        if (Motor == null || pista == null) return;

        SubtitulosHabilitados = true;
        SubtitulosIcon = "Subtitles";
        AbrirPistaSubtitulos(pista);
    }
```

En `EvaluarSubtitulosPorDefecto` y `DeshabilitarSubtitulos`: `Player` → `Motor` en las tres llamadas a `_subtitleCoordinator` (`PistaPorDefecto` ya devuelve `PistaSubtitulosMotor?`).

- [ ] **Paso 3: `ReproductorViewModel.Subtitulos.cs`**

  1. Campo `private FlyleafLib.MediaFramework.MediaStream.SubtitlesStream? _pistaTextoActual;` → `private PistaSubtitulosMotor? _pistaTextoActual;`
  2. `AbrirPistaSubtitulos`:

```csharp
    private void AbrirPistaSubtitulos(PistaSubtitulosMotor pista)
    {
        ReiniciarCuesSubtitulos();

        if (pista is { EsImagen: false, RutaExterna: null })
        {
            _subtitleCoordinator.Deshabilitar(Motor); // por si el motor tenía abierta otra pista (de imagen)
            IniciarCargaCuesSubtitulos(_rutaVideo, pista.Indice, null, pista);

            // Si además es ASS/SSA se prepara el dibujo con su estilo original; mientras tanto (y si falla) se ve el texto plano.
            _pistaTextoActual = pista;
            PistaActualEsAss = pista.EsAss;
            IniciarDibujoAssSiCorresponde();
            return;
        }

        _subtitleCoordinator.SeleccionarPista(Motor, pista);
    }
```

     (el comentario de documentación de encima se conserva).
  3. `CargarCuesDePistaDeFlyleaf` → `CargarCuesDePistaDelMotor`:

```csharp
    private void CargarCuesDePistaDelMotor()
    {
        ReiniciarCuesSubtitulos();

        int abierta = Motor?.IndicePistaSubtitulosDelMotor ?? -1;
        if (abierta < 0) return;
        var pista = Motor!.PistasSubtitulos.FirstOrDefault(s => s.Indice == abierta);
        if (pista == null) return;

        // Una pista externa (.srt/.ass suelto junto al video) trae su ruta real; una incrustada en el propio contenedor se
        // identifica por su índice de flujo.
        int? streamIndexEmbebido = pista.RutaExterna == null ? pista.Indice : null;

        IniciarCargaCuesSubtitulos(_rutaVideo, streamIndexEmbebido, pista.RutaExterna, null);
    }
```

  4. `IniciarCargaCuesSubtitulos` y `CargarCuesSubtitulosAsync`: el parámetro `object? pistaSinFlyleaf` pasa a `PistaSubtitulosMotor? pistaSinMotor`; `_subtitleCoordinator.SeleccionarPista(Player, pistaSinFlyleaf)` → `_subtitleCoordinator.SeleccionarPista(Motor, pistaSinMotor)`.
  5. `IniciarDibujoAssSiCorresponde`:

```csharp
        var pista = _pistaTextoActual;
        var motor = Motor;
        if (pista == null || motor == null) return;

        if (!Core.SubtitulosAss.DebeDibujarse(pista.EsAss, pista.EsImagen, pista.RutaExterna != null, UsarMiEstiloEnAss)) return;

        var (anchoVideo, altoVideo) = motor.TamanoVideo;
        var (ancho, alto) = Core.SubtitulosAss.TamanoDibujo(
            anchoVideo, altoVideo,
            (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
        int indice = Core.SubtitulosAss.IndiceEntreSubtitulos(
            motor.PistasSubtitulos.Where(s => s.RutaExterna == null).Select(s => s.Indice), pista.Indice);
```

     (el comentario `ponytail:` y el resto del método se conservan).
  6. Borrar `TamanoDelVideo(Player)` (vive en `MotorFlyleaf.TamanoVideo`).

- [ ] **Paso 4: compilar la app y correr las pruebas de las tareas 3 a 6**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false -nodeReuse:false --filter "FullyQualifiedName~ReproductorConMotorTests|FullyQualifiedName~CoordinadoresConMotorTests|FullyQualifiedName~Reproductor|FullyQualifiedName~PipNavigation|FullyQualifiedName~SubtitleCoordinator|FullyQualifiedName~EpisodeNavigator"
```

Esperado: 0 advertencias; todas en verde. La vista clásica y el mini reproductor siguen compilando porque `Player` existe como propiedad de paso (el mini se cambia en la tarea 7).

Si una prueba nueva falla porque el comportamiento real difiere de lo que describe, **manda el comportamiento de hoy**: se comprueba leyendo el código de `a2eb985` y se corrige la prueba, no el reproductor.

---

## Tarea 7: mini reproductor, vista clásica y limpieza

**Archivos:**
- Modificar: `AnimeLocalTracker/Views/MiniReproductorWindow.xaml.cs`
- Modificar: `AnimeLocalTracker/Views/ReproductorView.xaml.cs` (solo si deja de compilar por la nulabilidad de `Player`)
- Modificar: `AnimeLocalTracker.Tests/ViewModels/ReproductorConMotorTests.cs`

**Interfaces:**
- Consume: `IMotorVideo.ProporcionVideo`, `ProporcionVideoCambiada`.

- [ ] **Paso 1: prueba de la proporción** (la ventana no se puede crear en pruebas; se prueba lo que consume)

```csharp
    [Fact]
    public void ElMotorAvisaCuandoCambiaLaProporcionDelVideo()
    {
        int avisos = 0;
        _motor.ProporcionVideoCambiada += (_, _) => avisos++;

        _motor.CambiarProporcion(4 / 3.0);

        avisos.Should().Be(1);
        _motor.ProporcionVideo.Should().BeApproximately(1.333, 0.001);
    }
```

- [ ] **Paso 2: `MiniReproductorWindow.xaml.cs`**

Hoy observa `reproductor.Player?.Video` (un `INotifyPropertyChanged` de Flyleaf) y lee `Player.Video.AspectRatio.Value`. Pasa a:

| Antes | Después |
|---|---|
| `private INotifyPropertyChanged? _videoObservado;` | `private IMotorVideo? _motorObservado;` |
| `_videoObservado = reproductor.Player?.Video; if (_videoObservado != null) _videoObservado.PropertyChanged += Video_PropertyChanged;` | `_motorObservado = reproductor.Motor; if (_motorObservado != null) _motorObservado.ProporcionVideoCambiada += Motor_ProporcionCambiada;` |
| `if (_videoObservado != null) _videoObservado.PropertyChanged -= Video_PropertyChanged; _videoObservado = null;` | `if (_motorObservado != null) _motorObservado.ProporcionVideoCambiada -= Motor_ProporcionCambiada; _motorObservado = null;` |
| `Video_PropertyChanged(object? s, PropertyChangedEventArgs e)` (filtra por `AspectRatio` y llama a lo que ajusta el alto) | `Motor_ProporcionCambiada(object? s, EventArgs e)`: llama a lo mismo, sin filtro (el motor ya solo avisa de la proporción) |
| `double valor = _reproductor.Player?.Video.AspectRatio.Value ?? 0;` | `double valor = _reproductor.Motor?.ProporcionVideo ?? 0;` |

Conservar el salto al hilo de la interfaz que tenga el manejador actual: el aviso puede llegar desde un hilo del motor.

- [ ] **Paso 3: `ReproductorView.xaml.cs`**

`Player` es ahora `Player?`. Las líneas 82, 226, 227 y 341 ya usan `?.`; si el compilador avisa de nulabilidad en alguna otra, añadir `?.` o la comprobación de null. No se cambia nada más. El `{Binding Player}` del XAML y `{Binding Player.Subtitles.Streams}` siguen funcionando: al asignar `Motor` se notifica también `Player`.

- [ ] **Paso 4: comprobar que Flyleaf quedó acotado**

```
grep -rln "FlyleafLib\|Flyleaf\.FFmpeg" AnimeLocalTracker --include=*.cs | grep -v "/obj/"
```

Esperado, exactamente: `Services/MotorFlyleaf.cs`, `Services/MotorVideo.cs`, `Services/ParcheSubtitulosFlyleaf.cs`, `Services/SubtitleAssRenderer.cs`, `Controls/SubtitleTextView.cs`, `Views/ReproductorView.xaml.cs`, `ViewModels/ReproductorViewModel.cs` (solo `using FlyleafLib.MediaPlayer;` para la propiedad de paso) y los archivos que solo lo nombran en comentarios o textos (`App.xaml.cs`, `LocalizationService.cs`, `AppSettings.cs`, `UpdateService.cs`, `AudioTrackPlayer.cs`, `ClipPlayer.cs`, `Models/*.cs`, `Core/EcualizadorAudio.cs`, `Core/SubtitulosAss.cs`, `SubtitleCuesExtractorService.cs`). Si aparece otro con código que use tipos de Flyleaf, falta una sustitución.

- [ ] **Paso 5: verificación**

Compilar la app (0 advertencias) y correr `--filter "FullyQualifiedName~Reproductor|FullyQualifiedName~Coordinadores|FullyQualifiedName~PipNavigation|FullyQualifiedName~Views"`.

---

## Tarea 8: verificación final

- [ ] **Paso 1: suite completa, una vez**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false -nodeReuse:false
```

Esperado: 0 advertencias; total = 3076 + las pruebas nuevas, todas en verde.

- [ ] **Paso 2: en la app, con perfil aislado** (skills `perfil-aislado` y `wpf-visual-verification`; sin red; volumen 0)

`crear --con-rutas-reales` (solo se lee la biblioteca; no se borra ni descarga nada), foto de la carpeta real antes y después (0 diferencias). Con registro detallado, comprobar en el registro de la sesión y por UIA o captura:

| Qué | Cómo | Se espera |
|---|---|---|
| Abrir un episodio H.264 con ASS (Youjo Senki ep. 2) | Ficha → reproducir | `[Arranque] OpenCompleted`, `Primer fotograma`, línea `Video: h264 por tarjeta gráfica…`; 0 errores |
| Subtítulos | Menú de subtítulos | La lista de pistas aparece (enlace a `Player.Subtitles.Streams`); elegir otra pista la cambia; "Apagar" los quita |
| Reanudar | Salir a los ~2 min, volver a entrar | `Seek de reanudación a …` y `Reanudación asentada`; la barra abre en el punto guardado |
| Saltos | Flechas y clic en la barra | La posición cambia; sin `Excepción al ajustar posición` |
| AV1 (Tokyo Revengers T4 ep. 2) | Reproducir | Abre; si la tarjeta no da imagen, `se reabre decodificando por procesador` una sola vez |
| Pistas de audio | Un archivo con dos pistas, si lo hay en la biblioteca | El menú las lista y cambiar no reinicia el episodio |
| Ecualizador y modo noche | Encender y mover una banda | Sin `no pudo montar los filtros` |
| Captura | Tecla de captura | Aviso de captura lista (borrar la imagen creada en Imágenes\AnimeLocalTracker al acabar) |
| Mini reproductor | Entrar y salir | La ventana conserva la proporción; al volver, el video sigue |
| Episodio siguiente | Botón siguiente | `[Cambio] Episodio N: imagen visible a los … ms` |
| Memoria | Nada | No se mide en esta etapa |

Salir siempre con `Esc` antes del 85 % del episodio. Registro de la sesión: 0 errores; los avisos, solo los que ya salían antes.

- [ ] **Paso 3: informe**

Decir al usuario qué se verificó y qué no, con los números (pruebas, líneas del registro), y esperar su orden para el commit.

---

## Lo que esta etapa deja para las siguientes

- El reproductor conserva rodeos pensados para Flyleaf (espera entre saltos, salto diferido al abrir, reapertura por procesador) también cuando el motor sea libmpv. Funcionan igual con cualquier motor; se simplifican en la etapa 6.
- El aviso de apertura no dice de qué archivo es (foco de revisión 3).
- `ReproductorViewModel` sigue teniendo la propiedad de paso `Player` y `MotorFlyleaf.RecordarFiltrosParaElSiguienteArchivo`: se borran con Flyleaf.
