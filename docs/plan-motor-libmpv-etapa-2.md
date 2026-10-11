# Plan de implementación: motor libmpv, etapa 2 (libmpv básico tras un ajuste oculto)

> **Para quien lo ejecute:** SUB-SKILL OBLIGATORIO: `superpowers:executing-plans`, tarea a tarea, en esta misma sesión (en este repo no se usan subagentes salvo que el usuario lo pida). Los pasos usan casillas (`- [ ]`) para llevar la cuenta.

**Objetivo:** que, con `"MotorVideo": "Mpv"` en `settings.json`, un episodio se abra, se vea y se maneje con libmpv (pausar, saltar, volumen, velocidad, reanudar, episodio siguiente), con el video pintado dentro de la ventana de WPF. Sin ese ajuste, nada cambia.

**Arquitectura:** `MotorMpv` implementa `IMotorVideo` sobre `libmpv-2.dll` (llamadas en `Services/Native/LibMpv.cs`, eventos en un hilo propio). `SuperficieMpv` pinta el video con `OpenTK.GLWpfControl` y la interfaz de pintado de libmpv. `ReproductorMpvView` es una copia de la vista actual con la superficie en lugar del anfitrión de Flyleaf. El reproductor decide qué motor y qué vista usar al construirse, según el ajuste.

**Tecnologías:** C# .NET 8, WPF, libmpv 0.41 (compilación de shinchiro `20261011-git-accb7b2821`), OpenTK.GLWpfControl 4.3.6, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-motor-libmpv.md`. El contrato del motor es el de `Services/IMotorVideo.cs` (etapa 1). Quien ejecute lee los dos documentos.

## Restricciones globales

- **Sin commit ni push.** Donde un plan normal diría "commit", aquí hay un paso de verificación. Sin ramas, worktrees ni subagentes.
- **Con el ajuste en su valor por defecto (`"Flyleaf"`) no cambia nada:** ni el motor, ni la vista, ni lo que se carga en memoria (libmpv y OpenTK no se tocan hasta que se pide el motor nuevo).
- **`ReproductorView` (la vista clásica) no se modifica.**
- **Frontera nativa:** `[LibraryImport]` (nunca `[DllImport]`), cadenas en UTF-8 explícito, y `LibMpv.Disponible` antes de cualquier llamada: si falta la biblioteca, la app sigue con Flyleaf.
- **libmpv siempre con** `config=no`, `load-scripts=no`, `ytdl=no`, `input-default-bindings=no`, `input-vo-keyboard=no`, `osc=no`, `terminal=no`: no lee configuración ni scripts del usuario, no resuelve direcciones de internet y no atiende teclado ni ratón por su cuenta.
- **Los avisos del motor llegan desde su hilo:** nada observable se toca sin saltar al hilo de la interfaz (ya lo hace el reproductor).
- **Las pruebas nunca escriben bajo `AppDataPaths`**; los videos de prueba se generan en una carpeta temporal.
- **Textos visibles nuevos:** claves en las dos mitades (ES y EN) de `LocalizationService` (skill `localizacion`).
- **Verificación de C#:** skill `repo-build-test`. **En la app:** skill `perfil-aislado`, nunca más del 85 % de un episodio.
- Mensajes, comentarios y nombres en español.

## Lo que la etapa 2 NO hace (queda para la 3)

Con el motor nuevo activado a mano: no hay mini reproductor (el botón no hace nada y navegar a otra pestaña cierra el episodio guardando el progreso), el ecualizador y el modo noche no se aplican, la captura de fotograma avisa de que no pudo, no hay escalado ni informe de tarjeta, y los subtítulos de texto los sigue leyendo y dibujando la app (los de imagen y los externos los dibuja libmpv con su estilo por defecto).

## Foco de revisión

1. **Falta `libmpv-2.dll`** (instalación vieja, descarga fallida) con el ajuste en `Mpv`: la app usa Flyleaf y lo deja en el registro, sin fallar. → Tarea 5, `ConElAjusteEnMpvPeroSinBiblioteca_SeUsaFlyleaf`.
2. **Archivo que no existe o que libmpv no entiende:** aviso de apertura fallida con motivo, estado `Fallido`, y el motor sigue sirviendo para el siguiente archivo. → Tarea 3, `UnArchivoQueNoExiste_AvisaDelFalloYSePuedeAbrirOtro`.
3. **Desechar el motor a mitad de una apertura o con el video en marcha:** no se cuelga ni lanza, y una segunda llamada a `Dispose` no hace nada. → Tarea 3, `DesecharEnPlenaReproduccion_NoSeCuelga`.
4. **Idioma de pista raro** (tres letras bibliográficas como `ger`/`fre`/`chi`, mayúsculas, `und`, vacío): se traduce a dos letras o a "sin idioma", nunca lanza. → Tarea 2, `IdiomaDosLetras_*`.
5. **Ruta con tildes, eñes, corchetes o caracteres japoneses** (así son las de la biblioteca): se abre igual. → Tarea 3, `UnaRutaConTildesYCorchetes_SeAbre`.

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
|---|---|---|
| `tools/Get-Libmpv.ps1` | Crear | Descarga fijada (versión + SHA-256) de `libmpv-2.dll` |
| `build.ps1`, `.gitignore`, `AnimeLocalTracker/AnimeLocalTracker.csproj` | Modificar | Llamar al script; ignorar `AnimeLocalTracker/Mpv/`; copiar la carpeta a la salida; paquete OpenTK.GLWpfControl |
| `AnimeLocalTracker/Services/Native/LibMpv.cs` | Crear | Llamadas a libmpv |
| `AnimeLocalTracker/Services/MotorMpv.cs` | Crear | libmpv detrás de `IMotorVideo` |
| `AnimeLocalTracker/Controls/SuperficieMpv.cs` | Crear | Pintado del video dentro de WPF |
| `AnimeLocalTracker/Views/ReproductorMpvView.xaml` y `.xaml.cs` | Crear (copia adaptada) | Vista del reproductor con el motor nuevo |
| `AnimeLocalTracker/Models/AppSettings.cs` | Modificar | Ajuste `MotorVideo` |
| `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` | Modificar | Elegir motor; `UsaMotorMpv`; lista de pistas de subtítulos para el menú; sin mini con el motor nuevo |
| `AnimeLocalTracker/ViewModels/NavigationService.cs` | Modificar | Con el motor nuevo, navegar cierra el episodio en vez de pasarlo a mini |
| `AnimeLocalTracker/App.xaml` | Modificar | Plantilla que elige la vista |
| `AnimeLocalTracker/Services/LocalizationService.cs`, `Views/AcercaDeView.xaml`, `README.md` | Modificar | Aviso de licencia de mpv |
| `AnimeLocalTracker.Tests/Services/MotorMpvTests.cs` | Crear | Idiomas (puro) y motor real sin pantalla |
| `AnimeLocalTracker.Tests/ViewModels/ReproductorConMotorTests.cs` | Modificar | Elección de motor y menú de subtítulos |

---

## Tarea 1: empaquetado de libmpv y OpenTK

**Archivos:** crear `tools/Get-Libmpv.ps1`; modificar `build.ps1`, `.gitignore`, `AnimeLocalTracker/AnimeLocalTracker.csproj`.

**Interfaces — produce:** `AnimeLocalTracker\Mpv\libmpv-2.dll` copiada a `bin\…\Mpv\` de la app y de las pruebas.

- [ ] **Paso 1: `tools/Get-Libmpv.ps1`** (mismo patrón que `Get-FFmpegBinaries.ps1`; guardar en UTF-8 con BOM)

```powershell
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
```

  (SourceForge redirige a un espejo; con un agente de usuario de navegador devuelve una página HTML en vez del archivo, por eso el de `Wget`.)

- [ ] **Paso 2: enganchar el script**
  1. `build.ps1`, justo después de la llamada a `Get-FFmpegBinaries.ps1`: `& "$root\tools\Get-Libmpv.ps1" -Destino "$root\AnimeLocalTracker\Mpv"`, con un comentario de dos líneas que diga qué es y que la carpeta está en `.gitignore`.
  2. `.gitignore`, junto a `AnimeLocalTracker/FFmpeg/`: `AnimeLocalTracker/Mpv/`.
  3. `AnimeLocalTracker.csproj`, junto al bloque `<None Include="FFmpeg\**">`, uno igual para `Mpv\**` (mismo `CopyToOutputDirectory` y los mismos metadatos que lleve el de FFmpeg).
  4. `AnimeLocalTracker.csproj`: `<PackageReference Include="OpenTK.GLWpfControl" Version="4.3.6" />` con un comentario: "Lienzo OpenGL que WPF compone como imagen: ahí pinta libmpv (Controls/SuperficieMpv.cs)".

- [ ] **Paso 3: verificación**

```
powershell -ExecutionPolicy Bypass -File tools\Get-Libmpv.ps1 -Destino AnimeLocalTracker\Mpv
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false
```

Esperado: `AnimeLocalTracker\Mpv\libmpv-2.dll` (≈121 MB) y `version.txt`; segunda ejecución del script sin descargar; compilación con 0 advertencias; `bin\Debug\…\Mpv\libmpv-2.dll` existe; `git status` no muestra la carpeta `Mpv`. Anotar cuánto pesan los ensamblados de OpenTK en `bin` (dato para el informe).

---

## Tarea 2: `LibMpv` y las piezas puras de `MotorMpv`

**Archivos:** crear `AnimeLocalTracker/Services/Native/LibMpv.cs`, `AnimeLocalTracker/Services/MotorMpv.cs` (solo la parte estática de este paso), `AnimeLocalTracker.Tests/Services/MotorMpvTests.cs`.

**Interfaces — produce:** `LibMpv.Disponible`, las llamadas nativas, `LibMpv.Leer`, `LibMpv.Orden`; `MotorMpv.IdiomaDosLetras(string?) : string?`.

- [ ] **Paso 1: pruebas de idiomas (fallarán al compilar)** — `AnimeLocalTracker.Tests/Services/MotorMpvTests.cs`

```csharp
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public class MotorMpvIdiomasTests
{
    [Theory]
    [InlineData("spa", "es")]
    [InlineData("jpn", "ja")]
    [InlineData("eng", "en")]
    [InlineData("por", "pt")]
    [InlineData("ger", "de")] // códigos bibliográficos: los que usan muchos .mkv
    [InlineData("fre", "fr")]
    [InlineData("chi", "zh")]
    [InlineData("es", "es")]
    [InlineData("ES", "es")]
    [InlineData("es-419", "es")]
    [InlineData("pt-BR", "pt")]
    public void IdiomaDosLetras_TraduceLoQueEscribenLosArchivos(string deLaPista, string esperado)
    {
        MotorMpv.IdiomaDosLetras(deLaPista).Should().Be(esperado);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("und")]
    [InlineData("zxx")]
    [InlineData("esto no es un idioma")]
    public void IdiomaDosLetras_SinIdiomaReconocible_DaNulo(string? deLaPista)
    {
        MotorMpv.IdiomaDosLetras(deLaPista).Should().BeNull();
    }
}
```

- [ ] **Paso 2: `Services/Native/LibMpv.cs`**

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AnimeLocalTracker.Services.Native;

/// <summary>
/// Llamadas a libmpv-2.dll (motor de video nuevo). La biblioteca va en la carpeta Mpv de la instalación y se carga la primera vez
/// que se pregunta por <see cref="Disponible"/>: con el motor clásico ni se toca. Nadie llama a nada de aquí sin comprobarlo antes.
/// </summary>
internal static partial class LibMpv
{
    private const string Dll = "libmpv-2";

    private static readonly Lazy<bool> Cargada = new(() =>
    {
        try
        {
            string ruta = Path.Combine(AppContext.BaseDirectory, "Mpv", "libmpv-2.dll");
            // Cargada por su ruta completa, las llamadas de abajo la encuentran por su nombre (Windows reutiliza el módulo ya cargado).
            return File.Exists(ruta) && NativeLibrary.TryLoad(ruta, out _);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("LibMpv", $"No se pudo cargar libmpv: {ex.Message}");
            return false;
        }
    });

    public static bool Disponible => Cargada.Value;

    // Identificadores de evento y de formato que usa la app (client.h).
    internal const int EventoFinDeArchivo = 7, EventoArchivoCargado = 8, EventoReinicioDeReproduccion = 21, EventoCambioDePropiedad = 22;
    internal const int FormatoBandera = 3, FormatoDoble = 5;
    internal const int FinPorError = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Evento { public int Id; public int Error; public ulong Usuario; public nint Datos; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventoPropiedad { public nint Nombre; public int Formato; public nint Datos; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventoFin { public int Razon; public int Error; }

    [LibraryImport(Dll)] internal static partial nint mpv_create();
    [LibraryImport(Dll)] internal static partial int mpv_initialize(nint ctx);
    [LibraryImport(Dll)] internal static partial void mpv_terminate_destroy(nint ctx);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_option_string(nint ctx, string nombre, string valor);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_property_string(nint ctx, string nombre, string valor);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint mpv_get_property_string(nint ctx, string nombre);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_observe_property(nint ctx, ulong usuario, string nombre, int formato);
    [LibraryImport(Dll)] internal static partial void mpv_free(nint datos);
    [LibraryImport(Dll)] internal static partial int mpv_command(nint ctx, nint[] argumentos);
    [LibraryImport(Dll)] internal static partial nint mpv_wait_event(nint ctx, double espera);
    [LibraryImport(Dll)] internal static partial void mpv_wakeup(nint ctx);
    [LibraryImport(Dll)] internal static partial nint mpv_error_string(int error);

    // ── Pintado (render.h, render_gl.h) ──
    internal const int ParametroTipoApi = 1, ParametroInicioGl = 2, ParametroFbo = 3, ParametroVoltearY = 4, ParametroBloquearHastaSuMomento = 12;
    internal const ulong HayFotogramaNuevo = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ParametroDePintado { public int Tipo; public nint Datos; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InicioOpenGl { public nint ObtenerFuncion; public nint Contexto; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Fbo { public int Id, Ancho, Alto, Formato; }

    [LibraryImport(Dll)] internal static partial int mpv_render_context_create(out nint pintor, nint ctx, ParametroDePintado[] parametros);
    [LibraryImport(Dll)] internal static partial int mpv_render_context_render(nint pintor, ParametroDePintado[] parametros);
    [LibraryImport(Dll)] internal static partial void mpv_render_context_set_update_callback(nint pintor, nint aviso, nint datos);
    [LibraryImport(Dll)] internal static partial ulong mpv_render_context_update(nint pintor);
    [LibraryImport(Dll)] internal static partial void mpv_render_context_free(nint pintor);

    /// <summary>Una propiedad como texto ("" si no existe o aún no tiene valor).</summary>
    internal static string Leer(nint ctx, string nombre)
    {
        nint texto = mpv_get_property_string(ctx, nombre);
        if (texto == 0) return string.Empty;
        try { return Marshal.PtrToStringUTF8(texto) ?? string.Empty; }
        finally { mpv_free(texto); }
    }

    /// <summary>Una orden de mpv ("loadfile", "seek"…). Devuelve su código de error (negativo si falla).</summary>
    internal static int Orden(nint ctx, params string[] argumentos)
    {
        var punteros = new nint[argumentos.Length + 1]; // termina en nulo
        try
        {
            for (int i = 0; i < argumentos.Length; i++) punteros[i] = Marshal.StringToCoTaskMemUTF8(argumentos[i]);
            return mpv_command(ctx, punteros);
        }
        finally
        {
            foreach (nint p in punteros) if (p != 0) Marshal.FreeCoTaskMem(p);
        }
    }

    internal static string TextoDeError(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? error.ToString();
}
```

- [ ] **Paso 3: `Services/MotorMpv.cs`, de momento solo esto**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AnimeLocalTracker.Services;

public sealed partial class MotorMpv
{
    // Códigos de tres letras "bibliográficos" (ISO 639-2/B) que .NET no conoce por ese nombre y que muchos .mkv usan.
    private static readonly Dictionary<string, string> Bibliograficos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ger"] = "de", ["fre"] = "fr", ["chi"] = "zh", ["dut"] = "nl", ["gre"] = "el", ["cze"] = "cs", ["rum"] = "ro",
        ["per"] = "fa", ["may"] = "ms", ["ice"] = "is", ["slo"] = "sk", ["alb"] = "sq", ["arm"] = "hy", ["baq"] = "eu",
        ["bur"] = "my", ["geo"] = "ka", ["mac"] = "mk", ["mao"] = "mi", ["tib"] = "bo", ["wel"] = "cy"
    };

    private static readonly Lazy<Dictionary<string, string>> DeTresADos = new(() => CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(c => c.Name.Length > 0 && c.ThreeLetterISOLanguageName.Length == 3 && c.TwoLetterISOLanguageName.Length == 2)
        .GroupBy(c => c.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().TwoLetterISOLanguageName, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// El idioma de una pista tal como lo escribe el archivo ("spa", "jpn", "es-419", "ger"…) en dos letras, que es como lo compara
    /// la app con el idioma preferido. Null si no dice nada útil ("und", vacío, texto libre).
    /// </summary>
    public static string? IdiomaDosLetras(string? deLaPista)
    {
        string idioma = (deLaPista ?? string.Empty).Trim().ToLowerInvariant();
        int guion = idioma.IndexOf('-');
        if (guion > 0) idioma = idioma[..guion];

        if (idioma.Length == 2 && idioma.All(char.IsAsciiLetter)) return idioma;
        if (idioma.Length != 3) return null;
        if (Bibliograficos.TryGetValue(idioma, out string? bibliografico)) return bibliografico;
        return DeTresADos.Value.TryGetValue(idioma, out string? dos) ? dos : null;
    }
}
```

- [ ] **Paso 4: verificación**

Compilar la app (0 advertencias) y `--filter "FullyQualifiedName~MotorMpvIdiomasTests"`: todas en verde. Si `"und"` o `"zxx"` dieran un idioma (porque .NET los tenga como cultura), añadirlos a una lista de excluidos en `IdiomaDosLetras`.

---

## Tarea 3: `MotorMpv`

**Archivos:** completar `AnimeLocalTracker/Services/MotorMpv.cs`; ampliar `AnimeLocalTracker.Tests/Services/MotorMpvTests.cs`.

**Interfaces — produce:** `MotorMpv.Crear(AjustesInicialesMotor ajustes, bool sinSalida = false) : MotorMpv?`; para la superficie: `internal nint Manejador`, `internal void ContarFotograma()`, `internal event Action? Desechando`.

- [ ] **Paso 1: pruebas con el motor real y sin pantalla (fallarán al compilar)** — añadir a `MotorMpvTests.cs`

```csharp
/// <summary>Un video diminuto de 6 s con dos pistas de audio (japonés y español) y una de subtítulos, hecho con el FFmpeg de la app.</summary>
public sealed class VideoDePrueba : IDisposable
{
    public string Carpeta { get; } = Path.Combine(Path.GetTempPath(), "alt_mpv_" + Guid.NewGuid().ToString("N"));
    public string Ruta { get; }
    public string RutaConTildes { get; }

    public VideoDePrueba()
    {
        Directory.CreateDirectory(Carpeta);
        Ruta = Path.Combine(Carpeta, "prueba.mkv");
        string srt = Path.Combine(Carpeta, "subs.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:03,000\nHola\n\n2\n00:00:03,500 --> 00:00:05,000\nAdiós\n");
        string[] argumentos =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=6:size=320x180:rate=24",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-f", "lavfi", "-i", "sine=frequency=880:duration=6",
            "-i", srt,
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "24",
            "-c:a", "aac", "-b:a", "48k", "-c:s", "srt",
            "-metadata:s:a:0", "language=jpn", "-metadata:s:a:1", "language=spa", "-metadata:s:a:1", "title=Latino",
            "-metadata:s:s:0", "language=spa",
            Ruta
        ];
        var resultado = AnimeLocalTracker.Core.ProcesoExterno.EjecutarAsync(FfmpegLocator.Ffmpeg, argumentos, TimeSpan.FromSeconds(60), CancellationToken.None)
            .GetAwaiter().GetResult();
        if (resultado == null || resultado.Codigo != 0) throw new InvalidOperationException("No se pudo generar el video de prueba: " + resultado?.Error);

        RutaConTildes = Path.Combine(Carpeta, "[Fansub] Cañón ñandú 第02話 (1080p).mkv");
        File.Copy(Ruta, RutaConTildes);
    }

    public void Dispose()
    {
        try { Directory.Delete(Carpeta, true); } catch { }
    }
}

/// <summary>libmpv de verdad, sin ventana ni sonido. Si la biblioteca no está descargada (tools\Get-Libmpv.ps1), las pruebas no hacen nada.</summary>
public sealed class MotorMpvTests : IClassFixture<VideoDePrueba>
{
    private static readonly AjustesInicialesMotor Ajustes = new(false, null, []);
    private readonly VideoDePrueba _video;

    public MotorMpvTests(VideoDePrueba video) => _video = video;

    private static async Task EsperarHasta(Func<bool> condicion, int ms = 15000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue($"la condición debía cumplirse en {ms} ms");
    }

    private static (MotorMpv Motor, List<AperturaTerminada> Avisos) Crear()
    {
        var motor = MotorMpv.Crear(Ajustes, sinSalida: true)!;
        var avisos = new List<AperturaTerminada>();
        motor.AperturaTerminada += (_, e) => { lock (avisos) avisos.Add(e); };
        return (motor, avisos);
    }

    [Fact]
    public async Task AbrirUnVideo_AvisaReproduceYDaSusPistas()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Estado.Should().Be(EstadoMotor.Detenido);

        motor.Abrir(_video.Ruta);
        motor.Estado.Should().Be(EstadoMotor.Abriendo);

        await EsperarHasta(() => avisos.Count == 1);
        avisos[0].Should().Be(new AperturaTerminada(ClaseApertura.Video, true, null));
        await EsperarHasta(() => motor.Estado == EstadoMotor.Reproduciendo && motor.Posicion > TimeSpan.FromMilliseconds(200));
        motor.Duracion.TotalSeconds.Should().BeApproximately(6, 0.5);
        motor.TamanoVideo.Should().Be((320, 180));
        motor.ProporcionVideo.Should().BeApproximately(16 / 9.0, 0.01);
        motor.CodecVideo.Should().Be("h264");
        motor.FotogramasMostrados.Should().BeGreaterThan(0);

        motor.PistasAudio.Select(p => (p.Idioma, p.Titulo)).Should().Equal(("ja", null), ("es", "Latino"));
        motor.IndicePistaAudioActual.Should().Be(motor.PistasAudio[0].Indice);
        motor.PistasSubtitulos.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PistaSubtitulosMotor(3, "es", null, EsImagen: false, EsAss: false, RutaExterna: null));
        motor.IndicePistaSubtitulosDelMotor.Should().Be(-1, "el motor arranca con sus subtítulos apagados: la app decide");
    }

    [Fact]
    public async Task PausarSaltarYLlegarAlFinal()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1 && motor.Estado == EstadoMotor.Reproduciendo);

        motor.Pausar();
        await EsperarHasta(() => motor.Estado == EstadoMotor.EnPausa);
        long antes = motor.FotogramasMostrados;

        motor.Saltar(TimeSpan.FromSeconds(3.2), exacto: true);
        await EsperarHasta(() => Math.Abs(motor.Posicion.TotalSeconds - 3.2) < 0.1);
        await EsperarHasta(() => motor.FotogramasMostrados > antes + 1);
        motor.Estado.Should().Be(EstadoMotor.EnPausa, "saltar no reanuda");

        motor.Saltar(TimeSpan.FromSeconds(4.6), exacto: false);
        await EsperarHasta(() => motor.Posicion.TotalSeconds is > 3.9 and < 5.1); // al fotograma clave cercano (hay uno por segundo)

        motor.Velocidad = 2;
        motor.Reproducir();
        await EsperarHasta(() => motor.Estado == EstadoMotor.Terminado);
        motor.Posicion.TotalSeconds.Should().BeApproximately(6, 0.5);

        motor.Saltar(TimeSpan.Zero, exacto: false); // "reproducir de nuevo" tras el final
        motor.Reproducir();
        await EsperarHasta(() => motor.Estado == EstadoMotor.Reproduciendo && motor.Posicion.TotalSeconds < 3);
    }

    [Fact]
    public async Task CambiarDePistaDeAudio_AvisaComoUnaApertura_YNoReiniciaElVideo()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1 && motor.Posicion > TimeSpan.FromMilliseconds(300));

        motor.ElegirAudio(motor.PistasAudio[1]);

        await EsperarHasta(() => avisos.Count == 2); // el reproductor cuenta con este aviso para restaurar volumen y silencio
        avisos[1].Should().Be(new AperturaTerminada(ClaseApertura.Video, true, null));
        motor.IndicePistaAudioActual.Should().Be(motor.PistasAudio[1].Indice);
        motor.Estado.Should().Be(EstadoMotor.Reproduciendo);
    }

    [Fact]
    public async Task LosSubtitulosDelMotor_SeEnciendenYSeApagan()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1);
        var pista = motor.PistasSubtitulos[0];

        motor.MostrarSubtitulosDelMotor(pista);
        await EsperarHasta(() => motor.IndicePistaSubtitulosDelMotor == pista.Indice);

        motor.ApagarSubtitulosDelMotor();
        await EsperarHasta(() => motor.IndicePistaSubtitulosDelMotor == -1);
        avisos.Should().HaveCount(1, "los subtítulos del motor no avisan: los dibuja él y la app no tiene que leer nada");
    }

    [Fact]
    public async Task UnArchivoQueNoExiste_AvisaDelFalloYSePuedeAbrirOtro()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;

        motor.Abrir(Path.Combine(_video.Carpeta, "no_existe.mkv"));

        await EsperarHasta(() => avisos.Count == 1);
        avisos[0].Exito.Should().BeFalse();
        avisos[0].Error.Should().NotBeNullOrWhiteSpace();
        motor.Estado.Should().Be(EstadoMotor.Fallido);

        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 2 && motor.Estado == EstadoMotor.Reproduciendo);
        avisos[1].Exito.Should().BeTrue();
    }

    [Fact]
    public async Task UnaRutaConTildesYCorchetes_SeAbre()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;

        motor.Abrir(_video.RutaConTildes);

        await EsperarHasta(() => avisos.Count == 1);
        avisos[0].Exito.Should().BeTrue(avisos[0].Error);
    }

    [Fact]
    public async Task DetenerYVolverAAbrir()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        using var _ = motor;
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1 && motor.Estado == EstadoMotor.Reproduciendo);

        motor.Detener();
        await EsperarHasta(() => motor.Estado == EstadoMotor.Detenido);

        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 2 && motor.Estado == EstadoMotor.Reproduciendo);
    }

    [Fact]
    public async Task DesecharEnPlenaReproduccion_NoSeCuelga()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, avisos) = Crear();
        motor.Abrir(_video.Ruta);
        await EsperarHasta(() => avisos.Count == 1);

        var cierre = Task.Run(() => { motor.Dispose(); motor.Dispose(); });

        (await Task.WhenAny(cierre, Task.Delay(10000))).Should().Be(cierre, "desechar no debe colgarse");
        motor.Desechado.Should().BeTrue();
        motor.Estado.Should().Be(EstadoMotor.Detenido);
        motor.Abrir(_video.Ruta); // ya desechado: no hace nada ni lanza
        motor.Posicion.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Volumen_Velocidad_YSilencio_NoLanzanSinArchivo()
    {
        if (!Native.LibMpv.Disponible) return;
        var (motor, _) = Crear();
        using var __ = motor;

        motor.Volumen = 40;
        motor.Silencio = true;
        motor.Velocidad = 1.5;
        motor.AceleracionPorHardware = false;
        motor.Saltar(TimeSpan.FromSeconds(10), exacto: true);
        motor.Pausar();
        motor.Reproducir();

        motor.PistasAudio.Should().BeEmpty();
        motor.CapturarFotograma().Should().BeNull();
    }
}
```

Añadir los `using` que falten (`System`, `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Threading`, `System.Threading.Tasks`).

- [ ] **Paso 2: completar `MotorMpv.cs`** (la clase pasa a implementar `IMotorVideo`; lo del paso 3 de la tarea 2 se conserva)

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services.Native;

namespace AnimeLocalTracker.Services;

/// <summary>
/// libmpv detrás de <see cref="IMotorVideo"/>. Las órdenes se le dan desde cualquier hilo; lo que cambia (posición, pausa, fin,
/// pistas) llega por sus eventos a un hilo propio, que deja los valores listos para quien pregunte y da el aviso de apertura.
/// El video lo pinta <c>Controls/SuperficieMpv</c>; sin superficie (pruebas) el motor trabaja igual, sin imagen.
/// </summary>
public sealed partial class MotorMpv : IMotorVideo
{
    private const ulong IdPosicion = 1, IdDuracion = 2, IdPausa = 3, IdFin = 4, IdProporcion = 5;

    private readonly nint _mpv;
    private readonly bool _sinSalida;
    private readonly Thread _hiloEventos;
    private readonly object _candado = new();

    private volatile bool _desechado, _abriendo, _fallido, _hayArchivo, _pausa, _fin;
    private long _posicionTicks, _duracionTicks, _fotogramas, _proporcionBits;
    private List<(int IdMpv, PistaAudioMotor Pista)> _audio = [];
    private List<(int IdMpv, PistaSubtitulosMotor Pista)> _subtitulos = [];

    private MotorMpv(nint mpv, bool sinSalida)
    {
        _mpv = mpv;
        _sinSalida = sinSalida;
        _hiloEventos = new Thread(AtenderEventos) { IsBackground = true, Name = "Eventos de libmpv" };
        _hiloEventos.Start();
    }

    /// <summary>Null si falta la biblioteca o libmpv no arranca en este equipo.</summary>
    /// <param name="sinSalida">Sin imagen ni sonido (pruebas): todo lo demás funciona igual.</param>
    public static MotorMpv? Crear(AjustesInicialesMotor ajustes, bool sinSalida = false)
    {
        if (!LibMpv.Disponible) return null;

        nint mpv = 0;
        try
        {
            mpv = LibMpv.mpv_create();
            if (mpv == 0) return null;

            // No lee la configuración ni los scripts de mpv del usuario, no resuelve direcciones de internet y no atiende teclado ni ratón.
            foreach (var (nombre, valor) in new (string, string)[]
            {
                ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"), ("osc", "no"),
                ("input-default-bindings", "no"), ("input-vo-keyboard", "no"), ("input-cursor", "no"),
                ("idle", "yes"),          // sin archivo se queda esperando el siguiente
                ("keep-open", "yes"),     // al terminar se queda en el último fotograma: el reproductor decide qué hacer
                ("sid", "no"),            // sus subtítulos apagados: la app elige la pista y la lee ella (como con el motor clásico)
                ("sub-auto", "fuzzy"),    // los .srt/.ass sueltos junto al video aparecen como pistas
                ("audio-display", "no"),
                ("hwdec", ajustes.AceleracionPorHardware ? "auto-copy" : "no"),
                ("vo", sinSalida ? "null" : "libmpv"),
            })
            {
                LibMpv.mpv_set_option_string(mpv, nombre, valor);
            }
            if (sinSalida) LibMpv.mpv_set_option_string(mpv, "ao", "null");

            int resultado = LibMpv.mpv_initialize(mpv);
            if (resultado < 0)
            {
                AppLogger.Warn("MotorMpv", $"libmpv no arrancó: {LibMpv.TextoDeError(resultado)}.");
                LibMpv.mpv_terminate_destroy(mpv);
                return null;
            }

            LibMpv.mpv_observe_property(mpv, IdPosicion, "time-pos", LibMpv.FormatoDoble);
            LibMpv.mpv_observe_property(mpv, IdDuracion, "duration", LibMpv.FormatoDoble);
            LibMpv.mpv_observe_property(mpv, IdPausa, "pause", LibMpv.FormatoBandera);
            LibMpv.mpv_observe_property(mpv, IdFin, "eof-reached", LibMpv.FormatoBandera);
            LibMpv.mpv_observe_property(mpv, IdProporcion, "video-params/aspect", LibMpv.FormatoDoble);

            AppLogger.Info("MotorMpv", $"Motor libmpv listo ({LibMpv.Leer(mpv, "mpv-version")}).");
            return new MotorMpv(mpv, sinSalida);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MotorMpv", $"No se pudo crear el motor libmpv: {ex.Message}");
            return null;
        }
    }

    public event EventHandler<AperturaTerminada>? AperturaTerminada;
    public event EventHandler? ProporcionVideoCambiada;

    /// <summary>Para la superficie: el motor está a punto de cerrarse y hay que soltar el pintor ANTES (libmpv lo exige).</summary>
    internal event Action? Desechando;
    internal nint Manejador => _mpv;
    internal void ContarFotograma() => Interlocked.Increment(ref _fotogramas);

    public bool Desechado => _desechado;

    public EstadoMotor Estado =>
        _desechado ? EstadoMotor.Detenido
        : _abriendo ? EstadoMotor.Abriendo
        : _fallido ? EstadoMotor.Fallido
        : !_hayArchivo ? EstadoMotor.Detenido
        : _fin ? EstadoMotor.Terminado
        : _pausa ? EstadoMotor.EnPausa
        : EstadoMotor.Reproduciendo;

    public TimeSpan Posicion => TimeSpan.FromTicks(Interlocked.Read(ref _posicionTicks));
    public TimeSpan Duracion => TimeSpan.FromTicks(Interlocked.Read(ref _duracionTicks));

    public void Abrir(string ruta)
    {
        if (_desechado) return;
        _abriendo = true;
        _fallido = false;
        _fin = false;
        Interlocked.Exchange(ref _fotogramas, 0);
        Interlocked.Exchange(ref _posicionTicks, 0);
        Fijar("pause", "no"); // como el motor clásico: abrir es empezar a reproducir
        Ordenar("loadfile", ruta, "replace");
    }

    public void Reproducir() => Fijar("pause", "no");
    public void Pausar() => Fijar("pause", "yes");

    public void Detener()
    {
        if (_desechado) return;
        Ordenar("stop");
        _abriendo = false;
        _hayArchivo = false;
    }

    public void Saltar(TimeSpan destino, bool exacto)
    {
        if (_desechado || !_hayArchivo) return;
        _fin = false; // retroceder desde el final vuelve a ser "en pausa", no "terminado"
        Ordenar("seek", destino.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture), exacto ? "absolute+exact" : "absolute+keyframes");
    }

    public double Velocidad { set => Fijar("speed", value.ToString("F2", CultureInfo.InvariantCulture)); }
    public int Volumen { set => Fijar("volume", Math.Clamp(value, 0, 100).ToString(CultureInfo.InvariantCulture)); }
    public bool Silencio { set => Fijar("mute", value ? "yes" : "no"); }

    public long FotogramasMostrados => Interlocked.Read(ref _fotogramas);
    public bool DecodificaPorHardware => _hayArchivo && Leer("hwdec-current") is { Length: > 0 } actual && actual != "no";
    public bool AceleracionPorHardware { set => Fijar("hwdec", value ? "auto-copy" : "no"); }
    public string? CodecVideo => _hayArchivo && Leer("video-format") is { Length: > 0 } codec ? codec : null;

    public (int Ancho, int Alto) TamanoVideo =>
        _hayArchivo && int.TryParse(Leer("width"), out int ancho) && int.TryParse(Leer("height"), out int alto) ? (ancho, alto) : (0, 0);

    public double ProporcionVideo => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _proporcionBits));

    // Etapas 3 y 4 (docs/plan-motor-libmpv-etapa-2.md, "Lo que la etapa 2 NO hace").
    public TarjetaGrafica? TarjetaEnUso => null;
    public bool EscaladoInteligente { get => false; set { } }
    public BitmapSource? CapturarFotograma() => null;
    public bool AplicarFiltrosAudio(IReadOnlyList<FiltroAudio> filtros) => true;
    public bool CambiarFiltroEnVivo(string id, string clave, string valor) => true;
    public void PrecalentarArchivo(string ruta, CancellationToken ct) { } // con libmpv el cambio de episodio tarda ~0,1 s: no hace falta

    public IReadOnlyList<PistaAudioMotor> PistasAudio { get { lock (_candado) return _audio.Select(a => a.Pista).ToList(); } }
    public IReadOnlyList<PistaSubtitulosMotor> PistasSubtitulos { get { lock (_candado) return _subtitulos.Select(s => s.Pista).ToList(); } }

    public int IndicePistaAudioActual
    {
        get
        {
            if (!_hayArchivo || !int.TryParse(Leer("aid"), out int id)) return -1;
            lock (_candado) return _audio.Where(a => a.IdMpv == id).Select(a => a.Pista.Indice).DefaultIfEmpty(-1).First();
        }
    }

    public int IndicePistaSubtitulosDelMotor
    {
        get
        {
            if (!_hayArchivo || !int.TryParse(Leer("sid"), out int id)) return -1; // "no" = apagados
            lock (_candado) return _subtitulos.Where(s => s.IdMpv == id).Select(s => s.Pista.Indice).DefaultIfEmpty(-1).First();
        }
    }

    public PistaSubtitulosMotor? PistaSubtitulosDe(object nativa) => nativa as PistaSubtitulosMotor;

    public void ElegirAudio(PistaAudioMotor pista)
    {
        int id;
        lock (_candado) id = _audio.Where(a => a.Pista == pista).Select(a => a.IdMpv).DefaultIfEmpty(-1).First();
        if (id < 0) return;
        Fijar("aid", id.ToString(CultureInfo.InvariantCulture));
        // El reproductor espera el mismo aviso que al abrir (así lo hacía el motor clásico) para restaurar volumen y silencio.
        _ = Task.Run(() => AperturaTerminada?.Invoke(this, new AperturaTerminada(ClaseApertura.Video, true, null)));
    }

    /// <summary>Los dibuja libmpv dentro del video. No avisa: la app no tiene nada que leer ni que pintar.</summary>
    public void MostrarSubtitulosDelMotor(PistaSubtitulosMotor pista)
    {
        int id;
        lock (_candado) id = _subtitulos.Where(s => s.Pista == pista).Select(s => s.IdMpv).DefaultIfEmpty(-1).First();
        if (id >= 0) Fijar("sid", id.ToString(CultureInfo.InvariantCulture));
    }

    public void ApagarSubtitulosDelMotor() => Fijar("sid", "no");

    private void Fijar(string nombre, string valor)
    {
        if (!_desechado) LibMpv.mpv_set_property_string(_mpv, nombre, valor);
    }

    private string Leer(string nombre) => _desechado ? string.Empty : LibMpv.Leer(_mpv, nombre);

    private void Ordenar(params string[] argumentos)
    {
        if (_desechado) return;
        int resultado = LibMpv.Orden(_mpv, argumentos);
        if (resultado < 0) AppLogger.Debug("MotorMpv", $"La orden '{argumentos[0]}' falló: {LibMpv.TextoDeError(resultado)}.");
    }

    private void AtenderEventos()
    {
        try
        {
            while (!_desechado)
            {
                var evento = Marshal.PtrToStructure<LibMpv.Evento>(LibMpv.mpv_wait_event(_mpv, 0.5));
                switch (evento.Id)
                {
                    case LibMpv.EventoArchivoCargado:
                        LeerPistas();
                        _hayArchivo = true;
                        _abriendo = false;
                        AperturaTerminada?.Invoke(this, new AperturaTerminada(ClaseApertura.Video, true, null));
                        break;

                    case LibMpv.EventoFinDeArchivo:
                        var fin = Marshal.PtrToStructure<LibMpv.EventoFin>(evento.Datos);
                        if (fin.Razon != LibMpv.FinPorError) break; // parar o cargar otro archivo también pasan por aquí
                        _hayArchivo = false;
                        _abriendo = false;
                        _fallido = true;
                        AperturaTerminada?.Invoke(this, new AperturaTerminada(ClaseApertura.Video, false, LibMpv.TextoDeError(fin.Error)));
                        break;

                    case LibMpv.EventoReinicioDeReproduccion:
                        // Sin superficie nadie cuenta fotogramas: cada arranque tras abrir o saltar vale por los que se habrían pintado.
                        if (_sinSalida) Interlocked.Add(ref _fotogramas, 2);
                        break;

                    case LibMpv.EventoCambioDePropiedad:
                        AtenderPropiedad(evento.Usuario, Marshal.PtrToStructure<LibMpv.EventoPropiedad>(evento.Datos));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MotorMpv", $"El hilo de eventos de libmpv terminó con un error: {ex.Message}");
        }
        finally
        {
            LibMpv.mpv_terminate_destroy(_mpv);
        }
    }

    private void AtenderPropiedad(ulong id, LibMpv.EventoPropiedad propiedad)
    {
        if (propiedad.Datos == 0) return; // la propiedad dejó de tener valor (sin archivo)

        switch (id)
        {
            case IdPosicion when propiedad.Formato == LibMpv.FormatoDoble:
                Interlocked.Exchange(ref _posicionTicks, TimeSpan.FromSeconds(Math.Max(0, Marshal.PtrToStructure<double>(propiedad.Datos))).Ticks);
                break;
            case IdDuracion when propiedad.Formato == LibMpv.FormatoDoble:
                Interlocked.Exchange(ref _duracionTicks, TimeSpan.FromSeconds(Math.Max(0, Marshal.PtrToStructure<double>(propiedad.Datos))).Ticks);
                break;
            case IdPausa when propiedad.Formato == LibMpv.FormatoBandera:
                _pausa = Marshal.ReadInt32(propiedad.Datos) != 0;
                break;
            case IdFin when propiedad.Formato == LibMpv.FormatoBandera:
                _fin = Marshal.ReadInt32(propiedad.Datos) != 0;
                break;
            case IdProporcion when propiedad.Formato == LibMpv.FormatoDoble:
                Interlocked.Exchange(ref _proporcionBits, BitConverter.DoubleToInt64Bits(Marshal.PtrToStructure<double>(propiedad.Datos)));
                ProporcionVideoCambiada?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>
    /// Las pistas del archivo recién cargado. El índice que ve la app es el del flujo dentro del contenedor (el que usa ffmpeg para
    /// leer los subtítulos); libmpv las numera a su manera, así que se guarda la pareja.
    /// </summary>
    private void LeerPistas()
    {
        var audio = new List<(int, PistaAudioMotor)>();
        var subtitulos = new List<(int, PistaSubtitulosMotor)>();
        int cuantas = int.TryParse(Leer("track-list/count"), out int n) ? n : 0;
        for (int i = 0; i < cuantas; i++)
        {
            string raiz = $"track-list/{i}/";
            if (!int.TryParse(Leer(raiz + "id"), out int id)) continue;
            int indice = int.TryParse(Leer(raiz + "ff-index"), out int flujo) ? flujo : id;
            string? idioma = IdiomaDosLetras(Leer(raiz + "lang"));
            string? titulo = Leer(raiz + "title") is { Length: > 0 } t ? t : null;

            switch (Leer(raiz + "type"))
            {
                case "audio":
                    audio.Add((id, new PistaAudioMotor(indice, idioma, titulo)));
                    break;
                case "sub":
                    string codec = Leer(raiz + "codec");
                    string? externa = Leer(raiz + "external") == "yes" && Leer(raiz + "external-filename") is { Length: > 0 } ruta ? ruta : null;
                    bool esImagen = codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";
                    subtitulos.Add((id, new PistaSubtitulosMotor(indice, idioma, titulo, esImagen, codec is "ass" or "ssa", externa)));
                    break;
            }
        }
        lock (_candado)
        {
            _audio = audio;
            _subtitulos = subtitulos;
        }
    }

    public void Dispose()
    {
        if (_desechado) return;
        try { Desechando?.Invoke(); }
        catch (Exception ex) { AppLogger.Debug("MotorMpv", $"No se pudo soltar el pintor: {ex.Message}"); }
        _desechado = true;
        LibMpv.mpv_wakeup(_mpv); // el hilo de eventos sale de su espera y cierra libmpv
        if (Thread.CurrentThread != _hiloEventos) _hiloEventos.Join(TimeSpan.FromSeconds(5));
    }
}
```

Notas:
- `mpv_terminate_destroy` va en el hilo de eventos, después de salir del bucle: así nunca se llama a libmpv con el manejador ya destruido desde ese hilo. Las órdenes de otros hilos comprueban `_desechado` antes; la ventana entre esa comprobación y la destrucción es de microsegundos y solo se da al cerrar el reproductor, cuando ya nadie da órdenes.
- Si `video-format` no devolviera `h264` para el video de prueba, usar `video-codec` y ajustar la prueba al valor real, anotándolo.
- Si `ff-index` no existiera en esta versión para alguna pista, se usa el `id` de mpv (ya contemplado).

- [ ] **Paso 3: verificación**

Compilar la app (0 advertencias) y `--filter "FullyQualifiedName~MotorMpv"`. Esperado: todas en verde, con la biblioteca presente en `AnimeLocalTracker.Tests\bin\Debug\…\Mpv\`. Si alguna espera se queda corta en esta máquina, se sube el tope de `EsperarHasta`, no se añade una espera fija.

---

## Tarea 4: `SuperficieMpv`

**Archivos:** crear `AnimeLocalTracker/Controls/SuperficieMpv.cs`.

**Interfaces — consume:** `MotorMpv.Manejador`, `ContarFotograma()`, `Desechando`; las llamadas de pintado de `LibMpv`. **Produce:** el control `SuperficieMpv` con la propiedad `Motor` (`IMotorVideo`).

Sin pruebas automáticas (necesita ventana y tarjeta gráfica): se verifica en la app (tarea 7). El código parte del arnés medido en la prueba de pintado (sección 3.2 del diseño).

- [ ] **Paso 1: crear el archivo**

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Native;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Wpf;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// Donde se ve el video del motor nuevo: un lienzo OpenGL que WPF compone como una imagen más (así los controles van encima como
/// WPF normal, sin segunda ventana). libmpv avisa de que hay fotograma y se pinta en el siguiente paso de composición.
/// Con un motor que no sea <see cref="MotorMpv"/> no hace nada.
/// </summary>
public sealed class SuperficieMpv : Grid
{
    public static readonly DependencyProperty MotorProperty = DependencyProperty.Register(
        nameof(Motor), typeof(IMotorVideo), typeof(SuperficieMpv),
        new PropertyMetadata(null, (d, e) => ((SuperficieMpv)d).CambiarMotor(e.NewValue as MotorMpv)));

    public IMotorVideo? Motor
    {
        get => (IMotorVideo?)GetValue(MotorProperty);
        set => SetValue(MotorProperty, value);
    }

    private readonly GLWpfControl _lienzo = new();
    private MotorMpv? _motor;
    private nint _pintor;
    private int _pintadoPedido;

    // Vivos mientras exista el pintor: libmpv guarda los punteros.
    private ObtenerFuncionGl? _obtenerFuncion;
    private AvisoDePintado? _aviso;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ObtenerFuncionGl(nint contexto, [MarshalAs(UnmanagedType.LPStr)] string nombre);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvisoDePintado(nint contexto);

    public SuperficieMpv()
    {
        Children.Add(_lienzo);
        _lienzo.Render += Pintar;
        _lienzo.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = false });
        Unloaded += (_, _) => SoltarPintor();
        Loaded += (_, _) => PedirPintado();
        SizeChanged += (_, _) => PedirPintado(); // en pausa nadie avisa: al cambiar de tamaño hay que repintar el mismo fotograma
    }

    private void CambiarMotor(MotorMpv? motor)
    {
        SoltarPintor();
        if (_motor != null) _motor.Desechando -= SoltarPintor;
        _motor = motor;
        if (_motor != null) _motor.Desechando += SoltarPintor;
        PedirPintado();
    }

    /// <summary>Desde cualquier hilo: pide un pintado en el hilo de la interfaz, sin acumular peticiones.</summary>
    private void PedirPintado()
    {
        if (Interlocked.Exchange(ref _pintadoPedido, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _pintadoPedido, 0);
            _lienzo.InvalidateVisual();
        });
    }

    /// <summary>Con el contexto OpenGL del lienzo ya activo: la primera vez crea el pintor de libmpv; después pinta el fotograma.</summary>
    private void Pintar(TimeSpan _)
    {
        var motor = _motor;
        if (motor == null || motor.Desechado) return;

        if (_pintor == 0 && !CrearPintor(motor)) return;

        bool hayFotogramaNuevo = (LibMpv.mpv_render_context_update(_pintor) & LibMpv.HayFotogramaNuevo) != 0;
        var fbo = new LibMpv.Fbo { Id = _lienzo.Framebuffer, Ancho = _lienzo.FrameBufferWidth, Alto = _lienzo.FrameBufferHeight };
        nint pFbo = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpv.Fbo>());
        nint pVoltear = Marshal.AllocHGlobal(sizeof(int));
        nint pBloquear = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.StructureToPtr(fbo, pFbo, false);
            Marshal.WriteInt32(pVoltear, 1);
            Marshal.WriteInt32(pBloquear, 0); // no parar el hilo de la interfaz esperando al momento exacto del fotograma
            LibMpv.mpv_render_context_render(_pintor,
            [
                new() { Tipo = LibMpv.ParametroFbo, Datos = pFbo },
                new() { Tipo = LibMpv.ParametroVoltearY, Datos = pVoltear },
                new() { Tipo = LibMpv.ParametroBloquearHastaSuMomento, Datos = pBloquear },
                default
            ]);
            if (hayFotogramaNuevo) motor.ContarFotograma();
        }
        finally
        {
            Marshal.FreeHGlobal(pFbo);
            Marshal.FreeHGlobal(pVoltear);
            Marshal.FreeHGlobal(pBloquear);
        }
    }

    private bool CrearPintor(MotorMpv motor)
    {
        _obtenerFuncion = (_, nombre) => GLFW.GetProcAddress(nombre);
        _aviso = _ => PedirPintado();
        nint pInicio = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpv.InicioOpenGl>());
        nint api = Marshal.StringToHGlobalAnsi("opengl");
        try
        {
            Marshal.StructureToPtr(new LibMpv.InicioOpenGl { ObtenerFuncion = Marshal.GetFunctionPointerForDelegate(_obtenerFuncion) }, pInicio, false);
            int resultado = LibMpv.mpv_render_context_create(out _pintor, motor.Manejador,
            [
                new() { Tipo = LibMpv.ParametroTipoApi, Datos = api },
                new() { Tipo = LibMpv.ParametroInicioGl, Datos = pInicio },
                default
            ]);
            if (resultado < 0)
            {
                _pintor = 0;
                AppLogger.Warn("SuperficieMpv", $"libmpv no pudo pintar en la ventana: {LibMpv.TextoDeError(resultado)}.");
                return false;
            }
            LibMpv.mpv_render_context_set_update_callback(_pintor, Marshal.GetFunctionPointerForDelegate(_aviso), 0);
            AppLogger.Debug("SuperficieMpv", $"Pintor de libmpv creado ({_lienzo.FrameBufferWidth}x{_lienzo.FrameBufferHeight}).");
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pInicio);
            Marshal.FreeHGlobal(api);
        }
    }

    private void SoltarPintor()
    {
        if (_pintor == 0) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(SoltarPintor); return; }

        nint pintor = _pintor;
        _pintor = 0;
        try
        {
            LibMpv.mpv_render_context_set_update_callback(pintor, 0, 0);
            LibMpv.mpv_render_context_free(pintor);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SuperficieMpv", $"No se pudo soltar el pintor de libmpv: {ex.Message}");
        }
        _obtenerFuncion = null;
        _aviso = null;
    }
}
```

Notas:
- Si `mpv_render_context_free` exigiera el contexto OpenGL activo y fallara fuera del pintado, activarlo antes con el contexto del lienzo (`_lienzo.Context?.MakeCurrent()`), comprobándolo en la app al salir de un episodio (no debe quedar ningún aviso ni cierre).
- Si `GLFW.GetProcAddress` no fuera accesible con este paquete, usar el mismo camino que el arnés de la prueba de pintado (carpeta temporal de la sesión, `mpv/Wpf/Program.cs`).

- [ ] **Paso 2: verificación** — compilar la app, 0 advertencias.

---

## Tarea 5: el reproductor elige motor

**Archivos:** modificar `Models/AppSettings.cs`, `ViewModels/ReproductorViewModel.cs`, `ViewModels/NavigationService.cs`, `AnimeLocalTracker.Tests/ViewModels/ReproductorConMotorTests.cs`.

**Interfaces — produce:** `AppSettings.MotorVideo` (`"Flyleaf"` | `"Mpv"`), `MotorVideo.ElegirMotor(string? ajuste, bool mpvDisponible) : bool` (true = libmpv), `ReproductorViewModel.UsaMotorMpv`, `ReproductorViewModel.PistasSubtitulosMenu` (`ObservableCollection<OpcionPistaSubtitulos>`), `OpcionPistaSubtitulos(PistaSubtitulosMotor Pista, string Nombre)`.

- [ ] **Paso 1: pruebas (fallarán al compilar)** — añadir a `ReproductorConMotorTests`

```csharp
    [Theory]
    [InlineData("Mpv", true, true)]
    [InlineData("mpv", true, true)]
    [InlineData("Mpv", false, false)] // falta la biblioteca: se sigue con el motor clásico
    [InlineData("Flyleaf", true, false)]
    [InlineData(null, true, false)]
    [InlineData("cualquier otra cosa", true, false)]
    public void ConElAjusteEnMpvPeroSinBiblioteca_SeUsaFlyleaf(string? ajuste, bool mpvDisponible, bool esperado)
    {
        MotorVideo.ElegirMotor(ajuste, mpvDisponible).Should().Be(esperado);
    }

    [Fact]
    public async Task ElMenuDeSubtitulos_ListaLasPistasDelMotor_ConNombreLegible()
    {
        _motor.Subtitulos.Add(new PistaSubtitulosMotor(3, "es", null, true, false, null));
        _motor.Subtitulos.Add(new PistaSubtitulosMotor(4, "en", "Signs & Songs", true, false, null));
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();

        sut.PistasSubtitulosMenu.Select(p => p.Pista.Indice).Should().Equal(3, 4);
        sut.PistasSubtitulosMenu[1].Nombre.Should().Contain("Signs & Songs");

        sut.SelectSubtitleStreamCommand.Execute(sut.PistasSubtitulosMenu[1].Pista);
        _motor.SubtitulosDelMotor!.Indice.Should().Be(4);
    }

    [Fact]
    public void ConUnMotorInyectado_NoSeUsaLaVistaDelMotorNuevo()
    {
        using var sut = CrearSut(_motor);

        sut.UsaMotorMpv.Should().BeFalse("solo el motor que elige la app por el ajuste decide la vista");
    }
```

(Añadir `using System.Linq;` si falta.)

- [ ] **Paso 2: implementación**

  1. `AppSettings.cs`, en el bloque "Rendimiento de video":

```csharp
    /// <summary>Motor del reproductor de episodios: "Flyleaf" (el de siempre) o "Mpv" (libmpv, en pruebas: ver
    /// docs/investigacion-motor-libmpv.md). Sin interfaz todavía: se cambia a mano en settings.json.</summary>
    public string MotorVideo { get; set; } = "Flyleaf";
```

  2. `MotorVideo.cs`:

```csharp
    /// <summary>True si toca el motor nuevo: lo pide el ajuste y la biblioteca está en la instalación.</summary>
    public static bool ElegirMotor(string? ajuste, bool mpvDisponible) =>
        mpvDisponible && string.Equals(ajuste?.Trim(), "Mpv", StringComparison.OrdinalIgnoreCase);
```

  3. `ReproductorViewModel.cs`, constructor (sustituye a `_fabricaMotor = fabricaMotor ?? MotorFlyleaf.Crear;`):

```csharp
        if (fabricaMotor != null)
        {
            _fabricaMotor = fabricaMotor;
        }
        else
        {
            string? ajusteMotor = settingsService?.ObtenerConfiguracion()?.MotorVideo;
            bool pideMpv = string.Equals(ajusteMotor?.Trim(), "Mpv", StringComparison.OrdinalIgnoreCase);
            // La biblioteca solo se carga si el ajuste la pide: con el motor clásico ni se toca.
            UsaMotorMpv = pideMpv && !EsEntornoPruebas() && MotorVideo.ElegirMotor(ajusteMotor, Services.Native.LibMpv.Disponible);
            if (pideMpv && !UsaMotorMpv) AppLogger.Warn("ReproductorViewModel", "El ajuste pide el motor libmpv pero su biblioteca no está: se usa el motor clásico.");
            _fabricaMotor = UsaMotorMpv ? ajustes => MotorMpv.Crear(ajustes) : MotorFlyleaf.Crear;
        }
```

     y la propiedad:

```csharp
    /// <summary>El reproductor usa libmpv: la plantilla de App.xaml elige con esto la vista que pinta el video dentro de WPF.</summary>
    public bool UsaMotorMpv { get; }
```

     (Mover la lectura de `settingsService` a donde ya esté asignado si el constructor lo asigna antes; `EsEntornoPruebas()` evita cargar libmpv y OpenTK bajo el ejecutor de pruebas.)

  4. Menú de subtítulos neutral. Junto a `PistasAudio`:

```csharp
    /// <summary>Pistas de subtítulos del archivo abierto, para el menú de la vista del motor nuevo.</summary>
    public System.Collections.ObjectModel.ObservableCollection<OpcionPistaSubtitulos> PistasSubtitulosMenu { get; } = new();

    private void ActualizarPistasSubtitulosMenu()
    {
        var opciones = (Motor?.PistasSubtitulos ?? Array.Empty<PistaSubtitulosMotor>())
            .Select((p, i) => new OpcionPistaSubtitulos(p, OpcionPistaAudio.ConstruirNombre(p.Idioma, p.Titulo, i + 1)))
            .ToList();
        Core.HiloUi.Ejecutar(() =>
        {
            PistasSubtitulosMenu.Clear();
            foreach (var o in opciones) PistasSubtitulosMenu.Add(o);
        });
    }
```

     Se llama en `AlTerminarApertura`, justo antes de `EvaluarSubtitulosPorDefecto();`. El registro, en `Models/OpcionPistaAudio.cs` (mismo archivo, es su pareja):

```csharp
/// <summary>Una pista de subtítulos en el menú del reproductor.</summary>
public sealed record OpcionPistaSubtitulos(Services.PistaSubtitulosMotor Pista, string Nombre);
```

  5. Sin mini con el motor nuevo. En `MinimizarAMini()`, primera línea:

```csharp
        // El mini reproductor llega en la etapa 3: dos vistas no pueden pintar a la vez el mismo video de libmpv.
        if (UsaMotorMpv) return;
```

  6. `NavigationService.cs`, donde hoy dice `if (!ReproductorActivo.EsModoMini) ReproductorActivo.MinimizarAMini();`:

```csharp
        if (ReproductorActivo.UsaMotorMpv)
        {
            // Etapa 2: con el motor nuevo aún no hay mini reproductor; navegar cierra el episodio guardando el progreso.
            ReproductorActivo.CerrarPorNavegacion();
            return;
        }
        if (!ReproductorActivo.EsModoMini) ReproductorActivo.MinimizarAMini();
```

     `CerrarPorNavegacion()` es un método público nuevo del reproductor, junto a `SalirDelReproductor`: lo mismo sin navegar (la navegación ya ocurrió) ni recuperar el foco.

```csharp
    /// <summary>Cierra el episodio porque el usuario navegó a otra parte de la app (con el motor nuevo aún no hay mini reproductor
    /// que lo siga mostrando). Como <see cref="SalirDelReproductor"/>, pero sin volver a la vista anterior.</summary>
    public void CerrarPorNavegacion()
    {
        AppLogger.Info("ReproductorViewModel", $"Saliendo del reproductor (episodio {_episodio}, {TimeSpan.FromSeconds(CurrentSeconds):mm\\:ss}): se navegó a otra parte de la app.");
        _ = GuardarProgresoActualAsync();
        Dispose();
    }
```

     Tras llamarlo, `NavigationService` debe dejar de considerarlo el reproductor activo: buscar dónde se pone `ReproductorActivo = null` al recibir `NavegarMensaje_VolverDelReproductor` y hacer lo mismo aquí (sin navegar). Añadir a `ReproductorConMotorTests`:

```csharp
    [Fact]
    public async Task CerrarPorNavegacion_GuardaElProgresoYDesechaElMotor()
    {
        using var sut = CrearSut(_motor);
        await sut.CargarVideoAsync(_ruta, 101, "Frieren", 5);
        _motor.TerminarApertura();
        _motor.FotogramasMostrados = 3;
        await EsperarHasta(() => !sut.OcultarVideoInicio);
        _motor.Posicion = TimeSpan.FromSeconds(120);

        sut.CerrarPorNavegacion();

        _motor.VecesDesechado.Should().Be(1);
        await EsperarHasta(() => _estado.Invocations.Any(i => i.Method.Name == nameof(IPlaybackStateService.GuardarProgresoAsync)));
    }
```

- [ ] **Paso 3: verificación** — compilar (0 advertencias) y `--filter "FullyQualifiedName~ReproductorConMotorTests|FullyQualifiedName~Reproductor|FullyQualifiedName~PipNavigation|FullyQualifiedName~Navigation"`.

---

## Tarea 6: la vista del motor nuevo y los avisos de licencia

**Archivos:** crear `Views/ReproductorMpvView.xaml` y `.xaml.cs` (copias de `ReproductorView.*`); modificar `App.xaml`, `Services/LocalizationService.cs`, `Views/AcercaDeView.xaml`, `README.md`.

- [ ] **Paso 1: copiar y adaptar el XAML**

`cp Views/ReproductorView.xaml Views/ReproductorMpvView.xaml` y, en la copia:

| Antes | Después |
|---|---|
| `x:Class="AnimeLocalTracker.Views.ReproductorView"` | `x:Class="AnimeLocalTracker.Views.ReproductorMpvView"` |
| `xmlns:fl="clr-namespace:FlyleafLib.Controls.WPF;assembly=FlyleafLib"` | se borra (comprobar que existe `xmlns:ctl` para `AnimeLocalTracker.Controls`; si no, añadirlo) |
| el comentario del doble clic del host y `<fl:FlyleafHost Player="{Binding Player}" x:Name="HostFlyleaf">` + su `<Grid>` hijo | `<Grid x:Name="HostVideo">` con, como **primer** hijo, `<ctl:SuperficieMpv Motor="{Binding Motor}"/>`, y a continuación los hijos que tenía el `<Grid>` del host |
| `</Grid>` + `</fl:FlyleafHost>` del final | `</Grid>` |
| `ItemsSource="{Binding Player.Subtitles.Streams}"` y su plantilla (tres `Run` con `Language` y `Title`) | `ItemsSource="{Binding PistasSubtitulosMenu}"`; `CommandParameter="{Binding Pista}"`; el texto, `<TextBlock Text="{Binding Nombre, Mode=OneWay}"/>` |

Los enlaces por `ElementName` que el anfitrión de Flyleaf obligaba a usar se dejan como están: siguen siendo válidos.

- [ ] **Paso 2: copiar y adaptar el código de la vista**

`cp Views/ReproductorView.xaml.cs Views/ReproductorMpvView.xaml.cs` y, en la copia:

1. Clase y constructor → `ReproductorMpvView`. Comentario de cabecera: "Vista del reproductor con el motor nuevo (libmpv): copia de ReproductorView sin las ventanas nativas de Flyleaf. Mientras convivan, un cambio en los controles va en las dos."
2. **Se borran** con todas sus llamadas: `DesactivarInteraccionNativaDelHost`, `EngancharTecladoEnVentanasDeFlyleaf`, `DesengancharTecladoDeVentanasDeFlyleaf`, `HostFlyleaf_VentanaCreada`, `_ventanasConTeclado`, `FuenteNativa`, `BordeMiniHook`, `DesactivarTeclasPropiasDeFlyleaf`, las suscripciones a `HostFlyleaf.OverlayCreated`/`SurfaceCreated`, el bloque `try { HostFlyleaf.Dispose(); } …` de `Unloaded`, `EnlazarSubtitulosDelPlayer`, `Subtitulos_PropertyChanged`, el campo `_subtitulosPlayer` y la rama de `VmSubtitulos_PropertyChanged` que reacciona a `nameof(ReproductorViewModel.Player)`.
3. `AppActiva()` → `(Application.Current?.MainWindow?.IsActive ?? false) || (Window.GetWindow(this)?.IsActive ?? false)`.
4. `PunteroSobreElReproductor()` → la comparación queda solo con `Manejador(Window.GetWindow(this))`.
5. `ActualizarTextoSubtitulos`: donde cae al texto de Flyleaf (`(_subtitulosPlayer as FlyleafLib.MediaPlayer.Subtitles)?.SubsText`), queda `string.Empty` (con el motor nuevo, lo que no lee la app lo dibuja libmpv dentro del video).
6. `CapaAss_Rendering` (y cualquier otro uso de `vm.Player`): la posición sale de `vm.Motor?.Posicion` (`TimeSpan`); donde se comprobaba `player == null || player.IsDisposed`, `vm.Motor == null || vm.Motor.Desechado`.
7. Quitar los `using FlyleafLib…` y lo que deje de usarse (el compilador lo dirá: 0 advertencias).
8. `grep -n "Flyleaf\|HostFlyleaf\|\.Player\b" Views/ReproductorMpvView.xaml Views/ReproductorMpvView.xaml.cs` → solo comentarios que expliquen por qué algo es como es; ninguno de código.

- [ ] **Paso 3: la plantilla elige la vista** — `App.xaml`, sustituye la plantilla de `ReproductorViewModel`:

```xml
            <DataTemplate DataType="{x:Type vm:ReproductorViewModel}">
                <!-- Dos vistas mientras conviven los dos motores (docs/investigacion-motor-libmpv.md): la clásica (Flyleaf) y la del
                     motor nuevo, que pinta el video dentro de WPF. El reproductor lo decide al construirse. -->
                <ContentControl Content="{Binding}" Focusable="False">
                    <ContentControl.Style>
                        <Style TargetType="ContentControl">
                            <Setter Property="ContentTemplate">
                                <Setter.Value>
                                    <DataTemplate><views:ReproductorView/></DataTemplate>
                                </Setter.Value>
                            </Setter>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding UsaMotorMpv}" Value="True">
                                    <Setter Property="ContentTemplate">
                                        <Setter.Value>
                                            <DataTemplate><views:ReproductorMpvView/></DataTemplate>
                                        </Setter.Value>
                                    </Setter>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </ContentControl.Style>
                </ContentControl>
            </DataTemplate>
```

Comprobar que nada busca la vista por su tipo (`grep -rn "ReproductorView\b" AnimeLocalTracker --include=*.cs`): en `a2eb985` solo la nombran comentarios. Si la réplica de recursos de las pruebas de vistas (`AnimeLocalTracker.Tests/Views/WpfHostFixture.cs`) necesita algo, añadirlo.

- [ ] **Paso 4: avisos de licencia** (skill `localizacion`)

  1. `LocalizationService.cs`, en las dos mitades: `Acerca_Tecnologias` añade "libmpv" a la lista (ES: "…WPF, Flyleaf, libmpv, SQLite-net…"; EN igual), y una clave nueva:
     - ES `["Acerca_AvisoMpv"] = "Incluye mpv (libmpv), distribuido bajo la licencia GPL versión 2 o posterior. Su código fuente está en mpv.io; el de esta app, en GitHub."`
     - EN `["Acerca_AvisoMpv"] = "Includes mpv (libmpv), distributed under the GPL version 2 or later. Its source code is at mpv.io; this app's is on GitHub."`
  2. `AcercaDeView.xaml`: un chip `libmpv` junto al de `Flyleaf` (mismo estilo `AcercaTechChip`) y, bajo `LicenciaTexto`, un `TextBlock` con `{loc:T Acerca_AvisoMpv}`, estilo `AppText.Caption`, `TextWrapping="Wrap"`.
  3. `README.md`, bajo la línea de la licencia MIT: "El instalador incluye [mpv](https://mpv.io) (libmpv, compilación de shinchiro `20261011-git-accb7b2821`), distribuido bajo la GPL versión 2 o posterior; su código fuente está en https://github.com/mpv-player/mpv. El código de AnimeLocalTracker sigue bajo la licencia MIT."

- [ ] **Paso 5: verificación** — compilar la app (0 advertencias) y `--filter "FullyQualifiedName~Views|FullyQualifiedName~Localizacion|FullyQualifiedName~AcercaDe"`.

---

## Tarea 7: verificación final

- [ ] **Paso 1: suite completa, una vez.** Esperado: 0 advertencias; 3101 + las nuevas, todas en verde.

- [ ] **Paso 2: con el ajuste por defecto, nada cambia.** Perfil aislado sin tocar `MotorVideo`: abrir un episodio; en el registro, `Motor de video iniciado` (Flyleaf) y ninguna línea de `MotorMpv` ni de `SuperficieMpv`; el proceso no tiene cargada `libmpv-2.dll` (`(Get-Process -Id <pid>).Modules | Where-Object ModuleName -like 'libmpv*'` vacío).

- [ ] **Paso 3: con `"MotorVideo": "Mpv"` en el `settings.json` del perfil** (`crear --con-rutas-reales`, sin red, volumen 0), por registro y capturas:

| Qué | Se espera |
|---|---|
| Youjo Senki ep. 2 | `Motor libmpv listo`, `Pintor de libmpv creado`, `[Arranque] Primer fotograma`, `Video: h264 por tarjeta gráfica`; en la captura, el video con los subtítulos de la app encima y los controles |
| Reanudar | Cerrar y reabrir: `Seek de reanudación` y `Reanudación asentada` |
| Tokyo Revengers T4 ep. 2 (AV1) | Abre a la primera, `por procesador`, **sin** la línea de reapertura por procesador |
| Memoria | Con la app abierta en el episodio, anotar la memoria privada del proceso |
| Salida | Al cerrar la app no queda ningún proceso ni aviso de `SuperficieMpv` |

  Lo que no se pueda manejar por falta de foco (trampa conocida del equipo de pruebas) se deja en la lista para el propietario: pausar, saltos, volumen, velocidad, episodio siguiente y que navegar a otra pestaña cierre el episodio.

- [ ] **Paso 4: informe** — qué se verificó y qué no, el peso añadido al instalador (libmpv y OpenTK) y la memoria medida; esperar la orden para el commit.

---

## Lo que esta etapa deja para las siguientes

- Etapa 3: subtítulos dibujados por libmpv con el estilo del usuario, ecualizador y modo noche, captura, mini reproductor, tarjeta gráfica, informe de video.
- `MotorMpv.TarjetaEnUso`, `EscaladoInteligente`, `CapturarFotograma`, `AplicarFiltrosAudio`, `CambiarFiltroEnVivo` y `PrecalentarArchivo` no hacen nada todavía.
- El contador de fotogramas sin superficie (pruebas) se aproxima con los reinicios de reproducción.

---

## Notas de ejecución (2026-10-10 y 2026-10-11)

Lo que la prueba en la app obligó a cambiar respecto al código de este plan:

- **El archivo no se carga hasta que hay dónde pintar.** El reproductor abre en cuanto tiene motor y la vista tarda en crear el pintor (hasta 2 s recién abierta la app). Cargando antes, libmpv abría el archivo sin video ("No render context set") y lo iba reproduciendo en silencio: el episodio empezaba con segundos consumidos y, con la vista lenta, el vigilante de 3 s del reproductor lo reabría por procesador. `MotorMpv.Abrir` guarda la ruta y `PintorCreado` hace el `loadfile`.
- **Reactivar la pista de video** (`ReactivarVideoSiHaceFalta`): un archivo abierto que se queda sin pintor pierde su video y pedir `vid=auto` no lo recupera; hay que fijar `vid` al id de la pista. Queda para cuando el pintor se suelte con un archivo abierto (mini reproductor, etapa 3).
- **Duración, tamaño y códec se leen al cargar el archivo**, antes del aviso de apertura: las propiedades observadas llegan como eventos posteriores y el reproductor las necesita en ese aviso (salto de reanudación, dibujo de subtítulos ASS).
- Variable de diagnóstico `ANIMELOCALTRACKER_MPV_LOG=<ruta>`: registro propio de libmpv.

Medido en la app (Intel HD 620, 1366×768): imagen visible a 1,3–1,4 s en el primer episodio y 0,4–0,8 s en los siguientes; 24 fotogramas por segundo en H.264 (tarjeta gráfica) y AV1 (procesador); memoria de la app reproduciendo ~570 MB (Flyleaf 455–477 MB en la misma vuelta de seis episodios), sin crecer en una segunda vuelta.
