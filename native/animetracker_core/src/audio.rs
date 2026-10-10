//! Ubicación de un tema (opening/ending) dentro del audio de un episodio. Cálculo puro: sin procesos, archivos ni JSON.
//! Port de `detect_themes` (antes en tools/python/audio_skip_plugin.py); las constantes son las medidas con episodios reales.

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
        for fila in segmento.as_chunks::<COLUMNAS>().0 {
            for (m, &v) in media.iter_mut().zip(fila) {
                *m += v as f64;
            }
        }
        for m in media.iter_mut() {
            *m /= largo as f64;
        }
        for fila in segmento.as_chunks::<COLUMNAS>().0 {
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
                for (i, valor) in buffer.iter_mut().enumerate().take(self.n) {
                    valor.re = self.h[i * COLUMNAS + j];
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
            for (i, fila) in segmento.as_chunks::<COLUMNAS>().0.iter().enumerate() {
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
}
