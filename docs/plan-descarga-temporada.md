# Descarga de temporada completa: plan de implementación

> **Para quien ejecute el plan:** ejecutar en la sesión con `superpowers:executing-plans` (el repo prohíbe lanzar subagentes por iniciativa propia). Los pasos usan casillas `- [ ]`. **Sin commit**: la regla del repo dice que no se hace commit ni push sin orden del usuario, así que cada tarea termina en verificación, no en commit.

**Objetivo:** un botón en la ficha que encole de una vez los episodios ya emitidos que aún no están en disco, con confirmación y una estimación de espacio.

**Arquitectura:** dos funciones puras nuevas en `EpisodiosOrganizador` (qué episodios faltan y cuánto espacio ocuparían) y un comando nuevo en `EpisodiosFichaViewModel` que reutiliza `DescargarEpisodioAsync` y la cola de descargas actual. La ficha le pasa el "último episodio emitido" que ya calcula. Sin servicios nuevos.

**Tecnología:** C# 12 / .NET 8, WPF, CommunityToolkit.Mvvm, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md` (sección 3).

## Restricciones globales

- Solo la Parte 1. "Eliminar tras ver" tendrá su propio plan.
- 0 advertencias: `TreatWarningsAsErrors` está activo (`Directory.Build.props`).
- Textos con `{loc:T Clave}` en XAML y `LocalizationService.T("Clave")` en código; toda clave nueva, en ES **y** EN (`Services/LocalizationService.cs`).
- Colores solo de `Brush.*`/`AppText.*` o los literales ya usados en esa vista; botones con `AutomationProperties.Name` y `Cursor="Hand"` (`ui-wpf-vistas.md`).
- Las pruebas nunca escriben bajo `AppDataPaths`: solo carpetas temporales.
- Trabajo de disco fuera del hilo de interfaz (`Task.Run`).
- Verificación según `repo-build-test`: pruebas afectadas por tarea; la suite completa **una sola vez** al final de la Tarea 3.

## Enfoque de revisión (casos que el diseño implica y conviene fijar con pruebas)

1. **Serie vista casi entera con los archivos liberados** (One Piece: 1179 vistos, solo el último en disco): no debe ofrecer volver a descargar lo ya visto. Prueba en Tarea 1 y en Tarea 2.
2. **Total de episodios desconocido** (`UltimoEmitido = 0`): el botón no aparece. Pruebas en Tarea 1 y 2.
3. **Anime sin carpeta asignada**: avisa y no encola nada. Prueba en Tarea 2.
4. **El disco no alcanza**: el diálogo lo advierte pero no bloquea. Prueba en Tarea 2.
5. **Episodios ya en descarga y doble pulsación**: no se duplican en la cola y el botón desaparece al encolar. Pruebas en Tarea 1 y 2.

## Desviaciones respecto al diseño (ya corregidas en el documento de diseño)

- **Ubicación del botón:** el diseño decía "estado vacío de la ficha", pero la lista ya dibuja las filas 1..N aunque no haya archivos, así que ese estado casi nunca sale. El botón va en una fila sobre la lista, junto a los avisos.
- **Vistos excluidos:** los episodios ya vistos no se ofrecen (misma regla que `CalcularFaltantes`). Sin esto, con "Eliminar tras ver" activo el botón reofrecería lo que se acaba de borrar.
- **Aviso sin carpeta:** el mensaje existente `Det_AutoDescargaSinCarpetaMsj` habla de "volver a activarlo" y no encaja; se añade una clave propia.

## Estructura de archivos

| Archivo | Cambio |
|---|---|
| `AnimeLocalTracker/Core/EpisodiosOrganizador.cs` | Modificar: añadir `CalcularPendientesTemporada` y `EstimarBytes` |
| `AnimeLocalTracker.Tests/Core/EpisodiosOrganizadorTests.cs` | Modificar: pruebas de las dos funciones |
| `AnimeLocalTracker/ViewModels/EpisodiosFichaViewModel.cs` | Modificar: estado, comando, estimación y refresco |
| `AnimeLocalTracker/ViewModels/DetalleViewModel.ProximaEmision.cs` | Modificar: pasar `UltimoEmitido` a `Episodios` |
| `AnimeLocalTracker/Services/LocalizationService.cs` | Modificar: 6 claves en ES y en EN |
| `AnimeLocalTracker.Tests/ViewModels/DescargarTemporadaTests.cs` | Crear: pruebas del botón y del comando |
| `AnimeLocalTracker/Views/DetalleView.xaml` | Modificar: botón sobre la lista |

---

### Tarea 1: Funciones puras de pendientes y de espacio

**Archivos:**
- Modificar: `AnimeLocalTracker/Core/EpisodiosOrganizador.cs` (al final de la clase, después de `CalcularFaltantes`)
- Probar: `AnimeLocalTracker.Tests/Core/EpisodiosOrganizadorTests.cs`

**Interfaces:**
- Produce: `EpisodiosOrganizador.CalcularPendientesTemporada(IReadOnlyCollection<EpisodioItem> episodios, int ultimoEmitido) -> List<int>` y `EpisodiosOrganizador.EstimarBytes(IReadOnlyCollection<long> tamanosDescargados, int pendientes) -> long?`.

- [ ] **Paso 1: escribir las pruebas que fallan**

Añadir dentro de la clase `EpisodiosOrganizadorTests` (antes de la llave de cierre de la clase):

```csharp
    // ── Descargar temporada ──

    private static List<EpisodioItem> Filas(int hasta) =>
        Enumerable.Range(1, hasta).Select(n => new EpisodioItem { NumeroEpisodio = n }).ToList();

    [Fact]
    public void CalcularPendientesTemporada_CarpetaVacia_DevuelveTodosLosEmitidos()
    {
        EpisodiosOrganizador.CalcularPendientesTemporada(Filas(5), ultimoEmitido: 5).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public void CalcularPendientesTemporada_SerieEnEmision_SoloCuentaLosYaEmitidos()
    {
        // 24 filas en la lista, pero solo 9 episodios han salido.
        EpisodiosOrganizador.CalcularPendientesTemporada(Filas(24), ultimoEmitido: 9).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9);
    }

    [Fact]
    public void CalcularPendientesTemporada_ExcluyeDescargadosEnDescargaYVistos()
    {
        var episodios = Filas(5);
        episodios[0].Descargado = true;      // 1: ya está en disco
        episodios[1].IsDownloading = true;   // 2: ya se está descargando
        episodios[2].Visto = true;           // 3: ya visto (y liberado)

        EpisodiosOrganizador.CalcularPendientesTemporada(episodios, ultimoEmitido: 5).Should().Equal(4, 5);
    }

    [Fact]
    public void CalcularPendientesTemporada_TotalDesconocido_NoDevuelveNada()
    {
        EpisodiosOrganizador.CalcularPendientesTemporada(Filas(5), ultimoEmitido: 0).Should().BeEmpty();
    }

    [Fact]
    public void CalcularPendientesTemporada_TodoVistoYSoloElUltimoEnDisco_NoOfreceRedescargar()
    {
        // El caso de One Piece: todo visto y liberado salvo el último, que sigue en disco.
        var episodios = Filas(50);
        foreach (var e in episodios) e.Visto = true;
        episodios[^1].Descargado = true;

        EpisodiosOrganizador.CalcularPendientesTemporada(episodios, ultimoEmitido: 50).Should().BeEmpty();
    }

    [Fact]
    public void EstimarBytes_PromediaLosArchivosQueYaHayEnDisco()
    {
        EpisodiosOrganizador.EstimarBytes([1000L, 3000L], pendientes: 3).Should().Be(6000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void EstimarBytes_SinArchivosDeReferenciaOSinPendientes_NoInventaUnaCifra(int pendientes)
    {
        var tamanos = pendientes == 0 ? new[] { 1000L } : System.Array.Empty<long>();

        EpisodiosOrganizador.EstimarBytes(tamanos, pendientes).Should().BeNull();
    }
```

- [ ] **Paso 2: comprobar que fallan**

Ejecutar:
```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~EpisodiosOrganizadorTests"
```
Esperado: error de compilación `CS0117: 'EpisodiosOrganizador' no contiene una definición para 'CalcularPendientesTemporada'` (y para `EstimarBytes`).

- [ ] **Paso 3: implementar**

Añadir al final de la clase `EpisodiosOrganizador`, después de `CalcularFaltantes`:

```csharp

    /// <summary>
    /// Episodios que "Descargar temporada" pone en cola: los ya emitidos (1..<paramref name="ultimoEmitido"/>) que no están en
    /// disco, no se están descargando y no se han visto. Los vistos no cuentan por la misma razón que en
    /// <see cref="CalcularFaltantes"/> (ver y liberar el archivo es lo normal; con "Eliminar tras ver" activo, volver a ofrecer
    /// lo recién borrado sería absurdo). Sin episodio emitido conocido (<paramref name="ultimoEmitido"/> ≤ 0) no hay nada.
    /// </summary>
    public static List<int> CalcularPendientesTemporada(IReadOnlyCollection<EpisodioItem> episodios, int ultimoEmitido)
    {
        if (ultimoEmitido <= 0) return [];

        return episodios
            .Where(e => e.NumeroEpisodio >= 1 && e.NumeroEpisodio <= ultimoEmitido && !e.Descargado && !e.IsDownloading && !e.Visto)
            .Select(e => e.NumeroEpisodio)
            .OrderBy(n => n)
            .ToList();
    }

    /// <summary>
    /// Espacio estimado para <paramref name="pendientes"/> episodios: tamaño medio de los archivos que ya hay en disco por el
    /// número de pendientes. Null si no hay ningún archivo de referencia (no se inventa una cifra).
    /// </summary>
    public static long? EstimarBytes(IReadOnlyCollection<long> tamanosDescargados, int pendientes)
    {
        if (pendientes <= 0 || tamanosDescargados.Count == 0) return null;
        return (long)(tamanosDescargados.Average() * pendientes);
    }
```

- [ ] **Paso 4: comprobar que pasan**

Ejecutar los mismos dos comandos del Paso 2. Esperado: todas las pruebas de `EpisodiosOrganizadorTests` pasan (las 7 nuevas y las anteriores), 0 advertencias.

---

### Tarea 2: Estado, comando y confirmación en la ficha

**Archivos:**
- Modificar: `AnimeLocalTracker/Services/LocalizationService.cs` (líneas ~1064 ES y ~2392 EN)
- Modificar: `AnimeLocalTracker/ViewModels/EpisodiosFichaViewModel.cs`
- Modificar: `AnimeLocalTracker/ViewModels/DetalleViewModel.ProximaEmision.cs` (método `SincronizarUltimoEmitidoAsync`)
- Crear: `AnimeLocalTracker.Tests/ViewModels/DescargarTemporadaTests.cs`

**Interfaces:**
- Consume (Tarea 1): `EpisodiosOrganizador.CalcularPendientesTemporada` y `EpisodiosOrganizador.EstimarBytes`.
- Produce: `EpisodiosFichaViewModel.UltimoEmitido` (int), `HayPendientesTemporada` (bool), `DescargarTemporadaTexto` (string), `DescargarTemporadaCommand`, `internal Func<string, long?> EspacioLibreDe`. La Tarea 3 enlaza el botón a las tres primeras.

- [ ] **Paso 1: escribir las pruebas que fallan**

Crear `AnimeLocalTracker.Tests/ViewModels/DescargarTemporadaTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Botón "Descargar temporada" de la ficha (docs/investigacion-descarga-masiva-y-eliminar-tras-ver.md, parte 1): cuándo
/// aparece, qué dice, qué encola y cómo confirma.
/// </summary>
public sealed class DescargarTemporadaTests : IDisposable
{
    private const int Id = 21;

    private readonly Mock<IAnimeTrackingService> _tracking = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IAuthService> _auth = new();
    private readonly Mock<IFileScannerService> _escaner = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt-temporada-" + Guid.NewGuid().ToString("N"));

    public DescargarTemporadaTests() => Directory.CreateDirectory(_carpeta);

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); }
        catch (IOException) { /* limpieza de una carpeta temporal: si no se puede, el sistema la borrará */ }
    }

    /// <summary>Abre una ficha de <paramref name="total"/> episodios con esos archivos (de esos tamaños) en la carpeta temporal.</summary>
    private async Task<DetalleViewModel> AbrirFichaAsync(int total, Dictionary<int, int> enDisco, int[]? vistos = null, string? carpeta = null)
    {
        var anime = new AnimeItem { AniListId = Id, Titulo = "Frieren", TotalEpisodios = total, RutaCarpeta = carpeta ?? _carpeta };
        var encontrados = new List<EpisodioItem>();
        foreach (var (numero, bytes) in enDisco)
        {
            string ruta = Path.Combine(_carpeta, $"Episodio {numero}.mp4");
            File.WriteAllBytes(ruta, new byte[bytes]);
            encontrados.Add(new EpisodioItem { NumeroEpisodio = numero, RutaCompleta = ruta });
        }

        _escaner.Setup(e => e.EscanearEpisodiosAsync(anime.RutaCarpeta)).ReturnsAsync(encontrados);
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(Id))
            .ReturnsAsync((vistos ?? []).Select(n => new RegistroEpisodio { AniListId = Id, NumeroEpisodio = n, VistoLocal = true }).ToList());
        double p = 0;
        _descargas.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);

        var sut = new DetalleViewModel(_tracking.Object, _db.Object, _auth.Object, _escaner.Object, _dialogos.Object, _descargas.Object);
        await sut.InicializarAsync(anime);
        return sut;
    }

    private List<int> CapturarDescargas()
    {
        var numeros = new List<int>();
        _descargas.Setup(d => d.IniciarDescargaEpisodioAsync(Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IEnumerable<string>?>()))
            .Callback<int, string, string, int, IEnumerable<string>?>((_, _, _, n, _) => numeros.Add(n))
            .Returns(Task.CompletedTask);
        return numeros;
    }

    private void ResponderConfirmacion(bool respuesta) =>
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(respuesta);

    // ── El botón y su texto ──

    [Fact]
    public async Task Boton_CarpetaVacia_OfreceLaTemporadaCompleta()
    {
        var sut = await AbrirFichaAsync(total: 12, enDisco: []);

        sut.Episodios.UltimoEmitido = 12;

        sut.Episodios.HayPendientesTemporada.Should().BeTrue();
        sut.Episodios.DescargarTemporadaTexto.Should().Be(string.Format(LocalizationService.T("Det_TemporadaCompletaFormato"), 12));
    }

    [Fact]
    public async Task Boton_ConAlgunosEnDisco_OfreceLosQueFaltan()
    {
        var sut = await AbrirFichaAsync(total: 12, enDisco: new() { [1] = 10, [2] = 10 });

        sut.Episodios.UltimoEmitido = 12;

        sut.Episodios.DescargarTemporadaTexto.Should().Be(string.Format(LocalizationService.T("Det_TemporadaFaltanFormato"), 10));
    }

    [Fact]
    public async Task Boton_SerieEnEmision_SoloCuentaLosYaEmitidos()
    {
        var sut = await AbrirFichaAsync(total: 24, enDisco: []);

        sut.Episodios.UltimoEmitido = 9;

        sut.Episodios.DescargarTemporadaTexto.Should().Be(string.Format(LocalizationService.T("Det_TemporadaCompletaFormato"), 9));
    }

    [Fact]
    public async Task Boton_TotalDesconocido_NoAparece()
    {
        var sut = await AbrirFichaAsync(total: 12, enDisco: []);

        sut.Episodios.UltimoEmitido = 0;

        sut.Episodios.HayPendientesTemporada.Should().BeFalse();
        sut.Episodios.DescargarTemporadaTexto.Should().BeEmpty();
    }

    [Fact]
    public async Task Boton_TodoVistoYSoloElUltimoEnDisco_NoAparece()
    {
        var sut = await AbrirFichaAsync(total: 50, enDisco: new() { [50] = 10 }, vistos: Enumerable.Range(1, 50).ToArray());

        sut.Episodios.UltimoEmitido = 50;

        sut.Episodios.HayPendientesTemporada.Should().BeFalse("lo visto y liberado no se vuelve a ofrecer");
    }

    // ── El comando ──

    [Fact]
    public async Task Comando_Pocos_EncolaEnOrdenSinPreguntar()
    {
        var sut = await AbrirFichaAsync(total: 6, enDisco: []);
        sut.Episodios.UltimoEmitido = 3;
        var numeros = CapturarDescargas();

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        numeros.Should().Equal(1, 2, 3);
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Comando_AlEncolar_ElBotonDesapareceYUnSegundoClicNoDuplica()
    {
        var sut = await AbrirFichaAsync(total: 3, enDisco: []);
        sut.Episodios.UltimoEmitido = 3;
        var numeros = CapturarDescargas();

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);
        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        numeros.Should().Equal(1, 2, 3);
        sut.Episodios.HayPendientesTemporada.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 10)]
    public async Task Comando_Muchos_PreguntaAntesDePonerLaColaEnMarcha(bool confirma, int esperadas)
    {
        var sut = await AbrirFichaAsync(total: 10, enDisco: []);
        sut.Episodios.UltimoEmitido = 10;
        var numeros = CapturarDescargas();
        ResponderConfirmacion(confirma);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        numeros.Should().HaveCount(esperadas);
    }

    [Fact]
    public async Task Comando_SinArchivosDeReferencia_ConfirmaSoloConLaCantidad()
    {
        var sut = await AbrirFichaAsync(total: 10, enDisco: []);
        sut.Episodios.UltimoEmitido = 10;
        string? mensaje = null;
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, bool, string, string>((_, m, _, _, _) => mensaje = m)
            .ReturnsAsync(false);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        mensaje.Should().Be(string.Format(LocalizationService.T("Det_DescargarFaltantesConfirmacionFormato"), 10));
    }

    [Fact]
    public async Task Comando_ConArchivosDeReferencia_EstimaConElTamanoMedio()
    {
        // 2 archivos de 1000 y 3000 bytes → media 2000; 8 pendientes → 16000 bytes.
        var sut = await AbrirFichaAsync(total: 10, enDisco: new() { [1] = 1000, [2] = 3000 });
        sut.Episodios.UltimoEmitido = 10;
        sut.Episodios.EspacioLibreDe = _ => long.MaxValue;
        string? mensaje = null;
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, bool, string, string>((_, m, _, _, _) => mensaje = m)
            .ReturnsAsync(false);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        mensaje.Should().Be(string.Format(LocalizationService.T("Det_TemporadaConfirmacionFormato"), 8, AnimeLocalTracker.Core.Formato.Tamano(16000)));
    }

    [Fact]
    public async Task Comando_SiNoCabeEnElDisco_AvisaPeroNoBloquea()
    {
        var sut = await AbrirFichaAsync(total: 10, enDisco: new() { [1] = 1000, [2] = 3000 });
        sut.Episodios.UltimoEmitido = 10;
        sut.Episodios.EspacioLibreDe = _ => 5000;
        var numeros = CapturarDescargas();
        string? mensaje = null;
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, bool, string, string>((_, m, _, _, _) => mensaje = m)
            .ReturnsAsync(true);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        mensaje.Should().Contain(string.Format(LocalizationService.T("Det_TemporadaAvisoEspacioFormato"),
            AnimeLocalTracker.Core.Formato.Tamano(16000), AnimeLocalTracker.Core.Formato.Tamano(5000)));
        numeros.Should().HaveCount(8, "el aviso no impide descargar si el usuario acepta");
    }

    [Fact]
    public async Task Comando_SinCarpetaAsignada_AvisaYNoEncolaNada()
    {
        var sut = await AbrirFichaAsync(total: 4, enDisco: [], carpeta: "");
        sut.Episodios.UltimoEmitido = 4;
        var numeros = CapturarDescargas();
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await sut.Episodios.DescargarTemporadaCommand.ExecuteAsync(null);

        numeros.Should().BeEmpty();
        _dialogos.Verify(d => d.MostrarDialogoAsync(
            LocalizationService.T("Det_AutoDescargaSinCarpetaTitulo"), LocalizationService.T("Det_TemporadaSinCarpetaMsj"),
            false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DescargarTemporadaTests"
```
Esperado: error de compilación `CS1061: 'EpisodiosFichaViewModel' no contiene una definición para 'UltimoEmitido'` (y para `HayPendientesTemporada`, `DescargarTemporadaCommand`, `EspacioLibreDe`).

- [ ] **Paso 3: añadir las claves de localización**

En `AnimeLocalTracker/Services/LocalizationService.cs`, después de la línea ES `["Det_DescargarFaltantesConfirmacionFormato"] = "¿Poner en cola la descarga de {0} episodios?",`:

```csharp
        ["Det_TemporadaCompletaFormato"] = "Descargar temporada completa ({0})",
        ["Det_TemporadaFaltanFormato"] = "Descargar los que faltan ({0})",
        ["Det_TemporadaTitulo"] = "Descargar temporada",
        ["Det_TemporadaConfirmacionFormato"] = "¿Poner en cola la descarga de {0} episodios (≈ {1})?",
        ["Det_TemporadaAvisoEspacioFormato"] = "Ojo: necesitan ≈ {0} y solo quedan {1} libres en ese disco.",
        ["Det_TemporadaSinCarpetaMsj"] = "Este anime no tiene una carpeta asignada, así que no hay dónde guardar los episodios. Asígnale una carpeta y vuelve a intentarlo.",
```

Y después de la línea EN `["Det_DescargarFaltantesConfirmacionFormato"] = "Queue the download of {0} episodes?",`:

```csharp
        ["Det_TemporadaCompletaFormato"] = "Download full season ({0})",
        ["Det_TemporadaFaltanFormato"] = "Download the missing ones ({0})",
        ["Det_TemporadaTitulo"] = "Download season",
        ["Det_TemporadaConfirmacionFormato"] = "Queue the download of {0} episodes (≈ {1})?",
        ["Det_TemporadaAvisoEspacioFormato"] = "Heads up: they need ≈ {0} and only {1} are free on that drive.",
        ["Det_TemporadaSinCarpetaMsj"] = "This anime has no folder assigned, so there is nowhere to save the episodes. Assign it a folder and try again.",
```

- [ ] **Paso 4: implementar el estado, la estimación y el comando**

En `AnimeLocalTracker/ViewModels/EpisodiosFichaViewModel.cs`:

**(a)** Justo después de `private List<int> _numerosEpisodiosFaltantes = new();` añadir:

```csharp

    // === DESCARGAR TEMPORADA (los episodios ya emitidos que aún no están en disco ni se han visto) ===
    /// <summary>Último episodio ya emitido (lo calcula la ficha con <see cref="IEmisionMonitorService.UltimoEmitido"/>); 0 si no se sabe.</summary>
    [ObservableProperty] private int _ultimoEmitido;
    [ObservableProperty] private bool _hayPendientesTemporada;
    [ObservableProperty] private string _descargarTemporadaTexto = string.Empty;
    private List<int> _numerosPendientesTemporada = new();

    partial void OnUltimoEmitidoChanged(int value) => ActualizarPendientesTemporada();

    /// <summary>Recalcula qué episodios pondría en cola "Descargar temporada" y el texto del botón.</summary>
    private void ActualizarPendientesTemporada()
    {
        _numerosPendientesTemporada = Core.EpisodiosOrganizador.CalcularPendientesTemporada(_todosLosEpisodios, UltimoEmitido);
        int n = _numerosPendientesTemporada.Count;

        HayPendientesTemporada = n > 0;
        // "Temporada completa" solo si todos los episodios emitidos están pendientes; si ya tienes alguno, "los que faltan".
        DescargarTemporadaTexto = n == 0
            ? string.Empty
            : string.Format(LocalizationService.T(n == UltimoEmitido ? "Det_TemporadaCompletaFormato" : "Det_TemporadaFaltanFormato"), n);
    }
```

**(b)** En `ActualizarEpisodiosFaltantes()`, añadir una línea al final del cuerpo y ampliar su resumen. Resultado:

```csharp
    /// <summary>
    /// Detecta huecos reales (episodios sin archivo local y sin ver, por debajo del episodio descargado
    /// más alto): un hueco silencioso que haría saltarte una trama por accidente. Ver
    /// <see cref="Core.EpisodiosOrganizador.CalcularFaltantes"/> para lo que NO cuenta como hueco.
    /// También refresca el botón "Descargar temporada": se recalcula siempre que cambia el estado de la lista.
    /// </summary>
    private void ActualizarEpisodiosFaltantes()
    {
        _numerosEpisodiosFaltantes = Core.EpisodiosOrganizador.CalcularFaltantes(_todosLosEpisodios);

        HayEpisodiosFaltantes = _numerosEpisodiosFaltantes.Count > 0;
        EpisodiosFaltantesTexto = HayEpisodiosFaltantes
            ? string.Format(LocalizationService.T("Det_FaltantesBannerFormato"), _numerosEpisodiosFaltantes.Count, FormatearListaEpisodios(_numerosEpisodiosFaltantes))
            : string.Empty;

        ActualizarPendientesTemporada();
    }
```

**(c)** En `Receive(DescargaProgresoMensaje)`, dentro del `InvokeAsync`, cuando una descarga se cancela o falla el episodio vuelve a estar pendiente. Cambiar:

```csharp
                episodio.DownloadProgress = message.Progreso;
                ActualizarAccionPrincipal();
```
por:
```csharp
                episodio.DownloadProgress = message.Progreso;
                ActualizarAccionPrincipal();
                if (!message.IsDownloading) ActualizarPendientesTemporada(); // terminó, falló o se canceló: vuelve (o no) a estar pendiente
```

**(d)** Justo después del método `DescargarFaltantesAsync` (antes del comentario del "Doctor de integridad") añadir:

```csharp

    /// <summary>
    /// Espacio libre (bytes) de la unidad donde está la carpeta; null si no se puede saber (ruta vacía, de red o inválida).
    /// Ajustable solo en pruebas.
    /// </summary>
    internal Func<string, long?> EspacioLibreDe { get; set; } = EspacioLibreEnDisco;

    private static long? EspacioLibreEnDisco(string carpeta)
    {
        try { return new DriveInfo(Path.GetPathRoot(carpeta)!).AvailableFreeSpace; }
        catch (Exception) { return null; } // sin unidad reconocible: simplemente no se avisa del espacio
    }

    /// <summary>
    /// Pone en cola los episodios ya emitidos que faltan (ver <see cref="Core.EpisodiosOrganizador.CalcularPendientesTemporada"/>),
    /// en orden y con la misma cola que una descarga suelta (respeta el límite de descargas simultáneas).
    /// </summary>
    [RelayCommand]
    private async Task DescargarTemporadaAsync()
    {
        if (Anime == null) return;

        var numeros = _numerosPendientesTemporada.ToHashSet();
        var pendientes = _todosLosEpisodios
            .Where(e => numeros.Contains(e.NumeroEpisodio) && !e.Descargado && !e.IsDownloading && !e.Visto)
            .OrderBy(e => e.NumeroEpisodio)
            .ToList();
        if (pendientes.Count == 0) return;

        if (string.IsNullOrWhiteSpace(Anime.RutaCarpeta))
        {
            await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Det_AutoDescargaSinCarpetaTitulo"),
                LocalizationService.T("Det_TemporadaSinCarpetaMsj"),
                false, "FolderAlertOutline", "#F59E0B");
            return;
        }

        if (pendientes.Count > UmbralConfirmarDescargaFaltantes)
        {
            bool confirmar = await _dialogService.MostrarDialogoAsync(
                LocalizationService.T("Det_TemporadaTitulo"),
                await ComponerConfirmacionTemporadaAsync(pendientes.Count, Anime.RutaCarpeta),
                true, "DownloadMultiple", "#2563EB");
            if (!confirmar) return;
        }

        foreach (var episodio in pendientes)
        {
            await DescargarEpisodioAsync(episodio);
        }

        ActualizarPendientesTemporada();
    }

    /// <summary>Texto de la confirmación: cuántos episodios, el espacio estimado y, si no cabe en el disco, un aviso.</summary>
    private async Task<string> ComponerConfirmacionTemporadaAsync(int cantidad, string carpeta)
    {
        var archivos = _todosLosEpisodios
            .Where(e => e.Descargado && !string.IsNullOrWhiteSpace(e.RutaCompleta))
            .Select(e => e.RutaCompleta)
            .ToList();

        // Accesos a disco fuera del hilo de interfaz.
        var (tamanos, libre) = await Task.Run(() =>
        {
            var lista = new List<long>();
            foreach (var ruta in archivos)
            {
                try
                {
                    var info = new FileInfo(ruta);
                    if (info.Exists) lista.Add(info.Length);
                }
                catch (IOException) { /* archivo en uso o desaparecido: no cuenta */ }
            }
            return (lista, EspacioLibreDe(carpeta));
        });

        if (Core.EpisodiosOrganizador.EstimarBytes(tamanos, cantidad) is not { } bytes)
            return string.Format(LocalizationService.T("Det_DescargarFaltantesConfirmacionFormato"), cantidad);

        string texto = string.Format(LocalizationService.T("Det_TemporadaConfirmacionFormato"), cantidad, Core.Formato.Tamano(bytes));
        if (libre is { } disponible && bytes > disponible)
        {
            texto += "\n\n" + string.Format(LocalizationService.T("Det_TemporadaAvisoEspacioFormato"), Core.Formato.Tamano(bytes), Core.Formato.Tamano(disponible));
        }
        return texto;
    }
```

- [ ] **Paso 5: pasar el último emitido desde la ficha**

En `AnimeLocalTracker/ViewModels/DetalleViewModel.ProximaEmision.cs`, método `SincronizarUltimoEmitidoAsync`, cambiar:

```csharp
        int emitido = _monitorEmision.UltimoEmitido(proxima, anime, DateTime.UtcNow);
        if (emitido <= Episodios.Todos.Count) return;
```
por:
```csharp
        int emitido = _monitorEmision.UltimoEmitido(proxima, anime, DateTime.UtcNow);
        Episodios.UltimoEmitido = emitido; // alimenta el botón "Descargar temporada"
        if (emitido <= Episodios.Todos.Count) return;
```

- [ ] **Paso 6: comprobar que pasan**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~DescargarTemporadaTests|FullyQualifiedName~DetalleEpisodiosSeguridadTests|FullyQualifiedName~EpisodiosOrganizadorTests"
```
Esperado: 0 errores y 0 advertencias en el build; pasan las pruebas nuevas y las existentes de esas dos clases (en especial `DescargarFaltantes_*`, que no deben cambiar).

---

### Tarea 3: Botón en la ficha y verificación final

**Archivos:**
- Modificar: `AnimeLocalTracker/Views/DetalleView.xaml` (dentro del `StackPanel Grid.Row="0"` de la lista de episodios, justo antes del `Border` del banner de faltantes)

**Interfaces:**
- Consume (Tarea 2): `Episodios.HayPendientesTemporada`, `Episodios.DescargarTemporadaTexto`, `Episodios.DescargarTemporadaCommand`.

- [ ] **Paso 1: añadir el botón**

Insertar, justo antes del comentario `<!-- BANNER EPISODIOS FALTANTES (huecos silenciosos en la carpeta local) -->`:

```xml
                        <!-- DESCARGAR TEMPORADA: los episodios ya emitidos que aún no están en disco ni se han visto -->
                        <Button Command="{Binding Episodios.DescargarTemporadaCommand}"
                                Style="{StaticResource MaterialDesignOutlinedButton}"
                                BorderBrush="{StaticResource Brush.AccentSoft}" Foreground="{StaticResource Brush.AccentSoft}"
                                HorizontalAlignment="Left" Height="32" Padding="12,0" Margin="0,0,0,10" Cursor="Hand"
                                Visibility="{Binding Episodios.HayPendientesTemporada, Converter={StaticResource BoolToVis}}"
                                AutomationProperties.Name="{Binding Episodios.DescargarTemporadaTexto}">
                            <StackPanel Orientation="Horizontal">
                                <materialDesign:PackIcon Kind="DownloadMultiple" Width="16" Height="16" VerticalAlignment="Center" Margin="0,0,6,0"/>
                                <TextBlock Text="{Binding Episodios.DescargarTemporadaTexto}" VerticalAlignment="Center" FontWeight="SemiBold" FontSize="12"/>
                            </StackPanel>
                        </Button>

```

- [ ] **Paso 2: compilar (el XAML se valida al compilar)**

```
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug
```
Esperado: 0 errores y 0 advertencias. Si aparece `CS2001` con `*.g.cs` o `_wpftmp`, es el flake conocido: reintentar el mismo comando (skill `repo-build-test`).

- [ ] **Paso 3: verificación visual en un perfil aislado**

Seguir el skill `wpf-visual-verification` con un perfil aislado (variables de entorno, ver memoria "Pruebas en la app real"). Comprobar solo la **apariencia**: abrir la ficha de un anime con episodios sin descargar y mirar que el botón sale encima de la lista, con su texto, y que desaparece en un anime ya descargado. **No confirmar el diálogo** en la app real: arrancaría descargas de verdad. No cerrar la instancia del usuario; cerrar solo el proceso lanzado por PID.

- [ ] **Paso 4: suite completa, una sola vez**

```
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false
```
Esperado: todas las pruebas pasan (2787 antes de este trabajo, más las nuevas). Si falla una prueba intermitente conocida (`DownloadServiceTests`, `DescargasMusicaTests`), relanzarla sola antes de concluir que es regresión; si falla una de las que tocamos, corregir la causa.

- [ ] **Paso 5: dejar el trabajo sin commit**

`git status` debe mostrar solo los archivos de la tabla de estructura. No hacer commit ni push: el usuario decide cuándo.

---

## Revisión del plan contra el diseño

- **Función pura de pendientes** → Tarea 1. **Comando, orden, confirmación sobre 3 episodios, estimación y aviso de disco, sin carpeta** → Tarea 2. **Botón único que se adapta (completa / los que faltan)** → Tarea 2 (texto) y Tarea 3 (botón). **Total desconocido: sin botón** → Tareas 1 y 2.
- **Fuera de este plan:** "Eliminar tras ver" (Parte 2) y cualquier cambio al aviso de huecos existente.
- **No probado por pruebas unitarias:** el retraducido al cambiar de idioma (lo cubre `RefrescarTextosTraducidos` → `ActualizarEpisodiosFaltantes`) y el aspecto del botón (se comprueba en la Tarea 3, Paso 3).
