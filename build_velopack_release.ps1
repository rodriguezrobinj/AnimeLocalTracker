param(
    [Parameter(Mandatory=$false)]
    [string]$Version = "1.0.0",

    [Parameter(Mandatory=$false)]
    [string]$Channel = "win"
)

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "  AnimeLocalTracker - Generador de Release (Velopack)" -ForegroundColor Cyan
Write-Host "  Versión: $Version" -ForegroundColor Yellow
Write-Host "=================================================" -ForegroundColor Cyan

# 1. Asegurar herramienta vpk instalada globalmente
Write-Host "`n[1/4] Verificando herramienta vpk (Velopack CLI)..." -ForegroundColor Green
$vpkInstalled = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpkInstalled) {
    Write-Host "Instalando vpk globalmente con dotnet tool..." -ForegroundColor Yellow
    dotnet tool install -g vpk
} else {
    Write-Host "vpk encontrado: $($vpkInstalled.Source)" -ForegroundColor Gray
}

# 2. Compilar y publicar la aplicación WPF en modo Release SingleFile / Framework-Dependent
Write-Host "`n[2/4] Publicando binarios de la aplicación..." -ForegroundColor Green
$publishDir = "$PSScriptRoot\AnimeLocalTracker\bin\Release\net8.0-windows\win-x64\publish"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

dotnet publish "$PSScriptRoot\AnimeLocalTracker\AnimeLocalTracker.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Fallo en la publicación de dotnet publish."
    exit $LASTEXITCODE
}

# 3. Empaquetar con Velopack (vpk)
Write-Host "`n[3/4] Creando instalador y paquetes delta con vpk..." -ForegroundColor Green
$releasesDir = "$PSScriptRoot\Releases"
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

vpk pack `
    --packId "AnimeLocalTracker" `
    --packVersion $Version `
    --packDir $publishDir `
    --packAuthors "Robin Rodriguez" `
    --packTitle "AnimeLocalTracker" `
    --mainExe "AnimeLocalTracker.exe" `
    --outputDir $releasesDir `
    --channel $Channel

if ($LASTEXITCODE -ne 0) {
    Write-Error "Fallo al empaquetar con Velopack."
    exit $LASTEXITCODE
}

Write-Host "`n[4/4] ¡Paquete generado con éxito en el directorio Releases/!" -ForegroundColor Green
Write-Host "Archivos creados:" -ForegroundColor Yellow
Get-ChildItem -Path $releasesDir | ForEach-Object { Write-Host "  - $($_.Name) ($([math]::Round($_.Length / 1MB, 2)) MB)" }

Write-Host "`nPara publicar este release en GitHub:" -ForegroundColor Cyan
Write-Host "1. Crea un nuevo Release con tag 'v$Version' en https://github.com/rodriguezrobinj/AnimeLocalTracker/releases/new"
Write-Host "2. Adjunta todos los archivos de la carpeta '$releasesDir' al Release."
Write-Host "3. ¡Las aplicaciones cliente instaladas se actualizarán automáticamente en segundo plano!"
