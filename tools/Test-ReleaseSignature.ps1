<#
.SYNOPSIS
    Verifica que una release de Velopack esté firmada (instalador Y archivos de dentro).

.DESCRIPTION
    Comprueba la firma Authenticode de:
      * el instalador (*Setup.exe),
      * los binarios propios dentro del Portable.zip: AnimeLocalTracker.exe, animetracker_core.dll y el motor
        Python (AnimeTrackerTools.exe) — es lo que Windows/SmartScreen evalúa cuando el usuario los ejecuta.
    Exige además sello de tiempo (RFC 3161): sin él, la firma deja de ser válida cuando caduca el certificado.
    Falla (código 1) listando TODO lo que no cumpla.

.PARAMETER ReleasesDir
    Carpeta con los artefactos generados por vpk (por defecto Releases).

.PARAMETER AceptarRaizNoConfiable
    Solo para pruebas locales con un certificado AUTOFIRMADO: acepta que la cadena no llegue a una raíz de
    confianza (estado UnknownError/NotTrusted), pero sigue exigiendo firma presente y sello de tiempo.
    En el CI real NO se usa: ahí se exige estado Valid.
#>
param(
    [string]$ReleasesDir = "Releases",
    [switch]$AceptarRaizNoConfiable
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$setups = @(Get-ChildItem $ReleasesDir -Filter '*Setup.exe' -ErrorAction SilentlyContinue)
if ($setups.Count -eq 0) { throw "No hay ningún *Setup.exe en '$ReleasesDir'." }

$aVerificar = @{}                           # etiqueta -> ruta
foreach ($s in $setups) { $aVerificar[$s.Name] = $s.FullName }

# Binarios propios dentro del Portable.zip (se extraen a una carpeta temporal)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('sigcheck_' + [guid]::NewGuid().ToString('N'))
try {
    $portable = Get-ChildItem $ReleasesDir -Filter '*Portable.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($portable) {
        New-Item -ItemType Directory $tmp | Out-Null
        $zip = [IO.Compression.ZipFile]::OpenRead($portable.FullName)
        try {
            foreach ($nombre in 'AnimeLocalTracker.exe', 'animetracker_core.dll', 'AnimeTrackerTools.exe') {
                $entrada = $zip.Entries | Where-Object { $_.Name -eq $nombre } | Select-Object -First 1
                if (-not $entrada) { $aVerificar["(Portable) $nombre"] = $null; continue }
                $destino = Join-Path $tmp $nombre
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entrada, $destino, $true)
                $aVerificar["(Portable) $nombre"] = $destino
            }
        }
        finally { $zip.Dispose() }
    }

    $fallos = @()
    foreach ($etiqueta in ($aVerificar.Keys | Sort-Object)) {
        $ruta = $aVerificar[$etiqueta]
        if (-not $ruta) { $fallos += "$etiqueta : no se encontró dentro del Portable.zip"; continue }

        $sig = Get-AuthenticodeSignature $ruta
        $confiable = $sig.Status -eq 'Valid'
        $aceptable = $confiable -or ($AceptarRaizNoConfiable -and $sig.SignerCertificate -and $sig.Status -in 'UnknownError', 'NotTrusted')

        if (-not $aceptable) { $fallos += "$etiqueta : firma no válida (estado $($sig.Status))"; continue }
        if (-not $sig.TimeStamperCertificate) { $fallos += "$etiqueta : firmado SIN sello de tiempo (dejaría de ser válido al caducar el certificado)"; continue }

        Write-Host ("  OK  {0,-40} {1}  [{2}]" -f $etiqueta, $sig.SignerCertificate.Subject, $sig.Status) -ForegroundColor Green
    }

    if ($fallos.Count -gt 0) {
        $fallos | ForEach-Object { Write-Host "  FALLA  $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host "Todas las firmas verificadas ($($aVerificar.Count) archivos)." -ForegroundColor Green
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
