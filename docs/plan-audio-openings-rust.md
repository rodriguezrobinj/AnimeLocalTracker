# Plan de implementación: detección de openings y endings por audio en el núcleo Rust

> **Para quien lo ejecute:** SUB-SKILL OBLIGATORIO: `superpowers:executing-plans`, tarea a tarea, en esta misma sesión (en este repo no se usan subagentes salvo que el usuario lo pida). Los pasos usan casillas (`- [ ]`) para llevar la cuenta.

**Objetivo:** que ubicar el opening y el ending de un episodio lo haga el núcleo Rust (cálculo) dirigido por C# (ffmpeg, caché, orden de búsqueda), y retirar el plugin Python de audio y numpy.

**Arquitectura:** Rust exporta dos funciones puras (`audio_huella`, `audio_mejor_coincidencia`) con tipos directos. C# añade `DetectorTemasAudio` (port del flujo `detect_themes`) y `CacheHuellasAudio`; `SkipTimesCoordinator` llama al detector en lugar del daemon. Lo nuevo se construye junto a lo viejo y Python no se retira hasta que la validación con episodios reales pase.

**Tecnologías:** Rust (`rustfft`), C# .NET 8 (`LibraryImport`, xUnit, FluentAssertions, Moq), ffmpeg/ffprobe embebidos.

**Diseño:** `docs/investigacion-audio-openings-rust.md`. Quien ejecute lee los dos documentos.

## Restricciones globales

- **Sin commit ni push.** Donde un plan normal diría "commit", aquí hay un paso de verificación. Sin ramas, worktrees ni subagentes.
- **No se cambian constantes ni umbrales del algoritmo.** Valores exactos: 10 fotogramas/s; 8000 Hz; 800 muestras por fotograma; 8 columnas; bandas 0, 150, 300, 600, 1000, 1600, 2400, 4000 Hz; pesos `[0,5, 0,5/7 × 7]`; confianza segura 0,8; trozo 10 s cada 5 s; umbral de trozo 0,65; tramo mínimo 30 s; mínimo para trozos 0,45; máximo 4 candidatos por trozos; límite del opening 0,5 de la duración; confianza mínima 0,7; 480 s de inicio; 360 s de final.
- **Rust no lanza procesos ni toca archivos.** ffmpeg/ffprobe solo desde C# con `Core/ProcesoExterno`.
- **Frontera:** `[LibraryImport]` (nunca `[DllImport]`), tipos directos, todo cuerpo exportado dentro de `ffi_catch`.
- **Las pruebas nunca escriben bajo `AppDataPaths`:** toda carpeta de caché es inyectable y en pruebas es temporal.
- **Verificación de C#:** skill `repo-build-test` (compilar la app sola con 0 advertencias; pruebas con `--no-restore -p:BuildProjectReferences=false`; la suite completa una vez al final).
- **Verificación de Rust:** `cargo test` y `cargo clippy --release -- -D warnings` en `native/animetracker_core`. Tras compilar, copiar `target/release/animetracker_core.dll` a `AnimeLocalTracker/animetracker_core.dll`.
- **Textos visibles:** las claves se quitan de las dos mitades (ES y EN) de `LocalizationService` (skill `localizacion`).
- **Criterio de aceptación con episodios reales** (tarea 7): mismos tramos con el mismo tema, inicio y fin a menos de 0,1 s, confianza a menos de 0,01, mismo modo.
- Mensajes al usuario, comentarios y nombres en español, como el código que los rodea.

## Foco de revisión

Entradas que el diseño implica y que ninguna prueba de flujo cubre por sí sola; cada una lleva su prueba en la tarea indicada:

1. **Tema más largo que el tramo del episodio** (episodio corto o tema de 5 minutos): debe ubicarse por trozos, no descartarse. → Tarea 2, prueba `tema_mas_largo_que_la_ventana_se_ubica_por_trozos`.
2. **Episodio sin pista de audio o que ffmpeg no puede leer:** respuesta con `Success = false` y motivo, sin excepción. → Tarea 6, `SiNoSePuedeExtraerElAudio_RespondeConElMotivo`.
3. **Archivo de caché dañado** (vacío o cortado a medias): se ignora y se recalcula. → Tarea 5, `UnArchivoDeCacheDanado_SeIgnora`.
4. **Tema que es todo silencio o dura menos de 5 s:** se ignora sin romper el resto. → Tarea 6, `UnTemaDeSilencio_SeIgnoraYSiguenLosDemas`.
5. **Cancelar a mitad del análisis:** sale `OperationCanceledException` y no queda ningún archivo temporal en la caché. → Tarea 6, `LaCancelacionSePropaga_YNoDejaArchivosTemporales`.

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
|---|---|---|
| `native/animetracker_core/Cargo.toml` | Modificar | Dependencia `rustfft` |
| `native/animetracker_core/src/audio.rs` | Crear | Huella, curva de parecido, coincidencias (cálculo puro) |
| `native/animetracker_core/src/lib.rs` | Modificar | Exportar `audio_huella` y `audio_mejor_coincidencia` |
| `AnimeLocalTracker/Services/Native/NativeMethods.cs` | Modificar | Frontera C# de las dos funciones |
| `AnimeLocalTracker/Core/ProcesoExterno.cs` | Modificar | Variante con salida en bytes |
| `AnimeLocalTracker/Services/CacheHuellasAudio.cs` | Crear | Caché en disco de huellas de temas |
| `AnimeLocalTracker/Services/DetectorTemasAudio.cs` | Crear | Interfaz, registro de referencia y flujo de detección |
| `AnimeLocalTracker/Services/SkipTimesCoordinator.cs` | Modificar | Usar el detector; quitar la comparación entre episodios |
| `AnimeLocalTracker/App.xaml.cs` | Modificar | Registrar `IDetectorTemasAudio` |
| `AnimeLocalTracker.Tests/Services/AudioSintetico.cs` | Crear | Ruido reproducible para pruebas |
| `AnimeLocalTracker.Tests/Services/NativeAudioTests.cs` | Crear | Frontera contra la DLL real |
| `AnimeLocalTracker.Tests/Services/CacheHuellasAudioTests.cs` | Crear | Caché |
| `AnimeLocalTracker.Tests/Services/DetectorTemasAudioTests.cs` | Crear | Flujo (port de las pruebas Python) |
| `AnimeLocalTracker.Tests/Services/SkipTimesCoordinator*Tests.cs` | Modificar | Simular el detector en vez del daemon |
| `tools/python/audio_skip_plugin.py`, su copia en `PythonPlugins/`, pruebas, numpy | Borrar | Tarea 9 |

Generador de ruido común a Rust, C# y el oráculo Python (congruencial de Numerical Recipes): `estado = estado × 1664525 + 1013904223 (mod 2³²)`; `valor = estado / 2³² − 0,5`, en f32. `ruido(1, 1)` empieza por `-0.2635444700717926, -0.1307293325662613, 0.004242032300680876`.

Los valores de referencia de las tareas 1 a 3 salieron del plugin Python actual con el script `oraculo_audio.py` (carpeta temporal de la sesión; no se guarda en el repo). Con ese mismo ruido, las 14 pruebas de `test_audio_skip_plugin.py` pasan.

---

### Tarea 1: Rust — huella y curva de parecido

**Archivos:**
- Modificar: `native/animetracker_core/Cargo.toml`
- Crear: `native/animetracker_core/src/audio.rs`
- Modificar: `native/animetracker_core/src/lib.rs` (línea 1-2: declarar el módulo)

**Interfaces:**
- Consume: nada.
- Produce: `audio::huella(pcm: &[f32]) -> Vec<f32>`; `audio::Ventana::new(inicio: f64, huella: &[f32]) -> Ventana`; `Ventana::curva(&mut self, segmento: &[f32]) -> Vec<f64>`; constantes `audio::FPS`, `audio::MUESTRAS_POR_FOTOGRAMA`, `audio::COLUMNAS`; campos `Ventana.inicio` y `Ventana.n` visibles dentro del módulo.

- [ ] **Paso 1: añadir la dependencia y declarar el módulo**

En `Cargo.toml`, al final de `[dependencies]`:

```toml
rustfft = "6"
```

En `lib.rs`, junto a los otros `pub mod`:

```rust
pub mod audio;
```

- [ ] **Paso 2: escribir las pruebas que fallan**

Crear `native/animetracker_core/src/audio.rs` solo con las pruebas (el resto llega en el paso 4):

```rust
//! Ubicación de un tema (opening/ending) dentro del audio de un episodio. Cálculo puro: sin procesos, archivos ni JSON.
//! Port de `detect_themes` (antes en tools/python/audio_skip_plugin.py); las constantes son las medidas con episodios reales.

#[cfg(test)]
pub(crate) mod pruebas {
    use super::*;

    /// Ruido reproducible (el mismo generador que usan las pruebas de C#): fotogramas × COLUMNAS valores en [-0,5, 0,5).
    pub(crate) fn ruido(fotogramas: usize, semilla: u32) -> Vec<f32> {
        let mut estado = semilla;
        (0..fotogramas * COLUMNAS)
            .map(|_| {
                estado = estado.wrapping_mul(1664525).wrapping_add(1013904223);
                (estado as f64 / 4294967296.0 - 0.5) as f32
            })
            .collect()
    }

    #[test]
    fn el_ruido_es_el_mismo_que_en_las_otras_implementaciones() {
        let r = ruido(1, 1);
        assert!((r[0] as f64 - -0.2635444700717926).abs() < 1e-7);
        assert!((r[1] as f64 - -0.1307293325662613).abs() < 1e-7);
        assert!((r[2] as f64 - 0.004242032300680876).abs() < 1e-7);
    }

    #[test]
    fn la_huella_de_una_senal_fija_coincide_con_la_de_python() {
        // 3 fotogramas: tres tonos (440, 1250 y 3000 Hz); el tercero baja a un cuarto en el fotograma del medio.
        let pcm: Vec<f32> = (0..2400usize)
            .map(|i| {
                let t = i as f64 / 8000.0;
                let nivel = if (i / 800) % 2 == 0 { 1.0 } else { 0.25 };
                (0.3 * (2.0 * std::f64::consts::PI * 440.0 * t).sin()
                    + 0.2 * (2.0 * std::f64::consts::PI * 1250.0 * t + 0.5).sin()
                    + 0.1 * (2.0 * std::f64::consts::PI * 3000.0 * t).sin() * nivel) as f32
            })
            .collect();
        let esperado: [[f64; 8]; 3] = [
            [0.26457512378692627, -7.665646553039551, -6.316011905670166, 3.7318506240844727, -6.200735092163086, 3.3796679973602295, -7.546137809753418, 2.7776081562042236],
            [0.2555631101131439, -7.667977809906006, -6.316385746002197, 3.7318506240844727, -6.201665878295898, 3.3796679973602295, -7.583815097808838, 1.5734881162643433],
            [0.26457512378692627, -7.665646553039551, -6.316011905670166, 3.7318506240844727, -6.200735092163086, 3.3796679973602295, -7.546137809753418, 2.7776081562042236],
        ];

        let h = huella(&pcm);

        assert_eq!(h.len(), 3 * COLUMNAS);
        for (f, fila) in esperado.iter().enumerate() {
            for (j, &e) in fila.iter().enumerate() {
                let v = h[f * COLUMNAS + j] as f64;
                assert!((v - e).abs() < 1e-4, "fotograma {f}, columna {j}: {v} frente a {e}");
            }
        }
    }

    #[test]
    fn menos_de_un_fotograma_de_audio_no_da_huella() {
        assert!(huella(&[0.0f32; 799]).is_empty());
        assert!(huella(&[]).is_empty());
    }

    fn huellas_de_formula() -> (Vec<f32>, Vec<f32>) {
        let mut h = Vec::with_capacity(60 * COLUMNAS);
        for f in 0..60 {
            for j in 0..COLUMNAS {
                let (f, j) = (f as f64, j as f64);
                h.push(((0.37 * f + 1.3 * j).sin() + 0.5 * (0.11 * f * (j + 1.0)).cos()) as f32);
            }
        }
        let mut seg = Vec::with_capacity(10 * COLUMNAS);
        for r in 0..10 {
            for j in 0..COLUMNAS {
                seg.push((h[(20 + r) * COLUMNAS + j] as f64 + 0.1 * (2.1 * r as f64 + j as f64).sin()) as f32);
            }
        }
        (h, seg)
    }

    #[test]
    fn la_curva_coincide_con_la_de_python() {
        let (h, seg) = huellas_de_formula();

        let curva = Ventana::new(0.0, &h).curva(&seg);

        assert_eq!(curva.len(), 51);
        let maximo = (0..curva.len()).max_by(|&a, &b| curva[a].partial_cmp(&curva[b]).unwrap()).unwrap();
        assert_eq!(maximo, 20);
        for (i, e) in [(0usize, 0.4184729645943921), (20, 0.9933011463332803), (35, 0.5975859796521121), (50, -0.10835596766139018)] {
            assert!((curva[i] - e).abs() < 1e-6, "posición {i}: {} frente a {e}", curva[i]);
        }
    }

    #[test]
    fn la_curva_coincide_con_pearson_calculado_a_mano() {
        let ventana = ruido(300, 4);
        let segmento = ruido(40, 5);
        let pesos = [0.5, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0];

        let curva = Ventana::new(0.0, &ventana).curva(&segmento);

        assert_eq!(curva.len(), 300 - 40 + 1);
        for (i, &c) in curva.iter().enumerate() {
            let mut esperado = 0.0;
            for j in 0..COLUMNAS {
                let a: Vec<f64> = (0..40).map(|k| ventana[(i + k) * COLUMNAS + j] as f64).collect();
                let b: Vec<f64> = (0..40).map(|k| segmento[k * COLUMNAS + j] as f64).collect();
                let (ma, mb) = (a.iter().sum::<f64>() / 40.0, b.iter().sum::<f64>() / 40.0);
                let cov: f64 = a.iter().zip(&b).map(|(x, y)| (x - ma) * (y - mb)).sum();
                let (va, vb): (f64, f64) = (a.iter().map(|x| (x - ma).powi(2)).sum(), b.iter().map(|y| (y - mb).powi(2)).sum());
                esperado += pesos[j] * cov / (va.sqrt() * vb.sqrt());
            }
            assert!((c - esperado).abs() < 1e-6, "posición {i}: {c} frente a {esperado}");
        }
    }

    #[test]
    fn un_segmento_plano_o_mas_largo_que_la_ventana_no_da_curva() {
        let mut ventana = Ventana::new(0.0, &ruido(50, 1));
        assert!(ventana.curva(&vec![0.25f32; 20 * COLUMNAS]).is_empty(), "sin variación no hay con qué comparar");
        assert!(ventana.curva(&ruido(51, 2)).is_empty());
        assert!(ventana.curva(&ruido(1, 2)).is_empty());
    }
}
```

- [ ] **Paso 3: comprobar que fallan**

Ejecutar: `cargo test --manifest-path native/animetracker_core/Cargo.toml`
Esperado: no compila, con errores `cannot find function 'huella'` y `cannot find struct 'Ventana'`.

- [ ] **Paso 4: implementar**

En `audio.rs`, entre el comentario de cabecera y el módulo de pruebas:

```rust
use rustfft::num_complex::Complex;
use rustfft::FftPlanner;
use std::collections::HashMap;

pub const FPS: usize = 10;
/// 8000 Hz / 10 fotogramas por segundo.
pub const MUESTRAS_POR_FOTOGRAMA: usize = 800;
/// Volumen (RMS) + energía en 7 bandas.
pub const COLUMNAS: usize = 8;
/// Límites de las bandas en bins de 10 Hz: 0, 150, 300, 600, 1000, 1600, 2400 y 4000 Hz (el superior queda fuera).
const BANDAS_BIN: [usize; COLUMNAS] = [0, 15, 30, 60, 100, 160, 240, 400];
/// La mitad del peso para el volumen y la otra mitad repartida entre las bandas.
const PESOS: [f64; COLUMNAS] = [0.5, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0, 0.5 / 7.0];

/// Huella de audio mono a 8 kHz: por cada bloque de 800 muestras, `[rms, log10(energía de la banda + 1e-9) × 7]`.
/// Solo con el volumen, dos cortes de la misma canción daban 0,55-0,68 frente a 0,86-1,0 del correcto; con las bandas
/// los equivocados bajan a ~0,3-0,6 (medido con 25 episodios reales de 9 animes).
pub fn huella(pcm: &[f32]) -> Vec<f32> {
    let n = MUESTRAS_POR_FOTOGRAMA;
    let total = pcm.len() / n;
    let mut salida = Vec::with_capacity(total * COLUMNAS);
    if total == 0 {
        return salida;
    }

    let fft = FftPlanner::<f64>::new().plan_fft_forward(n);
    // Ventana de Hann simétrica (la de numpy.hanning).
    let hann: Vec<f64> = (0..n)
        .map(|k| 0.5 - 0.5 * (2.0 * std::f64::consts::PI * k as f64 / (n as f64 - 1.0)).cos())
        .collect();
    let mut buffer = vec![Complex::new(0.0, 0.0); n];

    for bloque in pcm.chunks_exact(n) {
        let mut suma2 = 0.0;
        for (i, &x) in bloque.iter().enumerate() {
            let x = x as f64;
            suma2 += x * x;
            buffer[i] = Complex::new(x * hann[i], 0.0);
        }
        fft.process(&mut buffer);

        salida.push((suma2 / n as f64).sqrt() as f32);
        for b in 0..COLUMNAS - 1 {
            let energia: f64 = buffer[BANDAS_BIN[b]..BANDAS_BIN[b + 1]].iter().map(|c| c.norm_sqr()).sum();
            salida.push((energia + 1e-9).log10() as f32);
        }
    }
    salida
}

/// Huella de un tramo del episodio con lo que se reutiliza entre temas (sumas acumuladas y espectros por tamaño de FFT).
pub struct Ventana {
    pub(crate) inicio: f64,
    pub(crate) n: usize,
    h: Vec<f64>,
    suma: Vec<f64>,
    suma2: Vec<f64>,
    espectros: HashMap<usize, Vec<Vec<Complex<f64>>>>,
    planner: FftPlanner<f64>,
}

impl Ventana {
    /// `inicio`: segundo del episodio donde empieza el tramo. `huella`: fotogramas × COLUMNAS, por filas.
    pub fn new(inicio: f64, huella: &[f32]) -> Self {
        let n = huella.len() / COLUMNAS;
        let h: Vec<f64> = huella[..n * COLUMNAS].iter().map(|&v| v as f64).collect();
        let mut suma = vec![0.0; (n + 1) * COLUMNAS];
        let mut suma2 = vec![0.0; (n + 1) * COLUMNAS];
        for i in 0..n {
            for j in 0..COLUMNAS {
                let v = h[i * COLUMNAS + j];
                suma[(i + 1) * COLUMNAS + j] = suma[i * COLUMNAS + j] + v;
                suma2[(i + 1) * COLUMNAS + j] = suma2[i * COLUMNAS + j] + v * v;
            }
        }
        Self { inicio, n, h, suma, suma2, espectros: HashMap::new(), planner: FftPlanner::new() }
    }

    /// Correlación de Pearson del segmento en cada posición de la ventana (media ponderada por columna, -1..1).
    /// Vacía si el segmento no cabe, tiene menos de 2 fotogramas o no varía en ninguna columna.
    pub fn curva(&mut self, segmento: &[f32]) -> Vec<f64> {
        let largo = segmento.len() / COLUMNAS;
        if largo < 2 || largo > self.n {
            return Vec::new();
        }

        let mut media = [0.0f64; COLUMNAS];
        let mut desv = [0.0f64; COLUMNAS];
        for fila in segmento.chunks_exact(COLUMNAS) {
            for (m, &v) in media.iter_mut().zip(fila) {
                *m += v as f64;
            }
        }
        for m in media.iter_mut() {
            *m /= largo as f64;
        }
        for fila in segmento.chunks_exact(COLUMNAS) {
            for ((d, &v), m) in desv.iter_mut().zip(fila).zip(&media) {
                let diferencia = v as f64 - m;
                *d += diferencia * diferencia;
            }
        }
        for d in desv.iter_mut() {
            *d = (*d / largo as f64).sqrt();
        }

        // Una columna que no varía en el segmento no aporta (y dividiría entre cero): su peso se reparte entre las demás.
        let suma_pesos: f64 = (0..COLUMNAS).filter(|&j| desv[j] > 1e-6).map(|j| PESOS[j]).sum();
        if suma_pesos <= 0.0 {
            return Vec::new();
        }

        let nfft = (self.n + largo).next_power_of_two();
        let directa = self.planner.plan_fft_forward(nfft);
        let inversa = self.planner.plan_fft_inverse(nfft);
        if !self.espectros.contains_key(&nfft) {
            let mut columnas = Vec::with_capacity(COLUMNAS);
            for j in 0..COLUMNAS {
                let mut buffer = vec![Complex::new(0.0, 0.0); nfft];
                for i in 0..self.n {
                    buffer[i].re = self.h[i * COLUMNAS + j];
                }
                directa.process(&mut buffer);
                columnas.push(buffer);
            }
            self.espectros.insert(nfft, columnas);
        }
        let espectros = &self.espectros[&nfft];

        let posiciones = self.n - largo + 1;
        let mut curva = vec![0.0f64; posiciones];
        let mut buffer = vec![Complex::new(0.0, 0.0); nfft];
        for j in 0..COLUMNAS {
            if desv[j] <= 1e-6 {
                continue;
            }
            buffer.fill(Complex::new(0.0, 0.0));
            for (i, fila) in segmento.chunks_exact(COLUMNAS).enumerate() {
                buffer[i].re = (fila[j] as f64 - media[j]) / desv[j];
            }
            directa.process(&mut buffer);
            for (b, e) in buffer.iter_mut().zip(espectros[j].iter()) {
                *b = *e * b.conj();
            }
            inversa.process(&mut buffer);

            let peso = PESOS[j] / suma_pesos;
            for (t, valor) in curva.iter_mut().enumerate() {
                let corr = buffer[t].re / nfft as f64; // rustfft no normaliza la inversa
                let s = self.suma[(t + largo) * COLUMNAS + j] - self.suma[t * COLUMNAS + j];
                let s2 = self.suma2[(t + largo) * COLUMNAS + j] - self.suma2[t * COLUMNAS + j];
                let m = s / largo as f64;
                let var = s2 / largo as f64 - m * m;
                *valor += peso * corr / (largo as f64 * var.max(1e-12).sqrt());
            }
        }
        for v in curva.iter_mut() {
            *v = v.clamp(-1.0, 1.0);
        }
        curva
    }
}
```

- [ ] **Paso 5: comprobar que pasan**

Ejecutar: `cargo test --manifest-path native/animetracker_core/Cargo.toml`
Esperado: `test result: ok. 6 passed`.

- [ ] **Paso 6: verificación**

Ejecutar: `cargo clippy --release --manifest-path native/animetracker_core/Cargo.toml -- -D warnings`
Esperado: `Finished` sin errores. (El aviso `linker stdout: Creando biblioteca…` que da la herramienta local al enlazar no es del código.)

---

### Tarea 2: Rust — coincidencia entera, por trozos y mejor candidato

**Archivos:**
- Modificar: `native/animetracker_core/src/audio.rs`

**Interfaces:**
- Consume: `Ventana`, `Ventana::curva`, `COLUMNAS`, `FPS` (tarea 1); en pruebas, `pruebas::ruido`.
- Produce: `audio::Coincidencia { inicio: f64, fin: f64, confianza: f64, parcial: bool }` (`Copy`); `audio::mejor_coincidencia(ventana: &mut Ventana, temas: &[&[f32]], excluir: Option<(f64, f64)>) -> Option<(usize, Coincidencia)>` (el `usize` es el índice del tema ganador).

- [ ] **Paso 1: escribir las pruebas que fallan**

Añadir dentro de `mod pruebas` de `audio.rs`:

```rust
    /// Copia `tema[desde..]` en `episodio` a partir de `fotograma`, con un poco de ruido encima (como un episodio real).
    pub(crate) fn incrustar(episodio: &[f32], tema: &[f32], fotograma: usize, desde: usize) -> Vec<f32> {
        let mut ep = episodio.to_vec();
        let parte = &tema[desde * COLUMNAS..];
        let extra = ruido(parte.len() / COLUMNAS, 99);
        for (i, (&p, &e)) in parte.iter().zip(&extra).enumerate() {
            ep[fotograma * COLUMNAS + i] = (p as f64 + 0.1 * e as f64) as f32;
        }
        ep
    }

    fn cerca(valor: f64, esperado: f64, margen: f64) -> bool {
        (valor - esperado).abs() <= margen
    }

    #[test]
    fn un_tema_que_suena_entero_se_ubica_con_confianza_alta() {
        let tema = ruido(300, 1);
        let episodio = incrustar(&ruido(1200, 3), &tema, 400, 0);

        let (indice, c) = mejor_coincidencia(&mut Ventana::new(0.0, &episodio), &[&tema], None).unwrap();

        assert_eq!(indice, 0);
        assert!(!c.parcial);
        assert!(cerca(c.inicio, 40.0, 1e-9) && cerca(c.fin, 70.0, 1e-9));
        assert!(cerca(c.confianza, 0.9950893339441097, 1e-6), "confianza {}", c.confianza);
    }

    #[test]
    fn un_tema_recortado_se_ubica_por_trozos() {
        // El episodio solo usa los últimos 60 s de un tema de 90 s; el tramo del episodio empieza en el segundo 100.
        let tema = ruido(900, 2);
        let episodio = incrustar(&ruido(1500, 3), &tema, 500, 300);

        let (_, c) = mejor_coincidencia(&mut Ventana::new(100.0, &episodio), &[&tema], None).unwrap();

        assert!(c.parcial);
        assert!(cerca(c.inicio, 150.0, 1e-9) && cerca(c.fin, 210.0, 1e-9), "{} - {}", c.inicio, c.fin);
        assert!(cerca(c.confianza, 0.9948767433137327, 1e-6), "confianza {}", c.confianza);
    }

    #[test]
    fn un_tema_que_no_suena_queda_con_confianza_baja() {
        let (_, c) = mejor_coincidencia(&mut Ventana::new(0.0, &ruido(1200, 3)), &[&ruido(300, 7)], None).unwrap();

        // Se devuelve el mejor aunque no llegue al mínimo: quien llama lo compara con su umbral (0,7).
        assert!(cerca(c.confianza, 0.09663914152236984, 1e-6), "confianza {}", c.confianza);
    }

    #[test]
    fn el_tramo_excluido_no_puede_volver_a_salir() {
        let tema = ruido(300, 1);
        let episodio = incrustar(&ruido(1200, 3), &tema, 400, 0);

        // El ending no puede ser el mismo opening ya encontrado en 40-70 s.
        assert!(mejor_coincidencia(&mut Ventana::new(0.0, &episodio), &[&tema], Some((40.0, 70.0))).is_none());
    }

    #[test]
    fn entre_varios_temas_gana_el_que_suena() {
        let tema = ruido(300, 1);
        let otro = ruido(300, 7);
        let episodio = incrustar(&ruido(1200, 3), &tema, 400, 0);

        let (indice, c) = mejor_coincidencia(&mut Ventana::new(0.0, &episodio), &[&otro, &tema], None).unwrap();

        assert_eq!(indice, 1);
        assert!(cerca(c.inicio, 40.0, 1e-9));
    }

    #[test]
    fn tema_mas_largo_que_la_ventana_se_ubica_por_trozos() {
        // Foco de revisión 1: tramo de 60 s del episodio y tema de 90 s. No cabe entero, pero 50 s suyos sí suenan.
        let tema = ruido(900, 2);
        let episodio = incrustar(&ruido(1500, 3), &tema, 500, 300);
        let tramo = &episodio[400 * COLUMNAS..1000 * COLUMNAS];

        let (_, c) = mejor_coincidencia(&mut Ventana::new(0.0, tramo), &[&tema], None).unwrap();

        assert!(c.parcial);
        assert!(cerca(c.inicio, 10.0, 1e-9) && cerca(c.fin, 60.0, 1e-9), "{} - {}", c.inicio, c.fin);
        assert!(cerca(c.confianza, 0.9949553923172204, 1e-6), "confianza {}", c.confianza);
    }

    #[test]
    fn sin_temas_no_hay_coincidencia() {
        assert!(mejor_coincidencia(&mut Ventana::new(0.0, &ruido(100, 1)), &[], None).is_none());
    }
```

- [ ] **Paso 2: comprobar que fallan**

Ejecutar: `cargo test --manifest-path native/animetracker_core/Cargo.toml`
Esperado: no compila, `cannot find function 'mejor_coincidencia'`.

- [ ] **Paso 3: implementar**

En `audio.rs`, después de las constantes de la tarea 1:

```rust
/// Coincidencia "entera" con esta confianza o más: no hace falta mirar por trozos.
const CONFIANZA_SEGURA: f64 = 0.8;
/// Por trozos (el episodio usa solo una parte del tema, o hay diálogo encima de un tramo): trozos de 10 s cada 5 s; un trozo
/// cuenta si pasa de 0,65 y el tramo encontrado debe durar al menos 30 s (o la mitad del tema) para no penalizarlo.
const TROZO: usize = 10 * FPS;
const PASO_TROZO: usize = 5 * FPS;
const UMBRAL_TROZO: f64 = 0.65;
const TRAMO_MINIMO: usize = 30 * FPS;
/// Solo se analizan por trozos los candidatos con cierta similitud entera (los demás no son la canción).
const MINIMO_PARA_TROZOS: f64 = 0.45;
const MAXIMO_CANDIDATOS_TROZOS: usize = 4;

/// Dónde suena un tema dentro del episodio (segundos del episodio).
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Coincidencia {
    pub inicio: f64,
    pub fin: f64,
    pub confianza: f64,
    /// El episodio usa solo una parte del tema.
    pub parcial: bool,
}
```

Y después de `impl Ventana`:

```rust
/// Primera posición del valor más alto (como numpy.argmax).
fn posicion_del_maximo(valores: &[f64]) -> usize {
    let mut mejor = 0;
    for (i, &v) in valores.iter().enumerate() {
        if v > valores[mejor] {
            mejor = i;
        }
    }
    mejor
}

fn coincidencia_completa(ventana: &mut Ventana, tema: &[f32]) -> Option<Coincidencia> {
    let largo = tema.len() / COLUMNAS;
    if largo > ventana.n {
        return None;
    }
    let curva = ventana.curva(tema);
    if curva.is_empty() {
        return None;
    }
    let i = posicion_del_maximo(&curva);
    Some(Coincidencia {
        inicio: ventana.inicio + i as f64 / FPS as f64,
        fin: ventana.inicio + (i + largo) as f64 / FPS as f64,
        confianza: curva[i],
        parcial: false,
    })
}

/// Busca un desfase en el que varios trozos seguidos del tema coinciden a la vez. Sirve cuando el episodio usa solo una parte
/// (ending recortado, opening que arranca a mitad) o hay diálogo encima de un tramo. Un trozo suelto de 10 s puede parecerse
/// por azar (~0,4-0,6 en cualquier sitio); varios seguidos con el mismo desfase, no.
fn coincidencia_por_trozos(ventana: &mut Ventana, tema: &[f32]) -> Option<Coincidencia> {
    let largo = tema.len() / COLUMNAS;
    if largo < TROZO || ventana.n < TROZO {
        return None;
    }

    let inicios: Vec<usize> = (0..=largo - TROZO).step_by(PASO_TROZO).collect();
    // El desfase del tema respecto a la ventana va de -(largo - TROZO) a ventana.n - TROZO; se guarda desplazado para indexar.
    let desplazamiento = largo - TROZO;
    let tamano = ventana.n - TROZO + desplazamiento + 1;
    let mut curvas = vec![-1.0f64; inicios.len() * tamano];
    for (k, &c) in inicios.iter().enumerate() {
        let curva = ventana.curva(&tema[c * COLUMNAS..(c + TROZO) * COLUMNAS]);
        let o0 = k * tamano + desplazamiento - c;
        curvas[o0..o0 + curva.len()].copy_from_slice(&curva);
    }

    let mut puntos = vec![0.0f64; tamano];
    for fila in curvas.chunks_exact(tamano) {
        for (p, &v) in puntos.iter_mut().zip(fila) {
            if v >= UMBRAL_TROZO {
                *p += v;
            }
        }
    }
    let o = posicion_del_maximo(&puntos);
    if puntos[o] <= 0.0 {
        return None;
    }
    let valores: Vec<f64> = (0..inicios.len()).map(|k| curvas[k * tamano + o]).collect();

    // Tramos de trozos buenos seguidos (se tolera un trozo malo en medio: un grito o un efecto tapando la música).
    let mut tramos: Vec<Vec<usize>> = Vec::new();
    let mut ultimo: Option<usize> = None;
    for (k, &v) in valores.iter().enumerate() {
        if v < UMBRAL_TROZO {
            continue;
        }
        match (ultimo, tramos.last_mut()) {
            (Some(u), Some(actual)) if k - u <= 2 => actual.push(k),
            _ => tramos.push(vec![k]),
        }
        ultimo = Some(k);
    }
    let alcance = |t: &Vec<usize>| inicios[t[t.len() - 1]] - inicios[t[0]];
    let mut tramo = &tramos[0];
    for t in &tramos[1..] {
        if alcance(t) > alcance(tramo) {
            tramo = t;
        }
    }

    let a = inicios[tramo[0]];
    let b = inicios[tramo[tramo.len() - 1]] + TROZO;
    let mut confianza = tramo.iter().map(|&k| valores[k]).sum::<f64>() / tramo.len() as f64;
    let necesario = (TRAMO_MINIMO as f64).min(0.5 * largo as f64);
    if ((b - a) as f64) < necesario {
        confianza *= (b - a) as f64 / necesario;
    }
    let desfase = o as isize - desplazamiento as isize;
    let desde = (desfase + a as isize).max(0) as f64;
    let hasta = (desfase + b as isize).min(ventana.n as isize) as f64;
    Some(Coincidencia {
        inicio: ventana.inicio + desde / FPS as f64,
        fin: ventana.inicio + hasta / FPS as f64,
        confianza,
        parcial: true,
    })
}

/// Más de la mitad del tramo cae dentro del excluido (el ending no puede ser el mismo opening ya encontrado).
fn solapa(c: &Coincidencia, excluir: Option<(f64, f64)>) -> bool {
    match excluir {
        None => false,
        Some((inicio, fin)) => {
            let comun = c.fin.min(fin) - c.inicio.max(inicio);
            comun > 0.5 * (c.fin - c.inicio).min(fin - inicio)
        }
    }
}

/// El tema que mejor suena en la ventana y dónde: (índice del tema, coincidencia). Devuelve el mejor aunque su confianza sea
/// baja; quien llama lo compara con su mínimo. None si ningún tema se puede comparar o todos caen en el tramo excluido.
pub fn mejor_coincidencia(ventana: &mut Ventana, temas: &[&[f32]], excluir: Option<(f64, f64)>) -> Option<(usize, Coincidencia)> {
    let mut completas: Vec<(f64, usize, Option<Coincidencia>)> = temas
        .iter()
        .enumerate()
        .map(|(i, tema)| {
            let c = coincidencia_completa(ventana, tema);
            (c.map_or(-1.0, |c| c.confianza), i, c)
        })
        .collect();

    let mut mejor: Option<(usize, Coincidencia)> = None;
    for (_, i, c) in &completas {
        if let Some(c) = c {
            if !solapa(c, excluir) && mejor.is_none_or(|(_, m)| c.confianza > m.confianza) {
                mejor = Some((*i, *c));
            }
        }
    }
    if let Some((_, m)) = mejor {
        if m.confianza >= CONFIANZA_SEGURA {
            return mejor;
        }
    }

    // Nadie coincide entero: probar por trozos solo los que se parecen algo (o los temas más largos que la ventana).
    completas.sort_by(|a, b| b.0.partial_cmp(&a.0).unwrap_or(std::cmp::Ordering::Equal));
    for (confianza, i, c) in completas.iter().take(MAXIMO_CANDIDATOS_TROZOS) {
        if c.is_some() && *confianza < MINIMO_PARA_TROZOS {
            continue;
        }
        if let Some(p) = coincidencia_por_trozos(ventana, temas[*i]) {
            if !solapa(&p, excluir) && mejor.is_none_or(|(_, m)| p.confianza > m.confianza) {
                mejor = Some((*i, p));
            }
        }
    }
    mejor
}
```

- [ ] **Paso 4: comprobar que pasan**

Ejecutar: `cargo test --manifest-path native/animetracker_core/Cargo.toml`
Esperado: `test result: ok. 13 passed`.

- [ ] **Paso 5: verificación**

Ejecutar: `cargo clippy --release --manifest-path native/animetracker_core/Cargo.toml -- -D warnings`
Esperado: `Finished` sin errores.

---

### Tarea 3: frontera Rust ↔ C#

**Archivos:**
- Modificar: `native/animetracker_core/src/audio.rs` (estructura `CoincidenciaAudio`)
- Modificar: `native/animetracker_core/src/lib.rs` (dos funciones exportadas, antes de `anitomy_free_string`)
- Modificar: `AnimeLocalTracker/Services/Native/NativeMethods.cs`
- Crear: `AnimeLocalTracker.Tests/Services/AudioSintetico.cs`
- Crear: `AnimeLocalTracker.Tests/Services/NativeAudioTests.cs`

**Interfaces:**
- Consume: `audio::huella`, `audio::Ventana::new`, `audio::mejor_coincidencia`, `audio::COLUMNAS`, `audio::MUESTRAS_POR_FOTOGRAMA` (tareas 1-2).
- Produce (C#): `NativeMethods.FotogramasPorSegundo` (10), `NativeMethods.MuestrasPorFotograma` (800), `NativeMethods.ColumnasHuella` (8); `struct CoincidenciaAudio { int Indice; int Parcial; double Inicio; double Fin; double Confianza; }` en el espacio `AnimeLocalTracker.Services.Native`; `NativeMethods.HuellaDeAudio(ReadOnlySpan<float> pcm) → float[]?` (null = fallo del motor); `NativeMethods.MejorCoincidencia(float[] ventana, double inicioVentana, IReadOnlyList<float[]> temas, (double Inicio, double Fin)? excluir, out CoincidenciaAudio? mejor) → bool` (false = fallo del motor; true con `mejor` null = ningún tema).
- Produce (pruebas C#): `AudioSintetico.Ruido(int fotogramas, uint semilla) → float[]`, `AudioSintetico.RuidoSegundos(double segundos, uint semilla) → float[]`, `AudioSintetico.Incrustar(float[] episodio, float[] tema, int fotograma, int desde = 0) → float[]`.

- [ ] **Paso 1: pruebas de la frontera en Rust**

En `audio.rs`, después de `Coincidencia`:

```rust
/// Lo que cruza la frontera hacia C# (mismos campos y orden que `CoincidenciaAudio` en NativeMethods.cs).
#[repr(C)]
#[derive(Debug, Clone, Copy)]
pub struct CoincidenciaAudio {
    pub indice: i32,
    pub parcial: i32,
    pub inicio: f64,
    pub fin: f64,
    pub confianza: f64,
}
```

Al final de `lib.rs`:

```rust
#[cfg(test)]
mod pruebas_audio {
    use super::*;

    #[test]
    fn audio_huella_rechaza_punteros_nulos_y_salidas_pequenas() {
        let pcm = vec![0.1f32; 1600];
        let mut salida = vec![0.0f32; 16];
        assert_eq!(audio_huella(std::ptr::null(), 1600, salida.as_mut_ptr(), 2), -1);
        assert_eq!(audio_huella(pcm.as_ptr(), 1600, std::ptr::null_mut(), 2), -1);
        assert_eq!(audio_huella(pcm.as_ptr(), 1600, salida.as_mut_ptr(), 1), -1, "caben 2 fotogramas, no 1");
        assert_eq!(audio_huella(pcm.as_ptr(), 1600, salida.as_mut_ptr(), 2), 2);
        assert_eq!(audio_huella(pcm.as_ptr(), 700, salida.as_mut_ptr(), 2), 0, "menos de un fotograma");
    }

    #[test]
    fn audio_mejor_coincidencia_devuelve_el_tema_y_su_tramo() {
        let tema = audio::pruebas::ruido(300, 1);
        let episodio = audio::pruebas::incrustar(&audio::pruebas::ruido(1200, 3), &tema, 400, 0);
        let largos = [300usize];
        let mut salida = audio::CoincidenciaAudio { indice: -1, parcial: -1, inicio: 0.0, fin: 0.0, confianza: 0.0 };

        let codigo = audio_mejor_coincidencia(episodio.as_ptr(), 1200, 0.0, tema.as_ptr(), largos.as_ptr(), 1, f64::NAN, f64::NAN, &mut salida);

        assert_eq!(codigo, 1);
        assert_eq!((salida.indice, salida.parcial), (0, 0));
        assert!((salida.inicio - 40.0).abs() < 1e-9 && (salida.fin - 70.0).abs() < 1e-9);

        // Con ese tramo excluido ya no hay coincidencia.
        let excluido = audio_mejor_coincidencia(episodio.as_ptr(), 1200, 0.0, tema.as_ptr(), largos.as_ptr(), 1, 40.0, 70.0, &mut salida);
        assert_eq!(excluido, 0);
    }

    #[test]
    fn audio_mejor_coincidencia_rechaza_entradas_incoherentes() {
        let datos = vec![0.0f32; 80];
        let largos = [10usize];
        let mut salida = audio::CoincidenciaAudio { indice: 0, parcial: 0, inicio: 0.0, fin: 0.0, confianza: 0.0 };
        let nulo = std::ptr::null();
        assert_eq!(audio_mejor_coincidencia(nulo, 10, 0.0, datos.as_ptr(), largos.as_ptr(), 1, f64::NAN, f64::NAN, &mut salida), -1);
        assert_eq!(audio_mejor_coincidencia(datos.as_ptr(), 10, 0.0, nulo, largos.as_ptr(), 1, f64::NAN, f64::NAN, &mut salida), -1);
        assert_eq!(audio_mejor_coincidencia(datos.as_ptr(), 10, 0.0, datos.as_ptr(), std::ptr::null(), 1, f64::NAN, f64::NAN, &mut salida), -1);
        assert_eq!(audio_mejor_coincidencia(datos.as_ptr(), 10, 0.0, datos.as_ptr(), largos.as_ptr(), 1, f64::NAN, f64::NAN, std::ptr::null_mut()), -1);
        assert_eq!(audio_mejor_coincidencia(datos.as_ptr(), 0, 0.0, datos.as_ptr(), largos.as_ptr(), 1, f64::NAN, f64::NAN, &mut salida), -1);
        assert_eq!(audio_mejor_coincidencia(datos.as_ptr(), 10, 0.0, datos.as_ptr(), largos.as_ptr(), 0, f64::NAN, f64::NAN, &mut salida), -1);
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

Ejecutar: `cargo test --manifest-path native/animetracker_core/Cargo.toml`
Esperado: no compila, `cannot find function 'audio_huella'`.

- [ ] **Paso 3: exportar las dos funciones**

En `lib.rs`, antes de `/// Libera la memoria de una cadena de texto…`:

```rust
/// Tope de entrada: 6 horas de audio. Por encima es un error de quien llama, no un episodio.
const MAXIMO_MUESTRAS_AUDIO: usize = 8000 * 3600 * 6;
const MAXIMO_FOTOGRAMAS_AUDIO: usize = 10 * 3600 * 6;
const MAXIMO_TEMAS_AUDIO: usize = 1000;

/// Huella de audio mono a 8 kHz en f32. Escribe fotogramas de 8 valores en `salida` (`capacidad` en fotogramas).
/// Devuelve los fotogramas escritos (muestras / 800), o -1 si un puntero es nulo o no cabe.
#[no_mangle]
pub extern "C" fn audio_huella(pcm: *const f32, muestras: usize, salida: *mut f32, capacidad: usize) -> i32 {
    ffi_catch(|| audio_huella_inner(pcm, muestras, salida, capacidad), -1)
}

fn audio_huella_inner(pcm: *const f32, muestras: usize, salida: *mut f32, capacidad: usize) -> i32 {
    if pcm.is_null() || salida.is_null() || muestras > MAXIMO_MUESTRAS_AUDIO {
        return -1;
    }
    let fotogramas = muestras / audio::MUESTRAS_POR_FOTOGRAMA;
    if fotogramas > capacidad {
        return -1;
    }
    if fotogramas == 0 {
        return 0;
    }

    let entrada = unsafe { std::slice::from_raw_parts(pcm, muestras) };
    let huella = audio::huella(entrada);
    unsafe { std::ptr::copy_nonoverlapping(huella.as_ptr(), salida, huella.len()) };
    fotogramas as i32
}

/// Mejor tema dentro de un tramo del episodio. `temas` son las huellas una detrás de otra y `largos` sus fotogramas.
/// `excluir_inicio`/`excluir_fin` en NaN = sin exclusión.
/// Devuelve 1 (hay coincidencia: rellena `salida`), 0 (ninguna) o -1 (entrada incoherente o fallo interno).
#[no_mangle]
#[allow(clippy::too_many_arguments)]
pub extern "C" fn audio_mejor_coincidencia(
    ventana: *const f32,
    fotogramas_ventana: usize,
    inicio_ventana: f64,
    temas: *const f32,
    largos: *const usize,
    n_temas: usize,
    excluir_inicio: f64,
    excluir_fin: f64,
    salida: *mut audio::CoincidenciaAudio,
) -> i32 {
    ffi_catch(
        || audio_mejor_coincidencia_inner(ventana, fotogramas_ventana, inicio_ventana, temas, largos, n_temas, excluir_inicio, excluir_fin, salida),
        -1,
    )
}

#[allow(clippy::too_many_arguments)]
fn audio_mejor_coincidencia_inner(
    ventana: *const f32,
    fotogramas_ventana: usize,
    inicio_ventana: f64,
    temas: *const f32,
    largos: *const usize,
    n_temas: usize,
    excluir_inicio: f64,
    excluir_fin: f64,
    salida: *mut audio::CoincidenciaAudio,
) -> i32 {
    if ventana.is_null() || temas.is_null() || largos.is_null() || salida.is_null() {
        return -1;
    }
    if fotogramas_ventana == 0 || fotogramas_ventana > MAXIMO_FOTOGRAMAS_AUDIO || n_temas == 0 || n_temas > MAXIMO_TEMAS_AUDIO {
        return -1;
    }

    let largos = unsafe { std::slice::from_raw_parts(largos, n_temas) };
    let mut total = 0usize;
    for &largo in largos {
        if largo > MAXIMO_FOTOGRAMAS_AUDIO {
            return -1;
        }
        total += largo;
    }
    let ventana = unsafe { std::slice::from_raw_parts(ventana, fotogramas_ventana * audio::COLUMNAS) };
    let todos = unsafe { std::slice::from_raw_parts(temas, total * audio::COLUMNAS) };

    let mut porciones: Vec<&[f32]> = Vec::with_capacity(n_temas);
    let mut desde = 0usize;
    for &largo in largos {
        porciones.push(&todos[desde * audio::COLUMNAS..(desde + largo) * audio::COLUMNAS]);
        desde += largo;
    }
    let excluir = if excluir_inicio.is_nan() || excluir_fin.is_nan() { None } else { Some((excluir_inicio, excluir_fin)) };

    match audio::mejor_coincidencia(&mut audio::Ventana::new(inicio_ventana, ventana), &porciones, excluir) {
        Some((indice, c)) => {
            let resultado = audio::CoincidenciaAudio { indice: indice as i32, parcial: c.parcial as i32, inicio: c.inicio, fin: c.fin, confianza: c.confianza };
            unsafe { *salida = resultado };
            1
        }
        None => 0,
    }
}
```

- [ ] **Paso 4: comprobar Rust y compilar la DLL**

Ejecutar, en orden:

```
cargo test --manifest-path native/animetracker_core/Cargo.toml
cargo clippy --release --manifest-path native/animetracker_core/Cargo.toml -- -D warnings
cargo build --release --manifest-path native/animetracker_core/Cargo.toml
cp native/animetracker_core/target/release/animetracker_core.dll AnimeLocalTracker/animetracker_core.dll
```

Esperado: `test result: ok. 16 passed`; clippy `Finished`; la DLL copiada (está en `.gitignore`: CI compila la suya).

Si `cargo audit --version` responde, ejecutar además `cargo audit` dentro de `native/animetracker_core` y comprobar que no hay avisos. Si no está instalado, anotarlo para el informe final (CI lo ejecuta y bloquea).

- [ ] **Paso 5: ruido reproducible para las pruebas de C#**

Crear `AnimeLocalTracker.Tests/Services/AudioSintetico.cs`:

```csharp
using System;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Huellas sintéticas para probar el análisis de audio sin ffmpeg. El generador es el mismo que usan las pruebas del núcleo
/// Rust (native/animetracker_core/src/audio.rs): con él, un tema "incrustado" en un episodio se encuentra donde se puso.
/// </summary>
internal static class AudioSintetico
{
    internal const int Fps = 10;
    internal const int Columnas = 8;

    /// <summary>Fotogramas × 8 valores en [-0,5, 0,5), por filas.</summary>
    internal static float[] Ruido(int fotogramas, uint semilla)
    {
        var valores = new float[fotogramas * Columnas];
        uint estado = semilla;
        for (int i = 0; i < valores.Length; i++)
        {
            estado = unchecked(estado * 1664525u + 1013904223u);
            valores[i] = (float)(estado / 4294967296.0 - 0.5);
        }
        return valores;
    }

    internal static float[] RuidoSegundos(double segundos, uint semilla) => Ruido((int)(segundos * Fps), semilla);

    /// <summary>Copia <paramref name="tema"/> (desde el fotograma <paramref name="desde"/>) en el episodio, con algo de ruido encima.</summary>
    internal static float[] Incrustar(float[] episodio, float[] tema, int fotograma, int desde = 0)
    {
        var resultado = (float[])episodio.Clone();
        var parte = tema.AsSpan(desde * Columnas);
        var extra = Ruido(parte.Length / Columnas, 99);
        for (int i = 0; i < parte.Length; i++)
        {
            resultado[fotograma * Columnas + i] = (float)(parte[i] + 0.1 * extra[i]);
        }
        return resultado;
    }

    internal static float[] IncrustarEnSegundo(float[] episodio, float[] tema, double segundo, int desde = 0) =>
        Incrustar(episodio, tema, (int)(segundo * Fps), desde);
}
```

- [ ] **Paso 6: pruebas de la frontera en C# que fallan**

Crear `AnimeLocalTracker.Tests/Services/NativeAudioTests.cs`:

```csharp
using System;
using AnimeLocalTracker.Services.Native;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Análisis de audio del núcleo Rust a través de la frontera real (animetracker_core.dll).</summary>
public class NativeAudioTests
{
    private static readonly float[] PrimerFotogramaEsperado =
        [0.26457512f, -7.6656466f, -6.316012f, 3.7318506f, -6.200735f, 3.379668f, -7.546138f, 2.7776082f];

    [Fact]
    public void ElNucleoNativoEstaDisponibleEnLasPruebas()
    {
        // Si falla: copiar native/animetracker_core/target/release/animetracker_core.dll a AnimeLocalTracker/ y recompilar.
        NativeMethods.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void HuellaDeAudio_DeUnaSenalFija_CoincideConLaDeReferencia()
    {
        var pcm = new float[2400];
        for (int i = 0; i < pcm.Length; i++)
        {
            double t = i / 8000.0;
            double nivel = i / 800 % 2 == 0 ? 1.0 : 0.25;
            pcm[i] = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * t) + 0.2 * Math.Sin(2 * Math.PI * 1250 * t + 0.5)
                             + 0.1 * Math.Sin(2 * Math.PI * 3000 * t) * nivel);
        }

        float[]? huella = NativeMethods.HuellaDeAudio(pcm);

        huella.Should().NotBeNull();
        huella!.Length.Should().Be(3 * NativeMethods.ColumnasHuella);
        for (int j = 0; j < NativeMethods.ColumnasHuella; j++)
        {
            huella[j].Should().BeApproximately(PrimerFotogramaEsperado[j], 1e-4f, $"columna {j}");
        }
    }

    [Fact]
    public void HuellaDeAudio_ConMenosDeUnFotograma_EsVacia()
    {
        NativeMethods.HuellaDeAudio(new float[799]).Should().BeEmpty();
    }

    [Fact]
    public void MejorCoincidencia_UbicaElTemaIncrustado()
    {
        float[] tema = AudioSintetico.Ruido(300, 1);
        float[] episodio = AudioSintetico.Incrustar(AudioSintetico.Ruido(1200, 3), tema, 400);

        bool ok = NativeMethods.MejorCoincidencia(episodio, 0.0, [AudioSintetico.Ruido(300, 7), tema], null, out var mejor);

        ok.Should().BeTrue();
        mejor.Should().NotBeNull();
        mejor!.Value.Indice.Should().Be(1);
        mejor.Value.Parcial.Should().Be(0);
        mejor.Value.Inicio.Should().BeApproximately(40.0, 1e-9);
        mejor.Value.Fin.Should().BeApproximately(70.0, 1e-9);
        mejor.Value.Confianza.Should().BeApproximately(0.9950893339441097, 1e-6);
    }

    [Fact]
    public void MejorCoincidencia_ConElTramoExcluido_NoDevuelveNada()
    {
        float[] tema = AudioSintetico.Ruido(300, 1);
        float[] episodio = AudioSintetico.Incrustar(AudioSintetico.Ruido(1200, 3), tema, 400);

        bool ok = NativeMethods.MejorCoincidencia(episodio, 0.0, [tema], (40.0, 70.0), out var mejor);

        ok.Should().BeTrue("no es un fallo del motor: simplemente no hay otro sitio donde suene");
        mejor.Should().BeNull();
    }

    [Fact]
    public void MejorCoincidencia_SinTemasOSinVentana_NoLlamaAlMotor()
    {
        NativeMethods.MejorCoincidencia(AudioSintetico.Ruido(100, 1), 0.0, [], null, out var sinTemas).Should().BeTrue();
        sinTemas.Should().BeNull();
        NativeMethods.MejorCoincidencia([], 0.0, [AudioSintetico.Ruido(100, 1)], null, out var sinVentana).Should().BeTrue();
        sinVentana.Should().BeNull();
    }
}
```

- [ ] **Paso 7: comprobar que fallan**

Ejecutar: `dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore`
Esperado: errores CS0117 (`NativeMethods` no contiene `HuellaDeAudio`, `ColumnasHuella`, `MejorCoincidencia`).

- [ ] **Paso 8: implementar la frontera en C#**

En `NativeMethods.cs`, tras la declaración de `NativeAnitomyVersion`:

```csharp
    public const int FotogramasPorSegundo = 10;
    /// <summary>Muestras de audio (mono, 8 kHz) por fotograma de huella.</summary>
    public const int MuestrasPorFotograma = 800;
    /// <summary>Valores por fotograma de huella: volumen + 7 bandas.</summary>
    public const int ColumnasHuella = 8;

    [LibraryImport(DllName, EntryPoint = "audio_huella")]
    private static unsafe partial int NativeAudioHuella(float* pcm, nuint muestras, float* salida, nuint capacidad);

    [LibraryImport(DllName, EntryPoint = "audio_mejor_coincidencia")]
    private static unsafe partial int NativeAudioMejorCoincidencia(float* ventana, nuint fotogramasVentana, double inicioVentana,
        float* temas, nuint* largos, nuint nTemas, double excluirInicio, double excluirFin, CoincidenciaAudio* salida);
```

Antes de `private static IntPtr StringToUtf8Ptr`:

```csharp
    /// <summary>
    /// Huella de audio mono a 8 kHz (fotogramas × <see cref="ColumnasHuella"/>, por filas). Vacía si hay menos de un fotograma.
    /// Null si el núcleo nativo no está o falla: quien llama lo trata como "el motor no respondió".
    /// </summary>
    public static unsafe float[]? HuellaDeAudio(ReadOnlySpan<float> pcm)
    {
        if (!IsAvailable) return null;

        int fotogramas = pcm.Length / MuestrasPorFotograma;
        var salida = new float[fotogramas * ColumnasHuella];
        if (fotogramas == 0) return salida;

        try
        {
            fixed (float* entrada = pcm)
            fixed (float* destino = salida)
            {
                return NativeAudioHuella(entrada, (nuint)pcm.Length, destino, (nuint)fotogramas) == fotogramas ? salida : null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en audio_huella nativo: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// El tema que mejor suena en un tramo del episodio. <paramref name="mejor"/> es null si ninguno se puede ubicar (o todos caen
    /// en el tramo excluido); se devuelve aunque su confianza sea baja. False si el núcleo nativo no está o falla.
    /// </summary>
    public static unsafe bool MejorCoincidencia(float[] ventana, double inicioVentana, IReadOnlyList<float[]> temas,
        (double Inicio, double Fin)? excluir, out CoincidenciaAudio? mejor)
    {
        mejor = null;
        if (!IsAvailable) return false;
        if (temas.Count == 0 || ventana.Length < ColumnasHuella) return true;

        try
        {
            var largos = new nuint[temas.Count];
            int total = 0;
            for (int i = 0; i < temas.Count; i++)
            {
                largos[i] = (nuint)(temas[i].Length / ColumnasHuella);
                total += (int)largos[i] * ColumnasHuella;
            }
            if (total == 0) return true;

            var juntos = new float[total];
            int posicion = 0;
            for (int i = 0; i < temas.Count; i++)
            {
                int valores = (int)largos[i] * ColumnasHuella;
                temas[i].AsSpan(0, valores).CopyTo(juntos.AsSpan(posicion));
                posicion += valores;
            }

            CoincidenciaAudio resultado = default;
            int codigo;
            fixed (float* v = ventana)
            fixed (float* t = juntos)
            fixed (nuint* l = largos)
            {
                codigo = NativeAudioMejorCoincidencia(v, (nuint)(ventana.Length / ColumnasHuella), inicioVentana, t, l, (nuint)temas.Count,
                    excluir?.Inicio ?? double.NaN, excluir?.Fin ?? double.NaN, &resultado);
            }
            if (codigo < 0) return false;
            if (codigo == 1) mejor = resultado;
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("NativeMethods", $"Error en audio_mejor_coincidencia nativo: {ex.Message}");
            return false;
        }
    }
```

Al final del archivo, junto a las otras clases de resultado:

```csharp
/// <summary>Dónde suena un tema dentro del episodio. Mismos campos y orden que <c>CoincidenciaAudio</c> en audio.rs.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct CoincidenciaAudio
{
    /// <summary>Posición del tema ganador en la lista enviada.</summary>
    public int Indice;
    /// <summary>1 si el episodio usa solo una parte del tema.</summary>
    public int Parcial;
    public double Inicio;
    public double Fin;
    public double Confianza;
}
```

- [ ] **Paso 9: compilar y comprobar que pasan**

Ejecutar:

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~NativeAudioTests|FullyQualifiedName~RustNativeTests"
```

Esperado: compilación con 0 advertencias y 0 errores; todas las pruebas filtradas en verde (las 6 nuevas más las de `RustNativeTests`).

---

### Tarea 4: `ProcesoExterno` con salida en bytes

**Archivos:**
- Modificar: `AnimeLocalTracker/Core/ProcesoExterno.cs`
- Modificar: `AnimeLocalTracker.Tests/Services/ProcesoExternoTests.cs`

**Interfaces:**
- Consume: nada.
- Produce: `ProcesoExterno.ResultadoBinario(int Codigo, byte[] Salida, string Error)`; `ProcesoExterno.EjecutarBinarioAsync(string ejecutable, IEnumerable<string> argumentos, TimeSpan? limite, CancellationToken ct) → Task<ResultadoBinario?>` (null si el proceso no arranca; `OperationCanceledException` si se cancela o vence el límite).

- [ ] **Paso 1: escribir las pruebas que fallan**

Añadir a `ProcesoExternoTests` (dentro de la clase; añadir `using AnimeLocalTracker.Services;` si no está):

```csharp
    [Fact]
    public async Task EjecutarBinario_DevuelveLosBytesDeLaSalidaSinTocarlos()
    {
        // 1 s de tono a 8 kHz en mono y f32: 8000 muestras × 4 bytes. Como texto se habría estropeado.
        string[] argumentos = ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
            "-ac", "1", "-ar", "8000", "-f", "f32le", "-"];

        var resultado = await ProcesoExterno.EjecutarBinarioAsync(FfmpegLocator.Ffmpeg, argumentos, TimeSpan.FromSeconds(30), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.Codigo.Should().Be(0, resultado.Error);
        resultado.Salida.Length.Should().Be(32000);
    }

    [Fact]
    public async Task EjecutarBinario_SiVenceElLimite_MataElProcesoYLanzaCancelacion()
    {
        string[] esperaLarga = ["/c", "ping", "-n", "30", "127.0.0.1"];

        Func<Task> accion = () => ProcesoExterno.EjecutarBinarioAsync("cmd.exe", esperaLarga, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        await accion.Should().ThrowAsync<OperationCanceledException>();
    }
```

- [ ] **Paso 2: comprobar que fallan**

Ejecutar: `dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore`
Esperado: error CS0117, `ProcesoExterno` no contiene `EjecutarBinarioAsync`.

- [ ] **Paso 3: implementar**

Dejar `AnimeLocalTracker/Core/ProcesoExterno.cs` así (el arranque y la cancelación pasan a ser comunes):

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Ejecuta un programa auxiliar (ffmpeg, ffprobe…) sin ventana y recoge lo que escribe. Reúne lo que antes repetía cada
/// servicio: leer las dos salidas a la vez (un pipe redirigido sin lector puede colgar el proceso, visto con ffmpeg), un
/// tiempo límite y matar el proceso si se cancela, porque no se detiene solo al soltar el <see cref="Process"/>.
/// </summary>
public static class ProcesoExterno
{
    public sealed record Resultado(int Codigo, string Salida, string Error);

    /// <summary>Como <see cref="Resultado"/>, con la salida en bytes (audio o imagen que el programa escribe por su salida).</summary>
    public sealed record ResultadoBinario(int Codigo, byte[] Salida, string Error);

    /// <summary>
    /// Null si el proceso no llegó a arrancar. Si se cancela o vence <paramref name="limite"/>, mata el proceso (y sus hijos)
    /// y lanza <see cref="OperationCanceledException"/>. Los argumentos van uno a uno: no hace falta entrecomillar rutas.
    /// <paramref name="prioridad"/>: para trabajo de fondo (miniaturas) que no debe quitarle procesador al reproductor.
    /// </summary>
    public static async Task<Resultado?> EjecutarAsync(string ejecutable, IEnumerable<string> argumentos, TimeSpan? limite, CancellationToken ct,
        ProcessPriorityClass? prioridad = null)
    {
        using var proceso = Iniciar(ejecutable, argumentos, prioridad);
        if (proceso == null) return null;

        return await HastaQueTermineAsync(proceso, limite, ct, async corte =>
        {
            var salida = proceso.StandardOutput.ReadToEndAsync(corte);
            var error = proceso.StandardError.ReadToEndAsync(corte);
            await proceso.WaitForExitAsync(corte);
            return new Resultado(proceso.ExitCode, await salida, await error);
        });
    }

    /// <summary>
    /// Igual que <see cref="EjecutarAsync"/>, pero devuelve la salida tal cual, en bytes: para el audio decodificado que
    /// ffmpeg escribe por su salida (leído como texto se estropea).
    /// </summary>
    public static async Task<ResultadoBinario?> EjecutarBinarioAsync(string ejecutable, IEnumerable<string> argumentos, TimeSpan? limite, CancellationToken ct)
    {
        using var proceso = Iniciar(ejecutable, argumentos, null);
        if (proceso == null) return null;

        return await HastaQueTermineAsync(proceso, limite, ct, async corte =>
        {
            using var memoria = new MemoryStream();
            var salida = proceso.StandardOutput.BaseStream.CopyToAsync(memoria, corte);
            var error = proceso.StandardError.ReadToEndAsync(corte);
            await proceso.WaitForExitAsync(corte);
            await salida;
            return new ResultadoBinario(proceso.ExitCode, memoria.ToArray(), await error);
        });
    }

    private static Process? Iniciar(string ejecutable, IEnumerable<string> argumentos, ProcessPriorityClass? prioridad)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ejecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argumento in argumentos) psi.ArgumentList.Add(argumento);

        var proceso = Process.Start(psi);
        if (proceso != null && prioridad is { } clase)
        {
            try { proceso.PriorityClass = clase; } catch { /* ya terminó: nada que bajar */ }
        }
        return proceso;
    }

    private static async Task<T> HastaQueTermineAsync<T>(Process proceso, TimeSpan? limite, CancellationToken ct, Func<CancellationToken, Task<T>> leer)
    {
        using var corte = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (limite is { } maximo) corte.CancelAfter(maximo);

        try
        {
            return await leer(corte.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proceso.HasExited) proceso.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw;
        }
    }
}
```

- [ ] **Paso 4: compilar y comprobar que pasan**

Ejecutar:

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~ProcesoExternoTests|FullyQualifiedName~PythonEpisodeEnricherTests|FullyQualifiedName~FotogramasClaveServiceTests"
```

Esperado: 0 advertencias; todas en verde (las dos nuevas y las que ya usaban `EjecutarAsync`: miniaturas y datos técnicos no deben cambiar).

---

### Tarea 5: caché de huellas en disco

**Archivos:**
- Crear: `AnimeLocalTracker/Services/CacheHuellasAudio.cs`
- Crear: `AnimeLocalTracker.Tests/Services/CacheHuellasAudioTests.cs`

**Interfaces:**
- Consume: `NativeMethods.ColumnasHuella` (tarea 3).
- Produce: `internal sealed class CacheHuellasAudio(string carpeta)` con `static string Clave(string rutaTema)` (lanza `IOException` si no se puede leer), `float[]? Leer(string clave)`, `void Guardar(string clave, float[] huella)`; constantes internas `MaximoArchivos` (800) y `ArchivosTrasPoda` (600).

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/Services/CacheHuellasAudioTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public sealed class CacheHuellasAudioTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests_" + Guid.NewGuid());
    private readonly string _huellas;

    public CacheHuellasAudioTests()
    {
        Directory.CreateDirectory(_carpeta);
        _huellas = Path.Combine(_carpeta, "huellas");
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* best-effort */ }
    }

    private string Tema(string nombre, int bytes, int semilla)
    {
        string ruta = Path.Combine(_carpeta, nombre);
        var contenido = new byte[bytes];
        new Random(semilla).NextBytes(contenido);
        File.WriteAllBytes(ruta, contenido);
        return ruta;
    }

    [Fact]
    public void LaClave_DependeDelContenido_NoDelNombre()
    {
        string original = Tema("OP_OP1_v1_ep1-.mp3", 200_000, 1);
        string renombrado = Path.Combine(_carpeta, "OP1 - Canción.mp3");
        File.Copy(original, renombrado);
        string otro = Tema("ED1.mp3", 200_000, 2);

        CacheHuellasAudio.Clave(renombrado).Should().Be(CacheHuellasAudio.Clave(original), "renombrar el mp3 no obliga a recalcular");
        CacheHuellasAudio.Clave(otro).Should().NotBe(CacheHuellasAudio.Clave(original));
    }

    [Fact]
    public void LaClave_DeUnArchivoPequeno_TambienSeCalcula()
    {
        string a = Tema("corto.ogg", 1000, 1);
        string b = Tema("corto2.ogg", 1000, 2);

        CacheHuellasAudio.Clave(a).Should().NotBe(CacheHuellasAudio.Clave(b));
    }

    [Fact]
    public void LoGuardadoSeLeeIgual()
    {
        var cache = new CacheHuellasAudio(_huellas);
        float[] huella = AudioSintetico.Ruido(120, 5);

        cache.Guardar("abc", huella);

        cache.Leer("abc").Should().Equal(huella);
        cache.Leer("otra").Should().BeNull();
        Directory.GetFiles(_huellas).Should().ContainSingle().Which.Should().EndWith("abc.huella", "no queda el archivo temporal");
    }

    [Fact]
    public void UnArchivoDeCacheDanado_SeIgnora()
    {
        // Foco de revisión 3: un archivo vacío o cortado a medias (la app se cerró escribiendo) no puede dar una huella.
        var cache = new CacheHuellasAudio(_huellas);
        Directory.CreateDirectory(_huellas);
        File.WriteAllBytes(Path.Combine(_huellas, "vacia.huella"), []);
        File.WriteAllBytes(Path.Combine(_huellas, "cortada.huella"), new byte[33]);

        cache.Leer("vacia").Should().BeNull();
        cache.Leer("cortada").Should().BeNull("33 bytes no son un número entero de fotogramas (32 bytes cada uno)");
    }

    [Fact]
    public void AlGuardar_SeBorranLasHuellasDelFormatoAnterior()
    {
        Directory.CreateDirectory(_huellas);
        File.WriteAllBytes(Path.Combine(_huellas, "vieja.npy"), new byte[64]);
        var cache = new CacheHuellasAudio(_huellas);

        cache.Guardar("nueva", AudioSintetico.Ruido(10, 1));

        Directory.GetFiles(_huellas).Select(Path.GetFileName).Should().Equal("nueva.huella");
    }

    [Fact]
    public void AlPasarElTope_SeBorranLasMenosUsadas()
    {
        var cache = new CacheHuellasAudio(_huellas);
        float[] huella = AudioSintetico.Ruido(1, 1);
        var ahora = DateTime.UtcNow;
        for (int i = 0; i < CacheHuellasAudio.MaximoArchivos; i++)
        {
            cache.Guardar($"h{i:D4}", huella);
            // Cuanto mayor el número, más antigua la última vez que se usó.
            File.SetLastWriteTimeUtc(Path.Combine(_huellas, $"h{i:D4}.huella"), ahora.AddMinutes(-i));
        }

        cache.Guardar("reciente", huella);

        var quedan = Directory.GetFiles(_huellas).Select(Path.GetFileNameWithoutExtension).ToList();
        quedan.Should().HaveCount(CacheHuellasAudio.ArchivosTrasPoda);
        quedan.Should().Contain("reciente").And.Contain("h0000");
        quedan.Should().NotContain($"h{CacheHuellasAudio.MaximoArchivos - 1:D4}", "era la que más tiempo llevaba sin usarse");
    }

    [Fact]
    public void Leer_MarcaLaHuellaComoRecienUsada()
    {
        var cache = new CacheHuellasAudio(_huellas);
        cache.Guardar("abc", AudioSintetico.Ruido(10, 1));
        string archivo = Path.Combine(_huellas, "abc.huella");
        File.SetLastWriteTimeUtc(archivo, DateTime.UtcNow.AddDays(-30));

        cache.Leer("abc");

        File.GetLastWriteTimeUtc(archivo).Should().BeAfter(DateTime.UtcNow.AddMinutes(-5));
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

Ejecutar: `dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore`
Esperado: error CS0246, no se encuentra `CacheHuellasAudio`.

- [ ] **Paso 3: implementar**

Crear `AnimeLocalTracker/Services/CacheHuellasAudio.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AnimeLocalTracker.Services.Native;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Huellas de audio de los temas (opening/ending) guardadas en disco: la segunda vez que hace falta un tema no se decodifica.
/// Un archivo por tema, f32 en crudo (fotogramas × 8). Si falla el disco solo se pierde velocidad: nada de aquí lanza.
/// </summary>
internal sealed class CacheHuellasAudio(string carpeta)
{
    /// <summary>Cambiarlo invalida todo lo guardado (forma parte de la clave).</summary>
    private const int FormatoHuella = 2;
    private const string Extension = ".huella";
    private const int BytesPorFotograma = NativeMethods.ColumnasHuella * sizeof(float);
    private const int BloqueDeClave = 65536;

    /// <summary>Tope de archivos (~30 KB cada uno): al pasarlo se borran los menos usados.</summary>
    internal const int MaximoArchivos = 800;
    internal const int ArchivosTrasPoda = 600;

    /// <summary>
    /// Por contenido (tamaño + primeros y últimos 64 KB), no por nombre: renombrar el mp3 no obliga a recalcular.
    /// Lanza <see cref="IOException"/> si el archivo no se puede leer.
    /// </summary>
    internal static string Clave(string rutaTema)
    {
        using var archivo = File.OpenRead(rutaTema);
        long tamano = archivo.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.ASCII.GetBytes($"{FormatoHuella}|{tamano}|"));

        var bloque = new byte[BloqueDeClave];
        sha.AppendData(bloque, 0, archivo.ReadAtLeast(bloque, BloqueDeClave, throwOnEndOfStream: false));
        if (tamano > 2 * BloqueDeClave)
        {
            archivo.Seek(-BloqueDeClave, SeekOrigin.End);
            sha.AppendData(bloque, 0, archivo.ReadAtLeast(bloque, BloqueDeClave, throwOnEndOfStream: false));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Null si no está guardada o el archivo está dañado (vacío o cortado a medias).</summary>
    public float[]? Leer(string clave)
    {
        try
        {
            string archivo = Ruta(clave);
            if (!File.Exists(archivo)) return null;

            byte[] bytes = File.ReadAllBytes(archivo);
            if (bytes.Length == 0 || bytes.Length % BytesPorFotograma != 0) return null;

            try { File.SetLastWriteTimeUtc(archivo, DateTime.UtcNow); } catch (IOException) { /* solo es el orden para la poda */ }
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Guardar(string clave, float[] huella)
    {
        try
        {
            Directory.CreateDirectory(carpeta);
            string destino = Ruta(clave);
            // Nombre propio por escritura: el análisis del episodio y el pre-análisis del siguiente pueden guardar el mismo tema a la vez.
            string temporal = $"{destino}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporal, MemoryMarshal.AsBytes(huella.AsSpan()).ToArray());
                File.Move(temporal, destino, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporal); } catch (IOException) { /* best-effort */ }
            }

            BorrarFormatoAnterior();
            Podar();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // sin caché solo se pierde velocidad
        }
    }

    private string Ruta(string clave) => Path.Combine(carpeta, clave + Extension);

    /// <summary>Las huellas que guardaba el plugin Python (.npy) ya no las lee nadie.</summary>
    private void BorrarFormatoAnterior()
    {
        foreach (string viejo in Directory.EnumerateFiles(carpeta, "*.npy"))
        {
            try { File.Delete(viejo); } catch (IOException) { /* se intentará en el siguiente guardado */ }
        }
    }

    private void Podar()
    {
        var archivos = new DirectoryInfo(carpeta).EnumerateFiles("*" + Extension).ToList();
        if (archivos.Count <= MaximoArchivos) return;

        foreach (var viejo in archivos.OrderBy(a => a.LastWriteTimeUtc).Take(archivos.Count - ArchivosTrasPoda))
        {
            try { viejo.Delete(); } catch (IOException) { /* best-effort */ }
        }
    }
}
```

- [ ] **Paso 4: compilar y comprobar que pasan**

Ejecutar:

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~CacheHuellasAudioTests"
```

Esperado: 0 advertencias; 7 pruebas en verde.

---

### Tarea 6: `DetectorTemasAudio` (flujo de detección)

**Archivos:**
- Crear: `AnimeLocalTracker/Services/DetectorTemasAudio.cs`
- Crear: `AnimeLocalTracker.Tests/Services/DetectorTemasAudioTests.cs`

**Interfaces:**
- Consume: `NativeMethods.HuellaDeAudio`, `NativeMethods.MejorCoincidencia`, `CoincidenciaAudio`, constantes de `NativeMethods` (tarea 3); `ProcesoExterno.EjecutarBinarioAsync` (tarea 4); `CacheHuellasAudio` (tarea 5); `SkipTimesCoordinator.DeteccionTemasResult` y `SkipTimesCoordinator.TemaDetectado` (existentes); `FfmpegLocator.Ffmpeg`/`Ffprobe`; `AudioSintetico` en pruebas.
- Produce: `public sealed record ReferenciaAudio(string Ruta, string Tipo, int Prioridad)`; `public interface IDetectorTemasAudio { bool Disponible { get; } Task<SkipTimesCoordinator.DeteccionTemasResult?> DetectarAsync(string rutaEpisodio, IReadOnlyList<ReferenciaAudio> referencias, double confianzaMinima, double segundosInicio, double segundosFinal, CancellationToken ct); }`; `public sealed class DetectorTemasAudio : IDetectorTemasAudio` con constructor público `DetectorTemasAudio(string? carpetaHuellas = null)` y constructor interno para pruebas `DetectorTemasAudio(string carpetaHuellas, Func<string, CancellationToken, Task<double>> duracion, Func<string, double, double?, CancellationToken, Task<float[]?>> huellaDeTramo, Func<string, CancellationToken, Task<float[]?>>? huellaDeTema)`; `internal static float[] RecortarSilencio(float[] huella)`.

Semántica del resultado: `null` = el motor no pudo trabajar (sin DLL o fallo nativo); `Success = false` con `Error` = motivo concreto; `Success = true` con `Matches` (0, 1 o 2 tramos, `Segment` "op"/"ed", `Mode` "full"/"partial").

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/Services/DetectorTemasAudioTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Flujo de detección de temas sobre huellas sintéticas (sin ffmpeg): el tema se "incrusta" en la huella del episodio y se
/// comprueba dónde lo encuentra, qué grupo de temas se llega a decodificar y cómo se guarda la huella. La comparación la hace
/// el núcleo Rust de verdad. Port de las pruebas de tools/python/tests/test_audio_skip_plugin.py.
/// </summary>
public sealed class DetectorTemasAudioTests : IDisposable
{
    private const int Fps = AudioSintetico.Fps;
    private const int C = AudioSintetico.Columnas;
    private const double Confianza = 0.7, Inicio = 480, Final = 360;

    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests_" + Guid.NewGuid());
    private readonly string _huellas;
    private readonly string _episodio;
    private readonly Dictionary<string, float[]> _temas = new();
    private readonly List<string> _decodificados = new();
    private float[] _sonido = [];
    private double _duracion = 1400;

    public DetectorTemasAudioTests()
    {
        Directory.CreateDirectory(_carpeta);
        _huellas = Path.Combine(_carpeta, "huellas");
        _episodio = Path.Combine(_carpeta, "Episodio 01.mkv");
        File.WriteAllBytes(_episodio, [1]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* best-effort */ }
    }

    private string Tema(string nombre, float[] huella)
    {
        string ruta = Path.Combine(_carpeta, nombre);
        // Contenido distinto por tema: la caché de huellas se indexa por contenido y dos temas iguales compartirían huella.
        File.WriteAllBytes(ruta, System.Text.Encoding.UTF8.GetBytes(nombre));
        _temas[ruta] = huella;
        return ruta;
    }

    /// <summary>El episodio "suena" como <see cref="_sonido"/>: cada petición devuelve el trozo de huella que le toca.</summary>
    private float[] Tramo(double desde, double? duracion)
    {
        int total = _sonido.Length / C;
        int a = Math.Min(total, (int)(desde * Fps));
        int b = duracion is { } d ? Math.Min(total, (int)((desde + d) * Fps)) : total;
        return _sonido.AsSpan(a * C, (b - a) * C).ToArray();
    }

    private DetectorTemasAudio CrearSut() => new(_huellas,
        (_, _) => Task.FromResult(_duracion),
        (_, desde, duracion, _) => Task.FromResult<float[]?>(Tramo(desde, duracion)),
        (ruta, _) =>
        {
            lock (_decodificados) _decodificados.Add(Path.GetFileName(ruta));
            return Task.FromResult<float[]?>(_temas[ruta]);
        });

    private Task<SkipTimesCoordinator.DeteccionTemasResult?> Detectar(params ReferenciaAudio[] referencias) =>
        CrearSut().DetectarAsync(_episodio, referencias, Confianza, Inicio, Final, CancellationToken.None);

    private static float[] R(double segundos, uint semilla) => AudioSintetico.RuidoSegundos(segundos, semilla);

    [Fact]
    public async Task UbicaOpeningYEndingConSusTemas()
    {
        float[] op = R(90, 1), ed = R(90, 2);
        _sonido = AudioSintetico.IncrustarEnSegundo(AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 120), ed, 1300);

        var r = await Detectar(new(Tema("OP1.mp3", op), "OP", 0), new(Tema("ED1.mp3", ed), "ED", 0));

        r!.Success.Should().BeTrue();
        var opening = r.Matches.Single(m => m.Segment == "op");
        var ending = r.Matches.Single(m => m.Segment == "ed");
        opening.Start.Should().BeApproximately(120, 0.2);
        opening.End.Should().BeApproximately(210, 0.2);
        opening.Confidence.Should().BeGreaterThan(0.9);
        opening.Mode.Should().Be("full");
        ending.Start.Should().BeApproximately(1300, 0.2);
        Path.GetFileName(ending.ReferencePath).Should().Be("ED1.mp3");
        r.Evaluated.Should().Be(2);
    }

    [Fact]
    public async Task UnOpeningTrasUnaAperturaLarga_SeBuscaHastaLaMitadDelEpisodio()
    {
        // Sasaki to Pii-chan 2, episodio 1 (47 min): el opening suena a los 8:21, fuera de los primeros 480 s.
        _duracion = 2840;
        float[] op = R(88.6, 1), ed = R(88.5, 2);
        _sonido = AudioSintetico.IncrustarEnSegundo(AudioSintetico.IncrustarEnSegundo(R(2840, 3), op, 501.5), ed, 2750.5);

        var r = await Detectar(new(Tema("OP1.mp3", op), "OP", 0), new(Tema("ED1.mp3", ed), "ED", 0));

        var opening = r!.Matches.Single(m => m.Segment == "op");
        opening.Start.Should().BeApproximately(501.5, 0.2);
        opening.End.Should().BeApproximately(590.1, 0.2);
        r.Matches.Single(m => m.Segment == "ed").Start.Should().BeApproximately(2750.5, 0.2);
    }

    [Fact]
    public async Task UnOpeningACaballoDelLimiteDeLosPrimerosMinutos_SeEncuentra()
    {
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 440); // 440-530 s: no cabe entero en los primeros 480

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", op), "OP", 0));

        var m = r!.Matches.Should().ContainSingle().Subject;
        (m.Segment, m.Mode).Should().Be(("op", "full"));
        m.Start.Should().BeApproximately(440, 0.2);
    }

    [Fact]
    public async Task ElOpeningSonandoEnLaSegundaMitad_NoSeMarcaComoOpening()
    {
        // Canción de fondo del clímax: marcarla haría que "saltar opening" se llevara la escena.
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 800);

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", op), "OP", 0));

        r!.Success.Should().BeTrue();
        r.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task UnTemaQueNoSuenaEnElEpisodio_NoSeAcepta()
    {
        _sonido = R(1400, 3);

        var r = await Detectar(new ReferenciaAudio(Tema("OP9.mp3", R(90, 7)), "OP", 0));

        r!.Success.Should().BeTrue();
        r.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task LosTemasQueNoAplican_NiSeDecodificanSiAciertaUnoQueAplica()
    {
        float[] op = R(90, 1), ed = R(90, 2);
        _sonido = AudioSintetico.IncrustarEnSegundo(AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 60), ed, 1300);

        await Detectar(new(Tema("OP2.mp3", op), "OP", 0), new(Tema("ED1.mp3", ed), "ED", 0),
            new(Tema("OP1.mp3", R(90, 5)), "OP", 1), new(Tema("OP3.mp3", R(90, 6)), "OP", 1));

        _decodificados.Should().BeEquivalentTo("OP2.mp3", "ED1.mp3");
    }

    [Fact]
    public async Task SiNingunoQueAplicaAcierta_PruebaLosDemas()
    {
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 60);

        var r = await Detectar(new(Tema("OP2.mp3", R(90, 5)), "OP", 0), new(Tema("OP1.mp3", op), "OP", 1));

        Path.GetFileName(r!.Matches[0].ReferencePath).Should().Be("OP1.mp3");
    }

    [Fact]
    public async Task UnOpeningQueSuenaAlFinal_EsElEndingDelEpisodio()
    {
        // Episodio 1 / final de temporada: no hay ending propio y el opening cierra el episodio.
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 1310);

        var r = await Detectar(new(Tema("OP1.mp3", op), "OP", 0), new(Tema("ED1.mp3", R(90, 2)), "ED", 0));

        r!.Matches.Select(m => m.Segment).Should().Equal("ed");
        r.Matches[0].Start.Should().BeApproximately(1310, 0.2);
    }

    [Fact]
    public async Task EnUnEpisodioCorto_ElEndingNoPuedeSerElMismoOpening()
    {
        // Episodio de 4 minutos: los tramos de inicio y final cubren el episodio entero.
        _duracion = 256;
        float[] op = R(60, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(256, 3), op, 24);

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", op), "OP", 0));

        r!.Matches.Select(m => m.Segment).Should().Equal("op");
    }

    [Fact]
    public async Task UnEndingRecortado_SeUbicaPorTrozos()
    {
        // El episodio solo usa los últimos 60 s del tema (los primeros 30 s suenan sobre otra escena distinta).
        float[] ed = R(90, 2);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), ed, 1330, desde: 30 * Fps);

        var r = await Detectar(new ReferenciaAudio(Tema("ED1.mp3", ed), "ED", 0));

        var m = r!.Matches.Should().ContainSingle().Subject;
        (m.Segment, m.Mode).Should().Be(("ed", "partial"));
        m.Start.Should().BeApproximately(1330, 5);
        m.End.Should().BeApproximately(1390, 5);
        m.Confidence.Should().BeGreaterThanOrEqualTo(0.7);
    }

    [Fact]
    public async Task SinReferenciasQueExistan_RespondeVacioSinMirarElEpisodio()
    {
        _sonido = R(1400, 3);

        var r = await Detectar(new ReferenciaAudio(Path.Combine(_carpeta, "no_existe.ogg"), "OP", 0),
            new ReferenciaAudio("https://animethemes.moe/a.ogg", "ED", 0));

        r!.Success.Should().BeTrue();
        r.Matches.Should().BeEmpty();
        _decodificados.Should().BeEmpty();
    }

    [Fact]
    public async Task SiElEpisodioNoExiste_RespondeConElMotivo()
    {
        var r = await CrearSut().DetectarAsync(Path.Combine(_carpeta, "no_existe.mkv"), [new(Tema("OP1.mp3", R(90, 1)), "OP", 0)],
            Confianza, Inicio, Final, CancellationToken.None);

        r!.Success.Should().BeFalse();
        r.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SiNoSePuedeExtraerElAudio_RespondeConElMotivo()
    {
        // Foco de revisión 2: episodio sin pista de audio o que ffmpeg no lee → huella vacía.
        _sonido = [];

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", R(90, 1)), "OP", 0));

        r!.Success.Should().BeFalse();
        r.Error.Should().Contain("audio");
    }

    [Fact]
    public async Task SiNoSeConoceLaDuracion_RespondeConElMotivo()
    {
        _duracion = 0;
        _sonido = R(1400, 3);

        var r = await Detectar(new ReferenciaAudio(Tema("OP1.mp3", R(90, 1)), "OP", 0));

        r!.Success.Should().BeFalse();
        r.Error.Should().Contain("duración");
    }

    [Fact]
    public async Task UnTemaDeSilencio_SeIgnoraYSiguenLosDemas()
    {
        // Foco de revisión 4: un tema que es todo silencio (o dura menos de 5 s) no sirve, pero no estropea el resto.
        // Aquí se usa la lectura real de temas (con recorte de silencio y caché): el "audio" de cada tema sale de _temas.
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 120);
        string silencio = Tema("OP_silencio.mp3", new float[90 * Fps * C]);
        string corto = Tema("OP_corto.mp3", R(3, 8));
        string bueno = Tema("OP1.mp3", op);
        var sut = new DetectorTemasAudio(_huellas,
            (_, _) => Task.FromResult(_duracion),
            (ruta, desde, duracion, _) => Task.FromResult<float[]?>(_temas.TryGetValue(ruta, out var tema) ? tema : Tramo(desde, duracion)),
            huellaDeTema: null);

        var r = await sut.DetectarAsync(_episodio, [new(silencio, "OP", 0), new(corto, "OP", 0), new(bueno, "OP", 0)],
            Confianza, Inicio, Final, CancellationToken.None);

        r!.Success.Should().BeTrue();
        Path.GetFileName(r.Matches.Single().ReferencePath).Should().Be("OP1.mp3");
        r.Evaluated.Should().Be(1, "los otros dos no llegan a ser candidatos");
    }

    [Fact]
    public async Task LaHuellaDelTema_SeGuardaYNoSeVuelveADecodificar()
    {
        float[] op = R(90, 1);
        _sonido = AudioSintetico.IncrustarEnSegundo(R(1400, 3), op, 120);
        string tema = Path.Combine(_carpeta, "OP1.mp3");
        File.WriteAllBytes(tema, new byte[4000]);
        int decodificaciones = 0;
        DetectorTemasAudio Sut() => new(_huellas,
            (_, _) => Task.FromResult(_duracion),
            (ruta, desde, duracion, _) =>
            {
                if (ruta != tema) return Task.FromResult<float[]?>(Tramo(desde, duracion));
                Interlocked.Increment(ref decodificaciones);
                return Task.FromResult<float[]?>(op);
            },
            huellaDeTema: null);

        var primera = await Sut().DetectarAsync(_episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, CancellationToken.None);
        var segunda = await Sut().DetectarAsync(_episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, CancellationToken.None);

        decodificaciones.Should().Be(1, "la segunda vez la huella sale del disco");
        segunda!.Matches.Single().Start.Should().Be(primera!.Matches.Single().Start);
        Directory.GetFiles(_huellas, "*.huella").Should().ContainSingle();
    }

    [Fact]
    public async Task LaCancelacionSePropaga_YNoDejaArchivosTemporales()
    {
        // Foco de revisión 5: cambiar de episodio a mitad del análisis.
        _sonido = R(1400, 3);
        using var cts = new CancellationTokenSource();
        string tema = Path.Combine(_carpeta, "OP1.mp3");
        File.WriteAllBytes(tema, new byte[4000]);
        var sut = new DetectorTemasAudio(_huellas,
            (_, _) => Task.FromResult(_duracion),
            (ruta, desde, duracion, ct) =>
            {
                if (ruta != tema) return Task.FromResult<float[]?>(Tramo(desde, duracion));
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<float[]?>(null);
            },
            huellaDeTema: null);

        Func<Task> accion = () => sut.DetectarAsync(_episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, cts.Token);

        await accion.Should().ThrowAsync<OperationCanceledException>();
        if (Directory.Exists(_huellas)) Directory.GetFiles(_huellas).Should().BeEmpty();
    }

    [Fact]
    public void RecortarSilencio_QuitaLosExtremosSinSonido()
    {
        var huella = new float[100 * C];
        for (int f = 20; f < 70; f++) huella[f * C] = 0.5f;

        DetectorTemasAudio.RecortarSilencio(huella).Length.Should().Be(50 * C);
        DetectorTemasAudio.RecortarSilencio(new float[30 * C]).Should().BeEmpty();
    }

    [Fact]
    public async Task ConFfmpegDeVerdad_UbicaUnTemaDentroDeUnEpisodio()
    {
        // Integración: 60 s de "episodio" (ruido rosa) con 20 s de "tema" (otro ruido, otra semilla) pegados en el segundo 15.
        string tema = Path.Combine(_carpeta, "tema.wav");
        string episodio = Path.Combine(_carpeta, "episodio.wav");
        await Ffmpeg("-f", "lavfi", "-i", "anoisesrc=d=20:c=brown:r=8000:s=7", "-ac", "1", tema);
        await Ffmpeg("-f", "lavfi", "-i", "anoisesrc=d=15:c=pink:r=8000:s=1", "-i", tema, "-f", "lavfi", "-i", "anoisesrc=d=25:c=pink:r=8000:s=2",
            "-filter_complex", "[0:a][1:a][2:a]concat=n=3:v=0:a=1", "-ac", "1", episodio);

        var r = await new DetectorTemasAudio(_huellas).DetectarAsync(episodio, [new(tema, "OP", 0)], Confianza, Inicio, Final, CancellationToken.None);

        r.Should().NotBeNull();
        r!.Success.Should().BeTrue(r.Error);
        var m = r.Matches.First(x => x.Segment == "op");
        m.Start.Should().BeApproximately(15, 0.3);
        m.End.Should().BeApproximately(35, 0.3);
        m.Confidence.Should().BeGreaterThan(0.9);
    }

    private static async Task Ffmpeg(params string[] argumentos)
    {
        string[] todos = ["-y", "-hide_banner", "-loglevel", "error", .. argumentos];
        var resultado = await ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffmpeg, todos, TimeSpan.FromSeconds(60), CancellationToken.None);
        resultado!.Codigo.Should().Be(0, resultado.Error);
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

Ejecutar: `dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore`
Esperado: errores CS0246, no se encuentran `DetectorTemasAudio` ni `ReferenciaAudio`.

- [ ] **Paso 3: implementar**

Crear `AnimeLocalTracker/Services/DetectorTemasAudio.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services.Native;

namespace AnimeLocalTracker.Services;

/// <summary>Audio oficial de un tema. <paramref name="Tipo"/>: "OP" o "ED". <paramref name="Prioridad"/>: 0 = aplica al episodio según AnimeThemes, 1 = resto.</summary>
public sealed record ReferenciaAudio(string Ruta, string Tipo, int Prioridad);

public interface IDetectorTemasAudio
{
    /// <summary>El núcleo nativo que hace el cálculo está cargado.</summary>
    bool Disponible { get; }

    /// <summary>
    /// Ubica el opening (al inicio) y el ending (al final) de un episodio comparándolo con los temas oficiales.
    /// Null = el motor no pudo trabajar (no es un dato sobre el episodio). Con Success = false, Error dice por qué.
    /// </summary>
    Task<SkipTimesCoordinator.DeteccionTemasResult?> DetectarAsync(string rutaEpisodio, IReadOnlyList<ReferenciaAudio> referencias,
        double confianzaMinima, double segundosInicio, double segundosFinal, CancellationToken ct);
}

/// <summary>
/// Dirige la detección: ffmpeg decodifica (aquí, con <see cref="Core.ProcesoExterno"/>), el núcleo Rust calcula huellas y
/// parecidos, y esta clase decide el orden. Por cada tramo se prueban grupos de temas y se para en el primero que acierta (así
/// los temas que no aplican ni se decodifican si el que aplica ya coincide):
///   opening → OP que aplican, OP restantes; si no aparece al principio, otra vez hasta la mitad del episodio.
///   ending  → ED que aplican, ED restantes y, por último, los OP: el episodio 1 y los finales suelen cerrar con el opening.
/// </summary>
public sealed class DetectorTemasAudio : IDetectorTemasAudio
{
    private const int Fps = NativeMethods.FotogramasPorSegundo;
    private const int Columnas = NativeMethods.ColumnasHuella;
    /// <summary>
    /// El opening se busca como mucho hasta esta fracción del episodio. Más allá el mismo tema suele sonar como canción de fondo
    /// del clímax (episodios finales): marcarlo haría que "saltar opening" se llevara la escena.
    /// </summary>
    private const double LimiteOpening = 0.5;
    private const int TemasALaVez = 4;
    /// <summary>Menos de 5 s de tema: archivo roto o vacío.</summary>
    private const int FotogramasMinimosDeTema = 5 * Fps;
    private static readonly TimeSpan TiempoMaximoFfmpeg = TimeSpan.FromMinutes(5);

    private readonly CacheHuellasAudio _cache;
    private readonly Func<string, CancellationToken, Task<double>> _duracion;
    private readonly Func<string, double, double?, CancellationToken, Task<float[]?>> _huellaDeTramo;
    private readonly Func<string, CancellationToken, Task<float[]?>> _huellaDeTema;

    /// <param name="carpetaHuellas">Dónde se guarda la huella de cada tema; por defecto <see cref="AppDataPaths.AudioFingerprintsDir"/>.</param>
    public DetectorTemasAudio(string? carpetaHuellas = null)
        : this(carpetaHuellas ?? AppDataPaths.AudioFingerprintsDir, DuracionConFfprobeAsync, HuellaConFfmpegAsync, null)
    {
    }

    /// <summary>Para pruebas: de dónde salen la duración, la huella de un tramo de un archivo (desde, duración o null = hasta el final) y,
    /// si se da, la huella ya lista de un tema (sin pasar por la caché).</summary>
    internal DetectorTemasAudio(string carpetaHuellas, Func<string, CancellationToken, Task<double>> duracion,
        Func<string, double, double?, CancellationToken, Task<float[]?>> huellaDeTramo, Func<string, CancellationToken, Task<float[]?>>? huellaDeTema)
    {
        _cache = new CacheHuellasAudio(carpetaHuellas);
        _duracion = duracion;
        _huellaDeTramo = huellaDeTramo;
        _huellaDeTema = huellaDeTema ?? HuellaDeTemaConCacheAsync;
    }

    public bool Disponible => NativeMethods.IsAvailable;

    private sealed record Ventana(double Inicio, float[] Huella);

    /// <summary>El núcleo nativo falló: no es un dato sobre el episodio y no debe guardarse como tal.</summary>
    private sealed class ErrorDelMotor : Exception;

    public async Task<SkipTimesCoordinator.DeteccionTemasResult?> DetectarAsync(string rutaEpisodio, IReadOnlyList<ReferenciaAudio> referencias,
        double confianzaMinima, double segundosInicio, double segundosFinal, CancellationToken ct)
    {
        var reloj = Stopwatch.StartNew();
        if (!Disponible) return null;
        // File.Exists descarta también las URL: ffmpeg las abriría.
        if (string.IsNullOrWhiteSpace(rutaEpisodio) || !File.Exists(rutaEpisodio)) return Fallo("El episodio no existe.");

        var temas = referencias.Where(r => !string.IsNullOrWhiteSpace(r.Ruta) && File.Exists(r.Ruta)).ToList();
        if (temas.Count == 0) return new SkipTimesCoordinator.DeteccionTemasResult { Success = true };

        try
        {
            double duracion = await _duracion(rutaEpisodio, ct);
            if (!(duracion > 0)) return Fallo("No se pudo obtener la duración del episodio (ffprobe).");

            var (inicio, final) = await VentanasDelEpisodioAsync(rutaEpisodio, duracion, segundosInicio, segundosFinal, ct);
            if (inicio == null || final == null) return Fallo("No se pudo extraer el audio del episodio.");

            var huellas = new Dictionary<string, float[]?>();
            var evaluados = new HashSet<string>();
            List<string> Grupo(string tipo, int prioridad) =>
                temas.Where(r => string.Equals(r.Tipo, tipo, StringComparison.OrdinalIgnoreCase) && r.Prioridad == prioridad).Select(r => r.Ruta).ToList();

            var resultado = new SkipTimesCoordinator.DeteccionTemasResult { Success = true };
            List<string>[] gruposOpening = [Grupo("OP", 0), Grupo("OP", 1)];
            var opening = await BuscarEnGruposAsync(inicio, gruposOpening, huellas, evaluados, confianzaMinima, null, ct);
            if (opening == null)
            {
                // No está en los primeros minutos: un episodio doble o con una apertura en frío larga lo trae más tarde (Sasaki to
                // Pii-chan 2, episodio 1 de 47 min: suena a los 8:21). Se mira hasta la mitad del episodio, y solo ahora, para no
                // decodificar de más en el caso normal. El tramo empieza un tema antes del límite: cubre el que cae a caballo.
                double largoTema = gruposOpening.SelectMany(g => g).Select(r => huellas.GetValueOrDefault(r)?.Length ?? 0).DefaultIfEmpty(0).Max()
                                   / (double)Columnas / Fps;
                double hasta = duracion * LimiteOpening;
                if (largoTema > 0 && hasta > segundosInicio)
                {
                    double desde = Math.Max(0.0, segundosInicio - largoTema);
                    var tardia = await _huellaDeTramo(rutaEpisodio, desde, hasta - desde, ct);
                    if (tardia is { Length: >= Columnas })
                    {
                        opening = await BuscarEnGruposAsync(new Ventana(desde, tardia), gruposOpening, huellas, evaluados, confianzaMinima, null, ct);
                    }
                }
            }
            if (opening is { } op) resultado.Matches.Add(ATema("op", op.Ruta, op.Coincidencia));

            (double, double)? excluir = opening is { } hallado ? (hallado.Coincidencia.Inicio, hallado.Coincidencia.Fin) : null;
            List<string>[] gruposEnding = [Grupo("ED", 0), Grupo("ED", 1), Grupo("OP", 0), Grupo("OP", 1)];
            var ending = await BuscarEnGruposAsync(final, gruposEnding, huellas, evaluados, confianzaMinima, excluir, ct);
            if (ending is { } ed) resultado.Matches.Add(ATema("ed", ed.Ruta, ed.Coincidencia));

            resultado.Evaluated = evaluados.Count;
            resultado.Seconds = Math.Round(reloj.Elapsed.TotalSeconds, 3);
            return resultado;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fallo("ffmpeg tardó demasiado en decodificar el audio.");
        }
        catch (ErrorDelMotor)
        {
            return null;
        }
        catch (Exception ex)
        {
            return Fallo(ex.Message);
        }
    }

    private static SkipTimesCoordinator.DeteccionTemasResult Fallo(string motivo) => new() { Success = false, Error = motivo };

    private static SkipTimesCoordinator.TemaDetectado ATema(string tramo, string ruta, CoincidenciaAudio c) => new()
    {
        Segment = tramo,
        ReferencePath = ruta,
        Start = c.Inicio,
        End = c.Fin,
        Confidence = c.Confianza,
        Mode = c.Parcial != 0 ? "partial" : "full"
    };

    /// <summary>
    /// Tramos de inicio y de final del episodio. Las dos decodificaciones van a la vez; en episodios cortos los tramos se
    /// solapan y se decodifica el archivo entero una sola vez.
    /// </summary>
    private async Task<(Ventana? Inicio, Ventana? Final)> VentanasDelEpisodioAsync(string ruta, double duracion, double segundosInicio, double segundosFinal, CancellationToken ct)
    {
        double largoInicio = Math.Min(segundosInicio, duracion);
        double comienzoFinal = Math.Max(0.0, duracion - segundosFinal);

        if (comienzoFinal <= largoInicio)
        {
            var entera = await _huellaDeTramo(ruta, 0.0, null, ct);
            if (entera is not { Length: >= Columnas }) return (null, null);

            int total = entera.Length / Columnas;
            int corte = Math.Min(total, (int)(comienzoFinal * Fps));
            int finInicio = Math.Min(total, (int)(largoInicio * Fps));
            if (finInicio == 0 || corte >= total) return (null, null);
            return (new Ventana(0.0, entera.AsSpan(0, finInicio * Columnas).ToArray()),
                    new Ventana(corte / (double)Fps, entera.AsSpan(corte * Columnas).ToArray()));
        }

        var tareaInicio = _huellaDeTramo(ruta, 0.0, largoInicio, ct);
        var tareaFinal = _huellaDeTramo(ruta, comienzoFinal, segundosFinal, ct);
        await Task.WhenAll(tareaInicio, tareaFinal);
        var huellaInicio = await tareaInicio;
        var huellaFinal = await tareaFinal;
        if (huellaInicio is not { Length: >= Columnas } || huellaFinal is not { Length: >= Columnas }) return (null, null);
        return (new Ventana(0.0, huellaInicio), new Ventana(comienzoFinal, huellaFinal));
    }

    private async Task<(string Ruta, CoincidenciaAudio Coincidencia)?> BuscarEnGruposAsync(Ventana ventana, IEnumerable<List<string>> grupos,
        Dictionary<string, float[]?> huellas, HashSet<string> evaluados, double confianzaMinima, (double, double)? excluir, CancellationToken ct)
    {
        foreach (var rutas in grupos)
        {
            var pendientes = rutas.Where(r => !huellas.ContainsKey(r)).Distinct().ToList();
            if (pendientes.Count > 0)
            {
                var nuevas = new ConcurrentDictionary<string, float[]?>();
                await Parallel.ForEachAsync(pendientes, new ParallelOptions { MaxDegreeOfParallelism = TemasALaVez, CancellationToken = ct },
                    async (ruta, corte) => nuevas[ruta] = await _huellaDeTema(ruta, corte));
                foreach (var (ruta, huella) in nuevas) huellas[ruta] = huella;
            }

            var candidatos = rutas.Where(r => huellas.GetValueOrDefault(r) != null).Distinct().ToList();
            if (candidatos.Count == 0) continue;
            evaluados.UnionWith(candidatos);

            if (!NativeMethods.MejorCoincidencia(ventana.Huella, ventana.Inicio, candidatos.Select(r => huellas[r]!).ToList(), excluir, out var mejor))
            {
                throw new ErrorDelMotor();
            }
            if (mejor is { } m && m.Confianza >= confianzaMinima) return (candidatos[m.Indice], m);
        }
        return null;
    }

    /// <summary>Huella del tema (sin silencios en los extremos), guardada en disco: la segunda vez no se decodifica. Null si no sirve.</summary>
    private async Task<float[]?> HuellaDeTemaConCacheAsync(string ruta, CancellationToken ct)
    {
        string clave;
        try
        {
            clave = CacheHuellasAudio.Clave(ruta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        float[]? huella = _cache.Leer(clave);
        if (huella == null)
        {
            var cruda = await _huellaDeTramo(ruta, 0.0, null, ct);
            if (cruda == null) return null;

            huella = RecortarSilencio(cruda);
            if (huella.Length > 0) _cache.Guardar(clave, huella);
        }
        return huella.Length >= FotogramasMinimosDeTema * Columnas ? huella : null;
    }

    /// <summary>Quita el silencio del principio y del final del tema: el tramo detectado empieza y acaba donde suena la música.</summary>
    internal static float[] RecortarSilencio(float[] huella)
    {
        int total = huella.Length / Columnas;
        float maximo = 0;
        for (int f = 0; f < total; f++) maximo = Math.Max(maximo, huella[f * Columnas]);
        float umbral = Math.Max(maximo * 0.02f, 1e-4f);

        int primero = -1, ultimo = -1;
        for (int f = 0; f < total; f++)
        {
            if (huella[f * Columnas] <= umbral) continue;
            if (primero < 0) primero = f;
            ultimo = f;
        }
        return primero < 0 ? [] : huella.AsSpan(primero * Columnas, (ultimo - primero + 1) * Columnas).ToArray();
    }

    private static async Task<double> DuracionConFfprobeAsync(string ruta, CancellationToken ct)
    {
        string[] argumentos = ["-protocol_whitelist", "file", "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", ruta];
        var resultado = await Core.ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffprobe, argumentos, TimeSpan.FromSeconds(30), ct);
        return resultado is { Codigo: 0 } && double.TryParse(resultado.Salida.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double segundos)
            ? segundos
            : 0.0;
    }

    /// <summary>Audio mono a 8 kHz de un tramo del archivo, ya convertido en huella. Null si ffmpeg falla o no hay audio.</summary>
    private static async Task<float[]?> HuellaConFfmpegAsync(string ruta, double desde, double? duracion, CancellationToken ct)
    {
        // -protocol_whitelist file: un archivo que en realidad sea una lista (HLS, concat) no puede hacer que ffmpeg salga a la
        // red. -max_alloc: tope de 2 GB por reserva de memoria ante una cabecera malformada.
        var argumentos = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-max_alloc", "2147483648", "-protocol_whitelist", "file" };
        if (desde > 0)
        {
            argumentos.Add("-ss");
            argumentos.Add(desde.ToString("0.000", CultureInfo.InvariantCulture));
        }
        argumentos.AddRange(["-i", ruta, "-vn", "-sn", "-dn"]);
        if (duracion is { } segundos)
        {
            argumentos.Add("-t");
            argumentos.Add(segundos.ToString("0.000", CultureInfo.InvariantCulture));
        }
        argumentos.AddRange(["-ac", "1", "-ar", "8000", "-f", "f32le", "-"]);

        var resultado = await Core.ProcesoExterno.EjecutarBinarioAsync(FfmpegLocator.Ffmpeg, argumentos, TiempoMaximoFfmpeg, ct);
        if (resultado is not { Codigo: 0 } || resultado.Salida.Length < sizeof(float)) return null;

        var pcm = MemoryMarshal.Cast<byte, float>(resultado.Salida.AsSpan(0, resultado.Salida.Length / sizeof(float) * sizeof(float)));
        return NativeMethods.HuellaDeAudio(pcm) ?? throw new ErrorDelMotor();
    }
}
```

- [ ] **Paso 4: compilar y comprobar que pasan**

Ejecutar:

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DetectorTemasAudioTests"
```

Esperado: 0 advertencias; 19 pruebas en verde.

Referencia para `ConFfmpegDeVerdad_UbicaUnTemaDentroDeUnEpisodio`: con esos mismos dos archivos, el plugin Python actual da el opening en 15,0–35,0 s con confianza 1,0 (comprobado al escribir el plan). Si es la única que falla, comprobar primero el archivo generado (`ffprobe` sobre `episodio.wav`: 60 s, 8000 Hz, mono) antes de tocar el detector: las otras 18 ya prueban el flujo.

Si el compilador rechaza `private sealed class ErrorDelMotor : Exception;` por una regla del analizador, darle cuerpo explícito con un constructor sin parámetros que pase el mensaje "El núcleo nativo de audio falló." a la base; su único uso es distinguir "el motor falló" de "el episodio no se pudo leer".

---

### Tarea 7: validación con episodios reales (puerta antes de cambiar nada)

Nada de esta tarea se queda en el repo. Solo lee los videos y la música del usuario; la base se abre en modo solo lectura y las cachés van a carpetas temporales.

**Archivos:**
- Crear (carpeta temporal de la sesión, fuera del repo): `oraculo_reales.py`, `casos.json`
- Crear y borrar al terminar: `AnimeLocalTracker.Tests/Services/ValidacionAudioTemporalTests.cs`

**Interfaces:**
- Consume: `DetectorTemasAudio(string? carpetaHuellas)` y `ReferenciaAudio` (tarea 6); el plugin Python todavía presente (`tools/python/audio_skip_plugin.py`).
- Produce: un informe en la salida de la prueba. Ninguna interfaz para tareas posteriores.

- [ ] **Paso 1: generar los casos con el plugin Python como referencia**

Crear `oraculo_reales.py` en la carpeta temporal de la sesión (sustituir `<TEMP>` por esa carpeta):

```python
"""Pasa detect_themes (Python) por los episodios reales que tienen temas y guarda entradas y resultados en casos.json.
Solo lectura sobre la biblioteca; la caché de huellas va a <TEMP>/cache_python."""
import json
import os
import re
import sqlite3
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")
REPO = r"C:\Users\HP\RiderProjects\AnimeLocalTracker"
TEMP = os.path.dirname(os.path.abspath(__file__))
DATOS = os.path.join(os.environ["LOCALAPPDATA"], "AnimeLocalTrackerData")
os.environ["PATH"] = os.path.join(REPO, "AnimeLocalTracker", "FFmpeg") + os.pathsep + os.environ["PATH"]
sys.path.insert(0, os.path.join(REPO, "tools", "python"))
import audio_skip_plugin as plugin

MAXIMO_POR_ANIME = 6
MAXIMO_TOTAL = 40
RANGO = re.compile(r"_ep(\d+)(?:-(\d*))?", re.I)


def referencias(anime_id, episodio):
    """Temas del anime en disco. Tipo por el prefijo del nombre (OP/ED); prioridad 0 si el rango del nombre cubre el episodio."""
    lista = []
    for carpeta, extension in ((os.path.join(DATOS, "SkipReferences", str(anime_id)), ".ogg"), (os.path.join(DATOS, "Music", str(anime_id)), ".mp3")):
        if not os.path.isdir(carpeta):
            continue
        for nombre in sorted(os.listdir(carpeta)):
            tipo = nombre[:2].upper()
            if not nombre.lower().endswith(extension) or tipo not in ("OP", "ED"):
                continue
            m = RANGO.search(nombre)
            aplica = True
            if m:
                desde = int(m.group(1))
                hasta = int(m.group(2)) if m.group(2) else (desde if m.group(2) is None else 10 ** 6)
                aplica = desde <= episodio <= hasta
            lista.append({"path": os.path.join(carpeta, nombre), "kind": tipo, "priority": 0 if aplica else 1})
    return lista


db = sqlite3.connect(f"file:{os.path.join(DATOS, 'biblioteca.db')}?mode=ro", uri=True)
filas = db.execute("select AniListId, NumeroEpisodio, RutaArchivo from RegistroEpisodio where RutaArchivo <> '' order by AniListId, NumeroEpisodio").fetchall()
guardados = {(a, e): [] for a, e, _ in filas}
for a, e, tipo, ini, fin, origen, conf in db.execute("select AnimeId, Episodio, Tipo, Inicio, Fin, Origen, Confianza from SegmentoSkipGuardado"):
    guardados.setdefault((a, e), []).append({"tipo": tipo, "inicio": ini, "fin": fin, "origen": origen, "confianza": conf})

casos, por_anime = [], {}
for anime_id, episodio, ruta in filas:
    if len(casos) >= MAXIMO_TOTAL or por_anime.get(anime_id, 0) >= MAXIMO_POR_ANIME or not os.path.isfile(ruta):
        continue
    refs = referencias(anime_id, episodio)
    if not refs:
        continue
    t0 = time.monotonic()
    r = plugin.detect_themes(ruta, refs, min_confidence=0.7, head_seconds=480.0, tail_seconds=360.0, cache_dir=os.path.join(TEMP, "cache_python"))
    por_anime[anime_id] = por_anime.get(anime_id, 0) + 1
    casos.append({"anime": anime_id, "episodio": episodio, "ruta": ruta, "referencias": refs, "python": r,
                  "segundos_python": round(time.monotonic() - t0, 2), "guardado": guardados.get((anime_id, episodio), [])})
    print(f"{anime_id} ep {episodio}: {len(refs)} temas, {len(r.get('matches', []))} tramos, {casos[-1]['segundos_python']} s")

json.dump(casos, open(os.path.join(TEMP, "casos.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(len(casos), "casos de", len(por_anime), "animes")
```

Ejecutar: `python <TEMP>/oraculo_reales.py`
Esperado: una línea por episodio y al final `N casos de M animes`, con N ≥ 10. Si N < 10, subir `MAXIMO_POR_ANIME` a 12 y repetir. Si hay episodios con 0 tramos, es válido (también se compara que Rust tampoco los encuentre).

- [ ] **Paso 2: prueba temporal que pasa los mismos casos por la implementación nueva**

Crear `AnimeLocalTracker.Tests/Services/ValidacionAudioTemporalTests.cs` (sustituir `<TEMP>` por la carpeta, con barras dobles):

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>TEMPORAL (tarea 7 de docs/plan-audio-openings-rust.md): compara el detector nuevo con el plugin Python sobre episodios reales.
/// Se borra antes de cerrar la tarea; no debe quedar en el repo.</summary>
public class ValidacionAudioTemporalTests(ITestOutputHelper salida)
{
    private const string Temp = "<TEMP>";

    [Fact]
    public async Task ElDetectorNuevoDaLoMismoQuePythonEnLosEpisodiosReales()
    {
        using var casos = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Temp, "casos.json")));
        var detector = new DetectorTemasAudio(Path.Combine(Temp, "cache_rust"));
        var diferencias = new List<string>();
        var informe = new StringBuilder();
        double totalPython = 0, totalNuevo = 0;

        foreach (var caso in casos.RootElement.EnumerateArray())
        {
            string etiqueta = $"{caso.GetProperty("anime").GetInt32()} ep {caso.GetProperty("episodio").GetInt32()}";
            var referencias = caso.GetProperty("referencias").EnumerateArray()
                .Select(r => new ReferenciaAudio(r.GetProperty("path").GetString()!, r.GetProperty("kind").GetString()!, r.GetProperty("priority").GetInt32())).ToList();

            var reloj = Stopwatch.StartNew();
            var nuevo = await detector.DetectarAsync(caso.GetProperty("ruta").GetString()!, referencias, 0.7, 480, 360, CancellationToken.None);
            totalNuevo += reloj.Elapsed.TotalSeconds;
            totalPython += caso.GetProperty("segundos_python").GetDouble();

            var python = caso.GetProperty("python");
            bool exitoPython = python.GetProperty("success").GetBoolean();
            if (nuevo == null || nuevo.Success != exitoPython)
            {
                diferencias.Add($"{etiqueta}: éxito Python={exitoPython}, nuevo={(nuevo == null ? "sin respuesta" : nuevo.Success + " " + nuevo.Error)}");
                continue;
            }
            if (!exitoPython) continue;

            var esperados = python.GetProperty("matches").EnumerateArray().ToList();
            informe.AppendLine($"{etiqueta}: {esperados.Count} tramo(s); Python {caso.GetProperty("segundos_python").GetDouble():F2} s, nuevo {reloj.Elapsed.TotalSeconds:F2} s");
            if (esperados.Count != nuevo.Matches.Count)
            {
                diferencias.Add($"{etiqueta}: Python halló {esperados.Count} tramo(s) y el nuevo {nuevo.Matches.Count}");
                continue;
            }
            foreach (var e in esperados)
            {
                string tramo = e.GetProperty("segment").GetString()!;
                var n = nuevo.Matches.FirstOrDefault(m => m.Segment == tramo);
                if (n == null) { diferencias.Add($"{etiqueta}: falta el tramo {tramo}"); continue; }

                double dInicio = Math.Abs(n.Start - e.GetProperty("start").GetDouble());
                double dFin = Math.Abs(n.End - e.GetProperty("end").GetDouble());
                double dConfianza = Math.Abs(n.Confidence - e.GetProperty("confidence").GetDouble());
                bool mismoTema = string.Equals(n.ReferencePath, e.GetProperty("reference_path").GetString(), StringComparison.OrdinalIgnoreCase);
                bool mismoModo = n.Mode == e.GetProperty("mode").GetString();
                informe.AppendLine($"    {tramo}: [{n.Start:F1} - {n.End:F1}] conf {n.Confidence:F3} {n.Mode}  (Δinicio {dInicio:F3}, Δfin {dFin:F3}, Δconf {dConfianza:F4})");
                if (dInicio >= 0.1 || dFin >= 0.1 || dConfianza >= 0.01 || !mismoTema || !mismoModo)
                {
                    diferencias.Add($"{etiqueta} {tramo}: Δinicio {dInicio:F3}, Δfin {dFin:F3}, Δconf {dConfianza:F4}, mismo tema {mismoTema}, mismo modo {mismoModo}");
                }
            }

            // Comprobación secundaria (informativa): lo que la app guardó en su día para este episodio.
            foreach (var g in caso.GetProperty("guardado").EnumerateArray().Where(g => g.GetProperty("origen").GetString() == "audio"))
            {
                var n = nuevo.Matches.FirstOrDefault(m => m.Segment == g.GetProperty("tipo").GetString());
                informe.AppendLine(n == null
                    ? $"    guardado {g.GetProperty("tipo").GetString()} [{g.GetProperty("inicio").GetDouble():F1}]: ahora no sale"
                    : $"    guardado {n.Segment}: Δinicio {Math.Abs(n.Start - g.GetProperty("inicio").GetDouble()):F2} s");
            }
        }

        informe.AppendLine($"TOTAL: Python {totalPython:F1} s, nuevo {totalNuevo:F1} s");
        salida.WriteLine(informe.ToString());
        diferencias.Should().BeEmpty();
        totalNuevo.Should().BeLessThanOrEqualTo(totalPython * 1.1, "la implementación nueva no debe ser más lenta");
    }
}
```

- [ ] **Paso 3: ejecutar la comparación**

Ejecutar:

```
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~ValidacionAudioTemporalTests" --logger "console;verbosity=detailed"
```

Esperado: 1 prueba en verde, con el informe por episodio y la línea `TOTAL`.

- [ ] **Paso 4: decidir**

- **Si pasa:** copiar el informe (número de casos, mayor Δinicio, mayor Δconfianza, tiempos totales) para el resumen final. Seguir con el paso 5.
- **Si falla:** **detenerse aquí.** No se ejecutan las tareas 8 a 10. Usar `superpowers:systematic-debugging` sobre el primer episodio que difiera (comparar la huella de un mismo tramo entre `plugin._huella(plugin._pcm(...))` y `NativeMethods.HuellaDeAudio`, luego la curva) e informar al usuario de la diferencia antes de cambiar nada más.

- [ ] **Paso 5: retirar el arnés**

Borrar `AnimeLocalTracker.Tests/Services/ValidacionAudioTemporalTests.cs`. Comprobar con `git status --short` que no aparece. Los archivos de `<TEMP>` no están en el repo y se quedan hasta el final de la sesión por si hay que repetir.

---

### Tarea 8: `SkipTimesCoordinator` usa el detector

**Archivos:**
- Modificar: `AnimeLocalTracker/Services/SkipTimesCoordinator.cs`
- Modificar: `AnimeLocalTracker/App.xaml.cs` (junto a la línea `services.AddSingleton<ISkipTimesCoordinator, SkipTimesCoordinator>();`)
- Modificar: `AnimeLocalTracker.Tests/Services/SkipTimesCoordinatorAudioTests.cs`
- Modificar: `AnimeLocalTracker.Tests/Services/SkipTimesCoordinatorTests.cs`

**Interfaces:**
- Consume: `IDetectorTemasAudio`, `ReferenciaAudio`, `DetectorTemasAudio` (tarea 6).
- Produce: constructor `SkipTimesCoordinator(IAniSkipService? aniSkipService, IDetectorTemasAudio? detector = null, IAnimeThemesDownloadService? themesDownload = null, IReferenciasAudioService? referencias = null, IDatabaseService? database = null)`. Desaparecen `SkipTimesCoordinator.AudioSkipResult`, `OrigenEscenas` y el parámetro `carpetaHuellas`.

- [ ] **Paso 1: adaptar las pruebas (fallarán al compilar)**

En `SkipTimesCoordinatorAudioTests.cs`:

1. Sustituir el campo `_python` por el detector, y quitar `_carpetaHuellas` y el `using AnimeLocalTracker.Services.Python;`:

```csharp
    private readonly Mock<IDetectorTemasAudio> _detector = new();
```

2. `_llamadas` pasa a guardar lo que recibe el detector:

```csharp
    private readonly List<(string Episodio, double Confianza, double SegundosInicio, double SegundosFinal)> _llamadas = new();
```

3. En el constructor, sustituir `_python.Setup(p => p.IsAvailableAsync()).ReturnsAsync(true);` por:

```csharp
        _detector.SetupGet(d => d.Disponible).Returns(true);
```

4. `CrearSut`:

```csharp
    private SkipTimesCoordinator CrearSut(bool conBaseDeDatos = false) =>
        new(_aniSkip.Object, _detector.Object, _descargas.Object, _referencias.Object, conBaseDeDatos ? _db.Object : null);
```

5. Sustituir `RespuestaDelMotor` (y borrar `Propiedad`, que ya no se usa) por:

```csharp
    /// <summary>Lo que responde el detector. Null = el motor no pudo trabajar.</summary>
    private void RespuestaDelMotor(SkipTimesCoordinator.DeteccionTemasResult? resultado) =>
        _detector.Setup(d => d.DetectarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ReferenciaAudio>>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Returns((string episodio, IReadOnlyList<ReferenciaAudio> referencias, double confianza, double inicio, double final, CancellationToken _) =>
            {
                _llamadas.Add((episodio, confianza, inicio, final));
                _enviadas.AddRange(referencias.Select(r => (r.Ruta, r.Tipo, r.Prioridad)));
                return Task.FromResult(resultado);
            });
```

6. En `TodosLosTemasVanEnUnaSolaLlamada_ConSuTipoYPrioridadSegunElRango`, sustituir las cinco líneas desde `object args = …` hasta `Propiedad(args, "min_confidence")…` por:

```csharp
        _llamadas[0].Should().Be((_episodio, SkipTimesCoordinator.ConfianzaMinima, SkipTimesCoordinator.SegundosBusquedaOpening, SkipTimesCoordinator.SegundosBusquedaEnding));
```

7. En `SiElMotorNoResponde_NoSeGuardaElAnalisis_YSeReintentaAlVolverAAbrir`, sustituir el `_python.Setup(…).ReturnsAsync((PluginDaemonResponse<…>?)null);` por:

```csharp
        RespuestaDelMotor(null);
```

8. Borrar el bloque "Último recurso (comparar con otro episodio)" entero: los ayudantes `OtroEpisodioLocal` y `ComparacionEntreEpisodios` y las tres pruebas `ConReferenciasCompletas_UnEpisodioSinOpening_NoLanzaLaComparacionLentaEntreEpisodios`, `SinReferencias_ElOpeningPuedeSalirDeCompararConOtroEpisodio` y `LaComparacionEntreEpisodios_ConConfianzaBaja_SeDescarta`. Añadir en su lugar:

```csharp
    [Fact]
    public async Task SinReferenciasYSinAniSkip_NoSeInventaUnOpening()
    {
        // Antes había un último recurso (comparar con otro episodio de la carpeta); en los datos reales nunca dio un resultado.
        File.WriteAllBytes(Path.Combine(_carpeta, "Episodio 06.mkv"), new byte[1024]);
        Referencias(false);
        Deteccion();

        var resultado = await CrearSut().CargarSkipTimesAsync(1, 5, 1400, _episodio);

        resultado.Should().BeEmpty();
    }
```

9. En `ConUnAnalisisVigenteGuardado_DevuelveLoGuardadoSinDetectarNiConsultarLaNube`, sustituir el `_python.Verify(…Times.Never);` por:

```csharp
        _detector.Verify(d => d.DetectarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ReferenciaAudio>>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
```

10. Sustituir `DeteccionQueEspera` y `ResponderCuandoSeLibere` por:

```csharp
    /// <summary>El detector queda "trabajando" hasta que se complete <paramref name="liberar"/>.</summary>
    private void DeteccionQueEspera(TaskCompletionSource liberar) =>
        _detector.Setup(d => d.DetectarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ReferenciaAudio>>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Returns(async (string episodio, IReadOnlyList<ReferenciaAudio> _, double confianza, double inicio, double final, CancellationToken ct) =>
            {
                _llamadas.Add((episodio, confianza, inicio, final));
                await liberar.Task.WaitAsync(ct);
                return (SkipTimesCoordinator.DeteccionTemasResult?)new SkipTimesCoordinator.DeteccionTemasResult
                {
                    Success = true,
                    Matches = [new SkipTimesCoordinator.TemaDetectado { Segment = "op", Start = 90, End = 180, Confidence = 0.9 }]
                };
            });
```

11. Comprobar que no queda rastro: `grep -n "_python\|PluginDaemonResponse\|_carpetaHuellas\|AudioSkipResult\|Propiedad(" AnimeLocalTracker.Tests/Services/SkipTimesCoordinatorAudioTests.cs` no debe devolver nada. Cualquier línea que quede con `RespuestaDelMotor(x, exito: false)` pasa a `RespuestaDelMotor(null)`.

En `SkipTimesCoordinatorTests.cs`:

1. Campo y constructor: `private readonly Mock<IDetectorTemasAudio> _detectorMock = new();`, en el constructor `_detectorMock.SetupGet(d => d.Disponible).Returns(true);` en lugar del `IsAvailableAsync`, quitar el `using AnimeLocalTracker.Services.Python;` y `CrearSut` queda `new(_aniSkipMock.Object, _detectorMock.Object, _themesDownloadMock.Object);`.
2. Añadir el ayudante:

```csharp
    private void ElDetectorEncuentra(params SkipTimesCoordinator.TemaDetectado[] tramos) =>
        _detectorMock.Setup(d => d.DetectarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ReferenciaAudio>>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SkipTimesCoordinator.DeteccionTemasResult { Success = true, Matches = tramos.ToList() });
```

   (añadir `using System.Linq;` si falta).
3. En las dos pruebas que montan `var respuesta = new PluginDaemonResponse<…>` y `_pythonBridgeMock.Setup(…).ReturnsAsync(respuesta)`, sustituir ambos bloques por `ElDetectorEncuentra(…)` con los mismos tramos (`{ Segment = "op", Start = 90.0, End = 180.0, Confidence = 0.8 }` en la primera; el `op` 10-100 y el `ed` 1300-1390, confianza 0,9, en la segunda).
4. En `…ConReferenciaQueNoAplicaPorRangoYNoAcierta_DeberiaCaerAAniSkip`, añadir `ElDetectorEncuentra();` antes de `var sut = CrearSut();` y sustituir el `_pythonBridgeMock.Verify(…, Times.Once);` por:

```csharp
        _detectorMock.Verify(d => d.DetectarAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ReferenciaAudio>>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Once);
```

5. Comprobar: `grep -n "_pythonBridgeMock\|PluginDaemonResponse" AnimeLocalTracker.Tests/Services/SkipTimesCoordinatorTests.cs` no debe devolver nada.

- [ ] **Paso 2: comprobar que fallan**

Ejecutar: `dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore`
Esperado: errores de compilación en los dos archivos (el constructor de `SkipTimesCoordinator` todavía espera `IPythonBridgeService`).

- [ ] **Paso 3: cambiar el coordinador**

En `SkipTimesCoordinator.cs`:

1. Quitar `using AnimeLocalTracker.Services.Python;`, la constante `OrigenEscenas`, el campo `_carpetaHuellas`, el campo estático `RutaPluginAudioSkip` con su comentario, y sustituir el campo `_pythonBridge` por:

```csharp
    private readonly IDetectorTemasAudio? _detector;
```

2. Constructor:

```csharp
    /// <param name="detector">Ubica los temas por audio (núcleo Rust); sin él solo queda AniSkip.</param>
    /// <param name="referencias">Consigue (y baja si falta) el audio oficial de los temas; sin él solo se usan las descargas de la Ficha.</param>
    /// <param name="database">Guarda el análisis de cada episodio (segunda vez: al instante y sin red). Sin él no hay caché.</param>
    public SkipTimesCoordinator(IAniSkipService? aniSkipService, IDetectorTemasAudio? detector = null, IAnimeThemesDownloadService? themesDownload = null,
        IReferenciasAudioService? referencias = null, IDatabaseService? database = null)
    {
        _aniSkipService = aniSkipService;
        _detector = detector;
        _themesDownload = themesDownload;
        _referencias = referencias;
        _database = database;
    }
```

3. En el resumen de `CargarSkipTimesAsync`, quitar "→ comparación con otro episodio local (último recurso para el opening)".

4. En `AnalizarAsync`, la condición de detección:

```csharp
        bool puedeDetectar = hayVideo && _detector is { Disponible: true } && (_referencias != null || _themesDownload != null);
```

   y borrar el bloque del paso "4) Último recurso…" (desde ese comentario hasta el cierre de su `if`), junto con la variable `referenciasCompletas` si deja de usarse (el compilador lo dirá; se conserva la línea `if (!referencias.Completa) completo = false;`).

5. En el aviso de `motorSinRespuesta`, el texto ya dice "el motor de audio no respondió": no cambia. En su comentario, sustituir "el motor Python" por "el motor de audio".

6. Sustituir `DetectarTemasAsync` entero (resumen incluido) por:

```csharp
    /// <summary>
    /// Ubica opening y ending con TODOS los temas en una sola pasada: el episodio se decodifica una vez y la huella de cada tema
    /// queda guardada en disco. Primero se prueban los temas que AnimeThemes dice que aplican al episodio y solo si ninguno acierta
    /// los demás (la numeración de los archivos no siempre coincide con la de AnimeThemes); para el ending también los openings (el
    /// episodio 1 y los finales suelen cerrar con él).
    /// </summary>
    private async Task<(List<AniSkipResult> Tramos, bool MotorSinRespuesta)> DetectarTemasAsync(int episodio, IReadOnlyList<TemaLocalDisponible> temas, string rutaEpisodio, CancellationToken ct)
    {
        var lista = new List<AniSkipResult>();
        var enviados = temas
            .Where(t => string.Equals(t.Tipo, "OP", StringComparison.OrdinalIgnoreCase) || string.Equals(t.Tipo, "ED", StringComparison.OrdinalIgnoreCase))
            .Select(t => new ReferenciaAudio(t.RutaArchivo, t.Tipo.ToUpperInvariant(), t.AplicaAlEpisodio(episodio) ? 0 : 1))
            .ToList();
        if (enviados.Count == 0) return (lista, false);

        var resultado = await _detector!.DetectarAsync(rutaEpisodio, enviados, ConfianzaMinima, SegundosBusquedaOpening, SegundosBusquedaEnding, ct);
        if (resultado is not { Success: true })
        {
            // Antes un fallo del motor (ffmpeg, archivo ilegible…) se tragaba en silencio y parecía "no hay opening".
            // Un error concreto viene con texto; sin él (null), el motor no pudo trabajar y el análisis no se guarda.
            string? motivo = resultado?.Error;
            AppLogger.Warn("SkipTimesCoordinator", $"No se pudo analizar el audio del episodio {episodio}: {motivo ?? "el motor de audio no respondió"}");
            return (lista, motivo == null);
        }

        foreach (var tramo in new[] { "op", "ed" })
        {
            var mejor = resultado.Matches
                .Where(m => string.Equals(m.Segment, tramo, StringComparison.OrdinalIgnoreCase) && m.Confidence >= ConfianzaMinima && m.End > m.Start)
                .OrderByDescending(m => m.Confidence)
                .FirstOrDefault();
            if (mejor == null) continue;

            lista.Add(CrearSkip(tramo, mejor.Start, mejor.End, OrigenAudio, mejor.Confidence));
            string parcial = mejor.Mode == "partial" ? ", parcial" : "";
            AppLogger.Info("SkipTimesCoordinator", $"REFERENCIA ANIMETHEMES: {tramo.ToUpperInvariant()} detectado [{mejor.Start:F1} - {mejor.End:F1}] (Conf: {mejor.Confidence:F2}{parcial}) con '{Path.GetFileName(mejor.ReferencePath)}'");
        }
        AppLogger.Debug("SkipTimesCoordinator", $"Audio de referencia del episodio {episodio}: {resultado.Evaluated} de {enviados.Count} tema(s) comparados en {resultado.Seconds:F1} s.");
        return (lista, false);
    }
```

   (Antes de sustituir, copiar del método actual el final exacto de la línea `AppLogger.Info(… REFERENCIA ANIMETHEMES …)` si difiere del de arriba: el texto del registro no debe cambiar.)

7. Borrar el registro `ReferenciaEnviada`, el método `DetectarPorComparacionAsync` entero y la clase `AudioSkipResult`. En el comentario de `DeteccionTemasResult`, sustituir "Respuesta de audio_skip_plugin.detect_themes (JSON en snake_case)." por "Resultado de <see cref="IDetectorTemasAudio.DetectarAsync"/>.".

En `Models/AniSkipModels.cs` (línea 35), quitar `o "escenas"` del comentario de `Origen`.

En `App.xaml.cs`, antes de `services.AddSingleton<ISkipTimesCoordinator, SkipTimesCoordinator>();`:

```csharp
        // El cálculo lo hace el núcleo Rust; ffmpeg y la caché de huellas, esta clase.
        services.AddSingleton<IDetectorTemasAudio>(_ => new DetectorTemasAudio());
```

`EpisodiosFichaViewModel.cs` usa `SkipTimesCoordinator.AudioSkipResult` en `AnalizarOpenings`: ese comando se borra en la tarea 9, pero para que esta tarea compile se borra ya aquí el método `AnalizarOpenings` entero (desde su `[RelayCommand]` hasta su llave de cierre) y el botón de `DetalleView.xaml` (el `<Button Command="{Binding Episodios.AnalizarOpeningsCommand}" …>` completo, hasta su `</Button>`). Los textos se limpian en la tarea 9.

- [ ] **Paso 4: compilar y comprobar que pasan**

Ejecutar:

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~SkipTimesCoordinator|FullyQualifiedName~CompositionRootTests|FullyQualifiedName~Reproductor"
```

Esperado: 0 advertencias; todo en verde. `SkipTimesCoordinatorAudioTests` queda con tres pruebas menos (las de la comparación entre episodios) y una más.

Si el compilador avisa de `_pluginService` sin usar en `EpisodiosFichaViewModel` (CS0414/IDE0052), quitar el campo, su asignación y el parámetro `pluginService` del constructor; buscar quién lo pasa con `grep -rn "pluginService" AnimeLocalTracker AnimeLocalTracker.Tests --include=*.cs` y quitar ese argumento en cada llamada (`DetalleViewModel` y las pruebas que lo pasen por nombre). Si `DetalleViewModel` tampoco lo usa ya para nada más, quitar también su parámetro con el mismo procedimiento.

---

### Tarea 9: retirar el plugin Python, numpy y los textos

**Archivos:**
- Borrar: `tools/python/audio_skip_plugin.py`, `tools/python/tests/test_audio_skip_plugin.py`, `AnimeLocalTracker/PythonPlugins/audio_skip_plugin.py` (y la carpeta `PythonPlugins` si queda vacía)
- Modificar: `AnimeLocalTracker/AnimeLocalTracker.csproj` (bloque `PythonPlugins`)
- Modificar: `AnimeLocalTracker/Services/Python/LocalizadorHerramientasPython.cs` y `AnimeLocalTracker.Tests/Services/LocalizadorHerramientasPythonTests.cs`
- Modificar: `tools/python/pyproject.toml`, `tools/python/requirements.txt`
- Modificar: `AnimeLocalTracker/Services/LocalizationService.cs`
- Modificar: `tools/python/tests/test_cli_run_plugin.py` solo si usa `audio_skip_plugin` (comprobar con `grep -n "audio_skip" tools/python/tests/*.py`)

**Interfaces:**
- Consume: nada de tareas anteriores (solo que la tarea 8 ya no use el plugin).
- Produce: nada.

- [ ] **Paso 1: comprobar que nadie usa ya el plugin**

Ejecutar: `grep -rn "audio_skip_plugin\|PluginAudioSkip\|detect_opening\|detect_themes" --include=*.cs --include=*.xaml --include=*.py --include=*.csproj --include=*.ps1 --include=*.yml . | grep -v "/obj/\|/bin/\|docs/"`
Esperado: solo el propio plugin y sus pruebas, sus dos copias, el bloque del `.csproj`, `LocalizadorHerramientasPython.PluginAudioSkip` con sus tres pruebas, y comentarios. Si aparece cualquier otro uso en código de la app, resolverlo antes de borrar.

- [ ] **Paso 2: borrar el plugin y su distribución**

1. Borrar los tres archivos de la lista (y `AnimeLocalTracker/PythonPlugins/` si queda vacía).
2. En `AnimeLocalTracker.csproj`, borrar el comentario "Copia sincronizada de tools/python/audio_skip_plugin.py…" y su elemento:

```xml
      <None Include="PythonPlugins\**">
        <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      </None>
```

3. En `LocalizadorHerramientasPython.cs`, borrar el método `PluginAudioSkip` con su resumen.
4. En `LocalizadorHerramientasPythonTests.cs`, borrar las tres pruebas `PluginAudioSkip_…`.

- [ ] **Paso 3: quitar numpy**

1. En `tools/python/pyproject.toml`, quitar la línea `"numpy>=1.26.0"` y la coma final de la línea anterior si queda colgando (la última dependencia de la lista no lleva coma).
2. En `tools/python/requirements.txt`, borrar el bloque que empieza en `numpy==2.4.6 \` y termina en su comentario `# via animetracker-tools (pyproject.toml)`, ambos incluidos. Antes de borrarlo, comprobar que ese `# via` no nombra ningún otro paquete: `awk '/^numpy==/{f=1} f&&/# via/{print; getline; print; exit}' tools/python/requirements.txt` debe mostrar `# via animetracker-tools (pyproject.toml)` seguido de la primera línea del paquete siguiente. (El archivo se edita a mano porque `pip-tools` no está instalado en el Python 3.11 de la máquina; CI lo valida al instalar con `--require-hashes`.)
3. Comprobar que nada más lo importa: `grep -rn "numpy\|import np\b" tools/python --include=*.py --include=*.toml --include=*.in --include=*.txt | grep -v egg-info` no debe devolver nada.

- [ ] **Paso 4: quitar los textos del botón retirado**

En `LocalizationService.cs`, borrar estas claves **en las dos mitades** (español e inglés), nueve claves y dieciocho líneas en total: `Det_DetectarOp`, `Det_DetectarOpTip`, `Det_Analizando`, `Det_MinEpisodiosOPMsj`, `Det_AnalizandoOpMsj`, `Det_OpEncontradoTitulo`, `Det_OpEncontradoMsj`, `Det_OpNoEncontradoTitulo`, `Det_OpNoEncontradoMsj`.

Antes de borrar cada una, comprobar que no tiene otro uso: `grep -rn "Det_DetectarOp\|Det_Analizando\b\|Det_MinEpisodiosOPMsj\|Det_AnalizandoOpMsj\|Det_OpEncontrado\|Det_OpNoEncontrado" AnimeLocalTracker --include=*.cs --include=*.xaml | grep -v LocalizationService.cs` no debe devolver nada. La clave que sí tenga otro uso se conserva.

- [ ] **Paso 5: verificar**

Ejecutar:

```
python -m pytest tools/python/tests -q
echo {} | python tools/python/cli.py --command ping
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~LocalizadorHerramientasPythonTests|FullyQualifiedName~Localiz|FullyQualifiedName~PluginService"
```

Esperado: pytest en verde (las 14 pruebas del plugin ya no están: de 109 quedan 95); `ping` responde `"success": true` sin numpy instalado en el camino de importación del daemon; compilación con 0 advertencias; pruebas filtradas en verde (si hay una prueba que compruebe que las dos mitades de `LocalizationService` tienen las mismas claves, debe seguir pasando).

---

### Tarea 10: verificación final y documentos

**Archivos:**
- Modificar: `CLAUDE.md` (sección "Estructura políglota"), `.claude/skills/nucleo-poliglota/SKILL.md` (tabla y sección B), `docs/investigacion-ecosistema-poliglota.md` (punto 4.3), `docs/investigacion-audio-openings-rust.md` (estado)
- Modificar: memoria `skip-op-ed.md`

**Interfaces:** ninguna.

- [ ] **Paso 1: suite completa**

Ejecutar:

```
cargo test --manifest-path native/animetracker_core/Cargo.toml
cargo clippy --release --manifest-path native/animetracker_core/Cargo.toml -- -D warnings
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false
python -m pytest tools/python/tests -q
```

Esperado: todo en verde, 0 advertencias en C#. El total de pruebas de C# debe ser mayor que antes de empezar (2.961).

- [ ] **Paso 2: comprobación en vivo con perfil aislado**

Skills `perfil-aislado` y `wpf-visual-verification`. El objetivo es ver que, al abrir un episodio en la app real, el análisis sale por el camino nuevo.

1. Comprobar que no hay una AnimeLocalTracker en marcha (`Get-Process AnimeLocalTracker`); si la hay, pedir al usuario que la cierre y no seguir con este paso.
2. `python .claude/skills/perfil-aislado/scripts/perfil_aislado.py crear` → anotar `PERFIL`.
3. Elegir de `casos.json` (tarea 7) un caso con opening y ending. Copiar su video a la carpeta de prueba de ese anime dentro del perfil (`<PERFIL>\Anime\<título>\Episodio NN.ext`, la ruta está en `AnimeItem.RutaCarpeta` de la copia de la base) y sus temas a `<PERFIL>\AppData\Local\AnimeLocalTrackerData\SkipReferences\<AniListId>\`. Borrar de la copia de la base el análisis guardado de ese episodio para que se repita: con `sqlite3` de Python sobre `<PERFIL>\…\biblioteca.db`, `delete from AnalisisSkipEpisodio where AnimeId=? and Episodio=?` y lo mismo en `SegmentoSkipGuardado`.
4. Foto de la carpeta de datos real (`find … -printf '%P|%s|%T@\n' | sort`), `iniciar PERFIL` (sin red), traer la ventana al primer plano minimizando y maximizando (`ShowWindow` 6 y 3), abrir la ficha del anime y reproducir ese episodio (comprobando antes de cada clic que la ventana bajo el punto es la del PID lanzado). Volumen 0 (lo deja así `crear`).
5. Esperar al registro de la sesión del perfil y buscar `REFERENCIA ANIMETHEMES: OP detectado` y `ED detectado`, con tiempos a menos de 0,1 s de los de `casos.json`. Debe aparecer también `Audio de referencia del episodio … comparados en … s` y ninguna línea de `PythonBridge` relacionada con `run-plugin`.
6. Salir del reproductor con `Esc` antes del 85 % del episodio, `cerrar PERFIL`, `borrar PERFIL`, y comparar la carpeta de datos real con la foto: 0 diferencias.

Si el paso no se puede completar (app del usuario abierta, clics que no llegan), decirlo tal cual en el informe final: las tareas 6 y 7 ya cubren el cálculo; lo que queda sin ver es solo el cableado en la app.

- [ ] **Paso 3: documentos**

1. `CLAUDE.md`, "Estructura políglota": la línea del núcleo Rust pasa a "(parseo de nombres de archivo, huella rápida de archivos y análisis de audio para ubicar openings y endings)"; la del daemon Python, a "para scraping/resolvers y plugins del usuario".
2. `.claude/skills/nucleo-poliglota/SKILL.md`: en la tabla, añadir `audio.rs` a la fila del núcleo Rust y "análisis de audio de openings/endings (dos funciones puras; ffmpeg lo lanza C#)" a su propósito; en la sección B.5, quitar la mención a numpy si la hay.
3. `docs/investigacion-ecosistema-poliglota.md`, punto 4.3: añadir "**Hecho el 2026-10-09**" con el resultado de la tarea 7 (casos comparados, mayor diferencia de inicio y de confianza, tiempos) y la nota de que la comparación entre dos episodios se retiró.
4. `docs/investigacion-audio-openings-rust.md`: cambiar "Estado:" a "implementado el 2026-10-09 según `docs/plan-audio-openings-rust.md`".
5. Memoria `skip-op-ed.md` (carpeta de memoria del proyecto): sustituir la descripción del pipeline (`audio_skip_plugin.detect_themes`, huellas `.npy`, "plugin de comparación de dos episodios") por la nueva (`DetectorTemasAudio` + núcleo Rust, huellas `.huella`, sin comparación entre episodios) y conservar las mediciones y los casos reales.

- [ ] **Paso 4: informe final**

`git status --short` y `git diff --stat`. Resumen para el usuario: qué cambió, resultado de la validación real, resultado en vivo, lo que no se verificó (el `AnimeTrackerTools.exe` local no se regenera: sigue llevando numpy hasta el siguiente empaquetado; `cargo audit` si no estaba instalado), y que no hay commit.

---

## Autorrevisión del plan

- **Cobertura del diseño:** 3.1 → tareas 1-3; 3.2 → tarea 3; 3.3 → tareas 4 y 6; 3.4 → tarea 5; 3.5 → tarea 8; sección 4 → tareas 8 (botón y comando) y 9; sección 5 → pruebas de las tareas 3, 6 y 8; sección 6 (orden) → el orden de las tareas, con la tarea 7 como puerta; sección 7 → pruebas de cada tarea; sección 8 → tarea 7; sección 9 → notas de las tareas 3, 9 y 10.
- **Desviación respecto al diseño, a propósito:** el botón de la ficha se quita en la tarea 8 y no en la 9, porque su código usa un tipo que desaparece al cambiar el coordinador y sin eso la tarea 8 no compila.
- **Nombres entre tareas:** `HuellaDeAudio`, `MejorCoincidencia`, `CoincidenciaAudio` (`Indice`, `Parcial`, `Inicio`, `Fin`, `Confianza`), `EjecutarBinarioAsync`/`ResultadoBinario`, `CacheHuellasAudio.Clave/Leer/Guardar`, `ReferenciaAudio(Ruta, Tipo, Prioridad)`, `IDetectorTemasAudio.Disponible/DetectarAsync` se usan con la misma forma en todas las tareas.

## Notas de ejecución (2026-10-09)

Ejecutado completo. El código de este plan difiere del que quedó en el repo en estos puntos, todos sin cambio de comportamiento:

- **Tarea 1:** clippy 1.98 rechaza `chunks_exact(CONSTANTE)` y un bucle por índice; en `audio.rs` se usa `as_chunks::<COLUMNAS>().0` e `iter_mut().enumerate().take(n)`.
- **Tarea 4:** el analizador (CA1068) exige el token de cancelación como último parámetro de `HastaQueTermineAsync`.
- **Tarea 6:** C# 12 no admite un `Span` como variable local en un método asíncrono (CS8652); la conversión de bytes a huella está en `HuellaDePcm`.
- **Tarea 8:** además de lo previsto se quitaron `referenciasCompletas` y el parámetro `pluginService` de `EpisodiosFichaViewModel`, que quedaban sin uso. El de `DetalleViewModel` sigue (quitarlo desplaza argumentos posicionales en muchas pruebas).
- **Revisión final:** `DetectarAsync` pasa todo su trabajo al grupo de hilos (`Task.Run`). El reproductor lo llama desde el hilo de la interfaz y el cálculo de huellas habría corrido ahí; prueba `ElCalculoNoVuelveAlHiloDeQuienLlama`.

Resultado de la tarea 7: 40 episodios de 10 animes, 65 tramos, diferencias de inicio, fin y confianza de 0,000 frente al plugin Python; 120,0 s en Python y 98,7 s en la implementación nueva.
