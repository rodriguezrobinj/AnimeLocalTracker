<#
.SYNOPSIS
    Obtiene ffmpeg.exe y ffprobe.exe (build "shared" de gyan.dev) para embeberlos en la app.

.DESCRIPTION
    La app ya distribuye las DLLs de FFmpeg (paquete NuGet Flyleaf.FFmpeg, usadas por el reproductor).
    Los ejecutables ffmpeg.exe/ffprobe.exe (núcleo Rust: miniaturas; daemon Python: yt-dlp, escenas,
    metadatos; VideoIntegrityService) antes eran builds ESTÁTICOS de ~98 MB cada uno, que llevan
    dentro una segunda copia completa de todos los códecs. Con el build "shared" pesan ~0,6 MB y
    ~0,2 MB porque cargan las DLLs que ya viajan en la misma carpeta -> el instalador baja unos
    72 MB comprimidos (~195 MB en disco), sin cambiar cómo se ven miniaturas ni cómo se analizan
    los videos (verificado: ffprobe, audio y detección de escenas idénticos bit a bit; JPEG idénticos
    píxel a píxel).

    REGLA DE COMPATIBILIDAD: el exe "shared" y las DLLs deben compartir la versión MAYOR de cada
    biblioteca (avcodec-63, avformat-63, avutil-61...). Por eso la versión de FFmpeg está FIJADA
    aquí (antes se bajaba "la última release" sin control). Si se actualiza Flyleaf.FFmpeg a una
    versión con otras bibliotecas (p. ej. avcodec-64), hay que subir $Version/$Sha256 a la release
    de gyan.dev cuyo `ffmpeg -version` reporte las mismas versiones mayores.

.PARAMETER Destino
    Carpeta donde dejar ffmpeg.exe y ffprobe.exe (AnimeLocalTracker\FFmpeg).

.PARAMETER Forzar
    Vuelve a descargar/copiar aunque ya existan los exe "shared".
#>
param(
    [Parameter(Mandatory)] [string]$Destino,
    [switch]$Forzar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest es mucho más rápido sin la barra de progreso

# ── Versión fijada (ver REGLA DE COMPATIBILIDAD arriba) ──
$Version            = '9.0.1'
$Sha256             = 'cb4d5e8db6a3353bffdb2100d3eb4b76733457fa443215e236f57c99f9ffdca4'   # publicado en https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.1-full_build-shared.7z.sha256
$AvcodecEsperada    = 'avcodec-63.dll'
$Url                = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-$Version-full_build-shared.7z"

# Los exe estáticos antiguos pesan ~98 MB; los "shared", menos de 1 MB.
function Test-EsShared([string]$ruta) {
    return (Test-Path $ruta) -and ((Get-Item $ruta).Length -lt 5MB)
}

if (-not $Forzar -and (Test-EsShared "$Destino\ffmpeg.exe") -and (Test-EsShared "$Destino\ffprobe.exe")) {
    return
}

# ── Comprobación previa: las DLLs del paquete de Flyleaf deben ser de la misma versión mayor ──
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
$flyleaf = Join-Path $nugetRoot 'flyleaf.ffmpeg'
if (Test-Path $flyleaf) {
    $dllFlyleaf = Get-ChildItem $flyleaf -Recurse -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($dllFlyleaf -and $dllFlyleaf.Name -ne $AvcodecEsperada) {
        throw ("Flyleaf.FFmpeg trae '$($dllFlyleaf.Name)' pero los ffmpeg.exe/ffprobe.exe fijados en tools\Get-FFmpegBinaries.ps1 " +
               "(FFmpeg $Version) esperan '$AvcodecEsperada'. Sube `$Version/`$Sha256/`$AvcodecEsperada a una release de gyan.dev " +
               "(build full_build-shared) cuyo `ffmpeg -version` reporte las mismas versiones mayores que esas DLLs.")
    }
}

# ── 7-Zip (los builds shared de gyan.dev solo se publican en .7z) ──
$sevenZip = @(
    (Get-Command 7z -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "$env:ProgramFiles\7-Zip\7z.exe",
    "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sevenZip) {
    throw "Se necesita 7-Zip para extraer FFmpeg (los runners de GitHub Actions ya lo traen). Instálalo con: winget install 7zip.7zip"
}

# ── Descarga (con caché en %TEMP%) y verificación del hash ──
$archivo = Join-Path $env:TEMP "ffmpeg-$Version-full_build-shared.7z"
function Test-HashOk([string]$ruta) {
    return (Test-Path $ruta) -and ((Get-FileHash $ruta -Algorithm SHA256).Hash -ieq $Sha256)
}
if (-not (Test-HashOk $archivo)) {
    Write-Host "[ffmpeg] Descargando FFmpeg $Version (shared) de gyan.dev..." -ForegroundColor Yellow
    Invoke-WebRequest $Url -OutFile $archivo -UseBasicParsing
    if (-not (Test-HashOk $archivo)) {
        Remove-Item $archivo -Force -ErrorAction SilentlyContinue
        throw "El SHA256 de FFmpeg $Version descargado no coincide con el fijado en el script. Descarga abortada por seguridad."
    }
}

# ── Extraer solo ffmpeg.exe y ffprobe.exe ──
$tmp = Join-Path $env:TEMP ('ffmpeg_shared_' + [guid]::NewGuid().ToString('N'))
try {
    & $sevenZip e $archivo "-o$tmp" -r ffmpeg.exe ffprobe.exe -y -bso0 -bsp0
    if ($LASTEXITCODE -ne 0) { throw "7-Zip falló al extraer FFmpeg (código $LASTEXITCODE)." }

    foreach ($nombre in 'ffmpeg.exe', 'ffprobe.exe') {
        if (-not (Test-Path "$tmp\$nombre")) { throw "No se encontró $nombre en el paquete descargado." }
    }

    New-Item -ItemType Directory -Path $Destino -Force | Out-Null
    foreach ($nombre in 'ffmpeg.exe', 'ffprobe.exe') {
        Copy-Item "$tmp\$nombre" "$Destino\$nombre" -Force
        # MSBuild copia a bin\ solo si el origen es MÁS NUEVO: los exe de gyan son más antiguos que los
        # estáticos que ya pueda haber en bin\, así que se sella la fecha para que los sustituyan.
        (Get-Item "$Destino\$nombre").LastWriteTime = Get-Date
    }
    Write-Host "[ffmpeg] ffmpeg.exe/ffprobe.exe (shared $Version) listos en $Destino" -ForegroundColor Green
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
