# Plan de implementación: motor libmpv, etapa 6 (retirada de Flyleaf)

> **Para quien lo ejecute:** SUB-SKILL OBLIGATORIO: `superpowers:executing-plans`, tarea a tarea, en esta misma sesión (en este repo no se usan subagentes salvo que el usuario lo pida). Los pasos usan casillas (`- [x]`) para llevar la cuenta.

**Objetivo:** que el reproductor de episodios tenga un solo motor (libmpv) y una sola vista; que un equipo donde el lienzo OpenGL no arranque siga viendo los episodios, pintados por software por el propio libmpv; y que del proyecto salgan Flyleaf, Harmony, el dibujante propio de subtítulos ASS y todo lo que solo existía para ellos.

**Arquitectura:** dos bloques que se pueden subir por separado.

- **Bloque A (no quita nada):** `SuperficieMpv` aprende una segunda forma de pintar, la interfaz de pintado por software de libmpv sobre una imagen de WPF. Se usa sola si el lienzo OpenGL falla, o a mano con un interruptor nuevo de Configuración. Se mide dentro de la app antes de seguir.
- **Bloque B (la retirada):** el reproductor deja de tener motor clásico; se borran `MotorFlyleaf`, la vista clásica, el parche de Harmony y los subtítulos que dibujaba la app; el contrato `IMotorVideo` pierde lo que solo tenía sentido con dos motores; salen cuatro paquetes.

**Tecnologías:** C# .NET 8, WPF, libmpv 0.41 (`MPV_RENDER_API_TYPE_SW`), OpenTK.GLWpfControl 4.3.6, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-motor-libmpv.md`, secciones 5 (modo de compatibilidad y medición por software), 7 y 10. Etapas anteriores: planes 1 a 5 con sus notas de ejecución.

## Lo que hay que saber antes de aprobarlo

**La condición que puso el diseño no se cumple.** La etapa 5 se daba por buena con «una versión publicada y usada un tiempo sin volver al clásico». La última versión publicada es la v1.0.5 (2026-09-01), que no lleva libmpv: el motor nuevo solo ha funcionado en este equipo, desde el 2026-10-11. Si se hace el bloque B ahora, quien actualice pasará de Flyleaf a libmpv sin haber tenido nunca el clásico como salida. Lo que queda como salida es el modo por software del bloque A, que cubre «no se ve la imagen» pero no otros fallos (sonido, un formato que libmpv lleve peor, un cierre dentro de la biblioteca).

**Esta etapa no tiene interruptor.** Hasta ahora todo se deshacía con un ajuste. La retirada se deshace con `git revert` de sus commits (por eso van separados de los del bloque A) y una versión nueva.

**Lo que desaparece para el usuario:** las filas «Motor de video» y «Tarjeta gráfica» y los dos controles del borde de la caja de subtítulos en Configuración. Aparece una: «Modo de compatibilidad de video».

**Lo que no cambia:** el instalador pesa prácticamente lo mismo (salen unos 7 MB; la carpeta `FFmpeg` de 168 MB sigue haciendo falta para `ffmpeg.exe` y `ffprobe.exe`). Sí baja la memoria con la app en reposo: hoy se cargan por adelantado 160 MB de bibliotecas de FFmpeg para Flyleaf aunque se use libmpv.

## Decisiones de esta etapa

1. **Orden.** Bloque A primero, con su medición; el bloque B solo empieza si la medición pasa los criterios de la tarea 2. *Recomendación:* publicar una versión entre A y B, como pedía el diseño. El plan vale igual si se hacen seguidos: lo decide el propietario al aprobar.
2. **Modo de compatibilidad = libmpv pintando por software.** Entra solo si el lienzo OpenGL no se puede crear o libmpv no puede pintar en él, en el mismo episodio y sin perder el punto. Además hay un interruptor en Configuración → Reproducción, apagado por defecto, para el caso que la app no puede detectar: el lienzo se crea pero se ve negro o con fallos. Con él encendido no hay escalado inteligente (Anime4K necesita OpenGL).
3. **Tope de tamaño por software: 1920×1080 píxeles.** En una ventana mayor, libmpv pinta a ese tamaño y WPF amplía. El coste por software crece con los píxeles; el tope se confirma o se cambia con la medición de la tarea 2.
4. **Ajustes retirados.** `MotorVideo`, `TarjetaGraficaVideo` y el borde de la caja (`GrosorBorde`, `ColorBorde`) salen de `AppSettings`/`EstiloSubtitulos`. Un `settings.json` antiguo que los traiga se lee sin error y las claves desaparecen la siguiente vez que se guarde. Quien tuviera `"Flyleaf"` pasa a libmpv.
5. **Si falta `libmpv-2.dll`** (instalación dañada) ya no hay a qué caer: el episodio no se abre y sale un aviso que lo dice y pide reinstalar. Sin reintentos.
6. **El contrato se adelgaza.** Salen de `IMotorVideo`: `DibujaSubtitulosDeTexto`, `PistaSubtitulosDe`, `PrecalentarArchivo` (y la precarga del episodio siguiente, que con libmpv era una llamada vacía), `ClaseDeEscalado` y la tarjeta de `AjustesInicialesMotor`. La interfaz se queda: `MotorDePrueba` es su segunda implementación y de ella dependen las pruebas del reproductor. No se toca lo que es neutral y sigue funcionando con libmpv (reintento por procesador, vigilante de arranque, coordinadores).
7. **La vista se llama `ReproductorView`.** Borrada la clásica, `ReproductorMpvView` pasa a llamarse así, en un commit propio y sin más cambios. `MotorMpv`, `SuperficieMpv` y `LibMpv` conservan el nombre: nombran la biblioteca.
8. **MaterialDesignThemes.** No existe una versión estable posterior a la que hay: la estable más reciente es la 5.3.2 y la del proyecto es `5.3.3-ci1462`, que Flyleaf obligaba a usar. Se intenta bajar a la 5.3.2 en un commit propio; si cambia un icono o un estilo, se deja la actual y solo se corrige el comentario (el motivo ya no es Flyleaf).
9. **Publicar, el aviso de la GPL en la release y archivar el paquete de libmpv** siguen en manos del propietario; al final va el texto actualizado.

## Restricciones globales

- **Sin commit ni push** hasta que el usuario lo ordene. Sin ramas, worktrees ni subagentes. Commits previstos, cada uno compila: (1) salida por software e interruptor, (2) retirada, (3) MaterialDesignThemes, (4) renombrado de la vista.
- **Borrar es la tarea:** nada de código nuevo en el bloque B salvo el aviso de la decisión 5. Cada borrado lo guía el compilador (0 advertencias) y la suite.
- **El total de pruebas baja a propósito** (se van las de lo borrado). El informe da la cuenta exacta: qué clases se retiran y cuántas pruebas llevaba cada una.
- **Textos visibles:** claves ES y EN en `LocalizationService`, `{loc:T}` en XAML (skill `localizacion`); las claves retiradas se borran de las dos mitades. Colores de la paleta. `AutomationProperties.Name` en el interruptor.
- **En la app nunca se pulsa «Guardar Preferencias»** ni la captura de fotograma; las opciones se preparan en el `settings.json` del perfil antes de lanzar.
- **Verificación de C#:** `repo-build-test`. **En la app:** `perfil-aislado`; nunca más del 85 % de un episodio real ni el último de un anime; el final de episodio, solo con clips de mentira de 30 s.
- `SubtitleTextView` no se borra: es la vista previa de Configuración.
- `AudioTrackPlayer` y `ClipPlayer` no usan Flyleaf (son el `MediaPlayer` de WPF): no se tocan.

## Foco de revisión

1. **`settings.json` de una versión anterior** con `"MotorVideo": "Flyleaf"`, `TarjetaGraficaVideo` y borde de caja: se carga, el episodio abre con libmpv y guardar no falla. → Tarea 4, `AjustesDeUnaVersionAnterior_SeLeenSinLasClavesRetiradas`.
2. **Sin `libmpv-2.dll`:** aviso una vez, sin bucle de reintentos y sin cerrar la app. → Tarea 3, `SinBiblioteca_ElReproductorAvisaYNoReintenta`.
3. **El lienzo OpenGL falla a mitad de sesión** (segundo episodio, mini reproductor): se sigue por software en ese episodio, donde iba. → Tarea 1, `SiElLienzoNoArranca_LaSuperficiePintaPorSoftware`, y tarea 9 en la app.
4. **Tampoco se puede por software:** un aviso de error, sin reabrir. → Tarea 3, `SiNoHaySalida_AvisaUnaVezYNoReabre`.
5. **Ventana enorme o cambio de tamaño continuo por software** (pantalla completa 4K, arrastrar el borde del mini): la imagen se recrea con el tope y sin acumular memoria. → Tarea 1, `TamanoPorSoftware_*`, y tarea 2 en la app.

---

## Mapa de archivos

| Archivo | Acción | Bloque |
|---|---|---|
| `Services/Native/LibMpv.cs` | Modificar: parámetros de pintado por software | A |
| `Services/PintorPorSoftware.cs` | Crear: el pintor de libmpv sobre un búfer de memoria (probable sin ventana) | A |
| `Controls/SuperficieMpv.cs` | Modificar: segunda forma de pintar y paso de una a otra | A |
| `Services/MotorMpv.cs` | Modificar: sabe si pinta por software (sin sombreadores) | A |
| `Models/AppSettings.cs`, `ViewModels/ConfiguracionViewModel.cs`, `Views/ConfiguracionView.xaml`, `Services/LocalizationService.cs` | Modificar: interruptor (A); retirar motor, tarjeta y borde de caja (B) | A y B |
| `ViewModels/ReproductorViewModel*.cs`, `App.xaml`, `App.xaml.cs`, `Services/MotorVideo.cs`, `Services/IMotorVideo.cs` | Modificar: un solo motor | B |
| `Services/MotorFlyleaf.cs`, `Services/ParcheSubtitulosFlyleaf.cs`, `Views/ReproductorView.xaml(.cs)` | Borrar | B |
| `Services/SubtitleAssRenderer.cs`, `Services/SubtitleCuesExtractorService.cs` (con sus interfaces), `Core/SubtitulosAss.cs`, `Core/SubtitulosSrtParser.cs`, `Core/SubtitulosSolapadosResolver.cs`, `Models/SubtitleCue.cs` | Borrar | B |
| `Services/EpisodeNavigator.cs`, `IEpisodeNavigator.cs` | Modificar: fuera la precarga | B |
| `AnimeLocalTracker.csproj`, `.github/dependabot.yml`, `tools/Get-FFmpegBinaries.ps1`, `build.ps1` | Modificar: paquetes y comentarios | B |
| `Views/ReproductorMpvView.xaml(.cs)` → `Views/ReproductorView.xaml(.cs)` | Renombrar | B |
| `Views/MiniReproductorWindow.xaml(.cs)`, `Views/AcercaDeView.xaml` | Modificar: comentarios; fuera la etiqueta «Flyleaf» | B |
| `AnimeLocalTracker.Tests/…` | Añadir, adaptar y borrar | A y B |
| `CLAUDE.md`, `README.md`, `docs/investigacion-motor-libmpv.md` | Modificar | B |

---

# Bloque A: salida por software

## Tarea 1: libmpv pinta por software

**Archivos:** `LibMpv.cs`, `PintorPorSoftware.cs` (nuevo), `SuperficieMpv.cs`, `MotorMpv.cs`, `AppSettings.cs`, `IMotorVideo.cs` (`AjustesInicialesMotor`), `ConfiguracionViewModel.cs`, `ConfiguracionView.xaml`, `LocalizationService.cs`, `MotorMpvTests.cs`, `RendimientoVideoTests.cs`.

**Interfaces que produce:**

```csharp
// LibMpv.cs
internal const int ParametroTamanoSw = 17, ParametroFormatoSw = 18, ParametroPasoSw = 19, ParametroPunteroSw = 20;

// PintorPorSoftware.cs: sin WPF, para poder probarlo sin ventana
internal sealed class PintorPorSoftware : IDisposable
{
    public static PintorPorSoftware? Crear(MotorMpv motor, Action hayFotograma); // null si libmpv lo rechaza
    /// <returns>True si lo pintado es un fotograma nuevo.</returns>
    public bool Pintar(nint bufer, int ancho, int alto, int paso);              // formato bgr0, sin esperar al momento del fotograma
    /// <summary>El tamaño al que se pinta: el de la zona de video en píxeles, con tope y la misma proporción.</summary>
    public static (int Ancho, int Alto) Tamano(double anchoPx, double altoPx, int topeAncho = 1920, int topeAlto = 1080);
}

// MotorMpv.cs
internal bool PintaPorSoftware { get; set; }   // con true, EscaladoInteligente no carga sombreadores y TarjetaEnUso es null

// IMotorVideo.cs
public sealed record AjustesInicialesMotor(bool AceleracionPorHardware, string? TarjetaGrafica, IReadOnlyList<FiltroAudio> FiltrosAudio, bool VideoPorSoftware = false);

// AppSettings.cs
public bool VideoPorSoftware { get; set; }
```

`SuperficieMpv` queda con dos caminos: el lienzo OpenGL de hoy o una `Image` (`Stretch="Fill"`) con un `WriteableBitmap` que se recrea cuando cambia `Tamano(...)`. `Para(motor)` prueba el lienzo salvo que el ajuste o `ANIMELOCALTRACKER_SIN_OPENGL=1` pidan software; si el lienzo lanza al crearse, o `mpv_render_context_create("opengl")` falla después, la misma superficie cambia a software (quita el lienzo, pone la imagen) y lo anota una vez en el registro. Solo si `PintorPorSoftware.Crear` devuelve null se llama a `motor.AvisarSinSalida`. El pintor por software también llama a `motor.PintorCreado()`: el archivo no se carga hasta tener dónde pintar.

- [x] **Paso 1: pruebas que fallan.**
  - `TamanoPorSoftware_RespetaElTopeYLaProporcion` (teoría: 1366×705 → igual; 3840×2160 → 1920×1080; 2560×1080 → 1920×810; 0×0 → 16×16).
  - `PintorPorSoftware_PintaUnFotogramaDeUnClipReal`: motor real con `vo=libmpv` y `ao=null` (variante nueva de `MotorMpv.Crear` para pruebas), clip de colores generado con el FFmpeg de la app, búfer de 320×180: tras abrir y esperar el aviso, el búfer no es todo negro. Se salta si falta la biblioteca, como las demás de `MotorMpvTests`.
  - `PorSoftware_ElEscaladoInteligenteNoCargaSombreadores`.
  - `SiElLienzoNoArranca_LaSuperficiePintaPorSoftware` (colección `WpfSmoke`, con la variable de diagnóstico): `Para` devuelve una superficie, no null, y el motor no avisa `SinSalida`.
  - `Configuracion_ElModoDeCompatibilidadSeCargaYSeGuarda`.
- [x] **Paso 2:** verlas fallar.
- [x] **Paso 3:** implementar. Fila nueva en Configuración → Reproducción (debajo de «Aceleración por hardware»): `Cfg_VideoCompatibilidad` = «Modo de compatibilidad de video» / «Compatibility video mode»; `Cfg_VideoCompatibilidadSub` = «Pinta el video sin usar OpenGL. Actívalo solo si el video se ve negro o con fallos: gasta más procesador y desactiva el escalado inteligente. Se aplica al abrir el siguiente episodio.» y su traducción.
- [x] **Paso 4:** compilar (0 advertencias) y `--filter "FullyQualifiedName~MotorMpv|FullyQualifiedName~RendimientoVideo|FullyQualifiedName~Configuracion|FullyQualifiedName~Vistas"`.

## Tarea 2: medirlo dentro de la app (decide si hay bloque B)

Perfil aislado con rutas reales, `ANIMELOCALTRACKER_SIN_OPENGL=1`, registro detallado. Los mismos dos episodios de la etapa 5 (Youjo Senki 02, H.264 1080p; Tokyo Revengers 02, AV1 1080p 10 bits), 60 s desde el minuto 2, y uno con subtítulos ASS con carteles.

- [x] Con los controles ocultos y con los controles visibles: fotogramas y perdidos (línea «Al salir» del registro), procesador y memoria del proceso. Lo mismo con el lienzo OpenGL, para comparar.
- [x] Pausa, salto, cambio de pista de subtítulos, pantalla completa, mini reproductor y vuelta, arrastrar el borde del mini (queda pendiente desde la etapa 5), `Esc`.
- [x] Coste a tamaños que esta pantalla (1366×768) no da: el arnés de la etapa 5 con la imagen a 1920×1080 y a 3840×2160. Con eso se confirma o se cambia el tope de la decisión 3.
- [x] Cerrar por PID, borrar el perfil, 0 diferencias en los datos reales.
- [x] Anotar la tabla en `docs/investigacion-motor-libmpv.md` (sección 5).

**Criterios para seguir con el bloque B:** a 1366×768, menos de un 1 % de fotogramas perdidos en los tres videos con los controles visibles; la interfaz responde (pausa y salto sin retraso apreciable); los subtítulos ASS se ven; el mini reproductor no corta el video. **Si alguno falla, se para aquí y se informa:** Flyleaf se queda hasta decidir.

**Sin probar hasta entonces** (el arnés de la etapa 5 medía sin subtítulos ni controles): que libmpv dibuje los subtítulos en la salida por software, y que la captura de fotograma funcione en ese modo (la cubre una prueba sin pantalla; no se pulsa en la app).

---

# Bloque B: la retirada

## Tarea 3: el reproductor con un solo motor

**Archivos:** `ReproductorViewModel.cs`, `App.xaml`, `MotorVideo.cs`, `LocalizationService.cs`, `ReproductorConMotorTests.cs`.

- [x] **Paso 1: pruebas que fallan.** `SinBiblioteca_ElReproductorAvisaYNoReintenta` (fábrica que devuelve null: un aviso con `Player_ErrorSinMotor`, `Motor` null, ningún reintento) y `SiNoHaySalida_AvisaUnaVezYNoReabre` (el motor de prueba avisa dos veces sin salida: un solo aviso con `Player_ErrorSinVideo`, ninguna apertura nueva).
- [x] **Paso 2:** verlas fallar.
- [x] **Paso 3:** implementar.
  - Fuera `Player`, `FabricaMotorClasico`, `UsaMotorMpv`, `PasarAlMotorClasico` y la elección de motor del constructor: la fábrica por defecto es la de libmpv. `SinSalida` lleva al aviso de error (lo que hoy hace `PasarAlMotorClasico` cuando tampoco hay clásico).
  - `App.xaml`: la plantilla del reproductor es una sola vista, sin disparador.
  - `MotorVideo.cs`: fuera `MotoresDeVideo`, `ElegirMotor`, `LienzoNoDisponible`.
  - Textos: `Player_ErrorSinVideo` = «Este equipo no puede mostrar el video.»; nuevo `Player_ErrorSinMotor` = «Falta el motor de video de la instalación. Vuelve a instalar la aplicación.» (ES y EN).
  - Se retiran las cuatro pruebas del modo de compatibilidad con el clásico y las de `ElegirMotor`.
- [x] **Paso 4:** compilar y `--filter "FullyQualifiedName~Reproductor"`.

## Tarea 4: Configuración sin motor, tarjeta ni borde de caja

**Archivos:** `AppSettings.cs`, `EstiloSubtitulos.cs`, `SubtitleTextView.cs`, `ConfiguracionViewModel.cs`, `ConfiguracionView.xaml`, `MotorVideo.cs`, `LocalizationService.cs`, `RendimientoVideoTests.cs`.

- [x] **Paso 1: prueba que falla.** `AjustesDeUnaVersionAnterior_SeLeenSinLasClavesRetiradas`: un `settings.json` en carpeta temporal con `"MotorVideo": "Flyleaf"`, `"TarjetaGraficaVideo": "Intel(R) HD Graphics 620"` y borde de caja se carga sin error, se guarda, y el archivo guardado ya no trae esas claves.
- [x] **Paso 2:** verla fallar. **Paso 3:** implementar.
  - Fuera las filas «Motor de video» y «Tarjeta gráfica», los dos controles del borde de la caja y sus separadores; `UsaMotorNuevo`, `MotorNuevoDisponible`, `HayMotorNuevo`, `MotorDeVideo`, `TarjetasGraficas`, `OpcionTarjetaGrafica`, `EstiloGrosorBorde`, `EstiloColorBorde`.
  - «Escalado inteligente» se queda con la descripción de Anime4K (el texto de `Cfg_EscaladoInteligenteSubMpv` pasa a `Cfg_EscaladoInteligenteSub`).
  - `MotorVideo.cs`: fuera `AsegurarIniciado`, `PrecargarBibliotecas`, `ListarTarjetas`, `PatronTarjeta`, `FabricanteDe`, `EscaladoDisponible` y el `using FlyleafLib`; `UsarEscaladoInteligente` pierde la rama del escalado por controlador.
  - Claves de texto retiradas en ES y EN: `Cfg_MotorVideo*` (5), `Cfg_TarjetaGrafica`, `Cfg_TarjetaGraficaSub`, `Cfg_TarjetaAutomatica`, `Cfg_TarjetaNoConectadaFormato`, `Cfg_EstiloBordeGrosor(Sub)`, `Cfg_EstiloBordeColor(Sub)`, `Cfg_EscaladoInteligenteSubMpv`.
  - La vista previa de subtítulos deja de dibujar el borde de la caja (el reproductor ya no lo dibujaba).
- [x] **Paso 4:** compilar y `--filter "FullyQualifiedName~Configuracion|FullyQualifiedName~RendimientoVideo|FullyQualifiedName~Localiz|FullyQualifiedName~Vistas|FullyQualifiedName~Subtit"`.

## Tarea 5: borrar el motor clásico y los subtítulos que dibujaba la app

- [x] **Paso 1:** borrar `MotorFlyleaf.cs`, `ParcheSubtitulosFlyleaf.cs`, `Views/ReproductorView.xaml(.cs)`, `SubtitleAssRenderer.cs`, `SubtitleCuesExtractorService.cs` y sus interfaces, `Core/SubtitulosAss.cs`, `Core/SubtitulosSrtParser.cs`, `Core/SubtitulosSolapadosResolver.cs`, `Models/SubtitleCue.cs`, y sus pruebas (`ParcheSubtitulosFlyleafTests` 4, `SubtitleAssRendererTests` 12, `SubtitleCuesExtractorServiceTests` 3, `SubtitulosAssTests` 6, `SubtitulosSolapadosResolverTests` 6, `SubtitulosSrtParserTests`).
- [x] **Paso 2:** `ReproductorViewModel.Subtitulos.cs` se queda con lo que usa libmpv: la pista elegida, `PistaActualEsAss`, `UsarMiEstiloEnAss` y aplicar el estilo al motor. Fuera las líneas solapadas, la extracción y el dibujo ASS; fuera sus dos parámetros del constructor y sus dos registros en `App.xaml.cs`.
- [x] **Paso 3:** contrato (decisión 6): fuera `DibujaSubtitulosDeTexto`, `PistaSubtitulosDe`, `PrecalentarArchivo`, `ClaseDeEscalado`/`ClaseEscalado` y la tarjeta de `AjustesInicialesMotor`; fuera la precarga de `EpisodeNavigator` y sus pruebas. `MotorDePrueba` y `MotorMpv` se ajustan.
- [x] **Paso 4:** `App.xaml.cs`: fuera `IniciarMotorDeVideoEnReposo` y su llamada.
- [x] **Paso 5:** comentarios que nombran a Flyleaf en el código que se queda (`FrameCaptureService`, los tres coordinadores, `EcualizadorAudio`, `OpcionPistaAudio`, `SubtitleTextView`, `MiniReproductorWindow`, `AudioTrackPlayer`, `ClipPlayer`, la vista y `ReproductorViewModel`): se corrigen a lo que hace el código hoy. `AcercaDeView` y `Acerca_Tecnologias` pierden «Flyleaf» (y la lista de `VistasSinTextoFijoTests`).
- [x] **Paso 6:** compilar (0 advertencias) y la suite completa. `grep -ri flyleaf` sobre `AnimeLocalTracker` y `AnimeLocalTracker.Tests` solo puede devolver la prueba de la tarea 4 (que usa el valor antiguo a propósito).

## Tarea 6: paquetes

- [x] `AnimeLocalTracker.csproj`: fuera `Flyleaf.FFmpeg`, `FlyleafLib`, `FlyleafLib.Controls.WPF`, `Lib.Harmony` y `PublishReadyToRunExclude` de `FlyleafLib.dll`. `Vortice.XInput` se queda (mando).
- [x] `.github/dependabot.yml`: fuera el grupo `flyleaf`.
- [x] `tools/Get-FFmpegBinaries.ps1` y `build.ps1`: los comentarios dicen para qué quedan las bibliotecas de FFmpeg (solo `ffmpeg.exe` y `ffprobe.exe`).
- [x] Compilar limpio (`obj` y `bin` borrados) y comprobar en `bin` que no quedan `Flyleaf*.dll`, `0Harmony.dll` ni las `Vortice.*` que traía Flyleaf. Suite completa. Anotar cuánto baja la carpeta.
- [x] Medir la primera apertura de un episodio tras abrir la app (antes 1,3–1,4 s). Solo si cargar la biblioteca explica 0,3 s o más, cargarla en reposo (`Task.Run(() => LibMpv.Disponible)` donde estaba la precarga de FFmpeg) y anotar la memoria que cuesta.

## Tarea 7: MaterialDesignThemes a la versión estable (commit propio)

- [x] Prueba nueva `LosIconosDeLasVistas_ExistenEnLaBiblioteca`: todo `Kind="…"` de los XAML es un valor de `PackIconKind`.
- [x] Bajar a `5.3.2`, compilar, suite completa. En la app (perfil aislado): galería, ficha, reproductor con controles, Configuración, minijuegos y Acerca de, comparadas con capturas de antes del cambio.
- [x] Si todo coincide: fuera el `ignore` de `dependabot.yml`. Si algo cambia: se deja `5.3.3-ci1462`, se corrige el comentario del proyecto y se conserva el `ignore`.

## Tarea 8: la vista recupera su nombre (commit propio)

- [x] `git mv` de `ReproductorMpvView.xaml(.cs)` a `ReproductorView.xaml(.cs)`; clase, `x:Class`, plantilla de `App.xaml`, etiquetas del registro y pruebas (`ReproductorToastTests`, `EstadisticasViewTests`, `VistasSinTextoFijoTests`). Ningún otro cambio. Compilar y `--filter "FullyQualifiedName~Views"`.

## Tarea 9: verificación en la app

- [x] **Con el lienzo OpenGL** (perfil con rutas reales, `settings.json` copiado tal cual, y otro con `"MotorVideo": "Flyleaf"` escrito): abre con libmpv; pausa, volumen, saltos, subtítulos y «usar mi estilo», pista de audio, ecualizador, mini reproductor, pantalla completa, «Continuar» desde Historial, `Esc`.
- [x] **Por software** (interruptor en el `settings.json` del perfil, y aparte con la variable): lo mismo; el registro dice una vez por qué pinta por software.
- [x] **Final de episodio** con clips de mentira de 30 s (perfil con rutas reescritas, `UmbralMarcadoVisto` 50): marcar visto, cuenta atrás, siguiente, y un `.srt` suelto.
- [x] **Configuración** en ES y EN: no están las filas retiradas, está la nueva con su nombre accesible, la vista previa de subtítulos se ve bien.
- [x] **Memoria en reposo** tras abrir la app y esperar 30 s, antes y después de la retirada.
- [x] Cerrar por PID, borrar perfiles, 0 diferencias en los datos reales y en las carpetas de video.
- [x] Suite completa una vez con el código final.

## Tarea 10: documentación

- [x] `CLAUDE.md` (primera línea y estructura), `README.md`, `docs/investigacion-motor-libmpv.md` (estado de las etapas, modo de compatibilidad definitivo, tabla de la tarea 2).
- [x] Notas de ejecución al final de este plan y memoria del proyecto.
- [x] Informe al usuario y esperar la orden para los commits.

## Texto para la página de la release (lo publica el propietario)

> **Motor de video nuevo.** Desde esta versión los episodios se reproducen con mpv (libmpv), que dibuja los subtítulos con su estilo original y permite mejorar la imagen con Anime4K al ampliar videos de baja resolución. Si el video se ve negro o con fallos, activa Configuración → Reproducción → Modo de compatibilidad de video.
>
> Ya no están los ajustes «Tarjeta gráfica» ni el borde de la caja de subtítulos. El instalador crece unos 120 MB. Incluye mpv (compilación de shinchiro `20261011-git-accb7b2821`), distribuido bajo la GPL versión 2 o posterior; su código fuente está en https://github.com/mpv-player/mpv. Los shaders de Anime4K son de bloc97 (licencia MIT). El código de AnimeLocalTracker sigue bajo la licencia MIT.

## Lo que esta etapa no hace

- No cambia la carpeta `FFmpeg` ni cómo se localizan `ffmpeg.exe` y `ffprobe.exe`.
- No detecta sola un lienzo que se crea pero pinta en negro: para eso está el interruptor.
- No archiva el paquete de libmpv en una release del repositorio (pendiente del propietario; el proveedor solo conserva unos dos meses de compilaciones).

---

## Notas de ejecución (2026-10-11)

**Hecho:** las diez tareas. Subida el 2026-10-11 en los cuatro commits previstos y uno de documentación: salida por software `594921c`, retirada `0a9a822`, MaterialDesignThemes `e7ee405`, renombrado de la vista `4b54579`.

**La medición de la tarea 2 pasó los cuatro criterios** (tabla en la sección 5 del diseño): por software se pierde como mucho un 0,25 % de los fotogramas (0,06 % con los controles visibles), los subtítulos ASS se ven con sus carteles, pausa y saltos responden y el mini reproductor no corta el video. Por eso se siguió con el bloque B en la misma sesión; no se publicó una versión entre A y B (decisión 1): el propietario ordenó subirlo todo junto.

**Lo que cambió respecto al plan**

- **`SuperficieMpv.Para` ya no devuelve null** y la superficie recibe el motor en el constructor (se quitó la propiedad de dependencia `Motor`, que nadie enlazaba). El paso a software tras un rechazo de `mpv_render_context_create("opengl")` se hace en la siguiente vuelta del despachador, porque el aviso llega a medio pintar en el lienzo que hay que quitar.
- **`PintorPorSoftware` llama él mismo a `PintorCreado`/`PintorSoltado`**: así se prueba sin ventana. `MotorMpv.Crear` ganó `sinSonido` (imagen sin tarjeta de sonido, para el CI).
- **La vista clásica se borró en la tarea 3**, no en la 5: sin `ReproductorViewModel.Player` no compilaba. `MotoresDeVideo` se fue en la tarea 4, con sus usos de Configuración.
- **El aviso de «sin video» es uno por reproductor** (`_sinVideo`): tras darlo, ese reproductor no vuelve a crear motor ni en el episodio siguiente. Bajo pruebas sin motor inyectado no hay fábrica (ni aviso).
- **`UsarEscaladoInteligente(modo)`** perdió también los parámetros de fabricante y memoria, que solo servían a la rama del controlador.
- **Diagnóstico «Tu equipo»:** la lista de tarjetas venía de Flyleaf. Ahora se muestra la tarjeta que pintó el último video de la sesión; antes de abrir ninguno (o pintando por software) solo los hilos del procesador, con una clave nueva (`Cfg_DiagEquipoSinTarjetaFormato`) en vez de un «?».
- **Además de lo listado se quitó:** `LibMpv.EnLaInstalacion`, `MiniReproductorWindow.EnBordeDeRedimension` (dejaba pasar los bordes a través de las ventanas nativas de Flyleaf), `IEpisodeNavigator : IDisposable` (solo desechaba la precarga), las pruebas `ReproductorViewModelSubtitulosAssTests` (8) y las notas de versión por defecto («con Flyleaf y DirectX» → «con mpv»).
- **MaterialDesignThemes baja a 5.3.2:** ningún nombre que use la app (vistas o código) existe solo en la compilación ci1462; ficha, Configuración, minijuegos y Acerca de salen idénticos píxel a píxel con las dos versiones, y los controles del reproductor también. Fuera el `ignore` de Dependabot.
- **No se carga libmpv en reposo:** cargar la biblioteca tarda 19 ms con el archivo en la caché de disco, y la primera apertura tras abrir la app queda en 1,0–1,2 s (antes 1,3–1,4 s).

**Cuentas.** Suite: 3230 antes de la etapa → 3239 con el bloque A (+9) → 3129 al final (−115 casos de lo retirado, +5 nuevos). Carpeta de salida: 469 → 462 MB; ya no están `Flyleaf*.dll`, `0Harmony.dll`, `Dragablz.dll`, `WpfColorFontDialog.dll`, `SharpGen.*` ni las `Vortice.*` salvo `XInput`. Memoria privada en reposo (55 s tras abrir, dos medidas de cada): 270 MB antes, 238–245 MB después (conjunto de trabajo 389 → 372 MB): menos de lo que sugería el plan, porque las bibliotecas de FFmpeg cargadas por adelantado eran sobre todo páginas compartidas.

**Verificado en la app** (Intel HD 620, 1366×768, perfiles aislados; 0 diferencias en los datos reales salvo `biblioteca.db-shm` al crear cada perfil, y 0 en las carpetas de video):

| Caso | Resultado |
|---|---|
| OpenGL, `settings.json` con `"MotorVideo": "Flyleaf"`, tarjeta y borde de caja | Abre con libmpv (Intel HD 620, Anime4K ligero); pausa, saltos, «Usar mi estilo» y menú de audio responden; al guardarse una preferencia las cuatro claves antiguas desaparecen del archivo |
| OpenGL: pantalla completa (F11), mini reproductor y vuelta | El video sigue (1870 fotogramas, 7 perdidos) |
| Interruptor `VideoPorSoftware: true` | El registro dice una vez «lo pide el modo de compatibilidad de Configuración»; cambio a la pista en inglés, «Usar mi estilo» y ecualizador funcionan (3306 fotogramas, 7 perdidos) |
| `ANIMELOCALTRACKER_SIN_OPENGL=1` | Tarea 2: 12 corridas de medición y una sesión con pausa, saltos, pista, pantalla completa, mini y arrastre del borde |
| Final de episodio (clips de 30 s, umbral al 50 %) | El 1 se marca visto, sale la cuenta atrás («Siguiente episodio en · Episodio 3 · 5s» capturada) y abre el siguiente a los 5 s; lo mismo del 2 al 3 |
| `.srt` suelto junto al episodio 2 | «Pistas de subtítulos disponibles: 1» y el texto en pantalla con el estilo de Configuración |
| Configuración → Reproducción, ES y EN | No están «Motor de video», «Tarjeta gráfica» ni el borde de la caja; está «Modo de compatibilidad de video» / «Compatibility video mode» con su nombre accesible; «Tu equipo: 4 hilos de procesador»; la vista previa de subtítulos se ve bien |

**No verificado en la app:** un equipo que de verdad no tenga OpenGL (se simuló con la variable y con el interruptor; que `GLWpfControl.Start` lance ahí es lo esperado, no lo comprobado); el paso a software cuando libmpv rechaza un lienzo ya creado (solo por lectura del código); una pantalla con la escala de Windows por encima del 100 %; la captura de fotograma (no se pulsa: la cubre una prueba, también por software); el botón de pantalla completa de la barra (se usó F11: el script no encontró el botón por UIA); cambiar de pista de audio (el archivo de prueba solo tiene una); y «Guardar Preferencias» (no se pulsa: lo cubren las pruebas).

**Límite conocido:** el interruptor se lee al crear el motor, que vive lo que el reproductor. Si se cambia con un episodio en el mini reproductor, se aplica al cerrar ese reproductor y abrir otro episodio, no al pasar al siguiente dentro del mismo.

**Prueba intermitente ajena a la etapa:** `DetalleExtrasTests.Espacio_ConTokenYaCancelado_NoEscaneaNiActualizaLaFicha` falló en 2 de las 4 últimas pasadas completas («Expected EspacioEnDiscoTexto to be empty, but found "2 KB"») y nunca sola ni junto a las 961 pruebas de ViewModels. Es una carrera de la propia prueba (un cálculo de espacio en segundo plano de la ficha que termina después de que la prueba ponga el texto a vacío); ni la ficha ni esa prueba se tocaron aquí. No se arregló: queda para una tarea aparte.

**Revisión final:** hecha por quien implementó (en este repo no se lanzan subagentes sin pedirlo); los cinco puntos del foco de revisión quedan cubiertos por las pruebas que nombra el plan y por la tabla de arriba.
