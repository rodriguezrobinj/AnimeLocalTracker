param(
    [Parameter(Mandatory=$false)]
    [string]$Version = "",

    [Parameter(Mandatory=$false)]
    [string]$Channel = "win",

    [Parameter(Mandatory=$false)]
    [string]$SignTemplate = ""
)

# DEV-09: sin -Version ya no existe un default silencioso ("1.0.0") que pueda pisar
# releases reales. Se lee la versión del csproj; si tampoco está, se aborta.
if (-not $Version) {
    $csproj = Get-Content "$PSScriptRoot\AnimeLocalTracker\AnimeLocalTracker.csproj" -Raw
    $match = [regex]::Match($csproj, '<Version>([^<]+)</Version>')
    if ($match.Success) {
        $Version = $match.Groups[1].Value.Trim()
        Write-Host "Versión leída del csproj: $Version" -ForegroundColor Yellow
    }
    else {
        Write-Error "No se especificó -Version y el csproj no define <Version>. Abortando para no generar un paquete con versión arbitraria."
        exit 1
    }
}

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "  AnimeLocalTracker - Generador de Release (Velopack)" -ForegroundColor Cyan
Write-Host "  Versión: $Version" -ForegroundColor Yellow
Write-Host "=================================================" -ForegroundColor Cyan

# 1. Asegurar herramienta vpk instalada globalmente (versión fijada = reproducible)
Write-Host "`n[1/5] Verificando herramienta vpk (Velopack CLI)..." -ForegroundColor Green
$vpkInstalled = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpkInstalled) {
    Write-Host "Instalando vpk 1.2.0 globalmente con dotnet tool..." -ForegroundColor Yellow
    dotnet tool install -g vpk --version 1.2.0
} else {
    Write-Host "vpk encontrado: $($vpkInstalled.Source)" -ForegroundColor Gray
}

# 2. Compilar y publicar la aplicación WPF en modo Release SingleFile / Framework-Dependent
Write-Host "`n[2/5] Publicando binarios de la aplicación..." -ForegroundColor Green
$publishDir = "$PSScriptRoot\AnimeLocalTracker\bin\Release\net8.0-windows\win-x64\publish"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

# 2.1 Compilar el motor Python (AnimeTrackerTools.exe) si el codigo cambio
Write-Host "[2/5] Compilando motor Python (PyInstaller)..." -ForegroundColor Green
$vtoolsExe = "$PSScriptRoot\AnimeLocalTracker\Tools\AnimeTrackerTools.exe"
$pythonChanged = $false
if (Test-Path $vtoolsExe) {
    $lastPyWrite = (Get-ChildItem "$PSScriptRoot\tools\python" -Recurse -Include *.py,pyproject.toml -File |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
    if ($lastPyWrite -gt (Get-Item $vtoolsExe).LastWriteTime) { $pythonChanged = $true }
} else {
    $pythonChanged = $true
}

if ($pythonChanged) {
    Write-Host "  El codigo Python es mas reciente o el binario no existe. Compilando..." -ForegroundColor Yellow
    try {
        # Asegurar dependencias PyPI antes de PyInstaller (sin ellas el exe queda sin modulos)
        & python -m pip install -q pyinstaller anitopy rapidfuzz "yt-dlp==2026.8.19" pydantic "opencv-python-headless>=4.9.0" numpy 2>&1 | Out-Host
        & python "$PSScriptRoot\tools\python\build_binary.py" 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $vtoolsExe)) {
            Write-Host "  PyInstaller fallo; se usara el binario existente si hay." -ForegroundColor Red
        }
    } catch {
        Write-Host "  Python no disponible; se usara el binario existente." -ForegroundColor Red
    }
} else {
    Write-Host "  El motor Python no cambio: se reutiliza el binario existente." -ForegroundColor Gray
}
dotnet publish "$PSScriptRoot\AnimeLocalTracker\AnimeLocalTracker.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    # Reintento: el flake conocido del SDK WPF (*_wpftmp con obj limpio) puede fallar
    # en la PRIMERA publicación de un runner limpio (CS2001 por .g.cs ausentes).
    Write-Host "  Publish falló en el primer intento (posible flake wpftmp); reintentando..." -ForegroundColor Yellow
    dotnet publish "$PSScriptRoot\AnimeLocalTracker\AnimeLocalTracker.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained false `
        -o $publishDir
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "Fallo en la publicación de dotnet publish."
    exit $LASTEXITCODE
}

# SEC-05: firma de código. El CI prepara el certificado en el runner y lo expone como
# SIGN_CERT_PATH/SIGN_CERT_PASSWORD; si no hay certificado, el paquete sale sin firmar.
if (-not $SignTemplate -and $env:SIGN_CERT_PATH) {
    $SignTemplate = "signtool sign /fd SHA256 /f `"$env:SIGN_CERT_PATH`" /p `"$env:SIGN_CERT_PASSWORD`" `$file"
}
if ($SignTemplate) {
    Write-Host "Firma de código ACTIVADA (template con signtool)" -ForegroundColor Green
} else {
    Write-Host "Sin certificado: la release se genera SIN firma (SEC-05 pendiente)." -ForegroundColor Yellow
}

# 3. Empaquetar con Velopack (vpk)
Write-Host "`n[3/5] Creando instalador y paquetes delta con vpk..." -ForegroundColor Green
$releasesDir = "$PSScriptRoot\Releases"
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

$vpkArgs = @(
    "pack",
    "--packId", "AnimeLocalTracker",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--packAuthors", "Robin Rodriguez",
    "--packTitle", "AnimeLocalTracker",
    "--mainExe", "AnimeLocalTracker.exe",
    "--outputDir", $releasesDir,
    "--channel", $Channel
)
if ($SignTemplate) {
    $vpkArgs += @("--signTemplate", $SignTemplate)
}

vpk @vpkArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Fallo al empaquetar con Velopack."
    exit $LASTEXITCODE
}

Write-Host "`n[4/5] ¡Paquete generado con éxito en el directorio Releases/!" -ForegroundColor Green
Write-Host "Archivos creados:" -ForegroundColor Yellow
Get-ChildItem -Path $releasesDir | ForEach-Object { Write-Host "  - $($_.Name) ($([math]::Round($_.Length / 1MB, 2)) MB)" }

Write-Host "`nPara publicar este release en GitHub:" -ForegroundColor Cyan
Write-Host "1. Crea un nuevo Release con tag 'v$Version' en https://github.com/rodriguezrobinj/AnimeLocalTracker/releases/new"
Write-Host "2. Adjunta todos los archivos de la carpeta '$releasesDir' al Release."
Write-Host "3. ¡Las aplicaciones cliente instaladas se actualizarán automáticamente en segundo plano!"
