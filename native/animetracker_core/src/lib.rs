pub mod parser;
pub mod hasher;

use std::ffi::{CStr, CString};
use std::os::raw::c_char;
use rayon::prelude::*;

/// Parsea un nombre de archivo de anime y retorna un JSON con los metadatos.
/// La cadena retornada DEBE ser liberada usando `anitomy_free_string`.
#[no_mangle]
pub extern "C" fn anitomy_parse(input: *const c_char) -> *mut c_char {
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
    let ver = "1.0.0 (Rust Core)";
    match CString::new(ver) {
        Ok(cs) => cs.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}
