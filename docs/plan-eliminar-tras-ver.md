# Eliminar tras ver: plan de implementación

> **Para quien ejecute el plan:** ejecutar en la sesión con `superpowers:executing-plans` (el repo prohíbe lanzar subagentes por iniciativa propia). Los pasos usan casillas `- [ ]`. **Sin commit**: la regla del repo dice que no se hace commit ni push sin orden del usuario, así que cada tarea termina en verificación, no en commit.

**Objetivo:** que, con un ajuste elegido por el usuario, la app borre sola el video de los episodios ya vistos (al terminar de verlos, al completar la serie o conservando solo los últimos N), conservando el historial.

**Arquitectura:** una función pura decide qué episodios borrar; un servicio pequeño (`LimpiadorDeEpisodios`) lo ejecuta con las protecciones; el reproductor lo avisa en dos puntos pequeños (al liberarse y al cambiar de episodio). El borrado de disco (video + miniatura) se extrae a un helper compartido con el borrado manual. Ajuste en Configuración con un selector de modo.

**Tecnología:** C# 12 / .NET 8, WPF, CommunityToolkit.Mvvm, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md` (sección 4).

## Restricciones globales

- Solo la Parte 2. La Parte 1 (botón de temporada) ya está hecha y no se toca.
- El borrado es **definitivo** (no Papelera) y el modo viene **apagado** por defecto. Un valor desconocido en los ajustes equivale a apagado.
- 0 advertencias: `TreatWarningsAsErrors` está activo (`Directory.Build.props`).
- Textos con `{loc:T Clave}` en XAML y `LocalizationService.T("Clave")` en código; toda clave nueva, en ES **y** EN.
- Colores solo de `Brush.*`/`AppText.*` o los literales ya usados en esa vista; `AutomationProperties.Name` en los controles nuevos.
- El trabajo de disco va fuera del hilo de interfaz (`Task.Run` o `ConfigureAwait(false)`).
- Las pruebas nunca escriben ni borran bajo `AppDataPaths`: solo carpetas temporales (`Path.GetTempPath()`); la carpeta de miniaturas ya la redirige `TestInitializer`.
- Verificación según `repo-build-test`: pruebas afectadas por tarea; la suite completa **una sola vez** al final de la Tarea 5.
- La prueba en la app real, **solo** con un perfil aislado y un video de prueba generado: nunca con series reales ni la instancia del usuario (memoria "Pruebas en la app real"). Cerrar solo el PID lanzado.

## Enfoque de revisión (casos que el diseño implica y conviene fijar con pruebas)

1. **Episodio sin marca de visto en la base de datos** (marcado manual, o salir antes de que se guarde): no borra nada. Pruebas en Tarea 3.
2. **Archivo fuera de la carpeta del anime**: no se borra. Prueba en Tarea 3.
3. **Archivo en uso**: se deja y se sigue con los demás; solo se conserva el registro de los que sí se borraron. Prueba en Tarea 3.
4. **Total de episodios desconocido** (`TotalEpisodios = 0`): "Al completar la serie" no actúa. Prueba en Tarea 3.
5. **Salir del reproductor justo al cruzar el umbral** (la marca de visto aún se está guardando): el servicio espera a que se guarde y, si no llega, no borra. Prueba en Tarea 3.
6. **El usuario rechaza el aviso al elegir un modo que borra**: el selector vuelve al valor anterior y no se guarda nada. Prueba en Tarea 2.

## Desviaciones respecto al diseño (ya corregidas en el documento de diseño)

- **Punto de enganche:** el diseño decía `SalirDelReproductor` e `IrAEpisodio`. El código lo corrige: `Dispose()` del reproductor es idempotente y también se ejecuta al navegar a otra pestaña (no solo al pulsar salir), y todos los cambios de episodio pasan por `AsignarMetadatosDeEpisodio`. Los enganches van ahí.
- **Cómo se sabe que se vio:** no se confía en un indicador del reproductor. El servicio espera (hasta 10 s) a que la base de datos muestre el episodio como visto; el guardado local ocurre antes de sincronizar con AniList, pero es asíncrono. Si no llega, no borra.
- **Espera inicial de 1 s** antes de borrar, para que el reproductor suelte el archivo (además de los 15 reintentos de `BorradoDeArchivos`).
- **N entre 1 y 10** en el modo de consumo ligero.
- **El borrado manual se refactoriza** para usar el mismo helper (`BorradoDeEpisodio`), sin cambiar su comportamiento visible.

## Estructura de archivos

| Archivo | Cambio |
|---|---|
| `AnimeLocalTracker/Core/BorradoDeEpisodio.cs` | Crear: helper de borrado de video + miniatura |
| `AnimeLocalTracker/ViewModels/DetalleViewModel.Extras.cs` | Modificar: `LiberarEspacioAsync` usa el helper |
| `AnimeLocalTracker/ViewModels/EpisodiosFichaViewModel.cs` | Modificar: `EliminarEpisodio` usa el helper; escucha `ArchivoEpisodioEliminadoMensaje` |
| `AnimeLocalTracker/Models/AppSettings.cs` | Modificar: 2 ajustes y la clase de valores |
| `AnimeLocalTracker/ViewModels/ConfiguracionViewModel.cs` | Modificar: propiedades, carga, guardado y aviso |
| `AnimeLocalTracker/Views/ConfiguracionView.xaml` | Modificar: selector y número, en el grupo de Descargas |
| `AnimeLocalTracker/Services/LocalizationService.cs` | Modificar: claves `Cfg_*` y `Lim_*` en ES y EN |
| `AnimeLocalTracker/Core/LimpiezaTrasVer.cs` | Crear: función pura de decisión |
| `AnimeLocalTracker/Services/ILimpiadorDeEpisodios.cs` | Crear: interfaz |
| `AnimeLocalTracker/Services/LimpiadorDeEpisodios.cs` | Crear: servicio |
| `AnimeLocalTracker/App.xaml.cs` | Modificar: registro en DI |
| `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` | Modificar: parámetro opcional y dos enganches |
| `AnimeLocalTracker.Tests/Core/BorradoDeEpisodioTests.cs` | Crear |
| `AnimeLocalTracker.Tests/Core/LimpiezaTrasVerTests.cs` | Crear |
| `AnimeLocalTracker.Tests/ViewModels/ConfiguracionEliminarTrasVerTests.cs` | Crear |
| `AnimeLocalTracker.Tests/Services/LimpiadorDeEpisodiosTests.cs` | Crear |
| `AnimeLocalTracker.Tests/ViewModels/ReproductorEliminarTrasVerTests.cs` | Crear |
| `AnimeLocalTracker.Tests/ViewModels/DetalleEpisodiosSeguridadTests.cs` | Modificar: 2 pruebas del aviso de archivo eliminado |

---

### Tarea 1: Helper de borrado compartido

**Archivos:**
- Crear: `AnimeLocalTracker/Core/BorradoDeEpisodio.cs`
- Modificar: `AnimeLocalTracker/ViewModels/DetalleViewModel.Extras.cs` (bloque `Task.Run` dentro de `LiberarEspacioAsync`)
- Modificar: `AnimeLocalTracker/ViewModels/EpisodiosFichaViewModel.cs` (método `EliminarEpisodio`)
- Probar: `AnimeLocalTracker.Tests/Core/BorradoDeEpisodioTests.cs`

**Interfaces:**
- Produce: `BorradoDeEpisodio.BorrarVideoYMiniatura(string rutaVideo, int intentos = BorradoDeArchivos.IntentosPorDefecto) -> long` (bytes liberados; 0 si el video ya no existía; lanza si el video no se pudo borrar).

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/Core/BorradoDeEpisodioTests.cs`:

```csharp
using System;
using System.IO;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>El borrado de un episodio del disco (video + miniatura), compartido por el borrado manual y "Eliminar tras ver".</summary>
public sealed class BorradoDeEpisodioTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt-borrado-" + Guid.NewGuid().ToString("N"));

    public BorradoDeEpisodioTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); }
        catch (IOException) { /* limpieza de una carpeta temporal */ }
    }

    private string CrearVideo(int bytes)
    {
        string ruta = Path.Combine(_carpeta, "Episodio 01.mp4");
        File.WriteAllBytes(ruta, new byte[bytes]);
        return ruta;
    }

    private static string CrearMiniatura(string rutaVideo)
    {
        string miniatura = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(rutaVideo);
        Directory.CreateDirectory(Path.GetDirectoryName(miniatura)!);
        File.WriteAllBytes(miniatura, [1, 2, 3]);
        return miniatura;
    }

    [Fact]
    public void BorraElVideoYDevuelveLosBytesLiberados()
    {
        string video = CrearVideo(2048);

        long bytes = BorradoDeEpisodio.BorrarVideoYMiniatura(video, intentos: 1);

        bytes.Should().Be(2048);
        File.Exists(video).Should().BeFalse();
    }

    [Fact]
    public void BorraTambienLaMiniatura()
    {
        string video = CrearVideo(10);
        string miniatura = CrearMiniatura(video);

        BorradoDeEpisodio.BorrarVideoYMiniatura(video, intentos: 1);

        File.Exists(miniatura).Should().BeFalse();
    }

    [Fact]
    public void VideoInexistente_DevuelveCeroSinLanzar()
    {
        string video = Path.Combine(_carpeta, "no-existe.mp4");

        BorradoDeEpisodio.BorrarVideoYMiniatura(video, intentos: 1).Should().Be(0);
    }

    [Fact]
    public void VideoEnUso_LanzaYNoTocaLaMiniatura()
    {
        string video = CrearVideo(10);
        string miniatura = CrearMiniatura(video);
        using var abierto = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.None);

        Action borrar = () => BorradoDeEpisodio.BorrarVideoYMiniatura(video, intentos: 1);

        borrar.Should().Throw<IOException>();
        File.Exists(video).Should().BeTrue();
        File.Exists(miniatura).Should().BeTrue("si el video no se pudo borrar, el episodio sigue como estaba");
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~BorradoDeEpisodioTests"
```
Esperado: error de compilación `CS0103`/`CS0117` porque `BorradoDeEpisodio` no existe.

- [ ] **Paso 3: crear el helper**

Crear `AnimeLocalTracker/Core/BorradoDeEpisodio.cs`:

```csharp
using System;
using System.IO;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;

namespace AnimeLocalTracker.Core;

/// <summary>
/// El borrado de un episodio del disco: el video (con reintentos) y su miniatura. Lo comparten el borrado manual de la
/// ficha y "Eliminar tras ver", para que los dos hagan exactamente lo mismo.
/// </summary>
public static class BorradoDeEpisodio
{
    /// <summary>
    /// Borra el video y devuelve los bytes que liberó (0 si ya no existía). Si el video no se puede borrar tras los
    /// reintentos, lanza la excepción y NO toca la miniatura. La miniatura es opcional: la ficha puede tenerla abierta en
    /// pantalla y Windows negar el borrado; un fallo ahí se ignora (si no, un video ya borrado seguiría apareciendo en la
    /// lista). Bloquea el hilo mientras espera: llamarlo desde un hilo de trabajo (<c>Task.Run</c>), nunca desde la interfaz.
    /// </summary>
    public static long BorrarVideoYMiniatura(string rutaVideo, int intentos = BorradoDeArchivos.IntentosPorDefecto)
    {
        long bytes = 0;
        if (File.Exists(rutaVideo))
        {
            bytes = new FileInfo(rutaVideo).Length;
            BorradoDeArchivos.BorrarConReintentos(rutaVideo, intentos);
        }

        try
        {
            string miniatura = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(rutaVideo);
            if (File.Exists(miniatura)) File.Delete(miniatura);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("BorradoDeEpisodio", $"No se pudo borrar la miniatura de '{rutaVideo}': {ex.Message}");
        }

        return bytes;
    }
}
```

- [ ] **Paso 4: comprobar que pasan**

Mismos dos comandos del Paso 2. Esperado: las 4 pruebas pasan, 0 advertencias.

- [ ] **Paso 5: `LiberarEspacioAsync` usa el helper**

En `AnimeLocalTracker/ViewModels/DetalleViewModel.Extras.cs`, reemplazar el bloque (los dos `try` dentro de `Task.Run`):

```csharp
            bool ok = await Task.Run(() =>
            {
                // Lo único que decide si el episodio "ya no está" es el archivo de VIDEO.
                try
                {
                    if (File.Exists(ruta))
                    {
                        tamano = new FileInfo(ruta).Length;
                        AnimeLocalTracker.Core.BorradoDeArchivos.BorrarConReintentos(ruta);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("DetalleViewModel", $"No se pudo borrar el episodio {episodio.NumeroEpisodio}: {ex.Message}");
                    return false;
                }

                // La miniatura es opcional: la propia ficha la tiene abierta (imagen en pantalla) y Windows puede negar el
                // borrado. Si su fallo se tratara como fallo del episodio, el video ya borrado seguiría apareciendo en la
                // lista hasta recargar la pestaña.
                try
                {
                    string miniatura = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(ruta);
                    if (File.Exists(miniatura)) File.Delete(miniatura);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("DetalleViewModel", $"No se pudo borrar la miniatura del episodio {episodio.NumeroEpisodio}: {ex.Message}");
                }
                return true;
            });
```
por:
```csharp
            bool ok = await Task.Run(() =>
            {
                // Lo único que decide si el episodio "ya no está" es el archivo de VIDEO (la miniatura es opcional: ver BorradoDeEpisodio).
                try
                {
                    tamano = AnimeLocalTracker.Core.BorradoDeEpisodio.BorrarVideoYMiniatura(ruta);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("DetalleViewModel", $"No se pudo borrar el episodio {episodio.NumeroEpisodio}: {ex.Message}");
                    return false;
                }
            });
```

- [ ] **Paso 6: `EliminarEpisodio` usa el helper**

En `AnimeLocalTracker/ViewModels/EpisodiosFichaViewModel.cs`, método `EliminarEpisodio`:

(a) Quitar la línea `string rutaMiniatura = PythonEpisodeEnricher.ObtenerRutaMiniaturaEsperada(rutaArchivo);`.

(b) Reemplazar el cuerpo del `Task.Run`:
```csharp
            try
            {
                if (File.Exists(rutaArchivo)) AnimeLocalTracker.Core.BorradoDeArchivos.BorrarConReintentos(rutaArchivo, intentos);
                return true;
            }
```
por:
```csharp
            try
            {
                AnimeLocalTracker.Core.BorradoDeEpisodio.BorrarVideoYMiniatura(rutaArchivo, intentos);
                return true;
            }
```

(c) Borrar el bloque del paso 2 (miniatura), que ya hace el helper:
```csharp
        // 2. Borrar su miniatura (opcional: la ficha puede tenerla abierta en pantalla)
        try { if (File.Exists(rutaMiniatura)) File.Delete(rutaMiniatura); } catch { }

```
y renumerar los comentarios siguientes: `// 3. Conservar el registro…` pasa a `// 2.` y `// 4. Reiniciar en la UI…` pasa a `// 3.`. Actualizar también el comentario del paso 1 a `// 1. Borrar el video y su miniatura. …` (el resto del texto se queda igual).

- [ ] **Paso 7: verificar que el borrado manual no cambió**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~BorradoDeEpisodioTests|FullyQualifiedName~DetalleExtrasTests|FullyQualifiedName~DetalleEpisodiosSeguridadTests"
```
Esperado: 0 advertencias (si `File`/`PythonEpisodeEnricher` quedan sin usar en algún archivo no habrá aviso porque se usan en otros sitios; si el compilador avisa de un `using` sobrante, quitarlo) y todas las pruebas pasan, en especial las 18 de `DetalleExtrasTests` que cubren `LiberarEspacio_*` y `EliminarEpisodio_*`.

---

### Tarea 2: El ajuste en Configuración

**Archivos:**
- Modificar: `AnimeLocalTracker/Models/AppSettings.cs`
- Modificar: `AnimeLocalTracker/ViewModels/ConfiguracionViewModel.cs`
- Modificar: `AnimeLocalTracker/Views/ConfiguracionView.xaml`
- Modificar: `AnimeLocalTracker/Services/LocalizationService.cs`
- Probar: `AnimeLocalTracker.Tests/ViewModels/ConfiguracionEliminarTrasVerTests.cs`

**Interfaces:**
- Produce: `AppSettings.ModoEliminarTrasVer` (string), `AppSettings.EpisodiosAConservar` (int), `ModoEliminarTrasVerValores.{Apagado, Automatico, AlCompletarSerie, ConsumoLigero}` y `ModoEliminarTrasVerValores.Normalizar(string?)`. La Tarea 3 los lee.

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/ViewModels/ConfiguracionEliminarTrasVerTests.cs`:

```csharp
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Configuración → Descargas → "Eliminar el video tras verlo": el modo, el número de episodios a conservar y el aviso.</summary>
public class ConfiguracionEliminarTrasVerTests
{
    private readonly Mock<IDialogService> _dialogos = new();

    private ConfiguracionViewModel CrearSut(AppSettings config)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(config);
        settings.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
        var db = Mock.Of<IDatabaseService>();
        return new ConfiguracionViewModel(settings.Object, Mock.Of<IAuthService>(), db, _dialogos.Object,
            new CacheMaintenanceService(db), Mock.Of<IPluginService>());
    }

    private void ResponderAviso(bool acepta) =>
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(acepta);

    private void VerificarAvisos(Times veces) =>
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), veces);

    [Fact]
    public void AlAbrir_MuestraLoGuardado()
    {
        var sut = CrearSut(new AppSettings { ModoEliminarTrasVer = ModoEliminarTrasVerValores.ConsumoLigero, EpisodiosAConservar = 5 });

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.ConsumoLigero);
        sut.EpisodiosAConservar.Should().Be(5);
        sut.EsConsumoLigero.Should().BeTrue();
        VerificarAvisos(Times.Never()); // cargar lo guardado no pregunta nada
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("otra cosa")]
    public void AlAbrir_ConUnValorRaro_QuedaApagado(string? valor)
    {
        var sut = CrearSut(new AppSettings { ModoEliminarTrasVer = valor! });

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.Apagado);
        sut.EsConsumoLigero.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 10)]
    public void AlAbrir_ElNumeroAConservarSeAcotaDe1a10(int guardado, int esperado)
    {
        CrearSut(new AppSettings { EpisodiosAConservar = guardado }).EpisodiosAConservar.Should().Be(esperado);
    }

    [Fact]
    public async Task ElegirUnModoQueBorra_Aceptando_LoDejaElegido()
    {
        ResponderAviso(true);
        var sut = CrearSut(new AppSettings());

        sut.ModoEliminarTrasVer = ModoEliminarTrasVerValores.Automatico;
        await Task.Delay(50); // el aviso se resuelve fuera del setter

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.Automatico);
        VerificarAvisos(Times.Once());
    }

    [Fact]
    public async Task ElegirUnModoQueBorra_Rechazando_VuelveAlAnterior()
    {
        ResponderAviso(false);
        var sut = CrearSut(new AppSettings());

        sut.ModoEliminarTrasVer = ModoEliminarTrasVerValores.Automatico;
        await Task.Delay(50);

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.Apagado);
        VerificarAvisos(Times.Once());
    }

    [Fact]
    public async Task ApagarElModo_NoPregunta()
    {
        ResponderAviso(true);
        var sut = CrearSut(new AppSettings { ModoEliminarTrasVer = ModoEliminarTrasVerValores.Automatico });

        sut.ModoEliminarTrasVer = ModoEliminarTrasVerValores.Apagado;
        await Task.Delay(50);

        sut.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.Apagado);
        VerificarAvisos(Times.Never());
    }

    [Fact]
    public async Task Guardar_EscribeElModoYElNumero()
    {
        ResponderAviso(true);
        var config = new AppSettings();
        var sut = CrearSut(config);

        sut.ModoEliminarTrasVer = ModoEliminarTrasVerValores.ConsumoLigero;
        await Task.Delay(50);
        sut.EpisodiosAConservar = 4;
        await sut.GuardarPreferenciasAsync();

        config.ModoEliminarTrasVer.Should().Be(ModoEliminarTrasVerValores.ConsumoLigero);
        config.EpisodiosAConservar.Should().Be(4);
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~ConfiguracionEliminarTrasVerTests"
```
Esperado: errores de compilación `CS0117`/`CS1061` (`ModoEliminarTrasVerValores`, `ModoEliminarTrasVer`, `EsConsumoLigero`, `EpisodiosAConservar` no existen).

- [ ] **Paso 3: los ajustes**

En `AnimeLocalTracker/Models/AppSettings.cs`, justo después de la propiedad `AccionFinEpisodio`:

```csharp

    /// <summary>Qué hacer con el video de un episodio ya visto: uno de los valores de <see cref="ModoEliminarTrasVerValores"/>.</summary>
    public string ModoEliminarTrasVer { get; set; } = ModoEliminarTrasVerValores.Apagado;

    /// <summary>Con el modo "consumo ligero": cuántos episodios vistos se conservan en disco (los de número más alto), de 1 a 10.</summary>
    public int EpisodiosAConservar { get; set; } = 3;
```

Y justo después de la clase `AccionFinEpisodioValores`:

```csharp

/// <summary>Valores válidos de <see cref="AppSettings.ModoEliminarTrasVer"/>.</summary>
public static class ModoEliminarTrasVerValores
{
    /// <summary>No se borra nada (predeterminado).</summary>
    public const string Apagado = "Apagado";
    /// <summary>Al terminar de ver un episodio se borra su video.</summary>
    public const string Automatico = "Automatico";
    /// <summary>Al ver el último episodio de la serie se pregunta una vez y se borran todos los vistos.</summary>
    public const string AlCompletarSerie = "AlCompletarSerie";
    /// <summary>Se conservan solo los N episodios vistos de número más alto; el resto se borra.</summary>
    public const string ConsumoLigero = "ConsumoLigero";

    /// <summary>Un valor desconocido o vacío (ajustes antiguos o editados a mano) equivale a Apagado: nunca se borra por error.</summary>
    public static string Normalizar(string? valor) => valor is Automatico or AlCompletarSerie or ConsumoLigero ? valor : Apagado;
}
```

- [ ] **Paso 4: el ViewModel**

En `AnimeLocalTracker/ViewModels/ConfiguracionViewModel.cs`:

(a) Junto a `[ObservableProperty] private string _accionFinEpisodio = …;` (línea ~143) añadir:

```csharp

    // === ELIMINAR EL VIDEO TRAS VERLO ===
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EsConsumoLigero))]
    private string _modoEliminarTrasVer = ModoEliminarTrasVerValores.Apagado;

    [ObservableProperty] private int _episodiosAConservar = 3;

    /// <summary>Solo con este modo tiene sentido pedir cuántos episodios se conservan.</summary>
    public bool EsConsumoLigero => ModoEliminarTrasVer == ModoEliminarTrasVerValores.ConsumoLigero;

    private bool _cargandoModoBorrado;
    private string _modoBorradoAceptado = ModoEliminarTrasVerValores.Apagado;

    partial void OnModoEliminarTrasVerChanged(string value)
    {
        if (_cargandoModoBorrado) return;
        _ = ConfirmarModoEliminarTrasVerAsync(value);
    }

    /// <summary>
    /// Elegir un modo que borra videos avisa de que es definitivo: si el usuario no acepta, el selector vuelve al valor anterior.
    /// </summary>
    private async Task ConfirmarModoEliminarTrasVerAsync(string nuevo)
    {
        try
        {
            bool acepta = true;
            if (nuevo != ModoEliminarTrasVerValores.Apagado)
            {
                acepta = await _dialogService.MostrarDialogoAsync(
                    LocalizationService.T("Cfg_EliminarTrasVerAvisoTitulo"),
                    LocalizationService.T("Cfg_EliminarTrasVerAvisoMsj"),
                    true, "DeleteAlertOutline", "#EF4444");
            }

            _cargandoModoBorrado = true;
            try
            {
                if (acepta) _modoBorradoAceptado = nuevo;
                else ModoEliminarTrasVer = _modoBorradoAceptado;
            }
            finally { _cargandoModoBorrado = false; }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ConfiguracionViewModel", $"No se pudo confirmar el modo de borrado tras ver: {ex.Message}");
        }
    }
```

(b) En `CargarDatosConfiguracion()`, justo después de la línea `AccionFinEpisodio = string.IsNullOrWhiteSpace(…) …;`:

```csharp
        _cargandoModoBorrado = true;
        ModoEliminarTrasVer = ModoEliminarTrasVerValores.Normalizar(config.ModoEliminarTrasVer);
        _modoBorradoAceptado = ModoEliminarTrasVer;
        _cargandoModoBorrado = false;
        EpisodiosAConservar = Math.Clamp(config.EpisodiosAConservar, 1, 10);
```

(c) En `GuardarPreferenciasAsync()`, justo después de `config.AccionFinEpisodio = AccionFinEpisodio;`:

```csharp
            config.ModoEliminarTrasVer = ModoEliminarTrasVerValores.Normalizar(ModoEliminarTrasVer);
            config.EpisodiosAConservar = Math.Clamp(EpisodiosAConservar, 1, 10);
```

- [ ] **Paso 5: claves de localización**

En `AnimeLocalTracker/Services/LocalizationService.cs`, después de la línea ES `["Cfg_DescargasSimultaneasSub"] = "Cantidad de episodios descargándose en paralelo (1 a 5)",`:

```csharp
        ["Cfg_EliminarTrasVer"] = "Eliminar el video tras verlo",
        ["Cfg_EliminarTrasVerSub"] = "Libera espacio borrando los episodios que ya viste. Se conservan tu historial, tu progreso y la ficha. El borrado es definitivo.",
        ["Cfg_EliminarTrasVer_Apagado"] = "Apagado",
        ["Cfg_EliminarTrasVer_Automatico"] = "Al terminar de ver cada episodio",
        ["Cfg_EliminarTrasVer_AlCompletar"] = "Al completar la serie (pregunta una vez)",
        ["Cfg_EliminarTrasVer_ConsumoLigero"] = "Consumo ligero (conservar solo los últimos)",
        ["Cfg_EpisodiosAConservar"] = "Episodios vistos que se conservan",
        ["Cfg_EpisodiosAConservarSub"] = "Se borran los vistos más antiguos y se quedan los de número más alto.",
        ["Cfg_EliminarTrasVerAvisoTitulo"] = "Borrado definitivo",
        ["Cfg_EliminarTrasVerAvisoMsj"] = "Los videos de los episodios que veas se borrarán del disco sin pasar por la Papelera. Tu historial, tu progreso y la ficha se conservan, pero para volver a verlos tendrás que descargarlos de nuevo.\n\n¿Activar este modo?",
        ["Lim_CompletarTitulo"] = "Serie completada",
        ["Lim_CompletarMsjFormato"] = "Terminaste {0}. ¿Liberar {1} borrando los videos de los {2} episodios que ya viste? Se conservan la ficha, tu historial y tus puntuaciones.",
        ["Lim_LiberadoTitulo"] = "Espacio liberado",
        ["Lim_LiberadoMsjFormato"] = "Episodios borrados: {0} · Espacio liberado: {1}",
```

Y después de la línea EN `["Cfg_DescargasSimultaneasSub"] = "Number of episodes downloading in parallel (1 to 5)",`:

```csharp
        ["Cfg_EliminarTrasVer"] = "Delete the video after watching",
        ["Cfg_EliminarTrasVerSub"] = "Frees space by deleting episodes you already watched. Your history, progress and the series page are kept. Deletion is permanent.",
        ["Cfg_EliminarTrasVer_Apagado"] = "Off",
        ["Cfg_EliminarTrasVer_Automatico"] = "After finishing each episode",
        ["Cfg_EliminarTrasVer_AlCompletar"] = "When the series is finished (asks once)",
        ["Cfg_EliminarTrasVer_ConsumoLigero"] = "Light mode (keep only the latest ones)",
        ["Cfg_EpisodiosAConservar"] = "Watched episodes to keep",
        ["Cfg_EpisodiosAConservarSub"] = "The oldest watched ones are deleted; the highest-numbered ones stay.",
        ["Cfg_EliminarTrasVerAvisoTitulo"] = "Permanent deletion",
        ["Cfg_EliminarTrasVerAvisoMsj"] = "The videos of the episodes you watch will be deleted from disk without going through the Recycle Bin. Your history, progress and the series page are kept, but to watch them again you will have to download them again.\n\nTurn this mode on?",
        ["Lim_CompletarTitulo"] = "Series finished",
        ["Lim_CompletarMsjFormato"] = "You finished {0}. Free up {1} by deleting the videos of the {2} episodes you already watched? The series page, your history and your scores are kept.",
        ["Lim_LiberadoTitulo"] = "Space freed",
        ["Lim_LiberadoMsjFormato"] = "Episodes deleted: {0} · Space freed: {1}",
```

- [ ] **Paso 6: el selector en la pantalla**

En `AnimeLocalTracker/Views/ConfiguracionView.xaml`, dentro del grupo de Descargas, después de la fila de `IntervaloSincronizacionMinutos` (cierra con `</ctl:SettingRow>` y justo antes del `</StackPanel>` que cierra el grupo, ~línea 992), añadir:

```xml
                                    <Border Style="{StaticResource CfgDivider}"/>

                                    <ctl:SettingRow Title="{loc:T Cfg_EliminarTrasVer}"
                                                    Description="{loc:T Cfg_EliminarTrasVerSub}">
                                        <ComboBox SelectedValue="{Binding ModoEliminarTrasVer}" SelectedValuePath="Tag" Height="36" MinWidth="230"
                                                  AutomationProperties.Name="{loc:T Cfg_EliminarTrasVer}">
                                            <ComboBoxItem Content="{loc:T Cfg_EliminarTrasVer_Apagado}" Tag="Apagado"/>
                                            <ComboBoxItem Content="{loc:T Cfg_EliminarTrasVer_Automatico}" Tag="Automatico"/>
                                            <ComboBoxItem Content="{loc:T Cfg_EliminarTrasVer_AlCompletar}" Tag="AlCompletarSerie"/>
                                            <ComboBoxItem Content="{loc:T Cfg_EliminarTrasVer_ConsumoLigero}" Tag="ConsumoLigero"/>
                                        </ComboBox>
                                    </ctl:SettingRow>

                                    <Border Style="{StaticResource CfgDivider}"
                                            Visibility="{Binding EsConsumoLigero, Converter={StaticResource BoolToVis}}"/>
                                    <ctl:SettingRow Title="{loc:T Cfg_EpisodiosAConservar}"
                                                    Description="{loc:T Cfg_EpisodiosAConservarSub}"
                                                    Visibility="{Binding EsConsumoLigero, Converter={StaticResource BoolToVis}}">
                                        <ComboBox SelectedValue="{Binding EpisodiosAConservar}" SelectedValuePath="Content" Height="36" MinWidth="80"
                                                  AutomationProperties.Name="{loc:T Cfg_EpisodiosAConservar}">
                                            <ComboBoxItem Content="1"/>
                                            <ComboBoxItem Content="2"/>
                                            <ComboBoxItem Content="3"/>
                                            <ComboBoxItem Content="4"/>
                                            <ComboBoxItem Content="5"/>
                                            <ComboBoxItem Content="6"/>
                                            <ComboBoxItem Content="7"/>
                                            <ComboBoxItem Content="8"/>
                                            <ComboBoxItem Content="9"/>
                                            <ComboBoxItem Content="10"/>
                                        </ComboBox>
                                    </ctl:SettingRow>
```

- [ ] **Paso 7: comprobar que pasan**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~ConfiguracionEliminarTrasVerTests|FullyQualifiedName~SettingsServiceTests|FullyQualifiedName~ConfiguracionSeccionesTests|FullyQualifiedName~ConfiguracionAmbitoAudioTests"
```
Esperado: 0 advertencias; pasan las pruebas nuevas y las existentes de Configuración. Si el compilador del XAML protesta por `Visibility` en `ctl:SettingRow`, envolver la fila en un `StackPanel` con ese `Visibility`.

---

### Tarea 3: Función de decisión y servicio

**Archivos:**
- Crear: `AnimeLocalTracker/Core/LimpiezaTrasVer.cs`
- Crear: `AnimeLocalTracker/Services/ILimpiadorDeEpisodios.cs`
- Crear: `AnimeLocalTracker/Services/LimpiadorDeEpisodios.cs`
- Probar: `AnimeLocalTracker.Tests/Core/LimpiezaTrasVerTests.cs`, `AnimeLocalTracker.Tests/Services/LimpiadorDeEpisodiosTests.cs`

**Interfaces:**
- Consume (Tareas 1 y 2): `BorradoDeEpisodio.BorrarVideoYMiniatura`, `ModoEliminarTrasVerValores`, `AppSettings.ModoEliminarTrasVer/EpisodiosAConservar`.
- Produce: `LimpiezaTrasVer.Decidir(string modo, int episodiosAConservar, int episodioTerminado, int totalEpisodios, IReadOnlyCollection<int> vistosConArchivo) -> List<int>` y `ILimpiadorDeEpisodios.AplicarTrasVerAsync(int aniListId, int episodioTerminado) -> Task` (no lanza nunca). La Tarea 4 la usa.

- [ ] **Paso 1: pruebas de la función pura (fallan)**

Crear `AnimeLocalTracker.Tests/Core/LimpiezaTrasVerTests.cs`:

```csharp
using System.Linq;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Models;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Core;

/// <summary>Qué episodios se borran tras terminar de ver uno, según el modo (docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md, parte 2).</summary>
public class LimpiezaTrasVerTests
{
    private static int[] De(params int[] numeros) => numeros;

    [Fact]
    public void Apagado_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.Apagado, 3, 5, 12, De(1, 2, 3, 4, 5)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("otra cosa")]
    public void UnModoRaro_NoBorraNada(string? modo)
    {
        LimpiezaTrasVer.Decidir(modo!, 3, 5, 12, De(1, 2, 3, 4, 5)).Should().BeEmpty();
    }

    [Fact]
    public void Automatico_BorraSoloElQueSeAcabaDeVer()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.Automatico, 3, 5, 12, De(1, 2, 3, 4, 5)).Should().Equal(5);
    }

    [Fact]
    public void Automatico_SiElEpisodioNoTieneArchivo_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.Automatico, 3, 5, 12, De(1, 2, 3)).Should().BeEmpty();
    }

    [Fact]
    public void ConsumoLigero_ConservaLosNDeNumeroMasAlto()
    {
        // Vistos del 1 al 8 con N = 3: se quedan 6, 7 y 8.
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.ConsumoLigero, 3, 8, 12, De(1, 2, 3, 4, 5, 6, 7, 8)).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void ConsumoLigero_ConPocosVistos_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.ConsumoLigero, 3, 2, 12, De(1, 2)).Should().BeEmpty();
    }

    [Fact]
    public void ConsumoLigero_NuncaConservaMenosDeUno()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.ConsumoLigero, 0, 4, 12, De(1, 2, 3, 4)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void AlCompletarSerie_EnElUltimoEpisodio_BorraTodosLosVistos()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.AlCompletarSerie, 3, 12, 12, De(3, 1, 2)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void AlCompletarSerie_SiAunNoEsElUltimo_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.AlCompletarSerie, 3, 7, 12, De(1, 2, 3, 4, 5, 6, 7)).Should().BeEmpty();
    }

    [Fact]
    public void AlCompletarSerie_ConTotalDesconocido_NoBorraNada()
    {
        LimpiezaTrasVer.Decidir(ModoEliminarTrasVerValores.AlCompletarSerie, 3, 12, 0, De(1, 2, 3, 12)).Should().BeEmpty();
    }
}
```

Ejecutar (esperado: error de compilación, `LimpiezaTrasVer` no existe):
```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~LimpiezaTrasVerTests"
```

- [ ] **Paso 2: implementar la función pura**

Crear `AnimeLocalTracker/Core/LimpiezaTrasVer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Core;

/// <summary>
/// Decide qué episodios hay que borrar tras terminar de ver uno (modo de "Eliminar tras ver"). Lógica pura, sin disco ni
/// base de datos: el servicio le pasa los episodios ya vistos que siguen teniendo archivo y ejecuta lo que decide.
/// </summary>
public static class LimpiezaTrasVer
{
    /// <summary>
    /// Números de episodio que se borran, de menor a mayor. Un modo desconocido, vacío o "Apagado" no borra nada.
    /// <paramref name="vistosConArchivo"/> son los episodios vistos que aún tienen su video en la carpeta del anime;
    /// los no vistos nunca entran en la decisión.
    /// </summary>
    public static List<int> Decidir(string modo, int episodiosAConservar, int episodioTerminado, int totalEpisodios, IReadOnlyCollection<int> vistosConArchivo)
    {
        switch (ModoEliminarTrasVerValores.Normalizar(modo))
        {
            case ModoEliminarTrasVerValores.Automatico:
                return vistosConArchivo.Contains(episodioTerminado) ? [episodioTerminado] : [];

            case ModoEliminarTrasVerValores.ConsumoLigero:
                return vistosConArchivo.OrderByDescending(n => n).Skip(Math.Max(1, episodiosAConservar)).OrderBy(n => n).ToList();

            case ModoEliminarTrasVerValores.AlCompletarSerie:
                return totalEpisodios > 0 && episodioTerminado >= totalEpisodios ? vistosConArchivo.OrderBy(n => n).ToList() : [];

            default:
                return [];
        }
    }
}
```

Ejecutar los mismos dos comandos del Paso 1. Esperado: las 12 pruebas (9 `Fact` + 3 casos del `Theory`) pasan.

- [ ] **Paso 3: pruebas del servicio (fallan)**

Crear `AnimeLocalTracker.Tests/Services/LimpiadorDeEpisodiosTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>"Eliminar tras ver": qué borra el servicio, cuándo pregunta y qué protecciones aplica.</summary>
public sealed class LimpiadorDeEpisodiosTests : IDisposable
{
    private const int Id = 77;

    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "alt-limpiador-" + Guid.NewGuid().ToString("N"));
    private readonly string _carpeta;
    private readonly List<int> _mensajes = [];

    public LimpiadorDeEpisodiosTests()
    {
        _carpeta = Path.Combine(_raiz, "Anime");
        Directory.CreateDirectory(_carpeta);
        WeakReferenceMessenger.Default.Register<LimpiadorDeEpisodiosTests, ArchivoEpisodioEliminadoMensaje>(this, (r, m) =>
        {
            if (m.AnimeId == Id) lock (r._mensajes) r._mensajes.Add(m.NumeroEpisodio);
        });
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        try { Directory.Delete(_raiz, recursive: true); }
        catch (IOException) { /* limpieza de una carpeta temporal */ }
    }

    private string Archivo(int n, string? carpeta = null)
    {
        string ruta = Path.Combine(carpeta ?? _carpeta, $"Episodio {n:D2}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllBytes(ruta, new byte[100]);
        return ruta;
    }

    /// <summary>Prepara el anime: esos episodios con archivo en la carpeta, esos marcados como vistos y ese modo.</summary>
    private LimpiadorDeEpisodios Preparar(string modo, int[] conArchivo, int[] vistos, int total = 12, int conservar = 3,
        IEnumerable<EpisodioItem>? extraEscaneados = null, bool confirma = true)
    {
        var escaneados = conArchivo.Select(n => new EpisodioItem { NumeroEpisodio = n, RutaCompleta = Archivo(n) }).ToList();
        if (extraEscaneados != null) escaneados.AddRange(extraEscaneados);

        _ajustes.Setup(a => a.ObtenerConfiguracion()).Returns(new AppSettings { ModoEliminarTrasVer = modo, EpisodiosAConservar = conservar });
        _db.Setup(d => d.ObtenerAnimePorIdAsync(Id)).ReturnsAsync(new AnimeItem { AniListId = Id, Titulo = "Frieren", TotalEpisodios = total, RutaCarpeta = _carpeta });
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(Id))
            .ReturnsAsync(vistos.Select(n => new RegistroEpisodio { AniListId = Id, NumeroEpisodio = n, VistoLocal = true }).ToList());
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ReturnsAsync(escaneados);
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(confirma);

        return new LimpiadorDeEpisodios(_db.Object, _escaner.Object, _ajustes.Object, _dialogos.Object)
        {
            EsperaInicial = TimeSpan.Zero,
            EsperaMaximaMarcaVisto = TimeSpan.FromMilliseconds(200),
            EsperaEntreComprobaciones = TimeSpan.FromMilliseconds(20),
            IntentosBorrado = 1,
        };
    }

    private bool Existe(int n) => File.Exists(Path.Combine(_carpeta, $"Episodio {n:D2}.mp4"));

    private void VerificarRegistroConservado(params int[] numeros)
    {
        foreach (int n in numeros) _db.Verify(d => d.ConservarRegistroTrasEliminarArchivoAsync(Id, n), Times.Once);
        _db.Verify(d => d.ConservarRegistroTrasEliminarArchivoAsync(Id, It.IsAny<int>()), Times.Exactly(numeros.Length));
    }

    [Fact]
    public async Task Apagado_NoBorraNada()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.Apagado, conArchivo: [1, 2, 3], vistos: [1, 2, 3]);

        await sut.AplicarTrasVerAsync(Id, 3);

        Existe(1).Should().BeTrue(); Existe(2).Should().BeTrue(); Existe(3).Should().BeTrue();
        VerificarRegistroConservado();
    }

    [Fact]
    public async Task Automatico_BorraSoloElEpisodioQueSeAcabaDeVer_YConservaSuRegistro()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.Automatico, conArchivo: [1, 2, 3, 4, 5], vistos: [1, 2, 3, 4, 5]);

        await sut.AplicarTrasVerAsync(Id, 5);

        Existe(5).Should().BeFalse();
        Existe(1).Should().BeTrue(); Existe(4).Should().BeTrue();
        VerificarRegistroConservado(5);
        _mensajes.Should().Equal(5);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConsumoLigero_ConservaLosNDeNumeroMasAlto()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.ConsumoLigero, conArchivo: [1, 2, 3, 4, 5, 6, 7, 8], vistos: [1, 2, 3, 4, 5, 6, 7, 8], conservar: 3);

        await sut.AplicarTrasVerAsync(Id, 8);

        foreach (int n in new[] { 1, 2, 3, 4, 5 }) Existe(n).Should().BeFalse();
        foreach (int n in new[] { 6, 7, 8 }) Existe(n).Should().BeTrue();
        VerificarRegistroConservado(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task ConsumoLigero_NuncaTocaLosEpisodiosSinVer()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.ConsumoLigero, conArchivo: [1, 2, 3, 4, 5, 6, 7, 8], vistos: [1, 2, 3, 4], conservar: 2);

        await sut.AplicarTrasVerAsync(Id, 4);

        Existe(1).Should().BeFalse(); Existe(2).Should().BeFalse();
        foreach (int n in new[] { 3, 4, 5, 6, 7, 8 }) Existe(n).Should().BeTrue();
    }

    [Fact]
    public async Task AlCompletarSerie_Aceptando_BorraTodosLosVistos()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.AlCompletarSerie, conArchivo: [1, 2, 3, 4], vistos: [1, 2, 3, 4], total: 4);

        await sut.AplicarTrasVerAsync(Id, 4);

        foreach (int n in new[] { 1, 2, 3, 4 }) Existe(n).Should().BeFalse();
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        VerificarRegistroConservado(1, 2, 3, 4);
    }

    [Fact]
    public async Task AlCompletarSerie_Rechazando_NoBorraNada()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.AlCompletarSerie, conArchivo: [1, 2, 3, 4], vistos: [1, 2, 3, 4], total: 4, confirma: false);

        await sut.AplicarTrasVerAsync(Id, 4);

        foreach (int n in new[] { 1, 2, 3, 4 }) Existe(n).Should().BeTrue();
        VerificarRegistroConservado();
    }

    [Fact]
    public async Task AlCompletarSerie_SiAunNoEsElUltimo_NoPreguntaNiBorra()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.AlCompletarSerie, conArchivo: [1, 2, 3], vistos: [1, 2, 3], total: 12);

        await sut.AplicarTrasVerAsync(Id, 3);

        Existe(3).Should().BeTrue();
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AlCompletarSerie_ConTotalDesconocido_NoHaceNada()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.AlCompletarSerie, conArchivo: [1, 2, 3], vistos: [1, 2, 3], total: 0);

        await sut.AplicarTrasVerAsync(Id, 3);

        Existe(3).Should().BeTrue();
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SiElEpisodioNoQuedoMarcadoComoVisto_NoBorraNada()
    {
        // Salir antes de que se guarde la marca, o un episodio que nunca llegó a marcarse: no se pierde nada.
        var sut = Preparar(ModoEliminarTrasVerValores.Automatico, conArchivo: [1, 2, 3], vistos: [1, 2]);

        await sut.AplicarTrasVerAsync(Id, 3);

        Existe(3).Should().BeTrue();
        VerificarRegistroConservado();
    }

    [Fact]
    public async Task NoBorraArchivosFueraDeLaCarpetaDelAnime()
    {
        string fuera = Archivo(5, Path.Combine(_raiz, "OtraCarpeta"));
        var sut = Preparar(ModoEliminarTrasVerValores.Automatico, conArchivo: [], vistos: [5],
            extraEscaneados: [new EpisodioItem { NumeroEpisodio = 5, RutaCompleta = fuera }]);

        await sut.AplicarTrasVerAsync(Id, 5);

        File.Exists(fuera).Should().BeTrue();
        VerificarRegistroConservado();
    }

    [Fact]
    public async Task ArchivoEnUso_LoDejaYSigueConLosDemas()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.ConsumoLigero, conArchivo: [1, 2, 3], vistos: [1, 2, 3], conservar: 1);
        using var abierto = new FileStream(Path.Combine(_carpeta, "Episodio 01.mp4"), FileMode.Open, FileAccess.Read, FileShare.None);

        await sut.AplicarTrasVerAsync(Id, 3);

        Existe(1).Should().BeTrue("estaba en uso");
        Existe(2).Should().BeFalse();
        Existe(3).Should().BeTrue();
        VerificarRegistroConservado(2);
    }

    [Fact]
    public async Task UnErrorInesperado_NoSePropaga()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.Automatico, conArchivo: [1], vistos: [1]);
        _escaner.Setup(e => e.EscanearEpisodiosAsync(_carpeta)).ThrowsAsync(new InvalidOperationException("fallo del escáner"));

        Func<Task> aplicar = () => sut.AplicarTrasVerAsync(Id, 1);

        await aplicar.Should().NotThrowAsync();
        Existe(1).Should().BeTrue();
    }
}
```

Ejecutar (esperado: error de compilación, `LimpiadorDeEpisodios` no existe):
```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~LimpiadorDeEpisodiosTests"
```

- [ ] **Paso 4: implementar la interfaz y el servicio**

Crear `AnimeLocalTracker/Services/ILimpiadorDeEpisodios.cs`:

```csharp
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>"Eliminar tras ver": borra el video de los episodios ya vistos según el modo elegido en Configuración.</summary>
public interface ILimpiadorDeEpisodios
{
    /// <summary>
    /// Se llama cuando se terminó de ver <paramref name="episodioTerminado"/> reproduciéndolo de verdad. Decide y ejecuta el
    /// borrado según el modo; con el modo apagado no hace nada. Nunca lanza: un fallo se registra y el episodio se queda como estaba.
    /// </summary>
    Task AplicarTrasVerAsync(int aniListId, int episodioTerminado);
}
```

Crear `AnimeLocalTracker/Services/LimpiadorDeEpisodios.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Services;

/// <inheritdoc cref="ILimpiadorDeEpisodios"/>
public sealed class LimpiadorDeEpisodios : ILimpiadorDeEpisodios
{
    private readonly IDatabaseService _database;
    private readonly IFileScannerService _escaner;
    private readonly ISettingsService _ajustes;
    private readonly IDialogService _dialogos;

    public LimpiadorDeEpisodios(IDatabaseService database, IFileScannerService escaner, ISettingsService ajustes, IDialogService dialogos)
    {
        _database = database;
        _escaner = escaner;
        _ajustes = ajustes;
        _dialogos = dialogos;
    }

    /// <summary>Pausa antes de empezar: el reproductor que acaba de cerrar el video necesita un instante para soltar el archivo. Ajustable solo en pruebas.</summary>
    internal TimeSpan EsperaInicial { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Cuánto se espera a que la base de datos muestre el episodio como visto (el guardado local es asíncrono). Ajustable solo en pruebas.</summary>
    internal TimeSpan EsperaMaximaMarcaVisto { get; set; } = TimeSpan.FromSeconds(10);

    internal TimeSpan EsperaEntreComprobaciones { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Intentos de borrado de cada video si está en uso (200 ms entre intentos). Ajustable solo en pruebas.</summary>
    internal int IntentosBorrado { get; set; } = BorradoDeArchivos.IntentosPorDefecto;

    public async Task AplicarTrasVerAsync(int aniListId, int episodioTerminado)
    {
        try
        {
            var config = _ajustes.ObtenerConfiguracion();
            string modo = ModoEliminarTrasVerValores.Normalizar(config.ModoEliminarTrasVer);
            if (modo == ModoEliminarTrasVerValores.Apagado || aniListId <= 0 || episodioTerminado <= 0) return;

            if (EsperaInicial > TimeSpan.Zero) await Task.Delay(EsperaInicial).ConfigureAwait(false);

            var anime = await _database.ObtenerAnimePorIdAsync(aniListId).ConfigureAwait(false);
            if (anime == null || string.IsNullOrWhiteSpace(anime.RutaCarpeta)) return;

            var registros = await EsperarMarcaDeVistoAsync(aniListId, episodioTerminado).ConfigureAwait(false);
            if (registros == null)
            {
                AppLogger.Debug("LimpiadorDeEpisodios", $"El episodio {episodioTerminado} de {anime.Titulo} no quedó marcado como visto: no se borra nada.");
                return;
            }

            var vistos = registros.Where(r => r.VistoLocal).Select(r => r.NumeroEpisodio).ToHashSet();
            var encontrados = await _escaner.EscanearEpisodiosAsync(anime.RutaCarpeta).ConfigureAwait(false);
            var archivos = encontrados
                .Where(e => vistos.Contains(e.NumeroEpisodio) && EstaDentroDe(anime.RutaCarpeta, e.RutaCompleta) && File.Exists(e.RutaCompleta))
                .GroupBy(e => e.NumeroEpisodio)
                .ToDictionary(g => g.Key, g => g.First().RutaCompleta);

            var aBorrar = LimpiezaTrasVer.Decidir(modo, Math.Clamp(config.EpisodiosAConservar, 1, 10), episodioTerminado, anime.TotalEpisodios, archivos.Keys.ToList());
            if (aBorrar.Count == 0) return;

            if (modo == ModoEliminarTrasVerValores.AlCompletarSerie && !await ConfirmarAsync(anime, aBorrar.Select(n => archivos[n]).ToList()).ConfigureAwait(false)) return;

            await BorrarAsync(anime, aBorrar.Select(n => (Numero: n, Ruta: archivos[n])).ToList()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LimpiadorDeEpisodios", $"No se pudo aplicar 'Eliminar tras ver' (anime {aniListId}, episodio {episodioTerminado}): {ex.Message}");
        }
    }

    /// <summary>Espera a que el episodio figure como visto en la base de datos; null si no llega a tiempo.</summary>
    private async Task<List<RegistroEpisodio>?> EsperarMarcaDeVistoAsync(int aniListId, int episodio)
    {
        var limite = DateTime.UtcNow + EsperaMaximaMarcaVisto;
        while (true)
        {
            var registros = await _database.ObtenerRegistrosPorAnimeAsync(aniListId).ConfigureAwait(false);
            if (registros != null && registros.Any(r => r.NumeroEpisodio == episodio && r.VistoLocal)) return registros;
            if (DateTime.UtcNow >= limite) return null;
            await Task.Delay(EsperaEntreComprobaciones).ConfigureAwait(false);
        }
    }

    /// <summary>El archivo debe estar dentro de la carpeta del anime: nunca se borra nada fuera de ella.</summary>
    internal static bool EstaDentroDe(string carpeta, string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return false;
        string raiz = Path.GetFullPath(carpeta).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(ruta).StartsWith(raiz, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ConfirmarAsync(AnimeItem anime, List<string> rutas)
    {
        long bytes = await Task.Run(() =>
        {
            long total = 0;
            foreach (string ruta in rutas)
            {
                try { total += new FileInfo(ruta).Length; }
                catch (IOException) { /* archivo en uso o desaparecido: no cuenta */ }
            }
            return total;
        }).ConfigureAwait(false);

        return await _dialogos.MostrarDialogoAsync(
            LocalizationService.T("Lim_CompletarTitulo"),
            string.Format(LocalizationService.T("Lim_CompletarMsjFormato"), anime.Titulo, Formato.Tamano(bytes), rutas.Count),
            true, "DeleteSweepOutline", "#EF4444").ConfigureAwait(false);
    }

    private async Task BorrarAsync(AnimeItem anime, List<(int Numero, string Ruta)> episodios)
    {
        long liberados = 0;
        int borrados = 0;

        foreach (var (numero, ruta) in episodios)
        {
            try
            {
                liberados += await Task.Run(() => BorradoDeEpisodio.BorrarVideoYMiniatura(ruta, IntentosBorrado)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("LimpiadorDeEpisodios", $"No se pudo borrar el episodio {numero} de {anime.Titulo} (se deja como estaba): {ex.Message}");
                continue;
            }

            borrados++;
            AppLogger.Info("LimpiadorDeEpisodios", $"Eliminar tras ver: borrado el episodio {numero} de {anime.Titulo}.");

            // El historial es permanente: borrar el archivo NO borra que se vio el episodio.
            try { await _database.ConservarRegistroTrasEliminarArchivoAsync(anime.AniListId, numero).ConfigureAwait(false); }
            catch (Exception ex) { AppLogger.Debug("LimpiadorDeEpisodios", $"No se pudo conservar el registro del episodio {numero}: {ex.Message}"); }

            WeakReferenceMessenger.Default.Send(new ArchivoEpisodioEliminadoMensaje(anime.AniListId, numero));
        }

        if (borrados > 0)
        {
            _dialogos.MostrarToast(
                LocalizationService.T("Lim_LiberadoTitulo"),
                string.Format(LocalizationService.T("Lim_LiberadoMsjFormato"), borrados, Formato.Tamano(liberados)),
                "DeleteSweepOutline", "#4CAF50");
        }
    }
}
```

- [ ] **Paso 5: comprobar que pasan**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~LimpiezaTrasVerTests|FullyQualifiedName~LimpiadorDeEpisodiosTests"
```
Esperado: 0 advertencias; pasan las 12 de `LimpiezaTrasVerTests` y las 12 de `LimpiadorDeEpisodiosTests`.

---

### Tarea 4: Enganche en el reproductor y la ficha

**Archivos:**
- Modificar: `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs`
- Modificar: `AnimeLocalTracker/App.xaml.cs`
- Modificar: `AnimeLocalTracker/ViewModels/EpisodiosFichaViewModel.cs`
- Probar: `AnimeLocalTracker.Tests/ViewModels/ReproductorEliminarTrasVerTests.cs`, `AnimeLocalTracker.Tests/ViewModels/DetalleEpisodiosSeguridadTests.cs`

**Interfaces:**
- Consume (Tarea 3): `ILimpiadorDeEpisodios.AplicarTrasVerAsync(int, int)`.
- Produce: parámetro opcional `limpiadorDeEpisodios` al final del constructor de `ReproductorViewModel`; `EpisodiosFichaViewModel : IRecipient<ArchivoEpisodioEliminadoMensaje>`.

- [ ] **Paso 1: pruebas del reproductor (fallan)**

Crear `AnimeLocalTracker.Tests/ViewModels/ReproductorEliminarTrasVerTests.cs`:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>El reproductor avisa al servicio de "Eliminar tras ver" solo cuando el episodio se vio de verdad reproduciéndolo.</summary>
public class ReproductorEliminarTrasVerTests
{
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<ILimpiadorDeEpisodios> _limpiador = new();

    private ReproductorViewModel CrearSut()
    {
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        return new ReproductorViewModel(_db.Object, _tracking.Object, _auth.Object, limpiadorDeEpisodios: _limpiador.Object);
    }

    [Fact]
    public async Task AlCerrarTrasVerElEpisodio_AvisaAlLimpiador()
    {
        var sut = CrearSut();
        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);
        await sut.RealizarAutoTrackingAsync();

        sut.Dispose();

        _limpiador.Verify(l => l.AplicarTrasVerAsync(101, 5), Times.Once);
    }

    [Fact]
    public void AlCerrarSinHaberlo_Visto_NoAvisa()
    {
        var sut = CrearSut();
        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);

        sut.Dispose();

        _limpiador.Verify(l => l.AplicarTrasVerAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task AlPasarAlSiguienteEpisodioTrasVerElAnterior_AvisaDelAnterior()
    {
        var sut = CrearSut();
        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);
        await sut.RealizarAutoTrackingAsync();

        sut.CargarVideo(@"C:\Anime\Ep06.mkv", 101, "Solo Leveling", 6);

        _limpiador.Verify(l => l.AplicarTrasVerAsync(101, 5), Times.Once);
        sut.Dispose();
        _limpiador.Verify(l => l.AplicarTrasVerAsync(101, 6), Times.Never); // el 6 no se llegó a ver
    }

    [Fact]
    public async Task AlRecargarElMismoEpisodio_NoAvisa()
    {
        var sut = CrearSut();
        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);
        await sut.RealizarAutoTrackingAsync();

        sut.CargarVideo(@"C:\Anime\Ep05.mkv", 101, "Solo Leveling", 5);

        _limpiador.Verify(l => l.AplicarTrasVerAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        sut.Dispose();
    }
}
```

Nota: el cuarto caso pasa por `Dispose()` al final solo para liberar; ese `Dispose` SÍ avisará (se vio el 5), por eso la verificación `Times.Never` va **antes** de `Dispose()`.

Ejecutar (esperado: `CS1739`, no existe el parámetro `limpiadorDeEpisodios`):
```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~ReproductorEliminarTrasVerTests"
```

- [ ] **Paso 2: el reproductor avisa**

En `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs`:

(a) Añadir el parámetro **al final** del constructor (después de `ISubtitleAssRenderer? subtitleAssRenderer = null`):
```csharp
        ISubtitleAssRenderer? subtitleAssRenderer = null,
        ILimpiadorDeEpisodios? limpiadorDeEpisodios = null)
```
y, en el cuerpo, junto a `_databaseService = databaseService;`:
```csharp
        _limpiadorDeEpisodios = limpiadorDeEpisodios;
```

(b) Junto a los campos `private string _rutaVideo = string.Empty;` (línea ~300) añadir:
```csharp

    /// <summary>"Eliminar tras ver": se le avisa al terminar de ver un episodio (al cerrar el reproductor o pasar a otro).</summary>
    private readonly ILimpiadorDeEpisodios? _limpiadorDeEpisodios;

    /// <summary>
    /// Avisa al servicio de que el episodio se vio de verdad reproduciéndolo (nunca por un marcado manual). No se espera: el
    /// servicio comprueba por su cuenta que la marca de "visto" esté guardada y registra sus propios errores.
    /// </summary>
    private void NotificarEpisodioTerminadoParaLimpieza(int animeId, int episodio)
    {
        if (_limpiadorDeEpisodios == null || animeId <= 0 || episodio <= 0) return;
        _ = _limpiadorDeEpisodios.AplicarTrasVerAsync(animeId, episodio);
    }
```

(c) En `Dispose()`, justo después de `GC.SuppressFinalize(this);`:
```csharp

        // "Eliminar tras ver": el reproductor también se libera al navegar a otra pestaña, no solo al pulsar salir.
        if (_fueMarcadoComoVisto) NotificarEpisodioTerminadoParaLimpieza(_animeId, _episodio);
```

(d) En `AsignarMetadatosDeEpisodio`, como **primeras líneas** del método (antes de `_rutaVideo = rutaVideo;`):
```csharp
        // Se pasa a otro episodio (autoplay, siguiente, anterior, el cajón): el que se deja, si se vio, ya terminó.
        if (_fueMarcadoComoVisto && (animeId != _animeId || episodio != _episodio)) NotificarEpisodioTerminadoParaLimpieza(_animeId, _episodio);

```

- [ ] **Paso 3: registrar el servicio**

En `AnimeLocalTracker/App.xaml.cs`, justo después de `services.AddSingleton<IPlaybackStateService, PlaybackStateService>();`:

```csharp

        // "Eliminar tras ver": borra el video de los episodios ya vistos según el modo de Configuración
        services.AddSingleton<ILimpiadorDeEpisodios, LimpiadorDeEpisodios>();
```

- [ ] **Paso 4: pruebas de la ficha (fallan)**

En `AnimeLocalTracker.Tests/ViewModels/DetalleEpisodiosSeguridadTests.cs`, añadir antes del cierre de la clase (junto a las pruebas de la sección "Aviso de episodios faltantes"):

```csharp
    // ── Archivo eliminado desde fuera de la lista ("Eliminar tras ver") ──

    [Fact]
    public async Task ArchivoEliminadoDesdeFuera_LaFilaDejaDeFigurarComoDescargada()
    {
        var sut = await AbrirFichaAsync(total: 4, enDisco: [1, 2, 3, 4], vistos: [1, 2, 3, 4]);
        sut.Episodios.Todos.First(e => e.NumeroEpisodio == 2).Descargado.Should().BeTrue();

        WeakReferenceMessenger.Default.Send(new ArchivoEpisodioEliminadoMensaje(Id, 2));

        var fila = sut.Episodios.Todos.First(e => e.NumeroEpisodio == 2);
        fila.Descargado.Should().BeFalse();
        fila.RutaCompleta.Should().BeEmpty();
        fila.Visto.Should().BeTrue("borrar el archivo no borra que se vio");
        sut.Episodios.Todos.First(e => e.NumeroEpisodio == 3).Descargado.Should().BeTrue();
    }

    [Fact]
    public async Task ArchivoEliminadoDeOtroAnime_NoTocaEstaFicha()
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: [1, 2, 3], vistos: []);

        WeakReferenceMessenger.Default.Send(new ArchivoEpisodioEliminadoMensaje(Id + 1, 2));

        sut.Episodios.Todos.First(e => e.NumeroEpisodio == 2).Descargado.Should().BeTrue();
    }
```

Ejecutar (esperado: falla la primera: la fila sigue como descargada; la segunda ya pasa):
```
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DetalleEpisodiosSeguridadTests"
```

- [ ] **Paso 5: la ficha escucha el aviso**

En `AnimeLocalTracker/ViewModels/EpisodiosFichaViewModel.cs`:

(a) En la declaración de la clase, añadir el receptor:
```csharp
    IRecipient<IdiomaCambiadoMensaje>,
    IRecipient<ArchivoEpisodioEliminadoMensaje>,
    IDisposable
```
(b) En el constructor, junto a los otros `Register`:
```csharp
        WeakReferenceMessenger.Default.Register<ArchivoEpisodioEliminadoMensaje>(this);
```
(c) Añadir, junto a `Receive(IdiomaCambiadoMensaje)`:
```csharp

    /// <summary>
    /// Un video se borró desde fuera de esta lista (p. ej. "Eliminar tras ver" al cerrar el reproductor): la fila deja de
    /// figurar como descargada. El borrado manual ya la limpió antes de avisar, así que aquí no hace nada.
    /// </summary>
    public void Receive(ArchivoEpisodioEliminadoMensaje message) => Core.HiloUi.Ejecutar(() =>
    {
        if (Anime == null || Anime.AniListId != message.AnimeId) return;
        var episodio = _todosLosEpisodios.FirstOrDefault(e => e.NumeroEpisodio == message.NumeroEpisodio);
        if (episodio is not { Descargado: true }) return;

        episodio.QuitarArchivo();
        AplicarFiltrosYOrdenamiento();
        ArchivosCambiados?.Invoke();
    });
```

- [ ] **Paso 6: comprobar que pasan**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~ReproductorEliminarTrasVerTests|FullyQualifiedName~DetalleEpisodiosSeguridadTests|FullyQualifiedName~ReproductorViewModelTests|FullyQualifiedName~DetalleExtrasTests"
```
Esperado: 0 advertencias; pasan las 4 pruebas nuevas del reproductor, las 2 nuevas de la ficha y todas las existentes de esas clases (los `new ReproductorViewModel(...)` de las pruebas antiguas no cambian: el parámetro es opcional).

---

### Tarea 5: Verificación final

- [ ] **Paso 1: suite completa, una sola vez**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false
```
Esperado: 0 advertencias y toda la suite en verde (2808 antes de este trabajo, más las nuevas: 4 + 10 + 12 + 12 + 4 + 2 = 44). Si falla una prueba intermitente conocida (`DownloadServiceTests`, `DescargasMusicaTests`), relanzarla sola antes de concluir que es regresión; si falla una de las que tocamos, corregir la causa.

- [ ] **Paso 2: comprobar que la suite no tocó tus datos reales**

Antes de la suite y después, listar `%LocalAppData%\AnimeLocalTrackerData` (`find "$LOCALAPPDATA/AnimeLocalTrackerData" -maxdepth 2 -printf '%P|%s|%T@\n'`, ignorando `Logs/` y `biblioteca.db-shm/-wal`) y hacer `diff`. Esperado: sin diferencias.

- [ ] **Paso 3: la prueba que las unitarias no pueden hacer: ¿suelta el archivo el reproductor real?**

Las pruebas unitarias no pueden saber si Flyleaf libera el video a tiempo. Verificar en la app real **con un perfil aislado** (memoria "Pruebas en la app real", procedimiento 0b):

1. Perfil temporal con una copia de la base de datos (`VACUUM INTO` desde una conexión `mode=ro`), todas las `RutaCarpeta` reescritas a una carpeta temporal (comprobar 0 fuera), `PreferenciaEmision.AutoDescargar = 0`, sin `anilist_token.txt` ni `cola_descargas.json`, `VolumenReproductor = 0` y, en su `settings.json`, `ModoEliminarTrasVer = "Automatico"` y `AccionFinEpisodio = "PausarYSalirFicha"`.
2. Generar un video de prueba corto con el `ffmpeg.exe` de `AnimeLocalTracker\FFmpeg\` (por ejemplo `-f lavfi -i testsrc=duration=12:size=320x240:rate=10 -f lavfi -i anullsrc -shortest -c:v libx264 -c:a aac`) y copiarlo como `Episodio 01.mp4` a la carpeta temporal de un anime del perfil (uno terminado).
3. Lanzar el exe de Debug con `USERPROFILE`, `LOCALAPPDATA` y `APPDATA` apuntando al perfil, `ANIMELOCALTRACKER_LOG_DETALLADO=1`, anotando antes los PID de otras instancias. Abrir la ficha por accesibilidad (UIA) y reproducir el episodio hasta el final.
4. Comprobar: el archivo ya no está en disco; el registro del episodio sigue en la copia de la base de datos con `VistoLocal = 1` y `RutaArchivo` vacía; el registro de sesión del perfil contiene "Eliminar tras ver: borrado el episodio 1"; y la fila de la ficha ya no figura como descargada.
5. Cerrar **solo el PID lanzado** y borrar el perfil temporal. Los scripts `.ps1` de esta prueba solo con ASCII (el filtro de PowerShell bloquea falsamente `*` y `//`).

Si no se puede conducir la reproducción por UIA de forma fiable, **decirlo en el informe** en vez de darlo por verificado: lo que queda sin comprobar es solo que el reproductor suelte el archivo a tiempo (el servicio ya espera 1 s y reintenta 3 s).

- [ ] **Paso 4: dejar el trabajo sin commit**

`git status` debe mostrar solo los archivos de la tabla de estructura. No hacer commit ni push: el usuario decide cuándo.

---

## Revisión del plan contra el diseño

- **Modos de Configuración y aviso de borrado definitivo** → Tarea 2. **Función de decisión y los tres modos** → Tarea 3 (`LimpiezaTrasVer`). **Protecciones** (dentro de la carpeta, sin ver, en uso, registro conservado, aviso) → Tarea 3 (`LimpiadorDeEpisodios`). **Disparo al terminar de ver** → Tarea 4. **Borrado manual con el mismo camino** → Tarea 1.
- **Fuera de este plan:** excepciones por anime, "preguntar tras cada episodio", deshacer y Papelera.
- **No cubierto por pruebas unitarias:** que Flyleaf libere el archivo a tiempo (Tarea 5, Paso 3), el aspecto del selector en Configuración (se ve al abrir la pantalla en el perfil aislado) y el retraducido del selector al cambiar de idioma (usa `{loc:T}` unidireccional).
