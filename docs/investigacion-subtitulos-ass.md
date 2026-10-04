# Diseño: subtítulos ASS/SSA con su estilo original (libass) (2026-10-04)

**Objetivo del producto:** que un subtítulo ASS/SSA se vea en el reproductor como lo diseñó el fansub: carteles colocados sobre su letrero, colores y fuentes originales, fundidos, movimientos y karaoke. Sin perder nada de lo que ya funciona (SRT, subtítulos de imagen, estilo configurable) y sin reintroducir el cierre de la app por el lector ASS de Flyleaf.

**Método:** revisión del código del reproductor y comprobación del ffmpeg incluido (`ffmpeg.exe -version`, `-filters`, `-h filter=subtitles`). **No se ha probado nada en la app real todavía**: los cuatro riesgos de la sección 7 se miden en la fase 0 antes de escribir código definitivo.

---

## 1. Situación actual

- Las pistas de texto incrustadas las lee la app, no Flyleaf (`ReproductorViewModel.AbrirPistaSubtitulos`): `SubtitleCuesExtractorService` las convierte a SRT con `ffmpeg.exe` y `SubtitulosSrtParser.LimpiarEtiquetas` borra todas las etiquetas de estilo.
- El texto se dibuja en dos `SubtitleTextView` fijos (abajo y arriba) con el estilo de Configuración (`EstiloSubtitulos`), actualizados en el sondeo de 250 ms (`ActualizarLineasSubtitulosSolapados`).
- Resultado: de un ASS solo sobreviven el texto y sus tiempos. Se pierden posición, colores, fuentes, fundidos, movimiento y karaoke.
- Flyleaf arranca siempre con `Config.Subtitles.Enabled = false` porque su lector de estilos ASS cerraba la app (`FormatException '&H'`, Re:Zero S4 ep 19). Eso **no cambia** con este diseño.
- Los subtítulos de imagen (PGS/VobSub) los pinta Flyleaf y ya se ven con su aspecto original.

## 2. Hallazgo: la app ya incluye libass

Comprobado en `AnimeLocalTracker/FFmpeg/`:

- `ffmpeg.exe` es la compilación `9.0.1-full_build` de gyan.dev, `--enable-shared --enable-libass --enable-libfreetype --enable-libfribidi --enable-libharfbuzz --enable-fontconfig`.
- `ffmpeg -filters` lista `ass` y `subtitles` ("Render … using the libass library"): están dentro de `avfilter-12.dll`, que la app ya distribuye.
- El filtro `subtitles` acepta `filename`, `stream_index`/`si`, `alpha` ("enable processing of alpha channel"), `fontsdir`, `original_size`, `force_style`.
- `Flyleaf.FFmpeg.dll` (9.0.0) contiene los bindings `avfilter_graph_create_filter`, `avfilter_graph_parse_ptr`, `avfilter_graph_config`, `av_buffersrc_add_frame` y `av_buffersink_get_frame`.

libass no está exportado como API propia (va enlazado dentro de `avfilter-12.dll`), así que se usa a través del filtro.

## 3. Decisiones tomadas

| Decisión | Elegido | Descartado |
|---|---|---|
| Estilo con pista ASS | **Original por defecto + interruptor "Usar mi estilo"** en el menú de subtítulos, que vuelve al texto plano actual | Siempre original sin salida; modo mixto (diálogo con estilo propio, carteles originales) |
| Origen de libass | **A: filtro `subtitles` del ffmpeg incluido**. Sin archivos nuevos en el instalador | B: `libass.dll` propio con sus dependencias (más control y menos CPU, pero instalador y release más complejos). C: compilarlo en el núcleo Rust |
| Mini reproductor | **Incluido** | — |

## 4. Diseño

### 4.1 Lo que ve el usuario

1. El episodio abre igual que hoy, con el subtítulo en texto plano. No hay espera nueva.
2. En segundo plano se prepara el dibujante ASS (lee la pista y las fuentes adjuntas del `.mkv`).
3. Cuando está listo, el texto plano se oculta y aparece la capa ASS.
4. Si la preparación falla, se queda el texto plano. Nunca se queda sin subtítulos.
5. En el menú de subtítulos, con una pista ASS activa, aparece el interruptor **"Usar mi estilo"**: encendido muestra el texto plano con el estilo de Configuración; apagado, el original. Cambia al instante y se recuerda.

### 4.2 Piezas

| Pieza | Responsabilidad | Ubicación |
|---|---|---|
| `ISubtitleAssRenderer` / `SubtitleAssRenderer` (nueva) | Monta el grafo `buffer → subtitles (alpha=1) → buffersink` y devuelve el fotograma BGRA transparente de los subtítulos para un instante. Sin tipos de WPF. Una sola pista abierta a la vez | `Services/` (transient en DI: uno por reproductor, para que el `Dispose` de un ViewModel no cierre la pista del siguiente) |
| Capa de imagen (nueva) | `Image` con `Stretch="Uniform"`, `IsHitTestVisible="False"`, en la raíz del contenido de `FlyleafHost`, junto a los `SubtitleTextView` y bajo `TapaVideo`. Su `WriteableBitmap` lo empuja el code-behind (en esa raíz los bindings se congelan) | `ReproductorView.xaml` |
| Ciclo de dibujo (nuevo) | Mientras hay modo ASS y la vista está visible: en cada fotograma de WPF, si el tiempo del video cambió, pide el fotograma a un hilo de fondo y copia el resultado al `WriteableBitmap` en el hilo de interfaz. Si llega una petición con otra en curso, gana la más reciente | `ReproductorView.xaml.cs` |
| Decisión de modo | Función pura: pista de texto ASS/SSA incrustada + "Usar mi estilo" apagado → ASS; cualquier otro caso → flujo actual | `Core/` (probada) + llamada desde `AbrirPistaSubtitulos` |
| Estado en el ViewModel | `SubtitulosAssActivo` (el dibujante está listo y manda) y `PistaActualEsAss` (para mostrar el interruptor). El ViewModel abre y cierra el dibujante | `ReproductorViewModel` |
| Interruptor y ajuste | `AppSettings.UsarMiEstiloEnAss` (por defecto `false`), entrada en el menú de subtítulos, claves de localización nuevas | menú de `ReproductorView.xaml`, `AppSettings`, `LocalizationService` |

### 4.3 Interfaz del dibujante

```csharp
public interface ISubtitleAssRenderer
{
    int Ancho { get; }
    int Alto { get; }

    /// Lee la pista y sus fuentes adjuntas. Lento (lee el archivo): siempre fuera del hilo de interfaz.
    /// false si no se pudo montar el filtro; el llamador sigue con el texto plano.
    Task<bool> AbrirAsync(string rutaVideo, int indicePistaEntreSubtitulos, int ancho, int alto, CancellationToken ct);

    /// Dibuja los subtítulos del instante dado en `destinoBgra` (Ancho × Alto × 4, fondo transparente).
    /// false si hubo error; el llamador desactiva el modo ASS para ese episodio.
    /// (`byte[]` y no `Span<byte>`: Moq no puede simular métodos con parámetros `Span<T>`.)
    /// Versión final: devuelve además la franja de filas que cambió, para repintar solo eso.
    bool Renderizar(TimeSpan instante, byte[] destinoBgra, out int filaInicial, out int filas);

    void Cerrar();
}
```

- `Renderizar` y `Cerrar` se serializan con un `lock`: nunca se libera el grafo mientras se dibuja.
- Las opciones del filtro se fijan con `av_opt_set` sobre el contexto (no con una cadena de argumentos), para no depender del escapado de `:` y `\` en rutas de Windows.
- El filtro cuenta la pista **entre las de subtítulos**, no con el índice absoluto del contenedor que da Flyleaf (`SubtitlesStream.StreamIndex`). La traducción es una función pura y probada. *(Comportamiento de `si` a confirmar en la fase 0.)*

### 4.4 Flujo de datos

```
AbrirPistaSubtitulos(pista)
 ├─ extracción a SRT (como hoy)            → texto plano visible de inmediato
 └─ si DecidirModo == ASS:
      AbrirAsync(ruta, índice, ancho, alto) en segundo plano
        ├─ ok    → SubtitulosAssActivo = true → la vista oculta el texto y arranca el ciclo
        └─ falla → log + se queda el texto plano

Ciclo (vista visible, SubtitulosAssActivo):
  CompositionTarget.Rendering → ¿cambió Player.CurTime? → Task de fondo: Renderizar(t, búfer)
     → Dispatcher: WritePixels al WriteableBitmap (se omite si el fotograma y el anterior son transparentes)
```

### 4.5 Decisiones técnicas

- **Tamaño fijo por episodio:** se dibuja a la resolución del video, con tope en la de la pantalla, conservando la proporción. WPF escala la imagen con `Stretch="Uniform"`, igual que Flyleaf encaja el video. Cambiar el tamaño de la ventana, la pantalla completa o el mini no reabren el filtro (reabrirlo releería el archivo).
- **Sin estado entre fotogramas:** cada imagen se calcula a partir del tiempo actual. Saltos adelante/atrás funcionan sin lógica extra; en pausa se dibuja una vez.
- **La extracción a SRT se mantiene** aunque el modo sea ASS: cubre el arranque, el fallo y el interruptor sin esperas.
- **Cancelación:** cambiar de episodio o de pista cancela la apertura en curso y descarta su resultado tardío, con el mismo patrón que `_subtitleCuesCts`.
- **Formato de píxel:** el filtro entrega BGRA con alfa recto; WPF prefiere `Pbgra32` (premultiplicado). Se decide en la fase 0 entre `PixelFormats.Bgra32` o premultiplicar en la copia, según lo que mida.

### 4.6 Mini reproductor

`MiniReproductorWindow` aloja otra instancia de la misma `ReproductorView` con el mismo ViewModel, así que hoy ya muestra los subtítulos de texto. Requisitos para que también muestre ASS:

- El dibujante y el estado pertenecen al ViewModel, no a la vista: pasar a mini no reabre nada.
- Cada instancia de la vista tiene su propio `WriteableBitmap` y su propio ciclo, y solo dibuja mientras está visible (`IsVisibleChanged`, más `Loaded`/`Unloaded` para la vista del mini).
- Al aparecer, la vista lee el estado actual (`SubtitulosAssActivo`) en vez de esperar un cambio de propiedad: en el mini el modo ASS ya estaba activo antes de crearse la vista.

## 5. Fallos y límites

- **El filtro no se monta o `Renderizar` falla:** se registra, se cierra el dibujante y ese episodio sigue en texto plano. Sin reintentos en bucle.
- **El equipo no da abasto:** se saltan fotogramas del subtítulo; el video y el audio no se frenan (el dibujo va en un hilo de fondo y gana la petición más reciente).
- **Fallo nativo dentro de libass:** no se puede capturar y cerraría la app. Es el motor de mpv y VLC, por lo que el riesgo se considera bajo, pero existe. El interruptor "Usar mi estilo" permite ver ese episodio sin pasar por libass.
- **Fuentes ausentes:** si el `.mkv` no adjunta la fuente, libass usa una del sistema; el cartel sale con otra letra pero en su sitio.

## 6. Fuera de alcance

- Archivos `.ass`/`.srt` sueltos junto al video (propuesta nº 13 de `propuestas-nuevas-funciones.md`). El dibujante acepta una ruta de archivo, así que quedaría listo para ello.
- Incluir los subtítulos en la captura de fotograma (`CapturarFrame`): sigue guardando solo el video.
- Modo mixto (diálogo con estilo propio y carteles originales).
- Corrección de desfase de subtítulos (no existe hoy).

## 7. Riesgos a medir en la fase 0

| # | Riesgo | Qué se mide | Si falla |
|---|---|---|---|
| 1 | Transparencia | Que el fotograma salga con fondo transparente con `alpha=1` y bordes limpios | Probar formato de entrada `yuva420p`/`rgba`; si no hay forma, pasar al enfoque B |
| 2 | Fluidez en Intel HD 620 | Tiempo de `Renderizar` + copia por fotograma en un cartel pesado y en un karaoke; fotogramas de video perdidos con y sin la capa | Bajar el tamaño de dibujo; si pintar en la capa de Flyleaf es el cuello de botella, se replantea el diseño (afectaría igual al enfoque B) |
| 3 | Tiempo de apertura | Segundos de `AbrirAsync` en un `.mkv` grande, primera vez y siguientes (incluye la búsqueda de fuentes del sistema) | Extraer la pista `.ass` y las fuentes a una carpeta temporal en la misma pasada de `ffmpeg.exe` que ya hace el SRT, y usar el filtro `ass` con `fontsdir` |
| 4 | Precisión del reloj | Si `Player.CurTime` avanza fotograma a fotograma o a saltos | Interpolar entre lecturas con un cronómetro mientras se reproduce, teniendo en cuenta la velocidad |

Criterio para seguir: transparencia correcta, dibujo + copia por debajo de ~10 ms de media en el equipo del usuario sin pérdida apreciable de fotogramas de video, y desfase del subtítulo no mayor que un fotograma (~42 ms). El tiempo de apertura se informa; no bloquea, porque el texto plano cubre la espera.

### 7.1 Resultados de la fase 0 (2026-10-04)

Medido en el equipo del usuario (4 núcleos lógicos, pantalla 1366×768) con dos programas desechables fuera del repo: uno que solo dibuja y otro que reproduce con el mismo FlyleafLib 3.11.3 y el mismo FFmpeg de la app, con la capa de imagen encima. No se usó la app real (reproducir ahí reescribe el progreso y puede sincronizar con AniList). Material: `[Erai-raws] Youjo Senki - 02 [1080p]` (H.264, 860 MB, 9 pistas ASS, sin fuentes adjuntas) y tres `.ass` sintéticos (transparencias, "típico" con karaoke + cartel inclinado + diálogo, y "pesado" con rotación, escalado y desenfoques animados).

| Riesgo | Resultado |
|---|---|
| 1. Transparencia | **`alpha=1` no sirve tal cual.** El filtro eleva al cuadrado la transparencia parcial (un 50 % sale como 25 %; con capas superpuestas no es corregible). **Solución probada:** dibujar dos veces sin canal alfa, sobre blanco y sobre negro; alfa = 255 − (blanco − negro) y el color premultiplicado es lo dibujado sobre negro. Da 127/191/63 para 50/75/25 %, exacto, y sale ya en `Pbgra32`. |
| 1b. Rango de color (nuevo) | Sobre fotogramas RGB el filtro comprime los colores a 16–235 (blanco = 235) salvo que el guion declare `YCbCr Matrix: None` o `PC.*`; lo decide `ass_get_color_range` según la cabecera del guion, no según el fotograma (comprobado en el código de `vf_subtitles.c` y con sondas de píxel). Hay que deshacerlo de nuestro lado leyendo esa cabecera. El ajuste de tono 601↔709 que hacen mpv/VSFilter **no** se aplica sobre RGB: los colores saturados de carteles pueden diferir ligeramente. |
| 2. Fluidez | Pintar no cuesta: copiar el fotograma completo al `WriteableBitmap` en cada fotograma de video son 1,0 ms de media, y solo las filas con subtítulo, 0,1–0,5 ms. **El video no perdió fotogramas en ninguna medición.** El coste está en el dibujo (hilo de fondo, 1364×768, doble pasada tocando solo las filas con contenido): diálogo real 6,5–6,9 ms (máx. 20); típico con karaoke 21,9 ms (máx. 44), al ritmo del video; pesado 64 ms (máx. 98), el subtítulo se actualiza a ~15 por segundo. CPU del proceso: 5 % de un núcleo sin capa, 26–32 % con diálogo, 68 % con karaoke, 115 % en el caso pesado. |
| 3. Apertura | 560–730 ms leyendo la pista directamente del `.mkv` (archivo ya en la caché de Windows; en frío no se midió). Con FFmpeg ya cargado por Flyleaf no hace falta `LoadLibraries`. |
| 4. Reloj | `Player.CurTime` ya cambia fotograma a fotograma con la configuración por defecto (23,9 cambios/s, salto medio 41,8 ms). No hace falta interpolar ni tocar `UICurTime`. |

Otros hallazgos:

- **Índice de pista:** confirmado, el filtro cuenta entre las pistas de subtítulos (0 = inglés, 4 = español en el archivo de prueba).
- **Nombres de archivo:** rutas con corchetes, espacios, coma y apóstrofo abren bien con `av_opt_set`.
- **Saltos atrás:** sin problema.
- **Dos ensamblados con los mismos tipos:** FlyleafLib usa `Flyleaf.FFmpeg.Bindings`; la app referencia además el paquete `Flyleaf.FFmpeg` (sin usarlo en su código). Con ambos, `Flyleaf.FFmpeg.Raw` es ambiguo (CS0433). El dibujante debe usar `Flyleaf.FFmpeg.Bindings`, que es el que tiene las bibliotecas cargadas; al paquete suelto hay que darle un alias o quitarlo.
- **Superposición sobre el video:** comprobada con capturas (tipografía, contorno y sombra correctos, bordes limpios).
- **No verificado:** fuentes adjuntas (ningún `.mkv` de la biblioteca las trae), apertura en frío, y la lectura de la cabecera `YCbCr Matrix` desde los datos de la pista.
- **Alcance real hoy:** la biblioteca tiene 12 `.mkv` con ASS (todos de esta serie, con subtítulos de estilo sencillo) y 369 `.mp4` sin pista.

Frente al criterio fijado (dibujo + copia < ~10 ms de media): se cumple con diálogo y **no** con karaoke o carteles animados, aunque en ningún caso se pierden fotogramas de video.

## 8. Pruebas

**Automáticas (xUnit + FluentAssertions):**

- Decisión de modo: combinaciones de tipo de pista (ASS, SSA, SRT, imagen, externa) e interruptor.
- Traducción del índice de pista de Flyleaf al índice entre subtítulos.
- Dibujo real: un `.ass` mínimo escrito en una carpeta temporal, con una línea roja en `\pos` arriba a la izquierda; se comprueba que hay píxeles rojos en esa zona y transparencia en el resto, y que un instante sin líneas da un fotograma vacío. *Depende de poder cargar las DLL de FFmpeg en el proceso de pruebas; se confirma en la fase 1. Si no es viable, este caso pasa a la verificación en vivo y se dice explícitamente.*
- Ninguna prueba escribe ni borra bajo `AppDataPaths`.

**En la app real (`wpf-visual-verification`):** episodio con ASS cargado (Re:Zero S4 ep 19), sobre copia de la base de datos y sin pasar del 85 % del episodio. Capturas de: cartel posicionado, karaoke, salto adelante y atrás, pausa, interruptor "Usar mi estilo" en ambos sentidos, paso a mini y vuelta, cambio de episodio con la apertura en curso. Además, un episodio SRT y uno con PGS para confirmar que no cambian.

**Compilación y suite completa** con `repo-build-test` antes de dar nada por terminado.

## 9. Fases

0. **Prueba desechable.** Mide los cuatro riesgos de la sección 7. El código no se conserva. Se informa con números y el usuario decide si se sigue.
1. `SubtitleAssRenderer`, la decisión de modo y la traducción de índice, con sus pruebas.
2. Capa de imagen y ciclo de dibujo en `ReproductorView`, incluido el caso del mini reproductor.
3. Integración en `ReproductorViewModel`, interruptor "Usar mi estilo", ajuste guardado y textos localizados.
4. Verificación en la app real y `repo-build-test`.

## 10. Implementación y verificación (2026-10-04)

**Cómo quedó respecto al diseño original**

- `SubtitleAssRenderer` dibuja cada instante **dos veces** (sobre blanco y sobre negro) sin la opción `alpha` del filtro, y de ahí saca la transparencia exacta; corrige el rango 16–235 leyendo la cabecera `YCbCr Matrix` del guion; solo reescribe y devuelve la franja de filas que cambió. Usa `Flyleaf.FFmpeg.Bindings` (al paquete `Flyleaf.FFmpeg` se le puso un alias en el `.csproj`).
- Las filas con subtítulo se detectan con **las dos** pasadas: con una sola, un cartel blanco puro era invisible sobre el fondo blanco (lo destaparon las pruebas).
- El tamaño del video se toma de la pista de video del contenedor cuando `Player.Video` aún vale 0×0, que es lo que pasa justo al abrir (lo destapó la verificación en la app).
- Sin reloj interpolado ni cambios en `UICurTime`.

**Pruebas automáticas:** 44 nuevas (21 de decisiones puras, 15 de dibujo real con libass, 8 del ViewModel). Suite completa: 2712 en verde; build con 0 errores y 0 advertencias. Las bibliotecas de FFmpeg cargan sin problema en el proceso de pruebas.

**Coste con el código final** (reproductor de pruebas, 1364×768, video 1080p H.264 en reproducción): diálogo 7,8 ms por fotograma y 29 % de un núcleo; karaoke + cartel + diálogo 25,9 ms y 79 %, al ritmo del video; caso extremo 74 ms, el subtítulo se actualiza a ~13 por segundo. El video perdió 0–1 fotogramas en cada medición.

**Verificación en la app real**, lanzada con un perfil aislado (variables `USERPROFILE`/`LOCALAPPDATA`/`APPDATA` apuntando a una carpeta temporal con una copia de la base de datos y de los ajustes, sin token de AniList; los datos reales no se tocaron). Youjo Senki, episodios 11 y 10:

| Caso | Resultado |
|---|---|
| Abrir el episodio | Texto plano al principio y paso al estilo original. Registro: `Dibujo ASS listo en 1048 ms`; en un episodio no leído antes (archivo fuera de la caché de Windows), `8047 ms` |
| Diálogo con el estilo del guion | Correcto (tipografía, contorno y sombra del guion; cursiva en las líneas que la llevan) |
| "Usar mi estilo" | La entrada aparece en el menú; encendida muestra el texto plano con el estilo de Configuración y la marca; apagarla vuelve al original sin reabrir el dibujante. El ajuste se guarda |
| Mini reproductor | Muestra el subtítulo ASS, reducido como el video, sin reabrir nada |
| Cambio de episodio | El dibujante se abre para el nuevo episodio; sin restos del anterior ni errores |
| "Apagar" y volver a elegir la pista | Desaparece y vuelve (el dibujante se reabre en ~1 s) |
| Registro de la sesión | 0 errores y 0 avisos |

**No verificado en la app:** carteles posicionados y karaoke de un fansub real (la biblioteca no tiene ninguno; sí se vieron con guiones sintéticos en el reproductor de pruebas), fuentes adjuntas, subtítulos PGS, un episodio SRT junto al cambio (los `.mp4` de la biblioteca no traen pista), pausa, saltos atrás dentro de la app y cambio de idioma con el menú abierto.

**Limitaciones conocidas**

- El ajuste de tono 601↔709 de mpv/VSFilter no se aplica: un cartel de color muy saturado puede verse ligeramente distinto.
- Mientras hay una pista ASS activa, cada fotograma de video cuesta un dibujo aunque la línea no cambie (el filtro no avisa de si cambió algo).
- La primera apertura de un archivo grande no leído antes puede tardar varios segundos; durante ese tiempo se ve el texto plano.
