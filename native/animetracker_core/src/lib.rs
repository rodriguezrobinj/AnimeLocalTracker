pub mod audio;
pub mod parser;
pub mod hasher;

use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};

/// Barrera de seguridad para TODA función exportada al FFI: un panic (p.ej. de
/// un crate interno) que cruce el borde `extern "C"` es
/// Undefined Behavior y derrumba el proceso .NET. Aquí se captura y se
/// devuelve el valor de fallback (null/false) para que el llamador degrade
/// con elegancia.
fn ffi_catch<T>(f: impl FnOnce() -> T, fallback: T) -> T {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(v) => v,
        Err(_) => fallback,
    }
}

/// Parsea un nombre de archivo de anime y retorna un JSON con los metadatos.
/// La cadena retornada DEBE ser liberada usando `anitomy_free_string`.
#[no_mangle]
pub extern "C" fn anitomy_parse(input: *const c_char) -> *mut c_char {
    ffi_catch(|| anitomy_parse_inner(input), std::ptr::null_mut())
}

fn anitomy_parse_inner(input: *const c_char) -> *mut c_char {
    if input.is_null() {
        return std::ptr::null_mut();
    }

    let c_str = unsafe { CStr::from_ptr(input) };
    let filename = match c_str.to_str() {
        Ok(s) => s,
        Err(_) => return std::ptr::null_mut(),
    };

    let result = parser::parse_filename(filename);
    let json = match serde_json::to_string(&result) {
        Ok(j) => j,
        Err(_) => return std::ptr::null_mut(),
    };

    match CString::new(json) {
        Ok(cs) => cs.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Calcula el fingerprint ultrarrápido por bloques de un archivo de video.
#[no_mangle]
pub extern "C" fn compute_file_fingerprint(video_path: *const c_char) -> *mut c_char {
    ffi_catch(|| compute_file_fingerprint_inner(video_path), std::ptr::null_mut())
}

fn compute_file_fingerprint_inner(video_path: *const c_char) -> *mut c_char {
    if video_path.is_null() {
        return std::ptr::null_mut();
    }

    let c_str = unsafe { CStr::from_ptr(video_path) };
    let path = match c_str.to_str() {
        Ok(s) => s,
        Err(_) => return std::ptr::null_mut(),
    };

    let result = hasher::compute_fingerprint(path);
    let json = match serde_json::to_string(&result) {
        Ok(j) => j,
        Err(_) => return std::ptr::null_mut(),
    };

    match CString::new(json) {
        Ok(cs) => cs.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

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

/// Libera la memoria de una cadena de texto creada en Rust para el llamador de C#.
///
/// # Safety
/// `ptr` debe ser un puntero producido por `CString::into_raw` (nulo permitido) y
/// solo debe liberarse una vez; cualquier otro uso es un double-free o un free inválido.
#[no_mangle]
pub unsafe extern "C" fn anitomy_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        unsafe {
            let _ = CString::from_raw(ptr);
        }
    }
}

/// Retorna la versión del motor nativo.
#[no_mangle]
pub extern "C" fn anitomy_version() -> *mut c_char {
    ffi_catch(anitomy_version_inner, std::ptr::null_mut())
}

fn anitomy_version_inner() -> *mut c_char {
    let ver = "1.1.0 (Rust Core)";
    match CString::new(ver) {
        Ok(cs) => cs.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

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
