using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using AnimeLocalTracker.Tests.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Los openings/endings descargados desde la ficha salen en la pestaña Descargas como cualquier otra descarga: en "Activas"
/// mientras bajan (con cancelar) y en el "Historial" al terminar (reintentar, abrir carpeta y escuchar en la ficha). Las
/// vistas previas no, porque son temporales. Carpetas temporales propias y un AniList ID distinto por prueba (el mensajero es
/// global y las pruebas corren en paralelo).
/// </summary>
public sealed class DescargasMusicaTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"AnimeTracker_DescargasMusica_{Guid.NewGuid():N}");
    private readonly Mock<IHttpClientFactory> _http = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly ConcurrentQueue<DescargaMusicaProgresoMensaje> _mensajes = new();
    private readonly object _receptor = new();
    private readonly int _id = 700_000_000 + Random.Shared.Next(1, 90_000_000);

    public DescargasMusicaTests()
    {
        WeakReferenceMessenger.Default.Register<object, DescargaMusicaProgresoMensaje>(_receptor, (_, m) =>
        {
            if (m.AniListId == _id) _mensajes.Enqueue(m);
        });
        _db.Setup(d => d.ObtenerAnimePorIdAsync(_id)).ReturnsAsync(new AnimeItem { AniListId = _id, Titulo = "ONE PIECE" });
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(_receptor);
        try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true); } catch { /* ignore */ }
    }

    private AnimeThemesDownloadService CrearSut(IDatabaseService? db = null) =>
        new(_http.Object, Path.Combine(_raiz, "Music"), Path.Combine(_raiz, "Previews"), baseDatos: db ?? _db.Object)
        {
            EsperaEntreIntentos = TimeSpan.Zero
        };

    private static AnimeThemeInfo Tema(string slug = "OP1", string titulo = "We Are!") =>
        new() { Slug = slug, Tipo = slug[..2], Version = 1, TituloCancion = titulo, NombreAnime = "One Piece", AudioUrlOgg = "https://a.animethemes.moe/x.ogg" };

    private sealed class Manejador(Func<HttpResponseMessage> responder, Task? retener = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (retener != null) await retener.WaitAsync(ct);
            return responder();
        }
    }

    private void Responder(Func<HttpResponseMessage> responder, Task? retener = null) =>
        _http.Setup(f => f.CreateClient("Downloader")).Returns(() => new HttpClient(new Manejador(responder, retener)));

    private static HttpResponseMessage Ogg(byte[] datos)
    {
        var contenido = new StreamContent(new MemoryStream(datos));
        contenido.Headers.ContentLength = datos.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = contenido };
    }

    // === Servicio: avisos y historial ===

    [Fact]
    public async Task UnaDescarga_AvisaDelAvanceYTerminaEnElHistorialComoMusica()
    {
        Responder(() => Ogg(GenerarOgg()));
        DescargaHistorial? guardada = null;
        _db.Setup(d => d.GuardarDescargaHistorialAsync(It.IsAny<DescargaHistorial>())).Callback<DescargaHistorial>(h => guardada = h).Returns(Task.CompletedTask);

        var ruta = await CrearSut().DescargarYConvertirAsync(_id, Tema(), null, CancellationToken.None);

        ruta.Should().NotBeNull();
        var mensajes = _mensajes.ToList();
        mensajes.Should().NotBeEmpty();
        mensajes.Should().OnlyContain(m => m.AnimeTitulo == "ONE PIECE" && m.TemaTitulo == "OP1 · We Are!" && m.TemaClave == "OP|OP1|1");
        mensajes[^1].Should().Match<DescargaMusicaProgresoMensaje>(m => m.Terminada && m.Completada && m.RutaArchivo == ruta && m.Progreso == 100);
        mensajes.Take(mensajes.Count - 1).Should().OnlyContain(m => !m.Terminada);
        mensajes.Count.Should().BeLessThan(20, "los avisos se espacian (no uno por trozo descargado)");

        guardada.Should().NotBeNull();
        guardada!.Tipo.Should().Be(DescargaHistorial.TipoMusica);
        guardada.Completada.Should().BeTrue();
        guardada.TemaClave.Should().Be("OP|OP1|1");
        guardada.TemaTitulo.Should().Be("OP1 · We Are!");
        guardada.AnimeTitulo.Should().Be("ONE PIECE", "el título de la biblioteca, como las descargas de episodios");
        guardada.RutaArchivo.Should().Be(ruta);
        guardada.TamanoBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task UnaDescargaFallida_QuedaEnElHistorialConElMotivo()
    {
        Responder(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        DescargaHistorial? guardada = null;
        _db.Setup(d => d.GuardarDescargaHistorialAsync(It.IsAny<DescargaHistorial>())).Callback<DescargaHistorial>(h => guardada = h).Returns(Task.CompletedTask);

        (await CrearSut().DescargarYConvertirAsync(_id, Tema(), null, CancellationToken.None)).Should().BeNull();

        guardada!.Completada.Should().BeFalse();
        guardada.Error.Should().Contain("404");
        _mensajes.Last().Should().Match<DescargaMusicaProgresoMensaje>(m => m.Terminada && !m.Completada && !m.Cancelada && m.Error!.Contains("404"));
    }

    [Fact]
    public async Task UnaVistaPrevia_NoSaleEnDescargasNiEnElHistorial()
    {
        Responder(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        await CrearSut().PrepararVistaPreviaAsync(_id, Tema(), null, CancellationToken.None);

        _mensajes.Should().BeEmpty();
        _db.Verify(d => d.GuardarDescargaHistorialAsync(It.IsAny<DescargaHistorial>()), Times.Never);
    }

    [Fact]
    public async Task CancelarDesdeDescargas_LaDetieneSinApuntarUnFallo()
    {
        var nunca = new TaskCompletionSource();
        Responder(() => Ogg([1, 2, 3]), nunca.Task);
        var sut = CrearSut();
        var tema = Tema();

        var descarga = sut.DescargarYConvertirAsync(_id, tema, null, CancellationToken.None);
        await EsperarAsync(() => sut.ObtenerDescargasMusicaActivas().Any(d => d.AniListId == _id));

        var activa = sut.ObtenerDescargasMusicaActivas().Single(d => d.AniListId == _id);
        activa.EsMusica.Should().BeTrue();
        activa.Subtitulo.Should().Be("OP1 · We Are!");

        sut.CancelarDescargaMusica(_id, "OP|OP1|1").Should().BeTrue();
        (await descarga.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();

        sut.FueCanceladaPorUsuario(_id, tema).Should().BeTrue();
        sut.FueCanceladaPorUsuario(_id, tema).Should().BeFalse("se consume una sola vez");
        sut.ObtenerDescargasMusicaActivas().Should().NotContain(d => d.AniListId == _id);
        _db.Verify(d => d.GuardarDescargaHistorialAsync(It.IsAny<DescargaHistorial>()), Times.Never);
        _mensajes.Last().Cancelada.Should().BeTrue();
    }

    [Fact]
    public void Cancelar_SinNadaEnMarcha_DevuelveFalso()
    {
        CrearSut().CancelarDescargaMusica(_id, "OP|OP1|1").Should().BeFalse();
    }

    // === Pestaña Descargas ===

    private DescargasViewModel CrearPestana(Mock<IAnimeThemesDownloadService> musica, Mock<IAnimeThemesService>? catalogo = null, Mock<IDownloadService>? episodios = null)
    {
        episodios ??= new Mock<IDownloadService>();
        episodios.Setup(d => d.ObtenerDescargasActivas()).Returns(new List<DescargaItem>());
        musica.Setup(m => m.ObtenerDescargasMusicaActivas()).Returns(new List<DescargaItem>());
        _db.Setup(d => d.ObtenerDescargasHistorialAsync(It.IsAny<int>())).ReturnsAsync(new List<DescargaHistorial>());
        return new DescargasViewModel(episodios.Object, _db.Object, musica.Object, catalogo?.Object);
    }

    private static DescargaMusicaProgresoMensaje Aviso(int id, double progreso, bool terminada = false, bool completada = false, bool convirtiendo = false, string? error = null) =>
        new(id, "ONE PIECE", "OP|OP1|1", "OP1 · We Are!", progreso, 1_500_000, convirtiendo, terminada, completada, false, completada ? @"C:\Music\1\OP1 - We Are!.mp3" : null, error);

    [Fact]
    public void LaPestana_MuestraLaCancionMientrasBajaYLaRetiraSiFalla()
    {
        var sut = CrearPestana(new Mock<IAnimeThemesDownloadService>());

        sut.AplicarMensajeMusica(Aviso(_id, 40));
        var fila = sut.ColaDescargas.Should().ContainSingle().Subject;
        fila.EsMusica.Should().BeTrue();
        fila.AnimeTitulo.Should().Be("ONE PIECE");
        fila.Subtitulo.Should().Be("OP1 · We Are!");
        fila.Progreso.Should().Be(40);
        fila.VelocidadDescarga.Should().Contain("MB/s");
        sut.TieneDescargas.Should().BeTrue();

        sut.AplicarMensajeMusica(Aviso(_id, 90, convirtiendo: true));
        sut.ColaDescargas.Should().ContainSingle().Which.Convirtiendo.Should().BeTrue();

        sut.AplicarMensajeMusica(Aviso(_id, 90, terminada: true, error: "HTTP 404"));
        sut.ColaDescargas.Should().BeEmpty();
    }

    [Fact]
    public void LaPestana_AlCompletarse_DejaLaFilaAlCienPorCien()
    {
        var sut = CrearPestana(new Mock<IAnimeThemesDownloadService>());

        sut.AplicarMensajeMusica(Aviso(_id, 50));
        sut.AplicarMensajeMusica(Aviso(_id, 100, terminada: true, completada: true));

        var fila = sut.ColaDescargas.Should().ContainSingle().Subject;
        fila.IsCompleted.Should().BeTrue();
        fila.Progreso.Should().Be(100);
    }

    [Fact]
    public void Cancelar_UnaCancion_UsaElServicioDeMusicaYNoElDeEpisodios()
    {
        var musica = new Mock<IAnimeThemesDownloadService>();
        var episodios = new Mock<IDownloadService>();
        var sut = CrearPestana(musica, episodios: episodios);
        sut.AplicarMensajeMusica(Aviso(_id, 30));

        sut.CancelarDescargaCommand.Execute(sut.ColaDescargas.Single());

        musica.Verify(m => m.CancelarDescargaMusica(_id, "OP|OP1|1"), Times.Once);
        episodios.Verify(d => d.CancelarDescarga(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        sut.ColaDescargas.Should().BeEmpty();
    }

    [Fact]
    public void PausarTodas_NoTocaLasCanciones()
    {
        var episodios = new Mock<IDownloadService>();
        var sut = CrearPestana(new Mock<IAnimeThemesDownloadService>(), episodios: episodios);
        sut.ColaDescargas.Add(new DescargaItem { AniListId = 1, NumeroEpisodio = 5, IsDownloading = true });
        sut.AplicarMensajeMusica(Aviso(_id, 30));

        sut.AlternarPausaTodasCommand.Execute(null);

        sut.ColaDescargas.Single(d => !d.EsMusica).IsPaused.Should().BeTrue();
        sut.ColaDescargas.Single(d => d.EsMusica).IsPaused.Should().BeFalse();
        sut.TodasPausadas.Should().BeTrue("todas las que se pueden pausar lo están");
        episodios.Verify(d => d.PausarTodas(), Times.Once);
    }

    [Fact]
    public void PausarTodas_SoloApareceSiHayAlgunEpisodioBajando()
    {
        var sut = CrearPestana(new Mock<IAnimeThemesDownloadService>());

        sut.AplicarMensajeMusica(Aviso(_id, 30));
        sut.TieneDescargas.Should().BeTrue();
        sut.MostrarPausarTodas.Should().BeFalse("una canción no se puede pausar");

        sut.ColaDescargas.Add(new DescargaItem { AniListId = 1, NumeroEpisodio = 5, IsDownloading = true });
        sut.CargarDescargas(); // recuenta
        sut.AplicarMensajeMusica(Aviso(_id, 40));
        sut.ColaDescargas.Add(new DescargaItem { AniListId = 1, NumeroEpisodio = 5, IsDownloading = true });
        sut.AplicarMensajeMusica(Aviso(_id, 50));
        sut.MostrarPausarTodas.Should().BeTrue();
    }

    [Theory]
    [InlineData(2_354_761L, "2.2 MB")]
    [InlineData(350L * 1024 * 1024, "350 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    public void FormatearTamano_UnaCancionLlevaDecimal(long bytes, string esperado)
    {
        DescargaHistorialItemViewModel.FormatearTamano(bytes).Should().Be(esperado);
    }

    [Fact]
    public void CancelarTodas_TambienCancelaLasCanciones()
    {
        var musica = new Mock<IAnimeThemesDownloadService>();
        var sut = CrearPestana(musica);
        sut.AplicarMensajeMusica(Aviso(_id, 30));

        sut.CancelarTodasCommand.Execute(null);

        musica.Verify(m => m.CancelarTodasMusica(), Times.Once);
    }

    [Fact]
    public void AlEntrarEnLaPestana_AparecenLasCancionesQueYaEstabanBajando()
    {
        var musica = new Mock<IAnimeThemesDownloadService>();
        var sut = CrearPestana(musica);
        musica.Setup(m => m.ObtenerDescargasMusicaActivas()).Returns(
        [
            new DescargaItem { EsMusica = true, AniListId = _id, AnimeTitulo = "ONE PIECE", TemaClave = "OP|OP1|1", TemaTitulo = "OP1 · We Are!", Progreso = 20, VelocidadBps = 800_000, IsDownloading = true }
        ]);

        sut.CargarDescargas();

        sut.ColaDescargas.Should().ContainSingle().Which.VelocidadDescarga.Should().EndWith("KB/s");
    }

    // === Historial ===

    private static DescargaHistorial FilaMusica(int id, bool ok, int aniListId) => new()
    {
        Id = id,
        AniListId = aniListId,
        AnimeTitulo = "ONE PIECE",
        CarpetaDestino = @"C:\Music\21",
        RutaArchivo = ok ? @"C:\NoExiste\OP1 - We Are!.mp3" : string.Empty,
        TamanoBytes = ok ? 2_700_000 : 0,
        FechaUtc = DateTime.UtcNow,
        Completada = ok,
        Error = ok ? null : "HTTP 404",
        Tipo = DescargaHistorial.TipoMusica,
        TemaClave = "OP|OP1|1",
        TemaTitulo = "OP1 · We Are!"
    };

    [Fact]
    public void UnaFilaDeMusica_MuestraElTemaEnVezDelEpisodio()
    {
        var item = new DescargaHistorialItemViewModel(FilaMusica(1, true, _id));

        item.EsMusica.Should().BeTrue();
        item.DetalleTexto.Should().StartWith("OP1 · We Are!").And.Contain("MB");
        new DescargaHistorialItemViewModel(new DescargaHistorial { NumeroEpisodio = 3, Completada = true }).EsMusica.Should().BeFalse("filas anteriores: episodios");
    }

    [Fact]
    public async Task Reintentar_UnaCancionFallida_LaVuelveAPedirYQuitaLaFilaVieja()
    {
        var musica = new Mock<IAnimeThemesDownloadService>();
        var catalogo = new Mock<IAnimeThemesService>();
        var tema = Tema();
        catalogo.Setup(c => c.ObtenerTemasAsync(_id, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { Tema("ED1", "Otra"), tema });
        var sut = CrearPestana(musica, catalogo);
        _db.Setup(d => d.ObtenerDescargasHistorialAsync(It.IsAny<int>())).ReturnsAsync(new List<DescargaHistorial> { FilaMusica(9, false, _id) });
        await sut.CargarHistorialAsync();

        await sut.ReintentarCommand.ExecuteAsync(sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Single());

        musica.Verify(m => m.DescargarYConvertirAsync(_id, tema, null, It.IsAny<CancellationToken>()), Times.Once);
        _db.Verify(d => d.EliminarDescargaHistorialAsync(9), Times.Once);
        sut.PestanaActual.Should().Be("Activas");
    }

    [Fact]
    public async Task Reintentar_UnaCancionQueYaNoExiste_NoBorraLaFila()
    {
        var musica = new Mock<IAnimeThemesDownloadService>();
        var catalogo = new Mock<IAnimeThemesService>();
        catalogo.Setup(c => c.ObtenerTemasAsync(_id, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>());
        var sut = CrearPestana(musica, catalogo);
        _db.Setup(d => d.ObtenerDescargasHistorialAsync(It.IsAny<int>())).ReturnsAsync(new List<DescargaHistorial> { FilaMusica(9, false, _id) });
        await sut.CargarHistorialAsync();

        await sut.ReintentarCommand.ExecuteAsync(sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Single());

        _db.Verify(d => d.EliminarDescargaHistorialAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Reproducir_UnaCancion_AbreSuFichaConEseTema()
    {
        string ruta = Path.Combine(_raiz, "OP1 - We Are!.mp3");
        Directory.CreateDirectory(_raiz);
        File.WriteAllText(ruta, "mp3");
        var fila = FilaMusica(1, true, _id);
        fila.RutaArchivo = ruta;
        var sut = CrearPestana(new Mock<IAnimeThemesDownloadService>());
        _db.Setup(d => d.ObtenerDescargasHistorialAsync(It.IsAny<int>())).ReturnsAsync(new List<DescargaHistorial> { fila });
        await sut.CargarHistorialAsync();
        NavegarMensaje_Detalle? navegacion = null;
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<object, NavegarMensaje_Detalle>(receptor, (_, m) => { if (m.AnimeSeleccionado.AniListId == _id) navegacion = m; });
        try
        {
            await sut.ReproducirCommand.ExecuteAsync(sut.ItemsAgrupados.OfType<DescargaHistorialItemViewModel>().Single());
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }

        navegacion.Should().NotBeNull();
        navegacion!.ReproducirTemaClave.Should().Be("OP|OP1|1");
    }

    // === Base de datos: migración v15 ===

    [Fact]
    public async Task Migracion15_AnadeLasColumnasSinPerderElHistorialAnterior()
    {
        string rutaDb = Path.Combine(_raiz, "biblioteca.db");
        Directory.CreateDirectory(_raiz);
        using (var nueva = new DatabaseService(rutaDb)) await nueva.InicializarBaseDatosAsync();

        // Se deja la tabla como estaba en la v14 (sin las columnas de música) con una descarga de episodio.
        using (var c = new SQLiteConnection(rutaDb))
        {
            c.Execute("DROP TABLE DescargaHistorial;");
            c.Execute("CREATE TABLE DescargaHistorial (Id INTEGER PRIMARY KEY AUTOINCREMENT, AniListId INTEGER, AnimeTitulo VARCHAR, NumeroEpisodio INTEGER, " +
                      "CarpetaDestino VARCHAR, RutaArchivo VARCHAR, TamanoBytes BIGINT, FechaUtc BIGINT, Completada INTEGER, Error VARCHAR, TitulosAlternativos VARCHAR);");
            c.Execute("INSERT INTO DescargaHistorial (AniListId, AnimeTitulo, NumeroEpisodio, CarpetaDestino, RutaArchivo, TamanoBytes, FechaUtc, Completada, TitulosAlternativos) " +
                      "VALUES (10, 'Frieren', 3, 'C:\\Anime', 'C:\\Anime\\Ep3.mp4', 1, ?, 1, '');", DateTime.UtcNow.Ticks);
            c.Execute("PRAGMA user_version = 14;");
        }

        using var migrada = new DatabaseService(rutaDb);
        await migrada.InicializarBaseDatosAsync();
        await migrada.GuardarDescargaHistorialAsync(FilaMusica(0, true, 21));
        var filas = await migrada.ObtenerDescargasHistorialAsync();

        filas.Should().HaveCount(2);
        filas.Single(f => f.NumeroEpisodio == 3).Tipo.Should().BeNull("las filas anteriores siguen siendo episodios");
        var musica = filas.Single(f => f.Tipo == DescargaHistorial.TipoMusica);
        musica.TemaClave.Should().Be("OP|OP1|1");
        musica.TemaTitulo.Should().Be("OP1 · We Are!");
    }

    // === Ficha: "Escuchar en la ficha" ===

    [Fact]
    public async Task LaFicha_AbreElPanelYReproduceElTemaPedido()
    {
        var themes = new Mock<IAnimeThemesService>();
        var descargas = new Mock<IAnimeThemesDownloadService>();
        var op = Tema("OP1", "We Are!");
        var ed = Tema("ED1", "Memories");
        themes.Setup(t => t.ObtenerTemasAsync(21, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo> { op, ed });
        descargas.Setup(d => d.EstaDescargado(21, It.IsAny<AnimeThemeInfo>())).Returns(true);
        descargas.Setup(d => d.ObtenerRutaLocalEsperada(21, It.IsAny<AnimeThemeInfo>())).Returns(@"C:\Music\21\x.mp3");
        var escaner = new Mock<IFileScannerService>();
        escaner.Setup(e => e.EscanearEpisodiosAsync(It.IsAny<string>())).ReturnsAsync(new List<EpisodioItem>());
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(It.IsAny<int>())).ReturnsAsync(new List<RegistroEpisodio>());
        var episodios = new Mock<IDownloadService>();
        double p = 0;
        episodios.Setup(d => d.EstaDescargando(It.IsAny<int>(), It.IsAny<int>(), out p)).Returns(false);
        using var reproductor = new FakeAudioTrackPlayer();
        var ficha = new DetalleViewModel(Mock.Of<IAnimeTrackingService>(), db.Object, Mock.Of<IAuthService>(), escaner.Object, Mock.Of<IDialogService>(), episodios.Object,
            animeThemesService: themes.Object, animeThemesDownload: descargas.Object, audioTrackPlayer: reproductor);

        await ficha.InicializarAsync(new AnimeItem { AniListId = 21, Titulo = "ONE PIECE" });
        await ficha.Musica.AbrirMusicaYReproducirAsync("ED|ED1|1");

        ficha.Musica.MostrandoPanelMusica.Should().BeTrue();
        ficha.Musica.TemasMusicales.Single(t => t.Slug == "ED1").Reproduciendo.Should().BeTrue();
        ficha.Musica.TemasMusicales.Single(t => t.Slug == "OP1").Reproduciendo.Should().BeFalse();
    }

    // === Utilidades ===

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        var reloj = Stopwatch.StartNew();
        while (!condicion() && reloj.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        condicion().Should().BeTrue();
    }

    private byte[] GenerarOgg()
    {
        Directory.CreateDirectory(_raiz);
        string ruta = Path.Combine(_raiz, "fuente.ogg");
        var psi = new ProcessStartInfo { FileName = FfmpegLocator.Ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "1", "-c:a", "libopus", ruta })
            psi.ArgumentList.Add(a);
        using var proceso = Process.Start(psi)!;
        proceso.StandardOutput.ReadToEnd();
        proceso.StandardError.ReadToEnd();
        proceso.WaitForExit(30_000);
        return File.ReadAllBytes(ruta);
    }
}
