use std::path::Path;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::process::Command;
use rayon::prelude::*;
use serde::{Deserialize, Serialize};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

/// Límite de procesos ffmpeg simultáneos para la extracción en lote de miniaturas:
/// con Rayon global se lanzaba un ffmpeg por núcleo (saturación de CPU); acotarlo
/// acelera el lote completo al no competir los procesos por los núcleos.
const THUMBNAIL_PARALLEL_LIMIT: usize = 4;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FrameExtractionRequest {
    pub video_path: String,
    pub out_path: String,
    pub timestamp: f64,
    pub width: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FrameExtractionResult {
    pub out_path: String,
    pub success: bool,
}

fn get_ffmpeg_path() -> String {
    if let Ok(exe_path) = std::env::current_exe() {
        if let Some(parent) = exe_path.parent() {
            let bundled = parent.join("FFmpeg").join("ffmpeg.exe");
            if bundled.exists() {
                return bundled.to_string_lossy().to_string();
            }
            let same_dir = parent.join("ffmpeg.exe");
            if same_dir.exists() {
                return same_dir.to_string_lossy().to_string();
            }
        }
    }
    "ffmpeg".to_string()
}

/// Extrae un fotograma individual de forma ultrarrápida usando fast-seek en FFmpeg (<15ms).
pub fn extract_frame(
    video_path: &str,
    out_path: &str,
    timestamp: f64,
    width: u32,
) -> bool {
    let video_p = Path::new(video_path);
    if !video_p.exists() {
        return false;
    }

    if let Some(parent) = Path::new(out_path).parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    let w = if width == 0 { 240 } else { width };
    let scale_filter = format!("scale={}:-2", w);
    let ts_str = format!("{:.2}", timestamp.max(0.0));
    let ffmpeg_bin = get_ffmpeg_path();

    let mut cmd = Command::new(&ffmpeg_bin);
    cmd.args([
        "-y",
        "-nostdin",
        "-loglevel", "error",
        "-ss", &ts_str,
        "-i", video_path,
        "-an",
        "-sn",
        "-dn",
        "-vframes", "1",
        "-vf", &scale_filter,
        "-threads", "0",
        "-q:v", "3",
        out_path,
    ]);

    #[cfg(windows)]
    {
        cmd.creation_flags(0x08000000 | 0x00004000);
    }

    match cmd.output() {
        Ok(out) => out.status.success() && Path::new(out_path).exists(),
        Err(_) => false,
    }
}

/// Extrae un lote de fotogramas en paralelo (pool acotado a THUMBNAIL_PARALLEL_LIMIT
/// procesos ffmpeg; con el pool global de Rayon se lanzaba uno por núcleo y los
/// procesos competían por la CPU, volviendo el lote más lento en lugar de más rápido).
pub fn extract_frames_batch(requests: Vec<FrameExtractionRequest>) -> Vec<FrameExtractionResult> {
    let extraer = |req: FrameExtractionRequest| {
        let ok = catch_unwind(AssertUnwindSafe(|| {
            extract_frame(&req.video_path, &req.out_path, req.timestamp, req.width)
        })).unwrap_or(false);

        FrameExtractionResult {
            out_path: req.out_path,
            success: ok,
        }
    };

    match rayon::ThreadPoolBuilder::new()
        .num_threads(THUMBNAIL_PARALLEL_LIMIT)
        .build()
    {
        Ok(pool) => pool.install(|| requests.into_par_iter().map(extraer).collect()),
        Err(_) => requests.into_par_iter().map(extraer).collect(),
    }
}
