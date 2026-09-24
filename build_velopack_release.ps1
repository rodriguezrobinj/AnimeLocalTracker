param(
    [Parameter(Mandatory=$false)]
    [string]$Version = "",

    [Parameter(Mandatory=$false)]
    [string]$Channel = "win",

    # Firma con signtool (certificado en archivo .pfx, p. ej. un OV o el de Azure Artifact Signing vía signtool):
    # parámetros de signtool SIN el archivo (vpk lo añade al final). Si se define SIGN_CERT_PATH (lo prepara el CI) se
    # genera solo. Para firmadores en la nube que no usan signtool, usar -SignTemplate con el marcador {{file}}.
    [Parameter(Mandatory=$false)]
    [string]$SignParams = "",

    [Parameter(Mandatory=$false)]
    [string]$SignTemplate = "",

    # Carpeta de salida de los paquetes (por defecto Releases\ del repo). Útil para pruebas sin tocar Releases\.
    [Parameter(Mandatory=$false)]
    [string]$ReleasesDir = "",

    # Requisitos que el instalador (Setup.exe) descarga e instala si faltan en el PC del usuario:
    #  - net8-x64-desktop     -> .NET 8 Desktop Runtime (la app se publica --self-contained false; sin
    #                            él, un Windows recién instalado no puede ni abrir la app).
    #  - vcredist143-x64      -> Visual C++ Redistributable 2015-2022: animetracker_core.dll (núcleo Rust)
    #                            importa vcruntime140.dll; sin él el núcleo nativo no carga (parseo de
    #                            nombres, huellas, miniaturas). Ninguna otra DLL nativa de la app lo usa.
    # Velopack también los instala al ACTUALIZAR si una versión nueva sube el requisito. Cadena vacía = no declarar.
    [Parameter(Mandatory=$false)]
    [string]$Framework = "net8-x64-desktop,vcredist143-x64"
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
# 1.2.158: subido desde 1.2.0 al activar --msi (esa versión antigua tenía el banner/logo del MSI
# intercambiados y otros bugs de MSI ya corregidos río arriba; ver CHANGELOG de Velopack). Se
# compara la versión instalada (no solo si el comando existe): un runner/máquina con la 1.2.0 de
# antes se actualiza solo, en vez de quedarse silenciosamente en la versión vieja con el bug.
Write-Host "`n[1/5] Verificando herramienta vpk (Velopack CLI)..." -ForegroundColor Green
$vpkVersionRequerida = "1.2.158"
$vpkInstaladaLinea = dotnet tool list -g 2>$null | Select-String "^vpk\s"
$vpkInstaladaVersion = if ($vpkInstaladaLinea) { ($vpkInstaladaLinea -split '\s+')[1] } else { $null }
if ($vpkInstaladaVersion -eq $vpkVersionRequerida) {
    Write-Host "vpk $vpkVersionRequerida ya instalado." -ForegroundColor Gray
} elseif ($vpkInstaladaVersion) {
    Write-Host "vpk $vpkInstaladaVersion instalado, actualizando a $vpkVersionRequerida..." -ForegroundColor Yellow
    dotnet tool update -g vpk --version $vpkVersionRequerida
} else {
    Write-Host "Instalando vpk $vpkVersionRequerida globalmente con dotnet tool..." -ForegroundColor Yellow
    dotnet tool install -g vpk --version $vpkVersionRequerida
}

# 2. Compilar y publicar la aplicación WPF en modo Release SingleFile / Framework-Dependent
Write-Host "`n[2/5] Publicando binarios de la aplicación..." -ForegroundColor Green
$publishDir = "$PSScriptRoot\AnimeLocalTracker\bin\Release\net8.0-windows\win-x64\publish"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

# 2.1 Compilar el motor Python (AnimeTrackerTools.exe) si el codigo cambio
Write-Host "[2/5] Compilando motor Python (PyInstaller)..." -ForegroundColor Green
$vtoolsExe = "$PSScriptRoot\AnimeLocalTracker\Tools\AnimeTrackerTools\AnimeTrackerTools.exe"
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
        # curl_cffi 0.10.x: única línea soportada por yt-dlp para impersonar Cloudflare
        # (0.16.x rompe con AssertionError y 0.9.x no es detectada por yt-dlp)
        & python -m pip install -q pyinstaller anitopy rapidfuzz "yt-dlp==2026.8.19" pydantic "opencv-python-headless>=4.9.0" numpy "curl_cffi>=0.10,<0.11" 2>&1 | Out-Host
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
# OJO con la sintaxis (errores que tenía la versión anterior y que impedían firmar de verdad):
#  * vpk sustituye el marcador {{file}} en --signTemplate (NO "$file"); con --signParams añade el archivo solo.
#  * /tr + /td = sello de tiempo RFC 3161: sin él, las firmas dejan de ser válidas cuando caduca el certificado.
$timestampUrl = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }
if (-not $SignTemplate -and -not $SignParams -and $env:SIGN_CERT_PATH) {
    $SignParams = "/f `"$env:SIGN_CERT_PATH`" /p `"$env:SIGN_CERT_PASSWORD`" /fd SHA256 /tr $timestampUrl /td SHA256"
}
if ($SignTemplate -or $SignParams) {
    Write-Host "Firma de código ACTIVADA ($(if ($SignTemplate) { 'plantilla personalizada' } else { 'signtool con sello de tiempo' }))" -ForegroundColor Green
} else {
    Write-Host "Sin certificado: la release se genera SIN firma (SEC-05 pendiente)." -ForegroundColor Yellow
}

# 3. Empaquetar con Velopack (vpk)
Write-Host "`n[3/5] Creando instalador y paquetes delta con vpk..." -ForegroundColor Green
$releasesDir = if ($ReleasesDir) { $ReleasesDir } else { "$PSScriptRoot\Releases" }
if (-not (Test-Path $releasesDir)) {
    New-Item -ItemType Directory -Path $releasesDir | Out-Null
}

# Además del Setup.exe de siempre, se genera un .msi con asistente completo
# (bienvenida/licencia/readme/conclusión + elegir instalación por usuario o por máquina) — el
# .msi sale ADEMÁS de los artefactos habituales, no los reemplaza, y las apps ya instaladas se
# actualizan igual después vía Update.exe sin importar con cuál se instalaron.
# instLicense usa una copia temporal del LICENSE real del repo (con extensión .txt, que es lo que
# vpk sabe interpretar) para no duplicar el contenido de la licencia en dos archivos distintos.
$licenciaTemp = Join-Path ([IO.Path]::GetTempPath()) "AnimeLocalTracker_LICENSE.txt"
Copy-Item "$PSScriptRoot\LICENSE" $licenciaTemp -Force
$instalerDir = "$PSScriptRoot\installer"

$vpkArgs = @(
    "pack",
    "--packId", "AnimeLocalTracker",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--packAuthors", "Robin Rodriguez",
    "--packTitle", "AnimeLocalTracker",
    "--mainExe", "AnimeLocalTracker.exe",
    "--outputDir", $releasesDir,
    "--channel", $Channel,
    "--msi",
    "--instWelcome", "$instalerDir\msi_welcome.txt",
    "--instLicense", $licenciaTemp,
    "--instReadme", "$instalerDir\msi_readme.txt",
    "--instConclusion", "$instalerDir\msi_conclusion.txt",
    "--msiTopBanner", "$instalerDir\msi_banner.bmp",
    "--msiDialogBackground", "$instalerDir\msi_logo.bmp"
)
if ($SignTemplate) {
    $vpkArgs += @("--signTemplate", $SignTemplate)
} elseif ($SignParams) {
    $vpkArgs += @("--signParams", $SignParams)
}
if ($Framework) {
    $vpkArgs += @("--framework", $Framework)
    Write-Host "Requisitos del instalador: $Framework" -ForegroundColor Gray
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
