# ============================================================
#  Build robusto para AnimeLocalTracker
#  Soluciona el flake conocido del SDK WPF: el proyecto temporal
#  *_wpftmp compila con los .g.cs del obj, y si el obj está
#  limpio el PRIMER build falla (CS2001/CS0103). La doble pasada
#  garantiza éxito siempre: pasada 1 puebla obj, pasada 2 compila.
# ============================================================

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$RunTests,
    [switch]$Coverage
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# ────────────────────────────────────────────────────────────
#  ffmpeg.exe / ffprobe.exe embebidos
#  El núcleo Rust (spritesheet.rs) y el daemon Python invocan
#  `ffmpeg`/`ffprobe` por nombre. Si no se distribuyen junto a la
#  app, miniaturas, sprite sheets y enriquecimiento fallan en
#  silencio en máquinas sin FFmpeg instalado.
#  Se usa el build "shared" (~0,8 MB los dos): cargan las DLLs de FFmpeg
#  de la misma carpeta (las del reproductor), en vez de llevar una segunda
#  copia de todos los códecs (los estáticos pesaban ~98 MB cada uno). Si
#  esas DLLs faltan (checkout limpio, CI) el script también las instala; las
#  que ya existan (equipo de desarrollo) no se tocan.
#  Versión y SHA256 fijados en tools\Get-FFmpegBinaries.ps1.
#  (La carpeta AnimeLocalTracker\FFmpeg\ está en .gitignore: cada
#  clon/build descarga los binarios si no existen.)
# ────────────────────────────────────────────────────────────
$ffmpegDir = "$root\AnimeLocalTracker\FFmpeg"
& "$root\tools\Get-FFmpegBinaries.ps1" -Destino $ffmpegDir

function Invoke-Build {
    param([string]$Label)
    Write-Host "[build] $Label..." -ForegroundColor Cyan

    # Compilación EXPLÍCITA y NO INCREMENTAL por proyecto (no `dotnet build` sobre la
    # solución): el build de la solución podía terminar con "OK" dejando el ensamblado
    # de tests AUSENTE (runner limpio, CI) u OBSOLETO (obj poblado en local) — el doble
    # pase no lo garantizaba. --no-incremental fuerza siempre el producto final real.
    # El proyecto de benchmarks se compila bajo demanda (workflow benchmarks.yml).
    & dotnet build "$root\AnimeLocalTracker\AnimeLocalTracker.csproj" -c $Configuration --nologo -v q -nodeReuse:false --no-incremental
    if ($LASTEXITCODE -ne 0) { return $false }
    & dotnet build "$root\AnimeLocalTracker.Tests\AnimeLocalTracker.Tests.csproj" -c $Configuration --nologo -v q -nodeReuse:false --no-incremental
    if ($LASTEXITCODE -ne 0) { return $false }
    return $true
}

# Pasada 1: puebla obj (puede fallar si obj estaba limpio — normal, no fatal)
Write-Host "== Pasada 1 (poblar obj) ==" -ForegroundColor Yellow
$pasada1 = Invoke-Build "pasada 1"
if (-not $pasada1) {
    Write-Host "[build] Pasada 1 falló (esperado si obj estaba limpio); reintento con pasada 2..." -ForegroundColor DarkYellow
}

# Pasada 2: compila de verdad
Write-Host "== Pasada 2 (compilación) ==" -ForegroundColor Yellow
$pasada2 = Invoke-Build "pasada 2"
if (-not $pasada2) {
    Write-Host "[build] ERROR: la pasada 2 falló." -ForegroundColor Red
    exit 1
}

# Verificación de artefactos: el build puede reportar "OK" sin generar el ensamblado de
# tests (observado en CI). Si falta, se reintenta con log DETALLADO para diagnosticar.
function Get-TestsDll {
    $candidates = @(
        (Join-Path $root "AnimeLocalTracker.Tests\bin\$Configuration\net8.0-windows10.0.26100.0\AnimeLocalTracker.Tests.dll"),
        (Join-Path $root "AnimeLocalTracker.Tests\bin\$Configuration\net8.0-windows\AnimeLocalTracker.Tests.dll")
    )
    foreach ($cand in $candidates) {
        if (Test-Path $cand) { return $cand }
    }
    return $null
}

$testsDll = Get-TestsDll
if (-not $testsDll) {
    Write-Host "[build] ERROR: no se generó el ensamblado de tests." -ForegroundColor Red
    Write-Host "[build] Reintentando con salida detallada para diagnosticar..." -ForegroundColor Yellow
    & dotnet build "$root\AnimeLocalTracker.Tests\AnimeLocalTracker.Tests.csproj" -c $Configuration --nologo -nodeReuse:false --no-incremental -v n 2>&1 | Select-Object -Last 100
    $testsDll = Get-TestsDll
    if (-not $testsDll) {
        Write-Host "[build] ERROR: el ensamblado de tests sigue sin generarse." -ForegroundColor Red
        exit 1
    }
    Write-Host "[build] AVISO: el ensamblado de tests se generó solo en el reintento detallado." -ForegroundColor DarkYellow
}

# Copiar librerías nativas si existen
# DEV-10: si la copia del DLL falla (bloqueado, disco lleno...), el build NO debe
# reportar "OK" en silencio: un exe sin el núcleo Rust rompe parse/hash/miniaturas.
function Copy-NativeDll {
    param([string]$Origen, [string]$Destino, [string]$Etiqueta)
    try {
        Copy-Item $Origen $Destino -Force
        Write-Host "[build] DLL nativo copiado -> $Etiqueta" -ForegroundColor Green
    }
    catch {
        Write-Host "[build] AVISO: no se pudo copiar animetracker_core.dll a $Etiqueta ($($_.Exception.Message))" -ForegroundColor Yellow
    }
}

# ARC-10: compilar el núcleo Rust si falta o está desactualizado respecto a sus fuentes.
# Antes se copiaba lo que hubiera en target\release sin verificar, pudiendo quedar una
# DLL stale en los builds locales (el CI sí compilaba cargo antes de copiar).
$rustDllPath = "$root\native\animetracker_core\target\release\animetracker_core.dll"
$rustNeedsBuild = -not (Test-Path $rustDllPath)
if (-not $rustNeedsBuild) {
    $rustDllTime = (Get-Item $rustDllPath).LastWriteTime
    $rustNewestSrc = Get-ChildItem "$root\native\animetracker_core\src" -Recurse -Filter "*.rs" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($rustNewestSrc -and $rustNewestSrc.LastWriteTime -gt $rustDllTime) {
        $rustNeedsBuild = $true
    }
}
if ($rustNeedsBuild) {
    Write-Host "[build] Compilando núcleo Rust (release)..." -ForegroundColor Yellow
    try {
        & cargo build --release --manifest-path "$root\native\animetracker_core\Cargo.toml"
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[build] ERROR: la compilación de animetracker_core falló." -ForegroundColor Red
            exit 1
        }
    }
    catch {
        Write-Host "[build] AVISO: cargo no disponible; se usará la DLL existente si la hay." -ForegroundColor Yellow
    }
}

if (Test-Path $rustDllPath) {
    # 1) Raíz del proyecto: la referencia el csproj (copiada a output en builds y publish de Velopack)
    Copy-NativeDll $rustDllPath "$root\AnimeLocalTracker\animetracker_core.dll" "raiz del proyecto"
    
    # 2) Binarios de app y tests si los directorios existen
    $appBinDirs = @(
        "$root\AnimeLocalTracker\bin\$Configuration\net8.0-windows",
        "$root\AnimeLocalTracker\bin\$Configuration\net8.0-windows10.0.26100.0"
    )
    $testsBinDirs = @(
        "$root\AnimeLocalTracker.Tests\bin\$Configuration\net8.0-windows",
        "$root\AnimeLocalTracker.Tests\bin\$Configuration\net8.0-windows10.0.26100.0"
    )

    foreach ($dir in $appBinDirs) {
        if (Test-Path $dir) {
            Copy-NativeDll $rustDllPath (Join-Path $dir "animetracker_core.dll") "bin de la app ($dir)"
        }
    }
    foreach ($dir in $testsBinDirs) {
        if (Test-Path $dir) {
            Copy-NativeDll $rustDllPath (Join-Path $dir "animetracker_core.dll") "bin de tests ($dir)"
        }
    }
}

# Sincronizar salida hacia net8.0-windows para compatibilidad con Rider y atajos existentes
$sdkAppDir = "$root\AnimeLocalTracker\bin\$Configuration\net8.0-windows10.0.26100.0"
$legacyAppDir = "$root\AnimeLocalTracker\bin\$Configuration\net8.0-windows"
if (Test-Path $sdkAppDir) {
    if (-not (Test-Path $legacyAppDir)) { New-Item -ItemType Directory -Path $legacyAppDir -Force | Out-Null }
    Copy-Item "$sdkAppDir\*" $legacyAppDir -Recurse -Force
}

$sdkTestsDir = "$root\AnimeLocalTracker.Tests\bin\$Configuration\net8.0-windows10.0.26100.0"
$legacyTestsDir = "$root\AnimeLocalTracker.Tests\bin\$Configuration\net8.0-windows"
if (Test-Path $sdkTestsDir) {
    if (-not (Test-Path $legacyTestsDir)) { New-Item -ItemType Directory -Path $legacyTestsDir -Force | Out-Null }
    Copy-Item "$sdkTestsDir\*" $legacyTestsDir -Recurse -Force
}

Write-Host "[build] OK ($Configuration)" -ForegroundColor Green

if ($RunTests) {
    Write-Host "== Tests ==" -ForegroundColor Yellow
    # --no-build: reutiliza los binarios de la pasada 2. Evita que VSTest recompile
    # el proyecto principal (WPF) con un graph distinto → BG1002/CS2001 intermitente.
    $testArgs = @("test", "$root\AnimeLocalTracker.Tests", "-c", $Configuration, "--no-build", "--nologo", "-v", "q", "-nodeReuse:false")
    if ($Coverage) {
        Write-Host "[tests] Recolectando cobertura de código (coverlet)..." -ForegroundColor Cyan
        # coverlet.runsettings excluye el ensamblado de tests del cálculo (DEV-06b)
        $testArgs += @("--collect:XPlat Code Coverage", "--settings", "$root\AnimeLocalTracker.Tests\coverlet.runsettings", "--logger:trx;LogFileName=tests.trx")
    }
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[tests] ERROR: algunos tests fallaron." -ForegroundColor Red
        exit 1
    }
    Write-Host "[tests] OK" -ForegroundColor Green
}
