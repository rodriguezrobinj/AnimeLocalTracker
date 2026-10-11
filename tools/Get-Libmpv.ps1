<#
.SYNOPSIS
    Deja libmpv-2.dll (motor de video nuevo) en la carpeta Mpv de la app.

.DESCRIPTION
    Compilación de shinchiro (la que enlaza mpv.io), variante GPL. Versión y SHA-256 FIJADOS: el proveedor solo conserva en GitHub
    unos dos meses de compilaciones, así que la descarga va primero a SourceForge (donde publica las mismas y las conserva) y, si
    falla, a GitHub. El archivo es el mismo en los dos sitios (misma huella). Para cambiar de versión: subir $Version y $Sha256 con
    los de un paquete "mpv-dev-x86_64-<fecha>-git-<commit>.7z" de https://sourceforge.net/projects/mpv-player-windows/files/libmpv/
    y repetir la verificación en la app (docs/plan-motor-libmpv-etapa-2.md, tarea 7).

.PARAMETER Destino
    Carpeta de destino (AnimeLocalTracker\Mpv).

.PARAMETER Forzar
    Vuelve a descargar y copiar aunque ya exista.
#>
param(
    [Parameter(Mandatory)] [string]$Destino,
    [switch]$Forzar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Version = '20261011-git-accb7b2821'
$Sha256  = 'cf63b4ce66e03e5e70acf2648cbc3177656c77c5b9583e16d83efc58801182b7'
$Nombre  = "mpv-dev-x86_64-$Version.7z"
$Urls    = @(
    "https://sourceforge.net/projects/mpv-player-windows/files/libmpv/$Nombre/download",
    "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/$($Version.Substring(0, 8))/$Nombre"
)
$Dll     = 'libmpv-2.dll'
$Marca   = Join-Path $Destino 'version.txt'

if (-not $Forzar -and (Test-Path "$Destino\$Dll") -and (Test-Path $Marca) -and ((Get-Content $Marca -Raw).Trim() -eq $Version)) {
    return
}

$sevenZip = @(
    (Get-Command 7z -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "$env:ProgramFiles\7-Zip\7z.exe",
    "${env:ProgramFiles(x86)}\7-Zip\7z.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $sevenZip) {
    throw "Se necesita 7-Zip para extraer libmpv (los runners de GitHub Actions ya lo traen). Instálalo con: winget install 7zip.7zip"
}

$archivo = Join-Path $env:TEMP $Nombre
function Test-HashOk([string]$ruta) {
    return (Test-Path $ruta) -and ((Get-FileHash $ruta -Algorithm SHA256).Hash -ieq $Sha256)
}

if (-not (Test-HashOk $archivo)) {
    foreach ($url in $Urls) {
        try {
            Write-Host "[libmpv] Descargando libmpv $Version de $(([uri]$url).Host)..." -ForegroundColor Yellow
            Invoke-WebRequest $url -OutFile $archivo -UseBasicParsing -UserAgent 'Wget/1.21'
            if (Test-HashOk $archivo) { break }
            Write-Host "[libmpv] La huella de lo descargado de $(([uri]$url).Host) no coincide; se prueba el siguiente origen." -ForegroundColor Yellow
        }
        catch {
            Write-Host "[libmpv] No se pudo descargar de $(([uri]$url).Host): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    if (-not (Test-HashOk $archivo)) {
        Remove-Item $archivo -Force -ErrorAction SilentlyContinue
        throw "No se pudo obtener libmpv $Version con la huella fijada en el script. Descarga abortada por seguridad."
    }
}

$tmp = Join-Path $env:TEMP ('libmpv_' + [guid]::NewGuid().ToString('N'))
try {
    & $sevenZip e $archivo "-o$tmp" $Dll -y -bso0 -bsp0
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path "$tmp\$Dll")) { throw "7-Zip no pudo extraer $Dll (código $LASTEXITCODE)." }

    New-Item -ItemType Directory -Path $Destino -Force | Out-Null
    Copy-Item "$tmp\$Dll" "$Destino\$Dll" -Force
    Set-Content -Path $Marca -Value $Version -Encoding ascii
    Write-Host "[libmpv] $Dll ($Version) listo en $Destino" -ForegroundColor Green
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
