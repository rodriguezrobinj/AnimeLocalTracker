<#
.SYNOPSIS
    Deja en la carpeta FFmpeg de la app un conjunto COMPLETO y coherente de FFmpeg (build "shared" de gyan.dev):
    ffmpeg.exe, ffprobe.exe y las DLLs de las que dependen.

.DESCRIPTION
    Las DLLs de FFmpeg (avcodec-63.dll, avformat-63.dll...) las usa el reproductor (Flyleaf, FFmpegPath=":FFmpeg") y,
    desde que los ejecutables son "shared", también ffmpeg.exe/ffprobe.exe (núcleo Rust: miniaturas; daemon Python:
    yt-dlp, escenas, metadatos; VideoIntegrityService). OJO: el paquete NuGet Flyleaf.FFmpeg NO trae esas DLLs (solo
    un ensamblado .NET de 0,5 MB); la carpeta AnimeLocalTracker\FFmpeg\ está en .gitignore y, en un equipo de
    desarrollo, las DLLs las coloca el desarrollador a mano. Este script cubre el resto de casos (checkout limpio,
    CI de GitHub) para que ffmpeg.exe/ffprobe.exe nunca queden sin sus DLLs.

    Comportamiento:
      * ffmpeg.exe y ffprobe.exe: se instalan siempre que no sean ya los "shared" (~0,6 MB y ~0,2 MB; los estáticos
        antiguos pesaban ~98 MB cada uno con otra copia completa de los códecs -> ~70 MB menos de instalador).
      * DLLs: se instalan SOLO si faltan. Nunca se sobrescriben las que ya haya (p. ej. las del reproductor en un PC
        de desarrollo); con -Forzar sí se reemplazan.

    REGLA DE COMPATIBILIDAD: los exe y las DLLs deben compartir la versión MAYOR de cada biblioteca (avcodec-63,
    avformat-63, avutil-61...). La versión de FFmpeg está FIJADA aquí (antes se bajaba "la última release" sin control)
    y se comprueba que las DLLs ya presentes en el destino sean de la misma versión mayor. Si se cambia de FFmpeg,
    subir $Version/$Sha256/$Dlls a la release de gyan.dev cuyo `ffmpeg -version` reporte las mismas versiones mayores.

.PARAMETER Destino
    Carpeta de destino (AnimeLocalTracker\FFmpeg).

.PARAMETER Forzar
    Vuelve a descargar/copiar todo, incluidas las DLLs, aunque ya existan.
#>
param(
    [Parameter(Mandatory)] [string]$Destino,
    [switch]$Forzar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest es mucho más rápido sin la barra de progreso

# ── Versión fijada (ver REGLA DE COMPATIBILIDAD arriba) ──
$Version = '9.0.1'
$Sha256  = 'cb4d5e8db6a3353bffdb2100d3eb4b76733457fa443215e236f57c99f9ffdca4'   # publicado en https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.1-full_build-shared.7z.sha256
$Url     = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-$Version-full_build-shared.7z"
$Exes    = @('ffmpeg.exe', 'ffprobe.exe')
$Dlls    = @('avcodec-63.dll', 'avdevice-63.dll', 'avfilter-12.dll', 'avformat-63.dll', 'avutil-61.dll', 'swresample-7.dll', 'swscale-10.dll')

# Los exe estáticos antiguos pesan ~98 MB; los "shared", menos de 1 MB.
function Test-EsShared([string]$ruta) {
    return (Test-Path $ruta) -and ((Get-Item $ruta).Length -lt 5MB)
}

# ── Comprobación de compatibilidad con las DLLs que YA haya en el destino ──
$avcodecExistentes = @(Get-ChildItem $Destino -Filter 'avcodec-*.dll' -ErrorAction SilentlyContinue)
if ($avcodecExistentes.Count -gt 0 -and ($avcodecExistentes.Name -notcontains 'avcodec-63.dll')) {
    throw ("En '$Destino' hay '$($avcodecExistentes[0].Name)', pero los ffmpeg.exe/ffprobe.exe fijados en este script " +
           "(FFmpeg $Version) esperan 'avcodec-63.dll'. Sube `$Version/`$Sha256/`$Dlls a una release de gyan.dev " +
           "(build full_build-shared) cuyo `ffmpeg -version` reporte las mismas versiones mayores que tus DLLs.")
}

$exesOk = -not $Forzar -and (($Exes | Where-Object { -not (Test-EsShared "$Destino\$_") }).Count -eq 0)
$dllsFaltan = @($Dlls | Where-Object { $Forzar -or -not (Test-Path "$Destino\$_") })
if ($exesOk -and $dllsFaltan.Count -eq 0) {
    return
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

# ── Extraer solo los archivos necesarios (los exe y las DLLs) ──
$tmp = Join-Path $env:TEMP ('ffmpeg_shared_' + [guid]::NewGuid().ToString('N'))
try {
    & $sevenZip e $archivo "-o$tmp" -r @($Exes + $Dlls) -y -bso0 -bsp0
    if ($LASTEXITCODE -ne 0) { throw "7-Zip falló al extraer FFmpeg (código $LASTEXITCODE)." }

    foreach ($nombre in ($Exes + $Dlls)) {
        if (-not (Test-Path "$tmp\$nombre")) { throw "No se encontró $nombre en el paquete descargado." }
    }

    New-Item -ItemType Directory -Path $Destino -Force | Out-Null

    foreach ($nombre in $Exes) {
        Copy-Item "$tmp\$nombre" "$Destino\$nombre" -Force
        # MSBuild copia a bin\ solo si el origen es MÁS NUEVO: los exe de gyan son más antiguos que los
        # estáticos que ya pueda haber en bin\, así que se sella la fecha para que los sustituyan.
        (Get-Item "$Destino\$nombre").LastWriteTime = Get-Date
    }

    foreach ($nombre in $dllsFaltan) {
        Copy-Item "$tmp\$nombre" "$Destino\$nombre" -Force
        (Get-Item "$Destino\$nombre").LastWriteTime = Get-Date
    }

    $tag = if ($dllsFaltan.Count -gt 0) { "y $($dllsFaltan.Count) DLL(s) que faltaban" } else { "(las DLLs existentes se respetan)" }
    Write-Host "[ffmpeg] ffmpeg.exe/ffprobe.exe (shared $Version) $tag listos en $Destino" -ForegroundColor Green
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
