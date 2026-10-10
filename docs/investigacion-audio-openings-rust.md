# Diseño: detección de openings y endings por audio en el núcleo Rust

Fecha: 2026-10-09. Estado: implementado el 2026-10-09 según `docs/plan-audio-openings-rust.md` (validación de la sección 8: 40 episodios, 65 tramos, diferencias de 0,000). Punto 3 de `docs/investigacion-ecosistema-poliglota.md`.

## 1. Objetivo

Que ubicar el opening y el ending de un episodio deje de depender del daemon Python y de numpy. El cálculo pasa al núcleo Rust y la dirección del proceso a C#.

**Lo que no cambia:** las marcas que se ven en la barra de progreso del reproductor. El algoritmo actual (`detect_themes` de `tools/python/audio_skip_plugin.py`) está afinado con episodios reales; el port conserva sus constantes y sus umbrales, y tiene que dar los mismos tramos con la misma confianza.

**Éxito:**

1. Mismos resultados que la implementación Python sobre episodios reales (criterio en la sección 8), comprobado antes de borrar nada.
2. numpy fuera del daemon (27 MB de los 72 MB que ocupa hoy).
3. Pasar al episodio siguiente a mitad de un análisis no reinicia ningún proceso: solo se corta el `ffmpeg` en curso.

## 2. Decisiones tomadas

| Decisión | Elegido | Motivo |
|---|---|---|
| Comparación entre dos episodios (`detect_opening`) | Se retira, junto con el botón de la ficha | En los datos reales: 0 tramos guardados y 0 detecciones en 31 sesiones; el propio código la describe como lenta y poco fiable. Sin referencias queda AniSkip. |
| Reparto del trabajo | Rust calcula, C# dirige | Rust sigue sin lanzar procesos ni tocar archivos; la cancelación es inmediata desde C#. |
| Validación | Incluye episodios reales del usuario, en solo lectura | Es la única forma de confirmar que los umbrales medidos siguen valiendo. |

Descartado: que Rust lance `ffmpeg` y lleve la caché (cancelar a través de la frontera obliga a una llamada bloqueante de hasta 30 s), y hacerlo todo en C# (la huella usa una transformada de 800 muestras, que no es potencia de 2: `rustfft` la calcula para cualquier tamaño; en C# haría falta una biblioteca numérica grande o escribirla a mano).

## 3. Piezas

### 3.1 Rust: `native/animetracker_core/src/audio.rs` (nuevo)

Dependencia nueva: `rustfft`. Dos funciones exportadas, las dos dentro de `ffi_catch` y con tipos directos (sin JSON ni cadenas):

```rust
/// Huella de audio mono a 8 kHz en f32. Escribe fotogramas de 8 valores en `salida` (capacidad en fotogramas).
/// Devuelve los fotogramas escritos (muestras / 800), o -1 si un puntero es nulo o no cabe.
pub extern "C" fn audio_huella(pcm: *const f32, muestras: usize, salida: *mut f32, capacidad: usize) -> i32;

#[repr(C)]
pub struct CoincidenciaAudio { pub indice: i32, pub parcial: i32, pub inicio: f64, pub fin: f64, pub confianza: f64 }

/// Mejor tema dentro de un tramo del episodio. `temas` son las huellas una detrás de otra y `largos` sus fotogramas.
/// `excluir_inicio`/`excluir_fin` en NaN = sin exclusión. Devuelve 1 (hay coincidencia, rellena `salida`), 0 (ninguna) o -1 (error).
pub extern "C" fn audio_mejor_coincidencia(
    ventana: *const f32, fotogramas_ventana: usize, inicio_ventana: f64,
    temas: *const f32, largos: *const usize, n_temas: usize,
    excluir_inicio: f64, excluir_fin: f64, salida: *mut CoincidenciaAudio) -> i32;
```

Contenido, port uno a uno del Python (los nombres entre paréntesis son los de hoy):

- **Huella** (`_huella`): bloques de 800 muestras; por bloque, RMS sin ventana y energía con ventana de Hann simétrica en 7 bandas con límites 0, 150, 300, 600, 1000, 1600, 2400 y 4000 Hz (bins de 10 Hz, límite superior excluido). Fotograma = `[rms, log10(banda + 1e-9) × 7]`. Cálculo en f64, resultado en f32.
- **Curva de parecido** (`_Ventana.curva`): Pearson del segmento en cada posición de la ventana, por columna, con correlación por FFT de tamaño potencia de 2 ≥ ventana + segmento, sumas acumuladas para media y varianza, pesos `[0,5, 0,5/7 × 7]` solo sobre las columnas con desviación > 1e-6, recorte a [-1, 1].
- **Coincidencia entera** (`_coincidencia_completa`), **por trozos** (`_coincidencia_por_trozos`: trozos de 10 s cada 5 s, umbral 0,65, se tolera un trozo malo en medio, tramo mínimo de 30 s o medio tema) y **mejor coincidencia** (`_mejor_coincidencia`: entera ≥ 0,8 gana; si no, por trozos sobre los 4 mejores candidatos con parecido entero ≥ 0,45 o más largos que la ventana) con la regla de exclusión (`_solapa`: más de la mitad del tramo dentro del excluido).

La función devuelve la mejor coincidencia aunque no llegue a la confianza mínima; quien llama compara con el umbral, igual que hoy.

### 3.2 C#: frontera

`Services/Native/NativeMethods.cs`: dos `[LibraryImport]` y dos envoltorios (`HuellaDeAudio(ReadOnlySpan<float> pcm) → float[]`, `MejorCoincidencia(...) → CoincidenciaAudio?`) con el patrón existente: comprueban `IsAvailable` y ante cualquier excepción registran y devuelven `null`.

### 3.3 C#: `Services/DetectorTemasAudio.cs` e `IDetectorTemasAudio` (nuevos)

```csharp
public interface IDetectorTemasAudio
{
    bool Disponible { get; }   // la DLL nativa está cargada
    /// Null = el motor no pudo trabajar (sin DLL, excepción). Con Success = false y Error = motivo concreto (episodio ilegible…).
    Task<SkipTimesCoordinator.DeteccionTemasResult?> DetectarAsync(string rutaEpisodio, IReadOnlyList<ReferenciaAudio> referencias,
        double confianzaMinima, double segundosInicio, double segundosFinal, CancellationToken ct);
}
public sealed record ReferenciaAudio(string Ruta, string Tipo, int Prioridad);   // Tipo "OP"/"ED"; 0 = aplica al episodio, 1 = resto
```

Se reutilizan los resultados que ya existen (`DeteccionTemasResult`, `TemaDetectado`) para que el coordinador y sus pruebas cambien lo mínimo.

Port del flujo de `detect_themes`:

1. Descarta rutas que no existen (eso descarta también las URL). Sin referencias válidas: éxito con lista vacía.
2. Duración con `ffprobe` (`format=duration`). Si falla: `Success = false`.
3. Audio del inicio (`segundosInicio`) y del final (`segundosFinal`) en paralelo; si las ventanas se solapan (episodio corto), una sola decodificación del archivo entero.
4. Opening: temas OP que aplican, luego el resto. Si no aparece, segunda pasada desde (inicio − largo del tema) hasta la mitad del episodio.
5. Ending: ED que aplican, ED restantes, OP que aplican, OP restantes; excluyendo el tramo del opening ya hallado.
6. En cada grupo se para en el primero que llegue a la confianza mínima; las huellas de un grupo solo se calculan si se llega a él (hasta 4 a la vez).

`ffmpeg` se lanza con `Core/ProcesoExterno` y los mismos argumentos que hoy (`-nostdin -max_alloc 2147483648 [-ss] -i … -vn -sn -dn [-t] -ac 1 -ar 8000 -f f32le -`), más `-protocol_whitelist file` en la entrada como en miniaturas y datos técnicos. `ProcesoExterno` recibe una variante que devuelve la salida en bytes (hoy solo texto).

### 3.4 Caché de huellas

- Carpeta: la de hoy (`AppDataPaths.AudioFingerprintsDir`), inyectable para las pruebas.
- Clave: por contenido, como hoy (versión de formato + tamaño + primeros y últimos 64 KB), con SHA-256 y versión de formato 2.
- Archivo: `<clave>.huella`, f32 en crudo (fotogramas × 8), con el silencio de los extremos ya recortado. Un archivo cuyo tamaño no sea múltiplo de 32 bytes se ignora y se recalcula.
- Escritura a un temporal y renombrado; al leer se actualiza la fecha para la poda; tope de 800 archivos, se borran los menos usados hasta dejar 600.
- Los `.npy` del formato anterior se borran al guardar la primera huella nueva. Son caché: se regeneran solos.
- Una huella de menos de 5 s se trata como tema roto y no se usa.

### 3.5 `SkipTimesCoordinator`

- El constructor recibe `IDetectorTemasAudio?` en lugar de `IPythonBridgeService?`; "se puede detectar" pasa a ser `detector?.Disponible == true`.
- `DetectarTemasAsync` llama al detector. Respuesta `null` = motor sin respuesta (el análisis no se guarda como completo, igual que hoy); `Success = false` = aviso en el registro con el motivo.
- Desaparecen `DetectarPorComparacionAsync`, `AudioSkipResult`, `RutaPluginAudioSkip` y el paso 4 del análisis.
- Registro en `App.xaml.cs`: `IDetectorTemasAudio` como singleton.

## 4. Lo que se retira

- `tools/python/audio_skip_plugin.py`, su copia `AnimeLocalTracker/PythonPlugins/audio_skip_plugin.py`, la entrada del `.csproj`, `LocalizadorHerramientasPython.PluginAudioSkip` y sus pruebas (Python y C#).
- `numpy` de `pyproject.toml` y de `requirements.txt`.
- El comando `AnalizarOpenings` de `EpisodiosFichaViewModel`, su botón en `DetalleView.xaml` y los textos que solo él usa, en español e inglés.
- El comando `run-plugin` del daemon **se queda**: lo usan los plugins del usuario.

## 5. Fallos y cancelación

| Situación | Comportamiento |
|---|---|
| Falta `animetracker_core.dll` | `Disponible = false`: no se intenta el audio, se usa AniSkip. Como hoy cuando no hay daemon. |
| `ffmpeg`/`ffprobe` fallan con el episodio | `Success = false` con motivo; aviso en el registro; AniSkip de respaldo. |
| Un tema no se puede decodificar | Ese tema se ignora; siguen los demás. |
| Se cancela (cambio de episodio) | `ProcesoExterno` mata el `ffmpeg` en curso y sale `OperationCanceledException`. Las llamadas a Rust duran milisegundos y no necesitan cancelación propia. |
| Pánico en Rust | Lo captura `ffi_catch` y devuelve -1; el detector responde `null`. |

## 6. Orden de trabajo

1. Rust: `audio.rs` con sus pruebas y valores de referencia. Python intacto.
2. C#: frontera, `ProcesoExterno` en bytes, `DetectorTemasAudio` con sus pruebas. El coordinador sigue usando Python.
3. Validación con episodios reales (sección 8). Si no pasa, se corrige antes de seguir.
4. Cambiar el coordinador al detector y adaptar sus pruebas.
5. Retirar lo de la sección 4.
6. Comprobación en vivo con perfil aislado y actualización de documentos (`CLAUDE.md`, skill `nucleo-poliglota`, memoria).

## 7. Pruebas

- **Rust (`cargo test`):**
  - Huella de una señal fija y curva de parecido contra valores generados con el Python actual (tolerancia 1e-4 en la huella, 1e-6 en la curva).
  - La curva contra Pearson calculado a mano.
  - Coincidencia entera, por trozos (ending recortado), tema que no suena, exclusión del opening, punteros nulos y tamaños incoherentes.
- **C# (xUnit), `DetectorTemasAudioTests`:** las 10 pruebas de flujo de `test_audio_skip_plugin.py` portadas con un decodificador simulado (opening tardío, opening a caballo del límite, opening en la segunda mitad que no se marca, opening que hace de ending, los temas que no aplican no se decodifican si acierta uno que aplica…), más la caché (misma huella con otro nombre de archivo, archivo dañado, poda, borrado de `.npy`) y una de integración con `ffmpeg` real sobre audio sintético.
- **C#, coordinador:** las 42 pruebas existentes pasan a simular `IDetectorTemasAudio`; se eliminan las de la comparación entre dos episodios.

## 8. Validación con episodios reales

Antes del paso 4. Arnés temporal que no se queda en el repo:

1. Se eligen los episodios de la biblioteca que tienen temas descargados (`Music/` y `SkipReferences/`). Solo lectura sobre videos y música; la caché de la prueba va a una carpeta temporal.
2. Las dos implementaciones reciben exactamente la misma lista de referencias y los mismos parámetros (confianza 0,7, 480 s de inicio, 360 s de final).
3. **Criterio de aceptación**, por episodio: los mismos tramos (op/ed) con el mismo tema, inicio y fin a menos de 0,1 s, confianza a menos de 0,01 y mismo modo (entero/por trozos).
4. Se anota el tiempo por episodio de cada implementación. La nueva no debe ser más lenta.
5. Comprobación secundaria: los tramos nuevos frente a los 51 ya guardados en la base (26 openings y 25 endings), que salieron de la app con Python.

Si algún episodio no cumple el criterio, no se retira Python hasta entender la diferencia.

## 9. Riesgos

- **Diferencias numéricas:** `rustfft` y numpy no redondean igual. La diferencia esperada es del orden de 1e-12 antes de pasar a f32; los valores de referencia en las pruebas de Rust y la validación real están para detectarlo si fuera mayor.
- **`AnimeTrackerTools.exe`:** no se regenera en este cambio. El de la máquina seguirá llevando numpy hasta el siguiente empaquetado; el ahorro de tamaño llega con la release que construye CI.
- **Auditoría de dependencias:** `rustfft` y sus transitivas tienen que pasar `cargo audit` (bloquea el CI).

## 10. Fuera de alcance

Cambiar umbrales o constantes del algoritmo, probar Chromaprint, el canal StreamJsonRpc, y quitar `NativeMethods.ParseBatch`/`rayon`.
