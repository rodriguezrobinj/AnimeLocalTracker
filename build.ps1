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
#  (La carpeta AnimeLocalTracker\FFmpeg\ está en .gitignore: cada
#  clon/build descarga los binarios si no existen.)
# ────────────────────────────────────────────────────────────
$ffmpegDir = "$root\AnimeLocalTracker\FFmpeg"
if (-not (Test-Path "$ffmpegDir\ffmpeg.exe") -or -not (Test-Path "$ffmpegDir\ffprobe.exe")) {
    Write-Host "[build] ffmpeg/ffprobe embebidos no encontrados; descargando essentials de gyan.dev..." -ForegroundColor Yellow
    $ffmpegZip = Join-Path $env:TEMP "ffmpeg-release-essentials.zip"
    if (-not (Test-Path $ffmpegZip)) {
        Invoke-WebRequest "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" -OutFile $ffmpegZip -UseBasicParsing
    }
    $ffmpegExtract = Join-Path $env:TEMP ("ffmpeg_" + [guid]::NewGuid().ToString("N"))
    Expand-Archive $ffmpegZip $ffmpegExtract
    try {
        $ffmpegBin = Get-ChildItem $ffmpegExtract -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1 -ExpandProperty DirectoryName
        if (-not $ffmpegBin) { throw "No se pudo localizar ffmpeg.exe en el zip descargado." }
        New-Item -ItemType Directory -Path $ffmpegDir -Force | Out-Null
        Copy-Item "$ffmpegBin\ffmpeg.exe" "$ffmpegDir\" -Force
        Copy-Item "$ffmpegBin\ffprobe.exe" "$ffmpegDir\" -Force
        Write-Host "[build] ffmpeg/ffprobe embebidos listos en $ffmpegDir" -ForegroundColor Green
    }
    finally {
        Remove-Item $ffmpegExtract -Recurse -Force -ErrorAction SilentlyContinue
    }
}

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
    $appBinDir = "$root\AnimeLocalTracker\bin\$Configuration\net8.0-windows"
    $testsBinDir = "$root\AnimeLocalTracker.Tests\bin\$Configuration\net8.0-windows"

    if (Test-Path $appBinDir) {
        Copy-NativeDll $rustDllPath (Join-Path $appBinDir "animetracker_core.dll") "bin de la app"
    }
    if (Test-Path $testsBinDir) {
        Copy-NativeDll $rustDllPath (Join-Path $testsBinDir "animetracker_core.dll") "bin de tests"
    }
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
