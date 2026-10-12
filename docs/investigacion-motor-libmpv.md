# Diseño: migrar el reproductor de episodios de Flyleaf a libmpv

Fecha: 2026-10-10. Estado al 2026-10-11: las seis etapas están hechas; la 6 (retirada de Flyleaf) está implementada y verificada, a falta de subirla. Cada etapa tiene su plan con notas de ejecución (`docs/plan-motor-libmpv-etapa-N.md`). Lo que sigue describe el diseño tal como se pensó; donde la etapa 6 lo cambió, se dice.

Antecedentes: sección 5.1 de `docs/investigacion-ecosistema-poliglota.md` (por qué se evaluó y el primer prototipo).

## 1. Qué se quiere conseguir

Decidido por el propietario del proyecto:

1. **Memoria estable** al encadenar episodios (con Flyleaf se quedan unos 17 MB por episodio, PERF-02).
2. **Quitar los parches**: el parche de Harmony sobre el lector de subtítulos, el dibujante propio de ASS, el reintento de AV1, la lectura de contadores privados por reflexión y la versión de integración continua de MaterialDesign que Flyleaf obliga a usar.
3. **Mejor imagen**: Anime4K.
4. **Apertura y saltos rápidos.**

Condiciones:

- El reproductor conserva todo lo que hace hoy.
- La migración va **por etapas y con interruptor**: los dos motores conviven hasta que el nuevo iguale al actual.
- Se usa la **compilación GPL ya hecha** de libmpv (shinchiro, la que enlaza mpv.io). El código de la app sigue bajo MIT; el paquete que se distribuye pasa a incluir un componente GPL.

## 2. Alcance

Flyleaf **solo** mueve el reproductor de episodios. La música de AnimeThemes (`AudioTrackPlayer`) y los clips de los minijuegos (`ClipPlayer`) usan el `MediaPlayer` de WPF y no entran.

Dependen hoy de Flyleaf:

| Pieza | Papel |
|---|---|
| `ViewModels/ReproductorViewModel*.cs` (3.056 líneas en tres archivos) | Usa `Player` directamente (unas 35 funciones distintas) |
| `Views/ReproductorView.xaml` y `.xaml.cs` (1.285 + 1.157 líneas) | `FlyleafHost`: video y controles son dos ventanas nativas superpuestas |
| `Views/MiniReproductorWindow` | Aloja la misma vista en una ventana flotante |
| `Services/PlaybackSeekCoordinator`, `PlaybackVolumeCoordinator`, `SubtitleCoordinator`, `FrameCaptureService` | Reciben un `Player` en sus métodos |
| `Services/EpisodeNavigator` | Lectura adelantada del episodio siguiente con un `Demuxer` de Flyleaf |
| `Services/MotorVideo` | Arranque del motor, tarjetas gráficas, política del modo automático |
| `Services/ParcheSubtitulosFlyleaf`, `SubtitleAssRenderer`, `SubtitleCuesExtractorService`, `Core/SubtitulosAss` | Subtítulos leídos y dibujados por la app para esquivar el lector de Flyleaf |
| `Controls/SubtitleTextView` | La capa de texto con el estilo del usuario. La usa también la vista previa de Configuración, así que **no se borra**: deja de usarse en el reproductor |
| `Core/EcualizadorAudio` | Filtros de audio de FFmpeg que se le pasan a Flyleaf |

Fuera de alcance: el mando web, cambios en los controles del reproductor que no pida la migración, y sustituir `ffmpeg.exe`/`ffprobe.exe` (siguen haciendo falta para miniaturas, datos técnicos y detección de openings).

## 3. Lo que ya está medido

Equipo de referencia: portátil con Intel HD 620, pantalla de 1366×768. mpv 0.41 (compilación de shinchiro del 2026-10-11). Arneses desechables fuera del repositorio; videos de la biblioteca en solo lectura y sin sonido.

### 3.1 Prototipo del motor (ventana nativa)

| Qué se midió | Resultado |
|---|---|
| Re:Zero T4 ep. 19 (el ASS que cerraba la app) | Abre en 0,6 s; 27 saltos exactos con mediana de 34 ms (máximo 57); ningún fotograma perdido; subtítulos con su estilo |
| Tokyo Revengers T4 ep. 2 (AV1 1080p) | Abre a la primera en 0,3 s; decodifica por software (la HD 620 no decodifica AV1) con 35–46 % de un núcleo y ningún fotograma perdido |
| Youjo Senki ep. 2 (1080p, ASS con estilos) | Cursivas y posición correctas; saltos de ~30 ms |
| Memoria, 10 episodios H.264 seguidos | 180–212 MB de memoria privada, sin crecer (−2,6 MB por episodio) |
| Memoria, 8 episodios AV1 seguidos | 285–292 MB, sin crecer (−0,8 MB por episodio) |
| Cambio de episodio | 0,1–0,15 s del segundo en adelante |

### 3.2 Dónde se pinta el video

Se compararon dos caminos, 25 s de reproducción por caso con la ventana maximizada:

| Caso | A: ventana nativa (como hoy) | B: dentro de la ventana de WPF |
|---|---|---|
| 720p H.264 | 0 perdidos, 2 % de un núcleo | 0 perdidos, 10 % |
| 1080p H.264 | 0 perdidos, 3 % | 0 perdidos, 9 % |
| 1080p AV1 | 0 perdidos, 20 % | 0 perdidos, 22–39 % (varió entre pasadas) |
| 1080p + Anime4K ligero | 0 perdidos | 0 perdidos |
| 1080p + Anime4K medio | 154 perdidos | 237 perdidos (14,6 de 24 por segundo) |
| 1080p + Anime4K muy pesado | no concluyente | 4 de 24 por segundo |

En B se comprobó con una captura que un panel translúcido y un deslizador de WPF se componen encima del video en una sola ventana.

**Decisión: camino B.** Cuesta unos 7 puntos de procesador en H.264 (el fotograma decodificado por la gráfica se copia una vez más: `d3d11va-copy`), pero los controles pasan a ser WPF normal. Anime4K solo es viable en su nivel ligero en este equipo, por cualquiera de los dos caminos.

No medido: la fluidez percibida (los contadores no dicen si un movimiento de cámara se ve suave), el mini reproductor y el pintado por software.

## 4. Arquitectura

**Un solo reproductor con el motor intercambiable.** `ReproductorViewModel` conserva su lógica (progreso, «visto», AniList, saltar opening, episodio siguiente, menús) y deja de hablar con Flyleaf: habla con `IMotorVideo`, que tiene dos implementaciones. Un ajuste elige cuál.

Se descartó copiar el reproductor entero para libmpv: duplicaría 3.000 líneas de lógica afinada y cualquier comportamiento que se le escapara a la copia se perdería sin aviso.

### 4.1 Piezas

| Pieza | Qué hace | Etapa |
|---|---|---|
| `Services/IMotorVideo.cs` | El contrato del motor (4.2) | 1 |
| `Services/MotorFlyleaf.cs` | Envuelve el Flyleaf de hoy y se lleva dentro sus rodeos: vigilancia del arranque, reapertura de AV1, contador de fotogramas por reflexión, ventanas de espera de los saltos, lectura adelantada del episodio siguiente | 1 |
| `MotorDePrueba` (proyecto de pruebas) | Motor de mentira para probar el reproductor sin video | 1 |
| `Services/Native/LibMpv.cs` | Llamadas a `libmpv-2.dll` con `[LibraryImport]`, comprobación `Disponible` y degradación limpia si falta | 2 |
| `Services/MotorMpv.cs` | libmpv detrás de `IMotorVideo`: órdenes, propiedades observadas y eventos en un hilo propio, que saltan al hilo de la interfaz antes de avisar | 2 |
| `Controls/SuperficieMpv.cs` | Lienzo OpenGL que WPF compone como una imagen, donde libmpv pinta cada fotograma | 2 |
| `Views/ReproductorMpvView.xaml` | Vista nueva: la superficie y, encima, los controles de hoy como WPF normal | 2 |

### 4.2 El contrato del motor

Recoge solo lo que el reproductor le pide hoy a Flyleaf. **La forma definitiva está en `docs/plan-motor-libmpv-etapa-1.md` (tarea 1)**, cerrada tras recorrer cada llamada; lo de abajo es el esbozo previo, que se conserva para ver de dónde se partió. Cambió en tres cosas: la apertura es una orden más un aviso (no una tarea que se espera), el contrato es más ancho (fotogramas mostrados, decodificación, tarjeta, proporción) porque la coreografía del arranque se queda en el reproductor, y no hay interfaz aparte para los subtítulos dibujados por la app.

```csharp
public enum EstadoMotor { Detenido, Abriendo, Reproduciendo, EnPausa, Terminado, Fallido }

/// <summary>Una pista de audio o de subtítulos, sin tipos del motor.</summary>
public sealed record PistaMedia(int Id, string? Idioma, string? Titulo, string Codec, bool EsExterna, bool Elegida);

public interface IMotorVideo : IDisposable
{
    EstadoMotor Estado { get; }
    TimeSpan Posicion { get; }
    TimeSpan Duracion { get; }
    event EventHandler<EstadoMotor>? EstadoCambiado;
    event EventHandler<AperturaTerminada>? AperturaTerminada;   // éxito o fallo, códec, tamaño, si decodifica la gráfica

    Task AbrirAsync(string ruta, TimeSpan? desde = null, CancellationToken ct = default);
    void Reproducir();
    void Pausar();
    void Detener();
    void Saltar(TimeSpan destino, bool exacto);
    double Velocidad { get; set; }
    int Volumen { get; set; }
    bool Silencio { get; set; }

    IReadOnlyList<PistaMedia> PistasAudio { get; }
    IReadOnlyList<PistaMedia> PistasSubtitulos { get; }
    void ElegirAudio(int id);
    void ElegirSubtitulos(int? id);                              // null = apagados

    void AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros); // ecualizador y modo noche
    Task<BitmapSource?> CapturarFotogramaAsync();
    void ConfigurarRendimiento(AjustesRendimientoVideo ajustes);  // aceleración, tarjeta, saltos, escalado
    InformeVideo? Informe { get; }
}
```

`AperturaTerminada` y `AjustesRendimientoVideo` son registros nuevos sin tipos del motor; `FiltroAudio` e `InformeVideo` ya existen.

Lo que solo tiene sentido con Flyleaf (el texto del subtítulo para la capa de WPF, el dibujante de ASS, la resolución de líneas solapadas) no entra en el contrato común. En el plan de la etapa 1 se resolvió sin interfaz aparte: el reproductor trabaja con pistas propias y la vista clásica llega al `Player` de Flyleaf por una propiedad de paso que se borra con él.

### 4.3 Qué cambia en lo que ya existe

- `ReproductorViewModel` y los coordinadores de saltos, volumen, subtítulos y captura reciben `IMotorVideo` en vez de `Player`. Su lógica no cambia.
- Todo rodeo específico de Flyleaf se muda a `MotorFlyleaf`; no se queda en el reproductor detrás de condicionales.
- `MotorVideo` (arranque y política del modo automático) pasa a decidir también qué motor crear.

### 4.4 Cómo se traduce cada función a libmpv

| Función | En libmpv |
|---|---|
| Abrir, reanudar | `loadfile` con la opción `start` del archivo; eventos de archivo cargado, primer fotograma y fin |
| Posición, duración, estado | Propiedades observadas: `time-pos`, `duration`, `pause`, `core-idle`, `eof-reached` (con `keep-open`) |
| Saltos | `seek … absolute+exact` o `absolute+keyframes` |
| Velocidad, volumen, silencio | `speed`, `volume`, `mute` |
| Pistas | `track-list`; `aid` y `sid` |
| Subtítulos ASS con su estilo | Nativo (libass), incluidas fuentes adjuntas y líneas solapadas |
| «Usar mi estilo» | `sub-ass-override=force` |
| Estilo de Configuración | `sub-font`, `sub-font-size`, `sub-color`, `sub-border-color`, `sub-border-size`, `sub-shadow-offset`, `sub-margin-y` |
| Ecualizador y modo noche | `af` con los mismos filtros de FFmpeg, etiquetados para cambiarlos en vivo |
| Captura | `screenshot-raw` |
| Aceleración por hardware | `hwdec=auto-copy` o `no` |
| Informe de video | `video-codec`, `hwdec-current`, `width`, `height` |
| Anime4K | `glsl-shaders` |

libmpv se arranca siempre con `config=no`, `load-scripts=no`, `ytdl=no`, `input-default-bindings=no` y `osc=no`: no lee la configuración ni los scripts de mpv del usuario, no resuelve direcciones de internet y no reacciona al teclado por su cuenta. Solo abre los archivos locales que la app ya valida.

**Los subtítulos los dibuja siempre libmpv**, también los de texto plano con el estilo de Configuración. Puede haber diferencias pequeñas de aspecto frente a la capa de WPF de hoy (grosor del contorno, sombra) que se ajustan a ojo en la etapa 3. La vista previa de Configuración sigue siendo la capa de WPF (`SubtitleTextView`): el ajuste de la etapa 3 consiste en que lo que se ve en el reproductor se parezca a esa vista previa.

## 5. La vista

**Pintado.** `SuperficieMpv` usa `OpenTK.GLWpfControl` (OpenGL compartido con WPF a través de Direct3D) y la interfaz de pintado de libmpv (`vo=libmpv`). libmpv avisa cuando hay fotograma nuevo y la superficie lo pinta en el siguiente paso de composición de WPF, sin bloquear el hilo de la interfaz.

**Controles.** `ReproductorMpvView` copia el árbol de controles de `ReproductorView` y lo coloca encima de la superficie como WPF normal. Sobran en la vista nueva la reflexión que apaga el ratón nativo de Flyleaf, el rodeo de los enlaces congelados en la raíz del `FlyleafHost` y el gancho de los bordes del mini reproductor. Desde la etapa 6 solo existe esa vista, con su nombre de siempre: `ReproductorView`.

**Qué vista se abre.** El reproductor expone qué motor usa y la plantilla de `App.xaml` elige `ReproductorView` o `ReproductorMpvView`. Navegación, ficha e historial no cambian. Desde la etapa 6 la plantilla es una sola vista y el reproductor ya no elige motor.

**Mini reproductor.** La misma ventana flotante de hoy con la vista dentro. Al pasar del formato normal al mini, la superficie se crea en la otra ventana y libmpv vuelve a engancharse sin cortar la reproducción. **Sin probar**: la etapa 3 empieza comprobándolo; si hubiera corte, la alternativa es mover la superficie entre ventanas en vez de recrearla.

**Rendimiento de video (Configuración → Reproducción).** Los ajustes conservan su aspecto:

| Ajuste | Con libmpv |
|---|---|
| Saltos | Exactos o a fotograma clave. En Automático, exactos |
| Aceleración por hardware | Decodificación por la gráfica encendida o apagada |
| Escalado inteligente | Anime4K (sección 6) |
| Tarjeta gráfica | Con este modo de pintado no se elige (la decide el lienzo): la fila se retiró en la etapa 6 |
| Motor de video | Existió en la etapa 5 («Nuevo» o «Clásico»); retirada en la etapa 6: solo queda libmpv |
| Modo de compatibilidad de video | Etapa 6: pintar por software aunque el equipo tenga OpenGL |

**Modo de compatibilidad (definitivo desde la etapa 6).** libmpv pinta por software (`MPV_RENDER_API_TYPE_SW`, `Services/PintorPorSoftware`) sobre una imagen de WPF dentro de la misma `SuperficieMpv`. Entra solo si el lienzo OpenGL no se puede crear o libmpv no puede pintar en él (máquina virtual, escritorio remoto, controlador antiguo): como aún no había pintor, el archivo no se había cargado y el episodio empieza donde tenía que empezar; el registro lo anota una vez. Además hay un interruptor, «Modo de compatibilidad de video» (Configuración → Reproducción, apagado por defecto), para lo que la app no puede detectar: un lienzo que se crea pero se ve negro o con fallos. Por software no hay escalado inteligente (Anime4K necesita OpenGL) y se pinta como mucho a 1920×1080 (en una ventana mayor WPF amplía). Si tampoco se puede por software, el reproductor avisa una vez («Este equipo no puede mostrar el video») y no reabre. Hasta la etapa 5 este papel lo hacía el motor clásico (`PasarAlMotorClasico`), que ya no existe.

**Salida por software, medida en la etapa 5 (2026-10-11).** Arnés desechable: la interfaz de pintado por software de libmpv (`MPV_RENDER_API_TYPE_SW`, formato `bgr0`) sobre un `WriteableBitmap` del tamaño de la ventana, pintando en el hilo de la interfaz. Intel HD 620, i5-7300U (4 hilos), ventana de 1366×705, 45 s por caso desde el minuto 2, sin sonido ni subtítulos:

| Caso | Fotogramas por segundo | Perdidos | Pintar cada fotograma | Procesador | Memoria del arnés |
|---|---|---|---|---|---|
| H.264 1080p, lienzo OpenGL (decodifica la tarjeta) | 24,0 | 0 | 0,9 ms (peor 2) | 3 % | 231 MB |
| H.264 1080p, por software (decodifica la tarjeta) | 24,0 | 0 | 3,0 ms (peor 5) | 7 % | 166 MB |
| H.264 1080p, por software (decodifica el procesador) | 24,0 | 0 | 3,4 ms (peor 5) | 12 % | 152 MB |
| AV1 1080p 10 bits, lienzo OpenGL (decodifica el procesador) | 24,0 | 0 | 1,4 ms (peor 10) | 12 % | 295 MB |
| AV1 1080p 10 bits, por software (decodifica el procesador) | 24,0 | 0 | 3,4 ms (peor 10) | 16 % | 224 MB |

La salida por software aguanta el video a su ritmo con un coste pequeño (unos 4 puntos de procesador más y 3 ms por fotograma en el hilo de la interfaz, que no se retrasó más de 11 ms) y gasta menos memoria que el lienzo. **Recomendación para la etapa 6:** retirar Flyleaf con esta salida como modo de compatibilidad, previa comprobación dentro de la app. **Lo que la medida no dice** (y la etapa 4 enseñó que el arnés no basta): cómo va con subtítulos ASS con muchas capas, con los controles encima y en una ventana más grande (1080p o 4K, donde el coste crece con los píxeles), ni en un equipo sin aceleración de verdad (allí WPF también compone por software); además sin OpenGL no hay Anime4K y el reescalado es el básico. Todo eso se mide en la app antes de quitar nada.

**Salida por software, medida dentro de la app (etapa 6, 2026-10-11).** Ya no es un arnés: `SuperficieMpv` pinta con `PintorPorSoftware` sobre una imagen de WPF. Mismo equipo, perfil aislado, ventana maximizada (zona de video de 1366×728), cada caso con la app recién abierta, el episodio reanudado en el minuto 2 y unos 65 s de reproducción; procesador y memoria son los del proceso entero durante 60 s. Los fotogramas y los perdidos salen de la línea «Al salir» del registro. «ASS» es Youjo Senki 03 desde 3:20, con tres carteles en el tramo.

| Caso | Controles | Fotogramas | Perdidos | Procesador | Memoria (media / pico) | Primera imagen |
|---|---|---|---|---|---|---|
| H.264 1080p, por software | ocultos | 1606 | 4 | 22 % | 378 / 389 MB | 0,70 s |
| H.264 1080p, por software | visibles | 1588 | 0 | 21 % | 377 / 384 MB | 0,59 s |
| AV1 1080p 10 bits, por software | ocultos | 1625 | 0 | 32 % | 426 / 434 MB | 0,52 s |
| AV1 1080p 10 bits, por software | visibles | 1602 | 1 | 29 % | 424 / 433 MB | 0,50 s |
| H.264 con carteles ASS, por software | ocultos | 1589 | 0 | 20 % | 377 / 386 MB | 0,62 s |
| H.264 con carteles ASS, por software | visibles | 1592 | 0 | 21 % | 378 / 389 MB | 0,56 s |
| H.264 1080p, lienzo OpenGL | ocultos | 1571 | 0 | 13 % | 483 / 548 MB | 1,93 s |
| H.264 1080p, lienzo OpenGL | visibles | 1575 | 1 | 13 % | 486 / 556 MB | 1,51 s |
| AV1 1080p 10 bits, lienzo OpenGL | ocultos | 1590 | 9 | 24 % | 537 / 611 MB | 1,38 s |
| AV1 1080p 10 bits, lienzo OpenGL | visibles | 1585 | 1 | 26 % | 540 / 613 MB | 1,37 s |
| H.264 con carteles ASS, lienzo OpenGL | ocultos | 1575 | 5 | 15 % | 489 / 550 MB | 1,36 s |
| H.264 con carteles ASS, lienzo OpenGL | visibles | 1579 | 0 | 14 % | 490 / 552 MB | 1,38 s |

Por software se pierde como mucho un 0,25 % de los fotogramas (un 0,06 % con los controles a la vista), cuesta entre 5 y 9 puntos de procesador más que el lienzo, gasta unos 110 MB menos y enseña la primera imagen antes (no hay que crear el contexto OpenGL). Los subtítulos ASS se ven con su estilo y sus carteles en su sitio, y los diálogos suben por encima de los controles igual que con el lienzo.

En una misma sesión por software (6224 fotogramas, 15 perdidos, un 0,24 %): pausa (el fotograma se queda quieto), saltos de ±10 s con teclado y con el botón, cambio a otra pista de subtítulos, pantalla completa (1366×768) y vuelta, mini reproductor y vuelta, y el borde del mini arrastrado 6 veces de 420×236 a unos 850×475 y vuelta (480 cambios de tamaño: 416 MB antes, 435 MB de pico, 415 MB después: la imagen se recrea sin acumular memoria). `Esc` sale como siempre.

**Coste según el tamaño** (lo que esta pantalla no da), con el mismo `PintorPorSoftware` sin ventana, 20 s por caso:

| Video | Tamaño pintado | Pintar cada fotograma | Perdidos | Procesador |
|---|---|---|---|---|
| H.264 1080p | 1366×705 | 2,9 ms (peor 25) | 0 | 5 % |
| H.264 1080p | 1920×1080 | 3,3 ms (peor 12) | 1 | 6 % |
| H.264 1080p | 3840×2160 | 15,2 ms (peor 33) | 2 | 31 % |
| AV1 1080p 10 bits | 1366×705 | 3,4 ms (peor 39) | 4 | 14 % |
| AV1 1080p 10 bits | 1920×1080 | 4,4 ms (peor 18) | 1 | 15 % |
| AV1 1080p 10 bits | 3840×2160 | 18,1 ms (peor 60) | 1 | 40 % |

A 4K cada fotograma ocuparía el hilo de la interfaz 15–18 ms (casi la mitad del tiempo a 24 fotogramas por segundo, y no daría abasto con un video a 60): **el tope de 1920×1080 se queda**. En una ventana mayor libmpv pinta a ese tamaño (3–4 ms) y WPF amplía.

**Sigue sin medir:** un equipo sin aceleración de verdad (allí WPF también compone por software), y una pantalla con la escala de Windows por encima del 100 %. La captura de fotograma en este modo no se pulsó en la app (escribe en Imágenes y en el portapapeles): la cubre la prueba `PorSoftware_LaCapturaDeFotogramaDaLaImagenDelVideo`.

## 6. Anime4K

El ajuste «Escalado inteligente» (`EscaladoInteligenteVideo`: Automático, Activado, Desactivado) pasa a gobernar Anime4K con el motor nuevo. No hay pantallas ni ajustes nuevos.

- **Niveles**: ligero (`Clamp_Highlights` + `Restore_CNN_S` + `Upscale_CNN_x2_S`, el medido sin pérdidas en la HD 620) y medio (variantes `M`) para gráficas dedicadas. La política que elige nivel vive en `MotorVideo`, junto a la de los saltos.
- **Automático**: se enciende en el nivel que corresponde al equipo y **se apaga solo** en ese episodio si el contador de fotogramas perdidos crece de forma sostenida; se anota en el registro.
- **Activado**: mismo nivel, sin apagado automático.
- **Desactivado**: nunca.

Los shaders (MIT, menos de 1 MB) van dentro del repositorio.

## 7. Etapas

Cada una termina en algo comprobable en la app y lleva su propio plan.

| Etapa | Contenido | Se da por buena cuando |
|---|---|---|
| 1. Costura | `IMotorVideo`, `MotorFlyleaf`, `MotorDePrueba`; reproductor y coordinadores pasan a la interfaz | La suite pasa, hay pruebas nuevas del reproductor con el motor de mentira y una pasada completa en la app con Flyleaf no muestra diferencias |
| 2. libmpv básico | `LibMpv`, `MotorMpv`, `SuperficieMpv`, `ReproductorMpvView`, empaquetado y avisos de licencia; ajuste oculto `MotorVideo` en `settings.json` | Con el ajuste activado: abrir, pausar, saltar, volumen, velocidad, reanudar y episodio siguiente funcionan, y el progreso llega a la base como siempre |
| 3. Paridad | Subtítulos, pistas de audio, ecualizador y modo noche, capturas, mini reproductor, pantalla completa, atajos, ajustes de rendimiento, informe de video | Cada función de la lista de comprobación del reproductor actual hace lo mismo con el motor nuevo |
| 4. Anime4K | Sección 6 | Se enciende, se nota y se apaga solo al perder fotogramas |
| 5. Predeterminado | libmpv por defecto; «Motor de video: Nuevo / Clásico» en Configuración → Reproducción; modo de compatibilidad | Una versión publicada y usada un tiempo sin volver al clásico |
| 6. Retirada | Fuera `MotorFlyleaf`, la vista clásica, `ParcheSubtitulosFlyleaf`, `SubtitleAssRenderer`, `SubtitleCuesExtractorService`, `SubtitulosAss`, y los paquetes FlyleafLib, FlyleafLib.Controls.WPF, Flyleaf.FFmpeg y Lib.Harmony; MaterialDesignThemes a la estable 5.3.2; antes, el modo de compatibilidad por software | La app compila y pasa sin ninguna referencia a Flyleaf. **Hecha el 2026-10-11** (`docs/plan-motor-libmpv-etapa-6.md`); la vista vuelve a llamarse `ReproductorView` |

Durante las etapas 2 a 5 hubo dos vistas del reproductor. Hasta la etapa 5 todo se revertía apagando un ajuste; la etapa 6 ya no tiene interruptor: se deshace revirtiendo sus commits. La condición que este diseño ponía para darla por buena («una versión publicada y usada un tiempo sin volver al clásico») no llegó a cumplirse: ninguna versión publicada llevó libmpv con el clásico como salida, y quien actualice pasará de Flyleaf a libmpv directamente, con el modo por software como única red.

## 8. Empaquetado y licencia

- `libmpv-2.dll` (121 MB) no entra en git. Se sigue el patrón de `tools\Get-FFmpegBinaries.ps1`: un `tools\Get-Libmpv.ps1` con versión y SHA-256 fijadas, usado por `build.ps1`, el CI y el corte de versión.
- El proveedor solo conserva sus compilaciones unos dos meses (30 publicadas el 2026-10-10). Para que la versión fijada no desaparezca hay que archivar el paquete en una release del propio repositorio. **Esa subida necesita el visto bueno del propietario** en la etapa 2.
- El instalador crece unos 120 MB desde la etapa 2 y no baja en la 6: la carpeta `FFmpeg/` sigue haciendo falta para `ffmpeg.exe` y `ffprobe.exe`.
- Paquetes nuevos: `OpenTK.GLWpfControl` y sus dependencias. Su peso real en el instalador se mide en la etapa 2.
- Licencia: el código sigue bajo MIT. «Acerca de», el README y la página de cada release avisan de que el paquete incluye mpv bajo GPL, con enlace a su código fuente y a la versión exacta. Si algún día se quisiera cerrar el código habría que pasar a una compilación LGPL.

## 9. Pruebas y verificación

- **Reproductor con motor de mentira (etapa 1)**: al terminar un episodio pasa al siguiente, marca visto en el umbral, reanuda donde iba, no fabrica fechas de reproducción. Hoy esas rutas corren sin motor.
- **Motor real sin pantalla (etapa 2)**: `MotorMpv` con `vo=null` y `ao=null` sobre videos diminutos generados con el FFmpeg de la app: abrir, saltar, listar pistas, llegar al final, cancelar a mitad. Corren si la biblioteca está descargada y se saltan si falta.
- **En la app con perfil aislado, al cerrar cada etapa**: los tres videos de referencia, un episodio con subtítulos SRT, el mini reproductor y diez episodios seguidos mirando la memoria. Nunca más del 85 % de un episodio ni el último de un anime sin completar.
- Reglas del repositorio que aplican: `[LibraryImport]` (nunca `[DllImport]`), saltos al hilo de la interfaz antes de tocar nada observable, textos nuevos por `LocalizationService`, colores de la paleta.

## 10. Riesgos

| Riesgo | Mitigación |
|---|---|
| Perder comportamientos afinados al mover código a `MotorFlyleaf` (etapa 1) | Pruebas nuevas con el motor de mentira y pasada completa en la app antes de seguir |
| El mini reproductor al cambiar de ventana | Sin probar: va lo primero de la etapa 3, con alternativa prevista |
| Equipos donde el lienzo OpenGL no arranque | Paso automático al motor clásico mientras exista Flyleaf (etapa 5); después, la salida por software, medida en un arnés (sección 5) y pendiente de medir en la app |
| Diferencias de aspecto en los subtítulos con el estilo del usuario | Ajuste a ojo en la etapa 3 |
| Fluidez percibida | La juzga el propietario al probar la etapa 2; si no convence, queda el camino A (ventana nativa) |
| Dos vistas durante la convivencia | No tocar el reproductor por otros motivos hasta la etapa 6 |
