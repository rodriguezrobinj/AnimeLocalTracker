# Arrastrar torrents y pegar enlaces magnet: diseño y plan

Fecha: 2026-10-18. Tercera de las cuatro funciones pedidas ese día (las dos primeras: desfase de audio/subtítulos `6ac2c4b` y orden de visionado `d0c2cb2`).

## Objetivo

Hoy, para bajar un torrent encontrado fuera de la app (Nyaa en el navegador, un foro), hay que abrir un cliente de torrents, elegir la carpeta del anime a mano, esperar, volver a la app y escanear. Con esta función se suelta el `.torrent` (o se pega el magnet) en la ventana de AnimeLocalTracker; la app reconoce a qué anime de la biblioteca pertenece, enseña qué episodios trae y, al confirmar, los descarga en la carpeta de ese anime con la cola de descargas normal.

Ejemplo: en Nyaa se arrastra *[SubsPlease] Sousou no Frieren (01-28) (1080p)* a la ventana; sale el cuadro con "Sousou no Frieren · episodios 1–28 · 5 ya los tienes"; Descargar → 23 filas en la pestaña Descargas que se bajan una tras otra.

## Decisiones tomadas (con el usuario)

- **Lotes:** una fila por episodio en la cola (avance, pausa, cancelar y aro de la ficha por episodio, como cualquier descarga). Descartadas: una sola fila por torrent (tipo de descarga nuevo, la ficha no sabría qué episodio va por dónde) y solo torrents de un episodio.
- **Confirmación:** siempre se confirma en un cuadro, también cuando el anime se reconoce con seguridad. Un arrastre accidental nunca dispara descargas. Descartado: automático si está claro (un acierto equivocado descargaría en la carpeta de otro anime).
- **Anime fuera de la biblioteca:** el cuadro solo ofrece la biblioteca; si no está, se añade antes desde la pestaña Agregar. Descartado: buscar y dar de alta en AniList desde el cuadro.
- **Entradas:** arrastrar a la ventana (archivo `.torrent` o enlace magnet desde el navegador), Ctrl+V con un magnet copiado (fuera de las cajas de texto) y un botón "Añadir torrent o magnet" en la pestaña Descargas. Descartado: registrar la app como programa para enlaces `magnet:` (toca el registro de Windows y el instalador).

## Lo que ya existe y se reutiliza

- `TorrentDownloadService.DescargarAsync(torrentUrl, carpetaTemporal, rutaDestinoEsperada, numeroEpisodio, …)`: MonoTorrent 3.0.2, elige el archivo del episodio dentro de un lote (`SeleccionArchivoTorrent.ElegirArchivo`, por número en el nombre del archivo), excluye el resto, vigilante de estancamiento, sembrado opcional, defensa SEC-03 (tope `MaxTorrentBytes` = 5 MB, rutas de dentro del torrent validadas). Hoy solo acepta URLs https de Nyaa (`UrlSeguridad.EsUrlNyaaPermitida`).
- `IDownloadService.IniciarDescargaTorrentManualAsync(aniListId, animeTitulo, carpetaDestino, numeroEpisodio, CandidatoTorrent, titulos)`: encola un episodio con un torrent concreto (vía del selector de la ficha). La cola se guarda en `cola_descargas.json` con el `CandidatoTorrent` y se restaura al arrancar.
- `CandidatoTorrent(Titulo, TorrentUrl, InfoHash, Seeders, TamanoBytes, EsBatch, Dudoso)`.
- `NyaaRssParser.AnalizarRelease(titulo)` (grupo, nombres, episodio, rango de lote) y `NyaaRssParser.EsDelAnime(release, titulos)` (con `FirmaTitulo.Evaluar`, umbral 0,8: distingue "Mushoku Tensei II" de "III"); `NyaaRssParser.ExtraerNumeroEpisodio(nombreArchivo)`.
- `ClientEngine.DownloadMetadataAsync(MagnetLink, CancellationToken)` y `MagnetLink.TryParse` de MonoTorrent 3.0.2.
- Patrón de cuadro superpuesto de toda la app: `ISelectorTorrentService` (estado observable + comandos, dibujado una vez en `MainWindow.xaml`).
- `LimpiarTemporalesTorrentHuerfanos()` (se llama al arrancar, tras `RestaurarColaPendiente`).

## Qué se ve

- **Arrastrar** un `.torrent` o un enlace magnet sobre cualquier parte de la ventana: mientras se arrastra algo aceptable, una capa "Suelta para descargar el torrent". Al soltar, el flujo de abajo.
- **Ctrl+V** con un magnet en el portapapeles, si el foco no está en una caja de texto (en una caja de texto Ctrl+V pega como siempre). Si lo copiado no es un magnet, no pasa nada.
- **Botón "Añadir torrent o magnet"** en la cabecera de la pestaña Descargas: si el portapapeles tiene un magnet, lo usa; si no, abre el selector de archivos (`*.torrent`).
- **Magnet:** cuadro "Obteniendo información del torrent…" con Cancelar; límite de 2 minutos.
- **Cuadro de confirmación** (superpuesto a toda la ventana):
  - nombre del torrent;
  - **Anime:** desplegable con la biblioteca (orden alfabético), preseleccionado si se reconoció; vacío si no;
  - **Episodios:** "1–12 (12 archivos)" y, si aplica, "3 ya los tienes" y "2 archivos sin número de episodio (se omiten)"; se recalcula al cambiar de anime;
  - **Carpeta:** la del anime;
  - [Cancelar] [Descargar]. Descargar está desactivado sin anime o sin episodios nuevos.
- **Al descargar:** una fila por episodio nuevo en la cola; aviso "N episodios añadidos a la cola". Los episodios del mismo torrent se bajan de uno en uno (ver Datos) sin ocupar los huecos de descarga mientras esperan.
- No se descargan: episodios que ya están en disco o en descarga, ni archivos que no son video. Un torrent con un solo video sin número de episodio (una película) se guarda como episodio 1.

## Cómo funciona

1. **Todo se convierte en un `.torrent` guardado** (`ImportadorTorrent`):
   - Archivo soltado: se lee con el tope `MaxTorrentBytes`, se valida con `Torrent.Load` y se copia a `AppDataPaths.TorrentsImportadosDir` (`%LocalAppData%\AnimeLocalTrackerData\TorrentsImportados\<infohash>.torrent`).
   - Magnet: `MagnetLink.TryParse` (y prefijo `magnet:?`), `DownloadMetadataAsync` con 2 minutos y cancelación, y se guarda igual.
   - Resultado: el nombre del torrent, sus archivos (ruta y tamaño), el infohash y la ruta del `.torrent` guardado.
   - Descartado: llevar el magnet hasta el motor de descarga (habría que pedir los metadatos para el cuadro y otra vez por cada episodio, y sin pares no se podría reanudar).
2. **Reconocer el anime** (`AnalisisTorrentImportado`, puro): `NyaaRssParser.AnalizarRelease(nombreDelTorrent)` y, para cada anime de la biblioteca, `EsDelAnime(release, [Titulo + NombresAlternativos])`. Si sale exactamente uno, se preselecciona; con cero o varios, el desplegable queda vacío.
3. **Repartir los episodios** (puro): por cada archivo de video (`.mkv`, `.mp4`, `.avi`), `ExtraerNumeroEpisodio(nombreDeArchivo)`; si hay un solo video y no tiene número, episodio 1; los videos sin número en un lote se cuentan como omitidos. Se quitan los episodios con archivo en disco (`RegistroEpisodio.RutaArchivo` existente) o en descarga (`IDownloadService.EstaDescargando`). Si dos archivos dan el mismo número, cuenta una vez (el motor ya elige el mayor).
4. **Encolar:** por cada episodio nuevo, `IniciarDescargaTorrentManualAsync` con `CandidatoTorrent(Titulo: nombre, TorrentUrl: <URI file:/// del .torrent guardado>, InfoHash, Seeders: 0, TamanoBytes, EsBatch: >1 episodio)`.
5. **El motor acepta torrents locales** solo si la URI es `file:` y apunta dentro de `TorrentsImportadosDir` (comprobado con la ruta completa normalizada); el resto sigue exigiendo Nyaa. Una cola manipulada no puede hacer leer un archivo cualquiera.
6. **De uno en uno por torrent:** en `DownloadService`, en la vía de torrent elegido a mano o restaurado de la cola (`EjecutarDescargaTorrentAsync`, la que usan los importados), antes de pedir hueco de descarga, cada descarga espera su turno en un `SemaphoreSlim` por torrent (`InfoHash`, o la URL si no lo hay). Pausar o cancelar mientras espera libera la espera (el token se cancela). La búsqueda automática de Nyaa no pasa por ahí: el choque de dos episodios del mismo lote automático en paralelo queda como está (ponerlo en el tramo común esperaría con el hueco ocupado).
7. **Limpieza:** `LimpiarTemporalesTorrentHuerfanos` borra también los `.torrent` de `TorrentsImportadosDir` que no usa ninguna descarga de la cola.
8. **Entradas** (`MainWindow`): `AllowDrop` + `PreviewDragOver`/`Drop` (archivo `.torrent`, o texto/URL que empiece por `magnet:`), Ctrl+V en `PreviewKeyDown` si `Keyboard.FocusedElement` no es `TextBoxBase`/`PasswordBox`/`ComboBox` editable, y el botón de Descargas. Las tres llaman a `IImportarTorrentService` (servicio superpuesto, mismo patrón que `ISelectorTorrentService`).

## Fallos

Siempre con mensaje (aviso de la app o texto en el cuadro), nunca en silencio:
- Archivo que no es un torrent válido o de más de 5 MB: "No es un archivo .torrent válido".
- Magnet mal formado: "El enlace magnet no es válido".
- Magnet sin pares en 2 minutos, cancelado o sin conexión: "No se pudo obtener la información del torrent". No se guarda nada.
- Torrent sin videos: "Este torrent no trae videos".
- Todos los episodios ya están: el cuadro lo dice y Descargar queda desactivado.
- Un `.torrent` se puede encolar sin conexión (la cola ya espera a la red).
- Arrastrar o pegar con el cuadro (o la espera del magnet) abierto: se ignora.
- Varios archivos a la vez: se usa el primer `.torrent` y se avisa de que se ignoraron los demás.
- Anime sin carpeta (`RutaCarpeta` vacía): el cuadro lo dice y Descargar queda desactivado, como en "Descargar temporada" de la ficha (que avisa y no descarga).

## Pruebas

- `AnalisisTorrentImportadoTests` (puro): reconocimiento con nombres reales (`[SubsPlease] Sousou no Frieren (01-28) (1080p)` con biblioteca "Sousou no Frieren"; `[Erai-raws] Mushoku Tensei II` frente a I y II en la biblioteca; nombre alternativo; dos candidatos → ninguno; sin coincidencia → ninguno); reparto (lote con números, película de un archivo sin número → 1, lote con archivos sin número → omitidos, no-videos ignorados, ya en disco/en descarga descontados, número repetido una vez).
- `ImportadorTorrentTests`: archivo válido se copia a la carpeta con nombre por infohash; archivo inválido o enorme se rechaza; magnet mal formado se rechaza; magnet resuelto con un sustituto de la descarga de metadatos (sin red); limpieza de importados huérfanos.
- `TorrentDownloadService`: URI `file:` dentro de la carpeta se acepta; fuera de ella (u otra unidad, o `..\`) se rechaza.
- `DownloadService`: dos episodios del mismo torrent no se solapan; pausar el que espera libera su espera y el siguiente sigue; torrents distintos no se esperan. (Guardar y restaurar de la cola no cambia: la URL `file:` es una cadena más en `CandidatoTorrent`, ya cubierto por las pruebas de la cola.)
- `ImportarTorrentViewModel`/servicio: preselección, cambiar de anime recalcula episodios, Descargar desactivado sin anime o sin nuevos, encola uno por episodio nuevo y cierra, Cancelar no encola nada, entradas ignoradas con el cuadro abierto.
- Textos ES/EN (`LocalizationServiceTests`, `buscar_literales.py`).
- En vivo (perfil aislado **con red**): soltar un `.torrent` real y pequeño de Nyaa de un anime de la biblioteca (uno o dos episodios cortos, para gastar poco), ver las filas y el archivo final; Ctrl+V con su magnet; el botón de Descargas; sin red, el magnet avisa y el `.torrent` se encola. Borrar los videos y el perfil al acabar.

## Fuera de alcance

- Abrir enlaces `magnet:` desde el navegador (asociación de protocolo).
- Dar de alta animes desde el cuadro.
- Una fila por torrent, bajar episodios del mismo torrent en paralelo, o compartir un único `TorrentManager` entre episodios.
- Elegir a mano qué episodios del lote bajar (se bajan todos los nuevos; se puede cancelar cada fila).
- Cambiar la extensión con la que se guardan hoy los episodios de torrent.

---

# Plan de implementación

> **Ejecución:** en esta sesión, tarea a tarea, con `superpowers:executing-plans` (sin subagentes, sin rama ni commits por tarea: reglas del repo). Al final se propone un único commit (`commit-es`).

**Objetivo:** soltar un `.torrent` / pegar un magnet en la ventana → cuadro de confirmación con el anime reconocido y los episodios nuevos → una descarga por episodio en la cola, de una en una por torrent.

**Arquitectura:** `AnalisisTorrentImportado` (puro: reconocer anime, repartir episodios, rangos) + `ImportadorTorrent` (archivo/magnet → `.torrent` guardado en `TorrentsImportadosDir`; ruta local permitida; limpieza) + cambios mínimos en `TorrentDownloadService` (acepta locales de esa carpeta; magnet → metadatos) y `DownloadService` (turno por torrent, limpieza) + `ImportarTorrentService` (cuadro superpuesto, patrón de `SelectorTorrentService`) + `EntradaTorrent` (arrastrar, Ctrl+V, botón).

**Tecnología:** .NET 8, WPF, CommunityToolkit.Mvvm, MonoTorrent 3.0.2, xUnit + FluentAssertions + Moq.

**Especificación:** la primera mitad de este documento.

## Restricciones globales

- `async void` solo en manejadores de la vista, con `try-catch` (`wpf-mvvm.md` #3); actualizaciones de UI en el hilo de la interfaz.
- Datos solo bajo `AppDataPaths` (`TorrentsImportadosDir`); las pruebas usan carpetas temporales y nunca esa ruta.
- Texto visible con `{loc:T}` / `LocalizationService.T`, claves `Desc_Importar*` en las dos mitades.
- Colores de la paleta `Brush.*`; botones con `ToolTip` + `AutomationProperties.Name` + `Cursor="Hand"`.
- `ITorrentDownloadService` cambia: compilar `AnimeLocalTracker.Benchmarks` no hace falta (no lo implementa), pero sí revisar dobles de pruebas.
- Registro con `AppLogger`.

## Focos de revisión

1. **Torrent de una temporada con numeración continua** (2.ª temporada con archivos 13–24 sobre un anime que cuenta 1–12): se guardarían como "Episodio 13…24". Fuera de alcance (lo dice la especificación); el cuadro enseña el rango real ("Episodios: 13–24") para que el usuario lo vea antes de confirmar → prueba `Repartir_NumeracionContinua_SeRespetaYSeVeEnElRango` (tarea 1).
2. **Un archivo de la biblioteca que existe con otra extensión** (`Episodio 05.mkv` copiado a mano): no está en la base ni como `.mp4` → se volvería a bajar. Se comprueba cualquier `Episodio NN.*` de vídeo en la carpeta → prueba `YaLoTiene_EnBaseEnDescargaOArchivoDeOtraExtension_Cuenta` (tarea 5).
3. **Ruta de la cola manipulada** (`file:///C:/Windows/...torrent` o `..\`): el motor la rechaza → `RutaLocalPermitida_FueraDeLaCarpeta_Null` (tarea 2).
4. **Pausar el episodio que espera turno**: no debe quedarse colgado ni bloquear al siguiente → `PausarElQueEspera_LiberaYNoArranca` (tarea 4).
5. **Arrastrar algo mientras el cuadro o la espera del magnet siguen abiertos**: se ignora sin perder lo que había → `Importar_ConElCuadroAbierto_SeIgnora` (tarea 5).

Comandos (skill `repo-build-test`): compilar `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false`; pruebas `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false -nodeReuse:false --filter "<filtro>"`.

---

## Tarea 1: análisis puro (reconocer anime, repartir episodios, rangos)

**Archivos:** crear `AnimeLocalTracker/Services/Torrents/AnalisisTorrentImportado.cs`; pruebas `AnimeLocalTracker.Tests/Services/AnalisisTorrentImportadoTests.cs`.

**Interfaces (produce):**
- `record ArchivoTorrent(string Ruta, long Tamano)`
- `record RepartoEpisodios(IReadOnlyList<int> Todos, IReadOnlyList<int> Nuevos, int YaEstan, int SinNumero, int Videos)`
- `static class AnalisisTorrentImportado { IReadOnlyList<string> TitulosDe(AnimeItem); AnimeItem? ReconocerAnime(string nombreTorrent, IEnumerable<AnimeItem> biblioteca); RepartoEpisodios Repartir(IEnumerable<ArchivoTorrent>, Func<int,bool> yaLoTiene); string FormatearRangos(IReadOnlyList<int>); bool EsVideo(string ruta) }`

- [ ] **Paso 1: pruebas**

```csharp
#pragma warning disable CA1861 // Datos constantes de prueba
using System;
using System.Collections.Generic;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services.Torrents;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Torrent soltado en la ventana: de qué anime es y qué episodios trae.</summary>
public class AnalisisTorrentImportadoTests
{
    private static AnimeItem A(int id, string titulo, string alternativos = "") =>
        new() { AniListId = id, Titulo = titulo, NombresAlternativos = alternativos };

    private static readonly AnimeItem[] Biblioteca =
    {
        A(1, "Sousou no Frieren", "Frieren: Beyond Journey's End"),
        A(2, "Mushoku Tensei: Isekai Ittara Honki Dasu"),
        A(3, "Mushoku Tensei II: Isekai Ittara Honki Dasu"),
        A(4, "Dandadan"),
    };

    [Theory]
    [InlineData("[SubsPlease] Sousou no Frieren (01-28) (1080p) [Batch]", 1)]
    [InlineData("[SubsPlease] Sousou no Frieren - 05 (1080p) [ABCD1234].mkv", 1)]
    [InlineData("[Erai-raws] Mushoku Tensei II - Isekai Ittara Honki Dasu - 03 [1080p]", 3)]
    [InlineData("[ASW] Dandadan - 07 [1080p HEVC]", 4)]
    public void ReconocerAnime_NombresReales(string nombre, int esperado)
    {
        AnalisisTorrentImportado.ReconocerAnime(nombre, Biblioteca)!.AniListId.Should().Be(esperado);
    }

    [Fact]
    public void ReconocerAnime_PorNombreAlternativo()
    {
        AnalisisTorrentImportado.ReconocerAnime("[Group] Frieren - Beyond Journey's End - 02 [1080p]", new[] { A(1, "Sousou no Frieren", "Frieren: Beyond Journey's End") })
            !.AniListId.Should().Be(1);
    }

    [Fact]
    public void ReconocerAnime_SinCoincidenciaODosCandidatos_Null()
    {
        AnalisisTorrentImportado.ReconocerAnime("[SubsPlease] Kaiju No. 8 - 01 (1080p)", Biblioteca).Should().BeNull();
        AnalisisTorrentImportado.ReconocerAnime("[SubsPlease] Dandadan - 01", new[] { A(4, "Dandadan"), A(5, "Dandadan", "Dan Da Dan") }).Should().BeNull();
    }

    private static ArchivoTorrent F(string ruta, long tamano = 100) => new(ruta, tamano);

    [Fact]
    public void Repartir_Lote_NumerosYYaEstan()
    {
        var archivos = new[]
        {
            F(@"Frieren\[SubsPlease] Sousou no Frieren - 01 (1080p).mkv"),
            F(@"Frieren\[SubsPlease] Sousou no Frieren - 02 (1080p).mkv"),
            F(@"Frieren\[SubsPlease] Sousou no Frieren - 03 (1080p).mkv"),
            F(@"Frieren\[SubsPlease] Sousou no Frieren - 03 (1080p).ass"),
            F(@"Frieren\info.nfo"),
        };

        var r = AnalisisTorrentImportado.Repartir(archivos, n => n == 2);

        r.Videos.Should().Be(3);
        r.Todos.Should().Equal(1, 2, 3);
        r.Nuevos.Should().Equal(1, 3);
        r.YaEstan.Should().Be(1);
        r.SinNumero.Should().Be(0);
    }

    [Fact]
    public void Repartir_PeliculaDeUnArchivoSinNumero_EsElEpisodioUno()
    {
        var r = AnalisisTorrentImportado.Repartir(new[] { F("[Group] Kimi no Na wa (BD 1080p).mkv", 5_000_000_000) }, _ => false);

        r.Nuevos.Should().Equal(1);
        r.SinNumero.Should().Be(0);
    }

    [Fact]
    public void Repartir_LoteConArchivosSinNumero_SeOmiten()
    {
        var archivos = new[] { F("Show - 01.mkv"), F("Show - 02.mkv"), F("Show - NCOP.mkv"), F("Show - Special.mkv") };

        var r = AnalisisTorrentImportado.Repartir(archivos, _ => false);

        r.Nuevos.Should().Equal(1, 2);
        r.SinNumero.Should().Be(2);
    }

    [Fact]
    public void Repartir_NumeroRepetido_CuentaUnaVez()
    {
        var r = AnalisisTorrentImportado.Repartir(new[] { F("Show - 01 [720p].mkv"), F("Show - 01 [1080p].mkv") }, _ => false);

        r.Nuevos.Should().Equal(1);
    }

    [Fact]
    public void Repartir_SinVideos()
    {
        var r = AnalisisTorrentImportado.Repartir(new[] { F("Show OST.flac"), F("cover.jpg") }, _ => false);

        r.Videos.Should().Be(0);
        r.Nuevos.Should().BeEmpty();
    }

    [Fact]
    public void Repartir_NumeracionContinua_SeRespetaYSeVeEnElRango()
    {
        var archivos = Enumerable.Range(13, 12).Select(n => F($"Show 2nd Season - {n:D2}.mkv")).ToArray();

        var r = AnalisisTorrentImportado.Repartir(archivos, _ => false);

        r.Todos.First().Should().Be(13);
        AnalisisTorrentImportado.FormatearRangos(r.Todos).Should().Be("13–24");
    }

    [Theory]
    [InlineData(new int[0], "")]
    [InlineData(new[] { 5 }, "5")]
    [InlineData(new[] { 1, 2, 3, 4 }, "1–4")]
    [InlineData(new[] { 1, 2, 3, 5, 7, 8 }, "1–3, 5, 7–8")]
    public void FormatearRangos(int[] numeros, string esperado)
    {
        AnalisisTorrentImportado.FormatearRangos(numeros).Should().Be(esperado);
    }
}
```

- [ ] **Paso 2: ver que fallan** (filtro `"FullyQualifiedName~AnalisisTorrentImportadoTests"` → no compila).

- [ ] **Paso 3: implementación**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.Torrents;

/// <summary>Un archivo dentro de un torrent (ruta relativa y tamaño).</summary>
public sealed record ArchivoTorrent(string Ruta, long Tamano);

/// <summary>
/// Qué episodios trae un torrent. <paramref name="Todos"/>: los números reconocidos (ordenados); <paramref name="Nuevos"/>: los que no
/// se tienen ya; <paramref name="SinNumero"/>: vídeos de un lote cuyo nombre no dice el episodio (se omiten).
/// </summary>
public sealed record RepartoEpisodios(IReadOnlyList<int> Todos, IReadOnlyList<int> Nuevos, int YaEstan, int SinNumero, int Videos);

/// <summary>
/// Torrent soltado en la ventana (ver docs/plan-torrents-arrastrar-magnet.md): de qué anime de la biblioteca es y qué episodios trae.
/// Cálculo puro: reutiliza el analizador de releases de Nyaa (<see cref="NyaaRssParser"/>), que ya distingue temporadas.
/// </summary>
public static class AnalisisTorrentImportado
{
    private static readonly string[] ExtensionesVideo = { ".mkv", ".mp4", ".avi" };

    public static bool EsVideo(string ruta) => ExtensionesVideo.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> TitulosDe(AnimeItem anime)
    {
        var titulos = new List<string> { anime.Titulo };
        if (!string.IsNullOrWhiteSpace(anime.NombresAlternativos))
            titulos.AddRange(anime.NombresAlternativos.Split([" | ", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return titulos.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>El anime de la biblioteca al que pertenece el torrent, si es uno solo; con ninguno o varios, null (lo elige el usuario).</summary>
    public static AnimeItem? ReconocerAnime(string nombreTorrent, IEnumerable<AnimeItem> biblioteca)
    {
        string nombre = EsVideo(nombreTorrent) ? Path.GetFileNameWithoutExtension(nombreTorrent) : nombreTorrent;
        var release = NyaaRssParser.AnalizarRelease(nombre);
        var coincidencias = biblioteca.Where(a => NyaaRssParser.EsDelAnime(release, TitulosDe(a))).Take(2).ToList();
        return coincidencias.Count == 1 ? coincidencias[0] : null;
    }

    public static RepartoEpisodios Repartir(IEnumerable<ArchivoTorrent> archivos, Func<int, bool> yaLoTiene)
    {
        var videos = archivos.Where(a => EsVideo(a.Ruta)).ToList();
        var numeros = videos.Select(v => NyaaRssParser.ExtraerNumeroEpisodio(Path.GetFileName(v.Ruta))).ToList();

        // Un solo vídeo sin número (una película): episodio 1. El motor ya cae al vídeo más grande en ese caso.
        if (videos.Count == 1 && numeros[0] is not > 0) numeros[0] = 1;

        var todos = numeros.Where(n => n is > 0).Select(n => n!.Value).Distinct().OrderBy(n => n).ToList();
        var nuevos = todos.Where(n => !yaLoTiene(n)).ToList();
        return new RepartoEpisodios(todos, nuevos, todos.Count - nuevos.Count, numeros.Count(n => n is not > 0), videos.Count);
    }

    /// <summary>[1,2,3,5,7,8] → "1–3, 5, 7–8".</summary>
    public static string FormatearRangos(IReadOnlyList<int> numeros)
    {
        var tramos = new List<string>();
        for (int i = 0; i < numeros.Count;)
        {
            int j = i;
            while (j + 1 < numeros.Count && numeros[j + 1] == numeros[j] + 1) j++;
            tramos.Add(j > i ? $"{numeros[i]}–{numeros[j]}" : numeros[i].ToString(LocalizationService.Cultura));
            i = j + 1;
        }
        return string.Join(", ", tramos);
    }
}
```

> Si alguna fila de `ReconocerAnime_NombresReales` falla porque `AnalizarRelease`/`EsDelAnime` leen ese nombre de otra forma, es un hallazgo sobre el analizador de Nyaa: anotarlo como ruling y cambiar el ejemplo por el formato real que sí reconoce (no tocar el analizador de Nyaa en esta tarea).

- [ ] **Paso 4: pruebas en verde.**

---

## Tarea 2: motor de torrents (locales de su carpeta, magnet → metadatos)

**Archivos:** `AnimeLocalTracker/Services/AppDataPaths.cs` (carpeta nueva), `AnimeLocalTracker/Services/ITorrentDownloadService.cs`, `AnimeLocalTracker/Services/TorrentDownloadService.cs`, crear `AnimeLocalTracker/Services/Torrents/ImportadorTorrent.cs` (aquí solo `RutaLocalPermitida`; el resto en la tarea 3), `LocalizationService.cs` (`Desc_TorrentImportadoNoExiste`); pruebas `AnimeLocalTracker.Tests/Services/ImportadorTorrentTests.cs`.

**Interfaces (produce):** `AppDataPaths.TorrentsImportadosDir`; `ImportadorTorrent.RutaLocalPermitida(string? url, string carpeta): string?`; `ITorrentDownloadService.ObtenerTorrentDeMagnetAsync(string enlace, CancellationToken ct): Task<byte[]?>`.

- [ ] **Paso 1: pruebas**

```csharp
using System;
using System.IO;
using AnimeLocalTracker.Services.Torrents;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public sealed partial class ImportadorTorrentTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "alt_importados_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    [Fact]
    public void RutaLocalPermitida_DentroDeLaCarpeta_DevuelveLaRuta()
    {
        string ruta = Path.Combine(_carpeta, "abc.torrent");

        ImportadorTorrent.RutaLocalPermitida(new Uri(ruta).AbsoluteUri, _carpeta).Should().Be(Path.GetFullPath(ruta));
    }

    [Theory]
    [InlineData("https://nyaa.si/download/1.torrent")]
    [InlineData("file:///C:/Windows/System32/x.torrent")]
    [InlineData("no es una url")]
    [InlineData(null)]
    public void RutaLocalPermitida_FueraDeLaCarpeta_Null(string? url)
    {
        ImportadorTorrent.RutaLocalPermitida(url, _carpeta).Should().BeNull();
    }

    [Fact]
    public void RutaLocalPermitida_ConPuntosPuntos_OOtraExtension_Null()
    {
        string escapada = Path.Combine(_carpeta, "..", "fuera.torrent");
        string otra = Path.Combine(_carpeta, "abc.exe");

        ImportadorTorrent.RutaLocalPermitida(new Uri(escapada).AbsoluteUri, _carpeta).Should().BeNull();
        ImportadorTorrent.RutaLocalPermitida(new Uri(otra).AbsoluteUri, _carpeta).Should().BeNull();
    }
}
```

- [ ] **Paso 2: ver que fallan.**

- [ ] **Paso 3: implementación**

`AppDataPaths.cs`, junto a `TorrentsCacheDir`:

```csharp
    /// <summary>.torrent soltados en la ventana o resueltos de un magnet (la cola de descargas los usa al reanudar).</summary>
    public static string TorrentsImportadosDir { get; } = Path.Combine(DataRoot, "TorrentsImportados");
```

`Services/Torrents/ImportadorTorrent.cs` (primera versión; la tarea 3 la completa):

```csharp
using System;
using System.IO;

namespace AnimeLocalTracker.Services.Torrents;

/// <summary>Torrents soltados en la ventana o pegados como magnet (ver docs/plan-torrents-arrastrar-magnet.md).</summary>
public sealed partial class ImportadorTorrent
{
    /// <summary>
    /// La ruta local de un .torrent importado si <paramref name="url"/> es un <c>file:</c> que apunta DENTRO de
    /// <paramref name="carpeta"/>; si no, null. Una cola manipulada no puede hacer leer al motor un archivo cualquiera.
    /// </summary>
    public static string? RutaLocalPermitida(string? url, string carpeta)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        string ruta = Path.GetFullPath(uri.LocalPath);
        string baseDir = Path.GetFullPath(carpeta).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return ruta.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && ruta.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
            ? ruta : null;
    }
}
```

`ITorrentDownloadService.cs`, tras `DetenerTodoElSeedingAsync`:

```csharp
    /// <summary>
    /// Descarga los metadatos de un enlace magnet (lo que tendría el .torrent) pidiéndoselos a los pares. Null si el enlace no es
    /// válido. Lanza <see cref="OperationCanceledException"/> si se cancela (el llamador pone el límite de tiempo).
    /// </summary>
    Task<byte[]?> ObtenerTorrentDeMagnetAsync(string enlace, CancellationToken ct);
```

`TorrentDownloadService.cs`: en `DescargarAsync`, sustituir la comprobación inicial

```csharp
        if (!Core.UrlSeguridad.EsUrlNyaaPermitida(torrentUrl))
            return new ResultadoTorrent(false, null, LocalizationService.T("Desc_TorrentUrlNoPermitida"));
```

por

```csharp
        // Torrents soltados en la ventana: solo de su carpeta (ver ImportadorTorrent.RutaLocalPermitida); el resto, solo de Nyaa.
        string? rutaLocal = Torrents.ImportadorTorrent.RutaLocalPermitida(torrentUrl, AppDataPaths.TorrentsImportadosDir);
        if (rutaLocal == null && !Core.UrlSeguridad.EsUrlNyaaPermitida(torrentUrl))
            return new ResultadoTorrent(false, null, LocalizationService.T("Desc_TorrentUrlNoPermitida"));
```

y, dentro del `try`, sustituir el bloque `byte[] torrentBytes; using (var req = …) { … }` por

```csharp
            byte[] torrentBytes;
            if (rutaLocal != null)
            {
                if (!File.Exists(rutaLocal) || new FileInfo(rutaLocal).Length > MaxTorrentBytes)
                    return new ResultadoTorrent(false, null, LocalizationService.T("Desc_TorrentImportadoNoExiste"));
                torrentBytes = await File.ReadAllBytesAsync(rutaLocal, ct);
            }
            else
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, torrentUrl);
                using var res = await _httpClient.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode)
                    return new ResultadoTorrent(false, null, string.Format(LocalizationService.T("Desc_TorrentHttpFormato"), (int)res.StatusCode));
                // SEC-03: un .torrent real pesa KB; el tope evita que un Nyaa comprometido (o un MITM) llene la memoria.
                torrentBytes = await Core.EntradaSegura.LeerAcotadoAsync(res.Content, MaxTorrentBytes, ct);
            }
```

y añadir el método (junto a `DetenerTodoElSeedingAsync`):

```csharp
    public async Task<byte[]?> ObtenerTorrentDeMagnetAsync(string enlace, CancellationToken ct)
    {
        if (!MagnetLink.TryParse(enlace?.Trim() ?? string.Empty, out var magnet)) return null;
        var datos = await _engine.DownloadMetadataAsync(magnet, ct);
        return datos.ToArray();
    }
```

`LocalizationService.cs`, junto a `Desc_TorrentUrlNoPermitida` (las dos mitades):

```csharp
        ["Desc_TorrentImportadoNoExiste"] = "El archivo .torrent importado ya no existe o no es válido",
```
```csharp
        ["Desc_TorrentImportadoNoExiste"] = "The imported .torrent file no longer exists or is not valid",
```

- [ ] **Paso 4: compilar (0 advertencias) y pruebas en verde** (filtro `"FullyQualifiedName~ImportadorTorrentTests|FullyQualifiedName~DownloadServiceTests"`).

---

## Tarea 3: importador (archivo y magnet → .torrent guardado; limpieza)

**Archivos:** completar `Services/Torrents/ImportadorTorrent.cs`; `LocalizationService.cs` (`Desc_Importar*` de error); ampliar `ImportadorTorrentTests.cs`.

**Interfaces:**
- Consume: `ITorrentDownloadService.ObtenerTorrentDeMagnetAsync`, `TorrentDownloadService.MaxTorrentBytes` (internal, mismo ensamblado), `ArchivoTorrent`.
- Produce: `record TorrentImportado(string Nombre, IReadOnlyList<ArchivoTorrent> Archivos, string Hash, string RutaGuardada) { string Url; long Tamano }`; `class ImportacionTorrentException(string mensaje)`; `ImportadorTorrent(ITorrentDownloadService motor, string carpeta)` con `string Carpeta`, `Task<TorrentImportado> DesdeArchivoAsync(string ruta, CancellationToken ct)`, `Task<TorrentImportado> DesdeMagnetAsync(string enlace, CancellationToken ct)`, `static bool PareceMagnet(string? texto)`, `static int LimpiarHuerfanos(string carpeta, IReadOnlySet<string> rutasEnUso)`, `static readonly TimeSpan EsperaMaximaMagnet = 2 min`.

- [ ] **Paso 1: pruebas** (añadir a `ImportadorTorrentTests`; el `.torrent` de prueba se construye a mano en bencode, sin red)

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using Moq;

public sealed partial class ImportadorTorrentTests
{
    private readonly Mock<ITorrentDownloadService> _motor = new();

    private ImportadorTorrent CrearSut() => new(_motor.Object, _carpeta);

    /// <summary>Un .torrent válido de varios archivos (bencode a mano: 1 pieza de 16 KiB, hash de pieza falso).</summary>
    private static byte[] TorrentDePrueba(string nombre, params (string Archivo, long Tamano)[] archivos)
    {
        static string S(string s) => $"{Encoding.UTF8.GetByteCount(s)}:{s}";
        var sb = new StringBuilder("d4:infod5:filesl");
        foreach (var (archivo, tamano) in archivos) sb.Append($"d6:lengthi{tamano}e4:pathl{S(archivo)}ee");
        sb.Append($"e4:name{S(nombre)}12:piece lengthi16384e6:pieces20:");
        var cabecera = Encoding.UTF8.GetBytes(sb.ToString());
        return cabecera.Concat(new byte[20]).Concat(Encoding.ASCII.GetBytes("ee")).ToArray();
    }

    private string Escribir(byte[] datos, string nombre = "x.torrent")
    {
        Directory.CreateDirectory(_carpeta + "_origen");
        string ruta = Path.Combine(_carpeta + "_origen", nombre);
        File.WriteAllBytes(ruta, datos);
        return ruta;
    }

    [Fact]
    public async Task DesdeArchivo_Valido_SeGuardaEnLaCarpetaYDaSusArchivos()
    {
        string origen = Escribir(TorrentDePrueba("Frieren (01-02)", ("Frieren - 01.mkv", 1000), ("Frieren - 02.mkv", 2000)));

        var t = await CrearSut().DesdeArchivoAsync(origen, CancellationToken.None);

        t.Nombre.Should().Be("Frieren (01-02)");
        t.Archivos.Select(a => a.Tamano).Should().Equal(1000, 2000);
        File.Exists(t.RutaGuardada).Should().BeTrue();
        Path.GetDirectoryName(t.RutaGuardada).Should().Be(Path.GetFullPath(_carpeta));
        ImportadorTorrent.RutaLocalPermitida(t.Url, _carpeta).Should().Be(t.RutaGuardada);
        t.Tamano.Should().Be(3000);
    }

    [Fact]
    public async Task DesdeArchivo_NoEsTorrent_OEsEnorme_SeRechaza()
    {
        string basura = Escribir(Encoding.UTF8.GetBytes("hola"), "basura.torrent");
        string enorme = Escribir(new byte[6 * 1024 * 1024], "enorme.torrent");

        await FluentActions.Awaiting(() => CrearSut().DesdeArchivoAsync(basura, CancellationToken.None)).Should().ThrowAsync<ImportacionTorrentException>();
        await FluentActions.Awaiting(() => CrearSut().DesdeArchivoAsync(enorme, CancellationToken.None)).Should().ThrowAsync<ImportacionTorrentException>();
        Directory.Exists(_carpeta).Should().BeFalse("no se guarda nada si no es válido");
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Frieren", true)]
    [InlineData("  magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567  ", true)]
    [InlineData("magnet:?dn=sin-hash", false)]
    [InlineData("https://nyaa.si/view/1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PareceMagnet(string? texto, bool esperado) => ImportadorTorrent.PareceMagnet(texto).Should().Be(esperado);

    private const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Frieren";

    [Fact]
    public async Task DesdeMagnet_ConMetadatos_SeGuardaIgual()
    {
        var datos = TorrentDePrueba("Frieren - 05", ("Frieren - 05.mkv", 500));
        _motor.Setup(m => m.ObtenerTorrentDeMagnetAsync(Magnet, It.IsAny<CancellationToken>())).ReturnsAsync(datos);

        var t = await CrearSut().DesdeMagnetAsync(Magnet, CancellationToken.None);

        t.Archivos.Should().ContainSingle();
        File.Exists(t.RutaGuardada).Should().BeTrue();
    }

    [Fact]
    public async Task DesdeMagnet_MalFormado_SinMetadatos_OCancelado_Lanza()
    {
        _motor.Setup(m => m.ObtenerTorrentDeMagnetAsync(Magnet, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await FluentActions.Awaiting(() => CrearSut().DesdeMagnetAsync("magnet:?dn=x", CancellationToken.None)).Should().ThrowAsync<ImportacionTorrentException>();
        await FluentActions.Awaiting(() => CrearSut().DesdeMagnetAsync(Magnet, CancellationToken.None)).Should().ThrowAsync<ImportacionTorrentException>();
    }

    [Fact]
    public void LimpiarHuerfanos_BorraSoloLosQueNoSeUsan()
    {
        Directory.CreateDirectory(_carpeta);
        string usado = Path.Combine(_carpeta, "a.torrent"), huerfano = Path.Combine(_carpeta, "b.torrent");
        File.WriteAllText(usado, "x");
        File.WriteAllText(huerfano, "x");

        int borrados = ImportadorTorrent.LimpiarHuerfanos(_carpeta, new HashSet<string>(new[] { Path.GetFullPath(usado) }, StringComparer.OrdinalIgnoreCase));

        borrados.Should().Be(1);
        File.Exists(usado).Should().BeTrue();
        File.Exists(huerfano).Should().BeFalse();
        ImportadorTorrent.LimpiarHuerfanos(Path.Combine(_carpeta, "no-existe"), new HashSet<string>()).Should().Be(0);
    }
}
```

Y en `Dispose` del archivo de pruebas, borrar también `_carpeta + "_origen"`.

- [ ] **Paso 2: ver que fallan.**

- [ ] **Paso 3: implementación** (completar la clase `partial` de la tarea 2)

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;

namespace AnimeLocalTracker.Services.Torrents;

/// <summary>Un torrent importado: guardado en la carpeta de la app y listo para el cuadro de confirmación.</summary>
public sealed record TorrentImportado(string Nombre, IReadOnlyList<ArchivoTorrent> Archivos, string Hash, string RutaGuardada)
{
    /// <summary>Lo que va en <c>CandidatoTorrent.TorrentUrl</c>: el motor solo acepta esta forma si es de la carpeta de importados.</summary>
    public string Url => new Uri(RutaGuardada).AbsoluteUri;
    public long Tamano => Archivos.Sum(a => a.Tamano);
}

/// <summary>Por qué no se pudo importar, ya en palabras del usuario.</summary>
public sealed class ImportacionTorrentException(string mensaje) : Exception(mensaje);

public sealed partial class ImportadorTorrent
{
    public static readonly TimeSpan EsperaMaximaMagnet = TimeSpan.FromMinutes(2);

    private readonly ITorrentDownloadService _motor;

    public ImportadorTorrent(ITorrentDownloadService motor, string carpeta)
    {
        _motor = motor;
        Carpeta = carpeta;
    }

    public string Carpeta { get; }

    public static bool PareceMagnet(string? texto)
    {
        string t = texto?.Trim() ?? string.Empty;
        return t.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) && MagnetLink.TryParse(t, out _);
    }

    public async Task<TorrentImportado> DesdeArchivoAsync(string ruta, CancellationToken ct)
    {
        byte[] datos;
        try
        {
            if (new FileInfo(ruta).Length > TorrentDownloadService.MaxTorrentBytes) throw NoValido();
            datos = await File.ReadAllBytesAsync(ruta, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("ImportadorTorrent", $"No se pudo leer el .torrent: {ex.Message}");
            throw NoValido();
        }
        return Guardar(datos);
    }

    public async Task<TorrentImportado> DesdeMagnetAsync(string enlace, CancellationToken ct)
    {
        if (!PareceMagnet(enlace)) throw new ImportacionTorrentException(LocalizationService.T("Desc_ImportarMagnetNoValido"));

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(EsperaMaximaMagnet);
        byte[]? datos;
        try
        {
            datos = await _motor.ObtenerTorrentDeMagnetAsync(enlace.Trim(), limite.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Info("ImportadorTorrent", $"No se obtuvieron los metadatos del magnet: {ex.Message}");
            throw new ImportacionTorrentException(LocalizationService.T("Desc_ImportarSinMetadatos"));
        }
        if (datos == null || datos.Length == 0 || datos.Length > TorrentDownloadService.MaxTorrentBytes)
            throw new ImportacionTorrentException(LocalizationService.T("Desc_ImportarSinMetadatos"));
        return Guardar(datos);
    }

    /// <summary>Borra los .torrent importados que ninguna descarga de la cola usa. Devuelve cuántos borró.</summary>
    public static int LimpiarHuerfanos(string carpeta, IReadOnlySet<string> rutasEnUso)
    {
        if (!Directory.Exists(carpeta)) return 0;
        int borrados = 0;
        foreach (string ruta in Directory.EnumerateFiles(carpeta, "*.torrent"))
        {
            if (rutasEnUso.Contains(Path.GetFullPath(ruta))) continue;
            try
            {
                File.Delete(ruta);
                borrados++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Debug("ImportadorTorrent", $"No se pudo borrar un .torrent importado: {ex.Message}");
            }
        }
        return borrados;
    }

    private TorrentImportado Guardar(byte[] datos)
    {
        Torrent torrent;
        try
        {
            torrent = Torrent.Load(datos);
        }
        catch (Exception ex)
        {
            AppLogger.Info("ImportadorTorrent", $"No es un .torrent válido: {ex.Message}");
            throw NoValido();
        }

        // ponytail: la identidad es el SHA-256 del archivo, no el infohash: el mismo torrent por magnet y por archivo puede dar dos
        // claves (y bajarse en paralelo). Usar el infohash si eso llega a pasar.
        string hash = Convert.ToHexString(SHA256.HashData(datos)).ToLowerInvariant();
        Directory.CreateDirectory(Carpeta);
        string ruta = Path.GetFullPath(Path.Combine(Carpeta, hash + ".torrent"));
        if (!File.Exists(ruta)) File.WriteAllBytes(ruta, datos);

        var archivos = torrent.Files.Select(f => new ArchivoTorrent(f.Path, f.Length)).ToList();
        return new TorrentImportado(torrent.Name, archivos, hash, ruta);
    }

    private static ImportacionTorrentException NoValido() => new(LocalizationService.T("Desc_ImportarNoValido"));
}
```

> Si `ITorrentFile.Path` no es `string` en 3.0.2 (p. ej. `TorrentPath`), usar `.ToString()`; lo dirá el compilador. Si `ObtenerTorrentDeMagnetAsync` no puede devolver `datos.ToArray()` por el tipo de `DownloadMetadataAsync`, ajustar allí (tarea 2) y anotarlo.

Textos de error (las dos mitades, junto a los `Desc_Torrent*`):

```csharp
        ["Desc_ImportarNoValido"] = "No es un archivo .torrent válido",
        ["Desc_ImportarMagnetNoValido"] = "El enlace magnet no es válido",
        ["Desc_ImportarSinMetadatos"] = "No se pudo obtener la información del torrent (sin pares o sin conexión)",
```
```csharp
        ["Desc_ImportarNoValido"] = "This is not a valid .torrent file",
        ["Desc_ImportarMagnetNoValido"] = "The magnet link is not valid",
        ["Desc_ImportarSinMetadatos"] = "Couldn't get the torrent information (no peers or offline)",
```

- [ ] **Paso 4: pruebas en verde** (filtro `"FullyQualifiedName~ImportadorTorrentTests|FullyQualifiedName~LocalizationServiceTests"`).

---

## Tarea 4: cola de descargas (turno por torrent, limpieza de importados)

**Archivos:** `AnimeLocalTracker/Services/DownloadService.cs`; pruebas `AnimeLocalTracker.Tests/Services/DownloadServiceTurnoTorrentTests.cs`.

**Interfaces:** consume `ImportadorTorrent.RutaLocalPermitida`, `ImportadorTorrent.LimpiarHuerfanos`, `AppDataPaths.TorrentsImportadosDir`. Produce `internal string CarpetaTorrentsImportados { get; set; }` en `DownloadService`.

Ruling previsto: el turno va solo en la vía de torrent elegido a mano / restaurado (`EjecutarDescargaTorrentAsync`), antes de pedir hueco. La búsqueda automática de Nyaa (`IntentarDescargaTorrentAsync`) no lo usa: ponerlo en el tramo común (`DescargarTorrentYCompletarAsync`) esperaría con el hueco ocupado y, al no ser reentrante, bloquearía la vía manual. La especificación decía que arreglaba también el choque de los lotes de Nyaa automáticos: queda fuera.

- [ ] **Paso 1: pruebas**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Episodios del mismo torrent: de uno en uno, sin ocupar huecos mientras esperan.</summary>
public sealed class DownloadServiceTurnoTorrentTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"alt_turno_{Guid.NewGuid():N}");
    private readonly Mock<ITorrentDownloadService> _torrent = new();
    private readonly Dictionary<int, TaskCompletionSource<bool>> _fin = new();
    private readonly List<int> _empezados = new();
    private int _activos, _maxActivos;
    private DownloadService? _sut;

    public void Dispose()
    {
        _sut?.CancelarTodas();
        try { Directory.Delete(_carpeta, true); } catch { }
    }

    private DownloadService CrearSut()
    {
        var fabrica = new Mock<IHttpClientFactory>();
        fabrica.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
        var ajustes = new Mock<ISettingsService>();
        ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings { DescargasSimultaneas = 3 });
        _torrent.Setup(t => t.DescargarAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(),
                It.IsAny<IProgress<(double, double)>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, string destino, int ep, bool _, IProgress<(double, double)>? _, CancellationToken ct) =>
            {
                lock (_empezados) { _empezados.Add(ep); _maxActivos = Math.Max(_maxActivos, ++_activos); }
                try { await _fin[ep].Task.WaitAsync(ct); }
                finally { lock (_empezados) _activos--; }
                return new ResultadoTorrent(true, destino, null);
            });
        _sut = new DownloadService(fabrica.Object, settingsService: ajustes.Object, database: Mock.Of<IDatabaseService>(), torrentDownloadService: _torrent.Object);
        return _sut;
    }

    private static CandidatoTorrent C(string hash) => new("Show", $"https://nyaa.si/download/{hash}.torrent", hash, 0, 1);

    private static async Task Esperar(Func<bool> condicion)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue();
    }

    [Fact]
    public async Task MismoTorrent_DeUnoEnUno()
    {
        var sut = CrearSut();
        foreach (int ep in new[] { 1, 2, 3 }) _fin[ep] = new(TaskCreationOptions.RunContinuationsAsynchronously);

        foreach (int ep in new[] { 1, 2, 3 }) await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, ep, C("h"));
        await Esperar(() => _empezados.Count == 1);
        await Task.Delay(200);
        _empezados.Should().HaveCount(1, "los otros esperan su turno");

        foreach (int ep in new[] { 1, 2, 3 }) _fin[ep].TrySetResult(true);
        await Esperar(() => _empezados.Count == 3);
        _maxActivos.Should().Be(1);
    }

    [Fact]
    public async Task TorrentsDistintos_NoSeEsperan()
    {
        var sut = CrearSut();
        _fin[1] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fin[2] = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 1, C("a"));
        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 2, C("b"));

        await Esperar(() => _activos == 2);
        _fin[1].TrySetResult(true);
        _fin[2].TrySetResult(true);
    }

    [Fact]
    public async Task PausarElQueEspera_LiberaYNoArranca()
    {
        var sut = CrearSut();
        _fin[1] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fin[2] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fin[3] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 1, C("h"));
        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 2, C("h"));
        await sut.IniciarDescargaTorrentManualAsync(9, "Show", _carpeta, 3, C("h"));
        await Esperar(() => _empezados.Count == 1);

        sut.PausarDescarga(9, 2);
        _fin[1].TrySetResult(true);
        _fin[3].TrySetResult(true);

        await Esperar(() => _empezados.Contains(3));
        _empezados.Should().NotContain(2, "está en pausa");
    }
}
```

- [ ] **Paso 2: ver que fallan** (`MismoTorrent_DeUnoEnUno` y `PausarElQueEspera_LiberaYNoArranca` en rojo: hoy arrancan a la vez).

- [ ] **Paso 3: implementación** (`DownloadService.cs`)

Campo, junto a `_activeDownloads`:

```csharp
    /// <summary>
    /// Turno por torrent: los episodios del mismo torrent se bajan de uno en uno (añadir el mismo torrent dos veces al motor
    /// retira el anterior). Se espera ANTES de pedir hueco, así que los que esperan no ocupan huecos de descarga.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _turnosTorrent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Carpeta de los .torrent importados (ver ImportadorTorrent). Las pruebas la cambian.</summary>
    internal string CarpetaTorrentsImportados { get; set; } = AppDataPaths.TorrentsImportadosDir;
```

En `EjecutarDescargaTorrentAsync`, sustituir

```csharp
            bool slotAdquirido = false;
            try
            {
                await EsperarEjecucionAnteriorAsync(anterior, ct);
                await AdquirirSlotAsync(key, state, ct);
```

por

```csharp
            bool slotAdquirido = false;
            var turno = _turnosTorrent.GetOrAdd(string.IsNullOrEmpty(candidato.InfoHash) ? candidato.TorrentUrl : candidato.InfoHash,
                _ => new SemaphoreSlim(1, 1));
            bool turnoTomado = false;
            try
            {
                await EsperarEjecucionAnteriorAsync(anterior, ct);
                await turno.WaitAsync(ct);
                turnoTomado = true;
                await AdquirirSlotAsync(key, state, ct);
```

y su `finally`

```csharp
            finally
            {
                if (slotAdquirido) LiberarSlot();
            }
```

por

```csharp
            finally
            {
                if (slotAdquirido) LiberarSlot();
                if (turnoTomado) turno.Release();
            }
```

(solo el `finally` de `EjecutarDescargaTorrentAsync`: hay otros con el mismo texto en el archivo).

En `LimpiarTemporalesTorrentHuerfanos`, al final:

```csharp
        // .torrent soltados en la ventana que ya no usa ninguna descarga de la cola.
        var enUso = _activeDownloads.Values
            .Select(s => Torrents.ImportadorTorrent.RutaLocalPermitida(s.Torrent?.TorrentUrl, CarpetaTorrentsImportados))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int importados = Torrents.ImportadorTorrent.LimpiarHuerfanos(CarpetaTorrentsImportados, enUso);
        if (importados > 0) AppLogger.Info("DownloadService", $"Limpieza de torrents importados sin uso: {importados}.");
```

- [ ] **Paso 4: pruebas en verde** (filtro `"FullyQualifiedName~DownloadService|FullyQualifiedName~OfflineFase3"`).

---

## Tarea 5: el cuadro (servicio superpuesto) y sus textos

**Archivos:** crear `AnimeLocalTracker/Services/Torrents/ImportarTorrentService.cs`; `LocalizationService.cs` (resto de `Desc_Importar*`); `App.xaml.cs` (registro); pruebas `AnimeLocalTracker.Tests/Services/ImportarTorrentServiceTests.cs`.

**Interfaces:**
- Consume: tareas 1–3; `IDatabaseService.ObtenerTodosLosAnimesAsync`, `ObtenerRegistrosPorAnimeAsync(int)`; `IDownloadService.EstaDescargando`, `IniciarDescargaTorrentManualAsync`; `IDialogService.MostrarToast`.
- Produce: `interface IImportarTorrentService : INotifyPropertyChanged { bool Visible; bool ObteniendoInformacion; bool Ocupado; string NombreTorrent; IReadOnlyList<AnimeItem> Animes; AnimeItem? AnimeElegido {get;set;}; string TextoEpisodios; string TextoDetalle; string TextoCarpeta; bool PuedeDescargar; IAsyncRelayCommand DescargarCommand; IRelayCommand CancelarCommand; Task ImportarArchivoAsync(string ruta, int ignorados = 0); Task ImportarMagnetAsync(string enlace); }`; `internal Task Recalculo`.

- [ ] **Paso 1: textos** (las dos mitades, junto a los anteriores `Desc_Importar*`)

```csharp
        ["Desc_ImportarBoton"] = "Añadir torrent o magnet",
        ["Desc_ImportarBotonTip"] = "Abre un archivo .torrent o usa el enlace magnet que tengas copiado",
        ["Desc_ImportarSoltar"] = "Suelta para descargar el torrent",
        ["Desc_ImportarTitulo"] = "Descargar torrent",
        ["Desc_ImportarObteniendo"] = "Obteniendo información del torrent…",
        ["Desc_ImportarAnime"] = "Anime",
        ["Desc_ImportarElegirAnime"] = "Elige el anime de tu biblioteca",
        ["Desc_ImportarEpisodiosFormato"] = "Episodios: {0} ({1} archivos)",
        ["Desc_ImportarYaTienesFormato"] = "{0} ya los tienes",
        ["Desc_ImportarSinNumeroFormato"] = "{0} archivos sin número de episodio (se omiten)",
        ["Desc_ImportarNingunoNuevo"] = "Ya tienes todos los episodios de este torrent",
        ["Desc_ImportarSinCarpeta"] = "Este anime no tiene carpeta asignada",
        ["Desc_ImportarCarpetaFormato"] = "Carpeta: {0}",
        ["Desc_ImportarDescargar"] = "Descargar",
        ["Desc_ImportarAnadidosTitulo"] = "Torrent añadido",
        ["Desc_ImportarAnadidosFormato"] = "{0} episodios añadidos a la cola",
        ["Desc_ImportarErrorTitulo"] = "No se pudo abrir el torrent",
        ["Desc_ImportarSinVideos"] = "Este torrent no trae videos",
        ["Desc_ImportarVariosArchivos"] = "Se usó el primer .torrent; los demás se ignoraron",
        ["Desc_ImportarFiltro"] = "Archivos torrent (*.torrent)|*.torrent",
```
```csharp
        ["Desc_ImportarBoton"] = "Add torrent or magnet",
        ["Desc_ImportarBotonTip"] = "Open a .torrent file or use the magnet link you have copied",
        ["Desc_ImportarSoltar"] = "Drop to download the torrent",
        ["Desc_ImportarTitulo"] = "Download torrent",
        ["Desc_ImportarObteniendo"] = "Getting torrent information…",
        ["Desc_ImportarAnime"] = "Anime",
        ["Desc_ImportarElegirAnime"] = "Choose the anime from your library",
        ["Desc_ImportarEpisodiosFormato"] = "Episodes: {0} ({1} files)",
        ["Desc_ImportarYaTienesFormato"] = "You already have {0}",
        ["Desc_ImportarSinNumeroFormato"] = "{0} files without an episode number (skipped)",
        ["Desc_ImportarNingunoNuevo"] = "You already have every episode in this torrent",
        ["Desc_ImportarSinCarpeta"] = "This anime has no folder assigned",
        ["Desc_ImportarCarpetaFormato"] = "Folder: {0}",
        ["Desc_ImportarDescargar"] = "Download",
        ["Desc_ImportarAnadidosTitulo"] = "Torrent added",
        ["Desc_ImportarAnadidosFormato"] = "{0} episodes added to the queue",
        ["Desc_ImportarErrorTitulo"] = "Couldn't open the torrent",
        ["Desc_ImportarSinVideos"] = "This torrent has no videos",
        ["Desc_ImportarVariosArchivos"] = "The first .torrent was used; the others were ignored",
        ["Desc_ImportarFiltro"] = "Torrent files (*.torrent)|*.torrent",
```

- [ ] **Paso 2: pruebas**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Torrents;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Cuadro "Descargar torrent": preselección, recálculo, qué se encola y cuándo se ignora una entrada.</summary>
public sealed class ImportarTorrentServiceTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "alt_imp_srv_" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IDownloadService> _descargas = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<ITorrentDownloadService> _motor = new();
    private readonly AnimeItem _frieren, _dandadan;

    public ImportarTorrentServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_raiz, "Frieren"));
        _frieren = new AnimeItem { AniListId = 1, Titulo = "Sousou no Frieren", RutaCarpeta = Path.Combine(_raiz, "Frieren") };
        _dandadan = new AnimeItem { AniListId = 4, Titulo = "Dandadan", RutaCarpeta = "" };
        _db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(() => new List<AnimeItem> { _frieren, _dandadan });
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
    }

    public void Dispose()
    {
        try { Directory.Delete(_raiz, true); } catch { }
    }

    private ImportarTorrentService CrearSut() =>
        new(new ImportadorTorrent(_motor.Object, Path.Combine(_raiz, "importados")), _db.Object, _descargas.Object, _dialogos.Object);

    private string TorrentEnDisco(string nombre, params string[] archivos)
    {
        static string S(string s) => $"{Encoding.UTF8.GetByteCount(s)}:{s}";
        var sb = new StringBuilder("d4:infod5:filesl");
        foreach (var a in archivos) sb.Append($"d6:lengthi100e4:pathl{S(a)}ee");
        sb.Append($"e4:name{S(nombre)}12:piece lengthi16384e6:pieces20:");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString()).Concat(new byte[20]).Concat(Encoding.ASCII.GetBytes("ee")).ToArray();
        string ruta = Path.Combine(_raiz, Guid.NewGuid().ToString("N") + ".torrent");
        File.WriteAllBytes(ruta, bytes);
        return ruta;
    }

    private string LoteFrieren() => TorrentEnDisco("[SubsPlease] Sousou no Frieren (01-03) (1080p)",
        "Sousou no Frieren - 01 (1080p).mkv", "Sousou no Frieren - 02 (1080p).mkv", "Sousou no Frieren - 03 (1080p).mkv");

    [Fact]
    public async Task Importar_ReconoceElAnime_YCuentaLosEpisodios()
    {
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        sut.Visible.Should().BeTrue();
        sut.AnimeElegido.Should().BeSameAs(_frieren);
        sut.TextoEpisodios.Should().Be(string.Format(LocalizationService.T("Desc_ImportarEpisodiosFormato"), "1–3", 3));
        sut.PuedeDescargar.Should().BeTrue();
    }

    [Fact]
    public async Task YaLoTiene_EnBaseEnDescargaOArchivoDeOtraExtension_Cuenta()
    {
        string ep1 = Path.Combine(_frieren.RutaCarpeta, "cualquiera.mkv");
        File.WriteAllText(ep1, "x");
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(1)).ReturnsAsync(new List<RegistroEpisodio> { new() { AniListId = 1, NumeroEpisodio = 1, RutaArchivo = ep1 } });
        double p;
        _descargas.Setup(d => d.EstaDescargando(1, 2, out p)).Returns(true);
        File.WriteAllText(Path.Combine(_frieren.RutaCarpeta, "Episodio 03.mkv"), "x");
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        sut.TextoDetalle.Should().Contain(string.Format(LocalizationService.T("Desc_ImportarYaTienesFormato"), 3));
        sut.TextoDetalle.Should().Contain(LocalizationService.T("Desc_ImportarNingunoNuevo"));
        sut.PuedeDescargar.Should().BeFalse();
    }

    [Fact]
    public async Task SinAnimeOAnimeSinCarpeta_NoDejaDescargar()
    {
        var sut = CrearSut();
        await sut.ImportarArchivoAsync(TorrentEnDisco("[X] Algo que no esta - 01", "Algo - 01.mkv"));
        await sut.Recalculo;
        sut.AnimeElegido.Should().BeNull();
        sut.PuedeDescargar.Should().BeFalse();

        sut.AnimeElegido = _dandadan;
        await sut.Recalculo;
        sut.TextoDetalle.Should().Contain(LocalizationService.T("Desc_ImportarSinCarpeta"));
        sut.PuedeDescargar.Should().BeFalse();
    }

    [Fact]
    public async Task Descargar_EncolaUnoPorEpisodioNuevo_YCierra()
    {
        var sut = CrearSut();
        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        await sut.DescargarCommand.ExecuteAsync(null);

        foreach (int ep in new[] { 1, 2, 3 })
            _descargas.Verify(d => d.IniciarDescargaTorrentManualAsync(1, "Sousou no Frieren", _frieren.RutaCarpeta, ep,
                It.Is<CandidatoTorrent>(c => c.TorrentUrl.StartsWith("file:") && c.EsBatch), It.IsAny<IEnumerable<string>?>()), Times.Once);
        sut.Visible.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelar_NoEncolaNada()
    {
        var sut = CrearSut();
        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        sut.CancelarCommand.Execute(null);

        sut.Visible.Should().BeFalse();
        _descargas.Verify(d => d.IniciarDescargaTorrentManualAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<CandidatoTorrent>(), It.IsAny<IEnumerable<string>?>()), Times.Never);
    }

    [Fact]
    public async Task Importar_ConElCuadroAbierto_SeIgnora()
    {
        var sut = CrearSut();
        await sut.ImportarArchivoAsync(LoteFrieren());
        await sut.Recalculo;

        await sut.ImportarArchivoAsync(TorrentEnDisco("[SubsPlease] Dandadan - 01", "Dandadan - 01.mkv"));

        sut.AnimeElegido.Should().BeSameAs(_frieren);
    }

    [Fact]
    public async Task ArchivoNoValido_AvisaYNoAbre()
    {
        string basura = Path.Combine(_raiz, "basura.torrent");
        File.WriteAllText(basura, "hola");
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(basura);

        sut.Visible.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(LocalizationService.T("Desc_ImportarErrorTitulo"), LocalizationService.T("Desc_ImportarNoValido"),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task TorrentSinVideos_AvisaYNoAbre()
    {
        var sut = CrearSut();

        await sut.ImportarArchivoAsync(TorrentEnDisco("OST", "01.flac"));

        sut.Visible.Should().BeFalse();
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), LocalizationService.T("Desc_ImportarSinVideos"), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }
}
```

- [ ] **Paso 3: ver que fallan.**

- [ ] **Paso 4: implementación**

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.Services.Torrents;

/// <summary>
/// Cuadro "Descargar torrent" (ver docs/plan-torrents-arrastrar-magnet.md). Mismo patrón que <see cref="SelectorTorrentService"/>:
/// estado observable que <c>MainWindow.xaml</c> dibuja encima de toda la ventana.
/// </summary>
public interface IImportarTorrentService : INotifyPropertyChanged
{
    bool Visible { get; }
    bool ObteniendoInformacion { get; }
    /// <summary>Hay un cuadro o una espera de magnet abiertos: las entradas nuevas se ignoran.</summary>
    bool Ocupado { get; }
    string NombreTorrent { get; }
    IReadOnlyList<AnimeItem> Animes { get; }
    AnimeItem? AnimeElegido { get; set; }
    string TextoEpisodios { get; }
    string TextoDetalle { get; }
    string TextoCarpeta { get; }
    bool PuedeDescargar { get; }
    IAsyncRelayCommand DescargarCommand { get; }
    IRelayCommand CancelarCommand { get; }
    Task ImportarArchivoAsync(string ruta, int ignorados = 0);
    Task ImportarMagnetAsync(string enlace);
}

public partial class ImportarTorrentService : ObservableObject, IImportarTorrentService
{
    private readonly ImportadorTorrent _importador;
    private readonly IDatabaseService _db;
    private readonly IDownloadService _descargas;
    private readonly IDialogService _dialogos;
    private TorrentImportado? _torrent;
    private RepartoEpisodios? _reparto;
    private CancellationTokenSource? _ctsMagnet;
    private int _version;

    public ImportarTorrentService(ImportadorTorrent importador, IDatabaseService db, IDownloadService descargas, IDialogService dialogos)
    {
        _importador = importador;
        _db = db;
        _descargas = descargas;
        _dialogos = dialogos;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Ocupado))] private bool _visible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Ocupado))] private bool _obteniendoInformacion;
    [ObservableProperty] private string _nombreTorrent = string.Empty;
    [ObservableProperty] private IReadOnlyList<AnimeItem> _animes = Array.Empty<AnimeItem>();
    [ObservableProperty] private AnimeItem? _animeElegido;
    [ObservableProperty] private string _textoEpisodios = string.Empty;
    [ObservableProperty] private string _textoDetalle = string.Empty;
    [ObservableProperty] private string _textoCarpeta = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DescargarCommand))] private bool _puedeDescargar;

    public bool Ocupado => Visible || ObteniendoInformacion;

    /// <summary>El último recálculo de episodios (para que las pruebas puedan esperarlo).</summary>
    internal Task Recalculo { get; private set; } = Task.CompletedTask;

    public async Task ImportarArchivoAsync(string ruta, int ignorados = 0)
    {
        if (Ocupado) return;
        try
        {
            await MostrarAsync(await _importador.DesdeArchivoAsync(ruta, CancellationToken.None));
            if (ignorados > 0) _dialogos.MostrarToast(LocalizationService.T("Desc_ImportarTitulo"), LocalizationService.T("Desc_ImportarVariosArchivos"));
        }
        catch (ImportacionTorrentException ex)
        {
            AvisarError(ex.Message);
        }
    }

    public async Task ImportarMagnetAsync(string enlace)
    {
        if (Ocupado) return;
        ObteniendoInformacion = true;
        Core.Cancelacion.Reemplazar(ref _ctsMagnet);
        try
        {
            var torrent = await _importador.DesdeMagnetAsync(enlace, _ctsMagnet.Token);
            ObteniendoInformacion = false;
            await MostrarAsync(torrent);
        }
        catch (ImportacionTorrentException ex)
        {
            // Cancelado por el usuario: sin aviso.
            if (_ctsMagnet?.IsCancellationRequested != true) AvisarError(ex.Message);
        }
        finally
        {
            ObteniendoInformacion = false;
        }
    }

    private async Task MostrarAsync(TorrentImportado torrent)
    {
        if (AnalisisTorrentImportado.Repartir(torrent.Archivos, _ => false).Videos == 0)
        {
            AvisarError(LocalizationService.T("Desc_ImportarSinVideos"));
            return;
        }
        var animes = (await _db.ObtenerTodosLosAnimesAsync() ?? new List<AnimeItem>())
            .OrderBy(a => a.Titulo, StringComparer.CurrentCultureIgnoreCase).ToList();
        _torrent = torrent;
        NombreTorrent = torrent.Nombre;
        Animes = animes;
        AnimeElegido = AnalisisTorrentImportado.ReconocerAnime(torrent.Nombre, animes);
        Recalculo = RecalcularAsync(); // también si AnimeElegido no cambió (null → null)
        Visible = true;
        await Recalculo;
    }

    partial void OnAnimeElegidoChanged(AnimeItem? value) => Recalculo = RecalcularAsync();

    private async Task RecalcularAsync()
    {
        int version = ++_version;
        var anime = AnimeElegido;
        var torrent = _torrent;
        PuedeDescargar = false;
        if (anime == null || torrent == null)
        {
            _reparto = null;
            TextoEpisodios = TextoDetalle = TextoCarpeta = string.Empty;
            return;
        }

        var registros = await _db.ObtenerRegistrosPorAnimeAsync(anime.AniListId) ?? new List<RegistroEpisodio>();
        string carpeta = anime.RutaCarpeta ?? string.Empty;
        var reparto = await Task.Run(() =>
        {
            var enDisco = registros.Where(r => !string.IsNullOrEmpty(r.RutaArchivo) && File.Exists(r.RutaArchivo))
                .Select(r => r.NumeroEpisodio).ToHashSet();
            var enCarpeta = Directory.Exists(carpeta)
                ? Directory.EnumerateFiles(carpeta, "Episodio *.*").Where(AnalisisTorrentImportado.EsVideo)
                    .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f)["Episodio ".Length..], out int n) ? n : 0).ToHashSet()
                : new HashSet<int>();
            return AnalisisTorrentImportado.Repartir(torrent.Archivos,
                n => enDisco.Contains(n) || enCarpeta.Contains(n) || _descargas.EstaDescargando(anime.AniListId, n, out _));
        });
        if (version != _version) return;

        _reparto = reparto;
        TextoEpisodios = string.Format(LocalizationService.T("Desc_ImportarEpisodiosFormato"),
            AnalisisTorrentImportado.FormatearRangos(reparto.Todos), reparto.Videos);
        var detalle = new List<string>();
        if (reparto.YaEstan > 0) detalle.Add(string.Format(LocalizationService.T("Desc_ImportarYaTienesFormato"), reparto.YaEstan));
        if (reparto.SinNumero > 0) detalle.Add(string.Format(LocalizationService.T("Desc_ImportarSinNumeroFormato"), reparto.SinNumero));
        if (reparto.Nuevos.Count == 0) detalle.Add(LocalizationService.T("Desc_ImportarNingunoNuevo"));
        if (carpeta.Length == 0) detalle.Add(LocalizationService.T("Desc_ImportarSinCarpeta"));
        TextoDetalle = string.Join(" · ", detalle);
        TextoCarpeta = carpeta.Length == 0 ? string.Empty : string.Format(LocalizationService.T("Desc_ImportarCarpetaFormato"), carpeta);
        PuedeDescargar = carpeta.Length > 0 && reparto.Nuevos.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(PuedeDescargar))]
    private async Task DescargarAsync()
    {
        var anime = AnimeElegido;
        var torrent = _torrent;
        var reparto = _reparto;
        if (anime == null || torrent == null || reparto == null || !PuedeDescargar) return;

        PuedeDescargar = false;
        var candidato = new CandidatoTorrent(torrent.Nombre, torrent.Url, torrent.Hash, 0, torrent.Tamano, EsBatch: reparto.Videos > 1);
        var titulos = AnalisisTorrentImportado.TitulosDe(anime);
        foreach (int episodio in reparto.Nuevos)
            await _descargas.IniciarDescargaTorrentManualAsync(anime.AniListId, anime.Titulo, anime.RutaCarpeta, episodio, candidato, titulos);

        Cerrar();
        _dialogos.MostrarToast(LocalizationService.T("Desc_ImportarAnadidosTitulo"),
            string.Format(LocalizationService.T("Desc_ImportarAnadidosFormato"), reparto.Nuevos.Count), "CheckCircleOutline", "#4CAF50");
    }

    [RelayCommand]
    private void Cancelar()
    {
        _ctsMagnet?.Cancel();
        Cerrar();
    }

    private void Cerrar()
    {
        _version++;
        Visible = false;
        _torrent = null;
        _reparto = null;
    }

    private void AvisarError(string mensaje) =>
        _dialogos.MostrarToast(LocalizationService.T("Desc_ImportarErrorTitulo"), mensaje, "AlertCircleOutline", "#EF4444");
}
```

Notas para la implementación:
- `ImportadorTorrent.DesdeArchivoAsync` lanza `ImportacionTorrentException` también si el archivo no existe (`FileNotFoundException` es `IOException`).
- El `catch` de `ImportarArchivoAsync` solo atrapa `ImportacionTorrentException`; cualquier otra excepción la atrapa el manejador de la vista (que registra).
- `#4CAF50` y `#EF4444` son los colores de aviso que ya usan otros toasts (`AgregarAnimeViewModel`, `AvisarSinVideo`).

Registro en `App.xaml.cs`, tras `ISelectorTorrentService`:

```csharp
        // Torrents soltados en la ventana o pegados como magnet (cuadro "Descargar torrent").
        services.AddSingleton(sp => new Services.Torrents.ImportadorTorrent(sp.GetRequiredService<ITorrentDownloadService>(), AppDataPaths.TorrentsImportadosDir));
        services.AddSingleton<Services.Torrents.IImportarTorrentService, Services.Torrents.ImportarTorrentService>();
```

- [ ] **Paso 5: pruebas en verde** (filtro `"FullyQualifiedName~ImportarTorrentServiceTests|FullyQualifiedName~LocalizationServiceTests"`).

---

## Tarea 6: entradas en la ventana y botón de Descargas

**Archivos:** crear `AnimeLocalTracker/Views/EntradaTorrent.cs`; `Views/MainWindow.xaml` (+ `.cs`); `ViewModels/MainViewModel.cs`; `ViewModels/DescargasViewModel.cs`; `Views/DescargasView.xaml` (+ `.cs`); `App.xaml.cs` (`ServiciosDeLaVentana`).

**Interfaces:** consume `IImportarTorrentService` (tarea 5), `ImportadorTorrent.PareceMagnet` (tarea 3), `AnalisisTorrentImportado` no.

- [ ] **Paso 1: `EntradaTorrent.cs`** (lógica de interfaz compartida por las tres entradas)

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Torrents;

namespace AnimeLocalTracker.Views;

/// <summary>Arrastrar, Ctrl+V y el botón de Descargas: todas llevan al cuadro "Descargar torrent".</summary>
internal static class EntradaTorrent
{
    public static bool EsAceptable(IDataObject datos) => ArchivosTorrent(datos).Length > 0 || ImportadorTorrent.PareceMagnet(Texto(datos));

    public static async Task SoltarAsync(IImportarTorrentService servicio, IDataObject datos)
    {
        var archivos = ArchivosTorrent(datos);
        if (archivos.Length > 0) await servicio.ImportarArchivoAsync(archivos[0], archivos.Length - 1);
        else if (Texto(datos) is { } texto && ImportadorTorrent.PareceMagnet(texto)) await servicio.ImportarMagnetAsync(texto.Trim());
    }

    /// <summary>El magnet del portapapeles, o null. El portapapeles puede estar bloqueado por otro programa.</summary>
    public static string? MagnetDelPortapapeles()
    {
        try
        {
            string texto = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            return ImportadorTorrent.PareceMagnet(texto) ? texto.Trim() : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ExternalException)
        {
            AppLogger.Debug("EntradaTorrent", $"No se pudo leer el portapapeles: {ex.Message}");
            return null;
        }
    }

    /// <summary>Ctrl+V solo es nuestro si el foco no está donde se escribe.</summary>
    public static bool FocoEnCajaDeTexto() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox || Keyboard.FocusedElement is ComboBox { IsEditable: true };

    /// <summary>Botón de Descargas: el magnet copiado si lo hay; si no, elegir un archivo .torrent.</summary>
    public static async Task PortapapelesOArchivoAsync(IImportarTorrentService servicio)
    {
        if (MagnetDelPortapapeles() is { } magnet)
        {
            await servicio.ImportarMagnetAsync(magnet);
            return;
        }
        var dialogo = new Microsoft.Win32.OpenFileDialog { Filter = LocalizationService.T("Desc_ImportarFiltro"), Multiselect = false };
        if (dialogo.ShowDialog() == true) await servicio.ImportarArchivoAsync(dialogo.FileName);
    }

    private static string[] ArchivosTorrent(IDataObject datos) =>
        datos.GetDataPresent(DataFormats.FileDrop) && datos.GetData(DataFormats.FileDrop) is string[] archivos
            ? archivos.Where(a => string.Equals(Path.GetExtension(a), ".torrent", StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();

    private static string? Texto(IDataObject datos) =>
        datos.GetDataPresent(DataFormats.UnicodeText) ? datos.GetData(DataFormats.UnicodeText) as string
        : datos.GetDataPresent(DataFormats.Text) ? datos.GetData(DataFormats.Text) as string
        : null;
}
```

> `ExternalException` es `System.Runtime.InteropServices.ExternalException` (base de `COMException`): con ella basta; dejar solo esa si el compilador avisa de redundancia.

- [ ] **Paso 2: `MainViewModel`** — parámetro opcional al final del constructor y propiedad:

```csharp
        IMusicaDeFondoService? musicaDeFondo = null,
        Services.Torrents.IImportarTorrentService? importarTorrent = null)
```
```csharp
        ImportarTorrent = importarTorrent;
```
```csharp
    /// <summary>Cuadro "Descargar torrent" (arrastrar / Ctrl+V / botón de Descargas). Null en pruebas que no lo usan.</summary>
    public Services.Torrents.IImportarTorrentService? ImportarTorrent { get; }
```

`App.xaml.cs`, en `ServiciosDeLaVentana`, añadir `typeof(Services.Torrents.IImportarTorrentService)`.

- [ ] **Paso 3: `MainWindow.xaml`** — en el `Window`, `AllowDrop="True" PreviewDragOver="Window_PreviewDragOver" PreviewDragLeave="Window_PreviewDragLeave" PreviewDrop="Window_PreviewDrop"`. Junto al overlay del selector de torrents (mismo `Grid` padre, tras él):

```xml
                <!-- ══ TORRENT ARRASTRADO O MAGNET PEGADO: capa al arrastrar, espera del magnet y cuadro de confirmación ══ -->
                <Grid x:Name="CapaSoltarTorrent" Visibility="Collapsed" Grid.Column="0" Grid.ColumnSpan="2"
                      Style="{StaticResource AppOverlayScrim}" Panel.ZIndex="2002" IsHitTestVisible="False">
                    <Border Style="{StaticResource AppDialogCard}" Padding="28,22">
                        <StackPanel Orientation="Horizontal">
                            <materialDesign:PackIcon Kind="Magnet" Width="26" Height="26" VerticalAlignment="Center" Foreground="{DynamicResource Brush.Accent}" Margin="0,0,12,0"/>
                            <TextBlock Text="{loc:T Desc_ImportarSoltar}" Style="{StaticResource AppText.Section}" VerticalAlignment="Center"/>
                        </StackPanel>
                    </Border>
                </Grid>

                <Grid Visibility="{Binding ImportarTorrent.ObteniendoInformacion, Converter={StaticResource BoolToVis}}"
                      Grid.Column="0" Grid.ColumnSpan="2" Style="{StaticResource AppOverlayScrim}" Panel.ZIndex="2001">
                    <Border Style="{StaticResource AppDialogCard}" Padding="28,22" AutomationProperties.Name="{loc:T Desc_ImportarObteniendo}">
                        <StackPanel>
                            <StackPanel Orientation="Horizontal">
                                <ProgressBar Style="{StaticResource MaterialDesignCircularProgressBar}" IsIndeterminate="True" Width="22" Height="22" Margin="0,0,12,0"/>
                                <TextBlock Text="{loc:T Desc_ImportarObteniendo}" Style="{StaticResource AppText.Section}" FontSize="15" VerticalAlignment="Center"/>
                            </StackPanel>
                            <Button HorizontalAlignment="Right" Margin="0,16,0,0" Style="{StaticResource AppDialogGhostButton}"
                                    Content="{loc:T Dlg_Cancelar}" Command="{Binding ImportarTorrent.CancelarCommand}"/>
                        </StackPanel>
                    </Border>
                </Grid>

                <Grid Visibility="{Binding ImportarTorrent.Visible, Converter={StaticResource BoolToVis}}"
                      Grid.Column="0" Grid.ColumnSpan="2" Style="{StaticResource AppOverlayScrim}" Panel.ZIndex="2001">
                    <Border Style="{StaticResource AppDialogCard}" Width="560"
                            KeyboardNavigation.TabNavigation="Cycle" AutomationProperties.Name="{loc:T Desc_ImportarTitulo}">
                        <Grid>
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto"/>
                                <RowDefinition Height="Auto"/>
                                <RowDefinition Height="Auto"/>
                            </Grid.RowDefinitions>
                            <Grid Margin="26,24,26,14">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>
                                <Border Width="48" Height="48" CornerRadius="14" VerticalAlignment="Center" Margin="0,0,16,0" Background="{DynamicResource Brush.AccentTint}">
                                    <materialDesign:PackIcon Kind="Magnet" Width="26" Height="26" HorizontalAlignment="Center" VerticalAlignment="Center" Foreground="{DynamicResource Brush.Accent}"/>
                                </Border>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock Text="{loc:T Desc_ImportarTitulo}" Style="{StaticResource AppText.Section}" FontSize="18"/>
                                    <TextBlock Text="{Binding ImportarTorrent.NombreTorrent}" Style="{StaticResource AppText.Secondary}" TextWrapping="Wrap" Margin="0,3,0,0"/>
                                </StackPanel>
                            </Grid>
                            <StackPanel Grid.Row="1" Margin="26,0,26,18">
                                <TextBlock Text="{loc:T Desc_ImportarAnime}" Style="{StaticResource AppText.Caption}" Margin="0,0,0,4"/>
                                <ComboBox ItemsSource="{Binding ImportarTorrent.Animes}" SelectedItem="{Binding ImportarTorrent.AnimeElegido, Mode=TwoWay}"
                                          DisplayMemberPath="Titulo" materialDesign:HintAssist.Hint="{loc:T Desc_ImportarElegirAnime}"
                                          IsTextSearchEnabled="True" TextSearch.TextPath="Titulo" Height="38"
                                          AutomationProperties.Name="{loc:T Desc_ImportarAnime}"/>
                                <TextBlock Text="{Binding ImportarTorrent.TextoEpisodios}" Style="{StaticResource AppText.Body}" Margin="0,14,0,0"/>
                                <TextBlock Text="{Binding ImportarTorrent.TextoDetalle}" Style="{StaticResource AppText.Caption}" TextWrapping="Wrap" Margin="0,4,0,0"/>
                                <TextBlock Text="{Binding ImportarTorrent.TextoCarpeta}" Style="{StaticResource AppText.Caption}" TextTrimming="CharacterEllipsis" Margin="0,4,0,0"/>
                            </StackPanel>
                            <Border Grid.Row="2" Background="{DynamicResource Brush.Surface2}" CornerRadius="0,0,16,16" Padding="20,14">
                                <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                                    <Button Style="{StaticResource AppDialogGhostButton}" Content="{loc:T Dlg_Cancelar}" Command="{Binding ImportarTorrent.CancelarCommand}" Margin="0,0,8,0"/>
                                    <Button Style="{StaticResource AppDialogPrimaryButton}" Content="{loc:T Desc_ImportarDescargar}" Command="{Binding ImportarTorrent.DescargarCommand}"/>
                                </StackPanel>
                            </Border>
                        </Grid>
                    </Border>
                </Grid>
```

- [ ] **Paso 4: `MainWindow.xaml.cs`** — manejadores (con `try-catch`) y Esc / Ctrl+V en `Window_PreviewKeyDown`:

```csharp
    // === Torrent arrastrado o magnet pegado (ver EntradaTorrent) ===

    private Services.Torrents.IImportarTorrentService? ImportarTorrent => (DataContext as MainViewModel)?.ImportarTorrent;

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool acepta = ImportarTorrent is { Ocupado: false } && EntradaTorrent.EsAceptable(e.Data);
        e.Effects = acepta ? DragDropEffects.Copy : DragDropEffects.None;
        CapaSoltarTorrent.Visibility = acepta ? Visibility.Visible : Visibility.Collapsed;
        if (acepta) e.Handled = true;
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e) => CapaSoltarTorrent.Visibility = Visibility.Collapsed;

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        CapaSoltarTorrent.Visibility = Visibility.Collapsed;
        if (ImportarTorrent is not { Ocupado: false } servicio || !EntradaTorrent.EsAceptable(e.Data)) return;
        e.Handled = true;
        try
        {
            await EntradaTorrent.SoltarAsync(servicio, e.Data);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MainWindow", $"No se pudo importar el torrent soltado: {ex.Message}");
        }
    }
```

En `Window_PreviewKeyDown`, añadir dos ramas antes del cierre del método:

```csharp
        else if (e.Key == System.Windows.Input.Key.Escape && ImportarTorrent is { Ocupado: true } abierto)
        {
            abierto.CancelarCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
                 && ImportarTorrent is { Ocupado: false } servicio && !EntradaTorrent.FocoEnCajaDeTexto()
                 && EntradaTorrent.MagnetDelPortapapeles() is { } magnet)
        {
            e.Handled = true;
            _ = ImportarMagnetSinBloquearAsync(servicio, magnet);
        }
```

```csharp
    private static async Task ImportarMagnetSinBloquearAsync(Services.Torrents.IImportarTorrentService servicio, string magnet)
    {
        try { await servicio.ImportarMagnetAsync(magnet); }
        catch (Exception ex) { AppLogger.Warn("MainWindow", $"No se pudo importar el magnet pegado: {ex.Message}"); }
    }
```

- [ ] **Paso 5: botón en Descargas** — `DescargasViewModel`: parámetro opcional y propiedad

```csharp
    public DescargasViewModel(IDownloadService downloadService, IDatabaseService? database = null,
        IAnimeThemesDownloadService? descargasMusica = null, IAnimeThemesService? catalogoMusica = null,
        Services.Torrents.IImportarTorrentService? importarTorrent = null)
```
```csharp
        ImportarTorrent = importarTorrent;
```
```csharp
    /// <summary>Botón "Añadir torrent o magnet" (null en pruebas: el botón no se ve).</summary>
    public Services.Torrents.IImportarTorrentService? ImportarTorrent { get; }
    public bool PuedeImportarTorrent => ImportarTorrent != null;
```

`DescargasView.xaml`, primero dentro del `StackPanel` de acciones de la derecha (antes del botón de pausar todas):

```xml
                <Button Click="ImportarTorrent_Click" Height="40" Padding="16,0" Margin="0,0,10,0"
                        Style="{StaticResource MaterialDesignOutlinedButton}" materialDesign:ButtonAssist.CornerRadius="10" Cursor="Hand"
                        Visibility="{Binding PuedeImportarTorrent, Converter={StaticResource BoolToVis}}"
                        ToolTip="{loc:T Desc_ImportarBotonTip}" AutomationProperties.Name="{loc:T Desc_ImportarBoton}">
                    <StackPanel Orientation="Horizontal">
                        <materialDesign:PackIcon Kind="Magnet" Width="18" Height="18" VerticalAlignment="Center" Margin="0,0,8,0"/>
                        <TextBlock Text="{loc:T Desc_ImportarBoton}" FontWeight="SemiBold" FontSize="13.5" VerticalAlignment="Center"/>
                    </StackPanel>
                </Button>
```

`DescargasView.xaml.cs`:

```csharp
    private async void ImportarTorrent_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is DescargasViewModel { ImportarTorrent: { Ocupado: false } servicio })
                await EntradaTorrent.PortapapelesOArchivoAsync(servicio);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DescargasView", $"No se pudo importar el torrent: {ex.Message}");
        }
    }
```

- [ ] **Paso 6: compilar (0 advertencias), pruebas afectadas y literales** — filtro `"FullyQualifiedName~ImportarTorrent|FullyQualifiedName~ImportadorTorrent|FullyQualifiedName~AnalisisTorrent|FullyQualifiedName~Descargas|FullyQualifiedName~MainViewModel|FullyQualifiedName~LocalizationServiceTests"`; `buscar_literales.py` sobre `EntradaTorrent.cs`, `ImportarTorrentService.cs`, `ImportadorTorrent.cs`, `MainWindow.xaml`, `DescargasView.xaml`.

---

## Tarea 7: cierre

- [ ] **Paso 1:** suite completa una vez (sube respecto a 3486).
- [ ] **Paso 2:** benchmarks compilan.
- [ ] **Paso 3: en vivo** (`perfil-aislado` + `wpf-visual-verification`):
  1. Elegir en Nyaa (con `curl` sobre su RSS, sin descargar vídeo) el torrent más pequeño de un anime de la biblioteca del perfil; bajar su `.torrent` al scratchpad.
  2. `crear` → `iniciar --con-red`. Soltar el `.torrent` sobre la ventana: no se puede arrastrar de verdad con UIA → simularlo llamando a `Ctrl+V` con el **magnet** de ese torrent en el portapapeles (`Set-Clipboard`) y, para el archivo, con el botón de Descargas en un perfil sin magnet copiado (el selector de archivos se maneja con UIA: escribir la ruta y Enter). Capturar el cuadro: anime preseleccionado, episodios, carpeta.
  3. Descargar; ver la fila en Descargas y esperar el archivo final (o, si tarda demasiado, comprobar que arranca y cancelarlo).
  4. Sin red (`iniciar` sin `--con-red`): pegar el magnet → aviso; el botón con el `.torrent` → se encola.
  5. `cerrar`; borrar los vídeos bajados; carpeta real con 0 diferencias; `borrar`.
- [ ] **Paso 4:** informe con ejemplo, verificado/no verificado (el arrastre real con el ratón no se puede automatizar aquí: se prueban sus piezas y Ctrl+V/botón, que usan el mismo servicio), y propuesta de commit.
