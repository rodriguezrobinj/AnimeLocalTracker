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
    [switch]$RunTests
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Invoke-Build {
    param([string]$Label)
    Write-Host "[build] $Label..." -ForegroundColor Cyan
    & dotnet build "$root\AnimeLocalTracker.sln" -c $Configuration --nologo -v q -nodeReuse:false
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
if (Test-Path "$root\native\animetracker_core\target\release\animetracker_core.dll") {
    Copy-Item "$root\native\animetracker_core\target\release\animetracker_core.dll" "$root\AnimeLocalTracker\bin\$Configuration\net8.0-windows\" -Force -ErrorAction SilentlyContinue
    Copy-Item "$root\native\animetracker_core\target\release\animetracker_core.dll" "$root\AnimeLocalTracker.Tests\bin\$Configuration\net8.0-windows\" -Force -ErrorAction SilentlyContinue
}

Write-Host "[build] OK ($Configuration)" -ForegroundColor Green

if ($RunTests) {
    Write-Host "== Tests ==" -ForegroundColor Yellow
    # --no-build: reutiliza los binarios de la pasada 2. Evita que VSTest recompile
    # el proyecto principal (WPF) con un graph distinto → BG1002/CS2001 intermitente.
    & dotnet test "$root\AnimeLocalTracker.Tests" -c $Configuration --no-build --nologo -v q -nodeReuse:false
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[tests] ERROR: algunos tests fallaron." -ForegroundColor Red
        exit 1
    }
    Write-Host "[tests] OK" -ForegroundColor Green
}
