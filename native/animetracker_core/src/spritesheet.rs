use std::io::Cursor;
use std::path::Path;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::process::Command;
use image::{imageops, RgbImage};
use rayon::prelude::*;
use serde::{Deserialize, Serialize};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

/// Número máximo de procesos ffmpeg simultáneos para spritesheet.
const FFMPEG_PARALLEL_LIMIT: usize = 2;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SpritesheetResult {
    pub success: bool,
    pub spritesheet_path: Option<String>,
    pub columns: u32,
    pub rows: u32,
    pub thumb_width: u32,
    pub thumb_height: u32,
    pub total_thumbs: u32,
    pub interval_seconds: f64,
    pub error: Option<String>,
}

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
        "-threads", "1",
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

/// Extrae un lote de fotogramas en paralelo utilizando todos los núcleos de la CPU con Rayon (<50ms).
pub fn extract_frames_batch(requests: Vec<FrameExtractionRequest>) -> Vec<FrameExtractionResult> {
    requests
        .into_par_iter()
        .map(|req| {
            let ok = catch_unwind(AssertUnwindSafe(|| {
                extract_frame(&req.video_path, &req.out_path, req.timestamp, req.width)
            })).unwrap_or(false);

            FrameExtractionResult {
                out_path: req.out_path,
                success: ok,
            }
        })
        .collect()
}

/// Genera una tira completa (Sprite Sheet mosaico) extrayendo fotogramas clave en paralelo con Rayon (<1s).
pub fn generate_spritesheet(
    video_path: &str,
    out_path: &str,
    total_seconds: f64,
    count: u32,
) -> SpritesheetResult {
    let video_p = Path::new(video_path);
    if !video_p.exists() {
        return SpritesheetResult {
            success: false,
            spritesheet_path: None,
            columns: 0,
            rows: 0,
            thumb_width: 0,
            thumb_height: 0,
            total_thumbs: 0,
            interval_seconds: 0.0,
            error: Some("El archivo de video no existe".to_string()),
        };
    }

    let target_count = if count == 0 { 60 } else { count.clamp(1, 1000) };
    let cols = 10u32;
    let rows = (target_count + cols - 1) / cols;
    let total_thumbs = cols * rows;

    let dur = if total_seconds <= 0.0 { 1440.0 } else { total_seconds };
    let interval = dur / total_thumbs as f64;
    let thumb_w = 160u32;
    let thumb_h = 90u32;

    let sheet_w = cols * thumb_w;
    let sheet_h = rows * thumb_h;
    let total_pixels = sheet_w as u64 * sheet_h as u64;
    if total_pixels > 64_000_000 {
        return SpritesheetResult {
            success: false,
            path: String::new(),
            cols,
            rows,
            thumb_width: thumb_w,
            thumb_height: thumb_h,
            total_thumbs: 0,
            interval_seconds: 0.0,
            error: Some("Dimensiones de spritesheet exceden el límite de seguridad".to_string()),
        };
    }

    if let Some(parent) = Path::new(out_path).parent() {
        let _ = std::fs::create_dir_all(parent);
    }

    let indices: Vec<u32> = (0..total_thumbs).collect();
    let extraer = |idx: u32| -> (u32, Option<RgbImage>) {
        let frame = catch_unwind(AssertUnwindSafe(|| {
            let ts = (idx as f64) * interval;
            let ts_str = format!("{:.2}", ts);
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
                "-vf", "scale=160:90",
                "-threads", "1",
                "-f", "image2pipe",
                "-vcodec", "mjpeg",
                "-q:v", "4",
                "-",
            ]);

            #[cfg(windows)]
            {
                cmd.creation_flags(0x08000000 | 0x00004000);
            }

            match cmd.output() {
                Ok(out) if out.status.success() && !out.stdout.is_empty() => {
                    let cursor = Cursor::new(out.stdout);
                    match image::load(cursor, image::ImageFormat::Jpeg) {
                        Ok(dyn_img) => Some(dyn_img.to_rgb8()),
                        Err(_) => None,
                    }
                }
                _ => None,
            }
        }));
        (idx, frame.unwrap_or(None))
    };

    let raw_frames: Vec<(u32, Option<RgbImage>)> = match rayon::ThreadPoolBuilder::new()
        .num_threads(FFMPEG_PARALLEL_LIMIT)
        .build()
    {
        Ok(pool) => pool.install(|| indices.par_iter().map(|&idx| extraer(idx)).collect()),
        Err(_) => indices.par_iter().map(|&idx| extraer(idx)).collect(),
    };

    let mut sheet = RgbImage::new(sheet_w, sheet_h);

    let mut successful_frames = 0;
    for (idx, frame_opt) in raw_frames {
        if let Some(frame) = frame_opt {
            let col = idx % cols;
            let row = idx / cols;
            let x = (col * thumb_w) as i64;
            let y = (row * thumb_h) as i64;
            imageops::overlay(&mut sheet, &frame, x, y);
            successful_frames += 1;
        }
    }

    if successful_frames == 0 {
        return SpritesheetResult {
            success: false,
            spritesheet_path: None,
            columns: 0,
            rows: 0,
            thumb_width: 0,
            thumb_height: 0,
            total_thumbs: 0,
            interval_seconds: 0.0,
            error: Some("No se pudo extraer ningún fotograma del video".to_string()),
        };
    }

    match sheet.save(out_path) {
        Ok(_) => SpritesheetResult {
            success: true,
            spritesheet_path: Some(out_path.to_string()),
            columns: cols,
            rows,
            thumb_width: thumb_w,
            thumb_height: thumb_h,
            total_thumbs,
            interval_seconds: interval,
            error: None,
        },
        Err(e) => SpritesheetResult {
            success: false,
            spritesheet_path: None,
            columns: 0,
            rows: 0,
            thumb_width: 0,
            thumb_height: 0,
            total_thumbs: 0,
            interval_seconds: 0.0,
            error: Some(e.to_string()),
        },
    }
}
