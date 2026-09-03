# Script de Ejecución de Tests, Benchmarks e Historial Comparativo
param(
    [ValidateSet("all", "tests", "benchmarks", "history")]
    [string]$Target = "all",
    [string]$BenchmarkCategory = "1" # 1: Reproductor, 2: Database, 3: FileScanner, 4: All
)

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " ANIME LOCAL TRACKER - SUITE DE RENDIMIENTO E HISTORIAL   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$historyDir = Join-Path $PSScriptRoot "BenchmarkHistory"
if (-not $env:ANIMELOCALTRACKER_BENCH_HISTORY) {
    # OPS-01: fijar la ruta del historial para el proceso de benchmarks (los reportes y el
    # JSON comparativo viven en la raíz del repo, no en bin\Release de la app).
    $env:ANIMELOCALTRACKER_BENCH_HISTORY = $historyDir
}
if (!(Test-Path $historyDir)) {
    New-Item -ItemType Directory -Path $historyDir -Force | Out-Null
}

if ($Target -eq "all" -or $Target -eq "tests") {
    Write-Host "`n[1/2] Ejecutando Tests Unitarios y Pruebas de Estrés..." -ForegroundColor Yellow
    $testResultFile = Join-Path $historyDir "test_run_$(Get-Date -Format 'yyyyMMdd_HHmmss').trx"
    dotnet test --logger "trx;LogFileName=$testResultFile" --verbosity normal
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Todas las pruebas se ejecutaron correctamente y se registraron en: $testResultFile" -ForegroundColor Green
    } else {
        Write-Host "Ocurrieron errores en las pruebas." -ForegroundColor Red
    }
}

if ($Target -eq "all" -or $Target -eq "benchmarks") {
    Write-Host "`n[2/2] Ejecutando Benchmarks en modo Release..." -ForegroundColor Yellow
    dotnet run --project "AnimeLocalTracker.Benchmarks\AnimeLocalTracker.Benchmarks.csproj" -c Release -- $BenchmarkCategory
}

if ($Target -eq "history") {
    Write-Host "`nHistorial de reportes disponibles en: $historyDir" -ForegroundColor Cyan
    Get-ChildItem -Path $historyDir -Filter "*.md" | ForEach-Object {
        Write-Host "  - $($_.Name)" -ForegroundColor Green
    }
}

Write-Host "`nProceso completado exitosamente." -ForegroundColor Green
