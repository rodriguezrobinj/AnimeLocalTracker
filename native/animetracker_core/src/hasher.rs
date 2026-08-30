use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FingerprintResult {
    pub success: bool,
    pub fingerprint: Option<String>,
    pub file_size: u64,
    pub error: Option<String>,
}

pub fn compute_fingerprint<P: AsRef<Path>>(path: P) -> FingerprintResult {
    let path = path.as_ref();
    let mut file = match File::open(path) {
        Ok(f) => f,
        Err(e) => {
            return FingerprintResult {
                success: false,
                fingerprint: None,
                file_size: 0,
                error: Some(e.to_string()),
            };
        }
    };

    let file_size = match file.metadata() {
        Ok(m) => m.len(),
        Err(e) => {
            return FingerprintResult {
                success: false,
                fingerprint: None,
                file_size: 0,
                error: Some(e.to_string()),
            };
        }
    };

    if file_size == 0 {
        return FingerprintResult {
            success: false,
            fingerprint: None,
            file_size: 0,
            error: Some("El archivo está vacío".to_string()),
        };
    }

    // Muestreo rápido de bloques: 0%, 25%, 50%, 75% y final
    let sample_size = 64 * 1024; // 64 KB por bloque
    let offsets = [
        0u64,
        file_size / 4,
        file_size / 2,
        (file_size * 3) / 4,
        file_size.saturating_sub(sample_size as u64),
    ];

    let mut combined_hash: u64 = 0xcbf29ce484222325 ^ file_size;
    let mut buffer = vec![0u8; sample_size];

    for &offset in &offsets {
        if file.seek(SeekFrom::Start(offset)).is_err() {
            continue;
        }
        let read_bytes = match file.read(&mut buffer) {
            Ok(n) => n,
            Err(_) => continue,
        };

        // FNV-1a 64-bit rápido por bloque
        for &byte in &buffer[..read_bytes] {
            combined_hash ^= byte as u64;
            combined_hash = combined_hash.wrapping_mul(0x100000001b3);
        }
    }

    FingerprintResult {
        success: true,
        fingerprint: Some(format!("{:016x}", combined_hash)),
        file_size,
        error: None,
    }
}
