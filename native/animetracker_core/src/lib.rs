pub mod parser;
pub mod hasher;
pub mod spritesheet;

use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use std::panic::{catch_unwind, AssertUnwindSafe};
use rayon::prelude::*;

/// Barrera de seguridad para TODA función exportada al FFI: un panic (p.ej. de
/// Rayon o de un crate interno) que cruce el borde `extern "C"` es
/// Undefined Behavior y derrumba el proceso .NET. Aquí se captura y se
/// devuelve el valor de fallback (null/false) para que el llamador degrade
/// con elegancia.
fn ffi_catch<T>(f: impl FnOnce() -> T, fallback: T) -> T {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(v) => v,
        Err(_) => fallback,
    }
}

/// Extrae un fotograma único en un timestamp de forma ultrarrápida (<20ms).
#[no_mangle]
pub extern "C" fn anitomy_extract_frame(
    video_path: *const c_char,
    out_path: *const c_char,
    timestamp: f64,
    width: i32,
) -> bool {
    ffi_catch(
        || anitomy_extract_frame_inner(video_path, out_path, timestamp, width),
        false,
    )
}

fn anitomy_extract_frame_inner(
    video_path: *const c_char,
    out_path: *const c_char,
    timestamp: f64,
    width: i32,
) -> bool {
    if video_path.is_null() || out_path.is_null() {
        return false;
    }

    let c_video = unsafe { CStr::from_ptr(video_path) };
    let c_out = unsafe { CStr::from_ptr(out_path) };

    let video_str = match c_video.to_str() {
        Ok(s) => s,
        Err(_) => return false,
    };
    let out_str = match c_out.to_str() {
        Ok(s) => s,
        Err(_) => return false,
    };

    spritesheet::extract_frame(video_str, out_str, timestamp, width.max(0) as u32)
}

/// Extrae múltiples fotogramas en paralelo utilizando Rayon y retorna un JSON con los resultados.
#[no_mangle]
pub extern "C" fn anitomy_extract_frames_batch(json_requests: *const c_char) -> *mut c_char {
    ffi_catch(
        || anitomy_extract_frames_batch_inner(json_requests),
        std::ptr::null_mut(),
    )
}

fn anitomy_extract_frames_batch_inner(json_requests: *const c_char) -> *mut c_char {
    if json_requests.is_null() {
        return std::ptr::null_mut();
    }

    let c_str = unsafe { CStr::from_ptr(json_requests) };
    let json_str = match c_str.to_str() {
        Ok(s) => s,
        Err(_) => return std::ptr::null_mut(),
    };

    let requests: Vec<spritesheet::FrameExtractionRequest> = match serde_json::from_str(json_str) {
        Ok(r) => r,
        Err(_) => return std::ptr::null_mut(),
    };

    let results = spritesheet::extract_frames_batch(requests);
    let out_json = match serde_json::to_string(&results) {
        Ok(j) => j,
        Err(_) => return std::ptr::null_mut(),
    };

    match CString::new(out_json) {
        Ok(cs) => cs.into_raw(),
        Err(_) => std::ptr::null_mut(),
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

/// Parsea una lista JSON de nombres de archivo en paralelo (usando todos los núcleos de CPU con Rayon)
/// y retorna un JSON array con los resultados estructurados.
#[no_mangle]
pub extern "C" fn anitomy_parse_batch(input_json_array: *const c_char) -> *mut c_char {
    ffi_catch(|| anitomy_parse_batch_inner(input_json_array), std::ptr::null_mut())
}

fn anitomy_parse_batch_inner(input_json_array: *const c_char) -> *mut c_char {
    if input_json_array.is_null() {
        return std::ptr::null_mut();
    }

    let c_str = unsafe { CStr::from_ptr(input_json_array) };
    let json_text = match c_str.to_str() {
        Ok(s) => s,
        Err(_) => return std::ptr::null_mut(),
    };

    let filenames: Vec<String> = match serde_json::from_str(json_text) {
        Ok(v) => v,
        Err(_) => return std::ptr::null_mut(),
    };

    let results: Vec<parser::ParsedAnimeInfo> = filenames
        .par_iter()
        .map(|f| parser::parse_filename(f))
        .collect();

    let json_output = match serde_json::to_string(&results) {
        Ok(j) => j,
        Err(_) => return std::ptr::null_mut(),
    };

    match CString::new(json_output) {
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

/// Libera la memoria de una cadena de texto creada en Rust para el llamador de C#.
#[no_mangle]
pub extern "C" fn anitomy_free_string(ptr: *mut c_char) {
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
