using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Música descargada con nombre legible ("OP1 - We Are!.mp3") y la organización de lo que se bajó antes: renombrar los
/// nombres técnicos antiguos y ponerles etiquetas y portada sin volver a descargar ni a convertir. Carpetas temporales.
/// </summary>
public sealed class OrganizarMusicaTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"AnimeTracker_OrganizarMusica_{Guid.NewGuid():N}");
    private readonly string _musica;
    private readonly string _portadas;

    public OrganizarMusicaTests()
    {
        _musica = Path.Combine(_raiz, "Music");
        _portadas = Path.Combine(_raiz, "Covers");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true); } catch { /* ignore */ }
    }

    private AnimeThemesDownloadService CrearSut() =>
        new(Mock.Of<IHttpClientFactory>(), _musica, Path.Combine(_raiz, "Previews"), Path.Combine(_raiz, "Refs"), _portadas);

    private static AnimeThemeInfo Tema(string tipo, string slug, int version = 1, string titulo = "", string? rango = null) =>
        new() { Slug = slug, Tipo = tipo, Version = version, TituloCancion = titulo, Artistas = "Artista", NombreAnime = "One Piece",
                RangoEpisodios = rango, AudioUrlOgg = "https://a.animethemes.moe/x.ogg" };

    private string Crear(int id, string nombre, string contenido = "mp3")
    {
        string ruta = Path.Combine(_musica, id.ToString(), nombre);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, contenido);
        return ruta;
    }

    private string[] ArchivosDe(int id) => Directory.GetFiles(Path.Combine(_musica, id.ToString())).Select(f => Path.GetFileName(f)).ToArray();

    // === Nombre legible ===

    [Theory]
    [InlineData("OP1", 1, "We Are!", "OP1 - We Are!.mp3")]
    [InlineData("OP1", 2, "We Are!", "OP1 v2 - We Are!.mp3")]
    [InlineData("ED1-TV", 1, "Re:Re:", "ED1-TV - Re Re.mp3")]
    [InlineData("OP3", 1, "Kokoro/Heart?  ", "OP3 - Kokoro Heart.mp3")]
    [InlineData("ED2", 1, "", "ED2.mp3")]
    [InlineData("ED2", 3, "   ", "ED2 v3.mp3")]
    [InlineData("OP5", 1, "Fin.", "OP5 - Fin.mp3")]
    public void NombreArchivoLegible_SeLeeBienYEsValidoEnWindows(string slug, int version, string titulo, string esperado)
    {
        string nombre = Tema(slug[..2], slug, version, titulo).NombreArchivoLegible();

        nombre.Should().Be(esperado);
        nombre.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1);
    }

    [Fact]
    public void NombreArchivoLegible_ConUnTituloLarguisimo_SeRecorta()
    {
        Tema("OP", "OP1", titulo: new string('a', 500)).NombreArchivoLegible().Length.Should().BeLessThan(110);
    }

    [Theory]
    [InlineData("OP1 - We Are!.mp3", "OP", "OP1", 1)]
    [InlineData("ED1-TV v2 - Título - con guiones.mp3", "ED", "ED1-TV", 2)]
    [InlineData("OP1.mp3", "OP", "OP1", 1)]
    [InlineData("ED3 v4.mp3", "ED", "ED3", 4)]
    [InlineData("IN1 - Canción insertada.mp3", "IN", "IN1", 1)]
    [InlineData("ED1-2 - OVA.mp3", "ED", "ED1-2", 1)]
    public void TryParseNombreLegible_ReconoceSlugYVersion(string archivo, string tipo, string slug, int version)
    {
        AnimeThemesDownloadService.TryParseNombreLegible(archivo, out var tema).Should().BeTrue();

        tema!.Tipo.Should().Be(tipo);
        tema.Slug.Should().Be(slug);
        tema.Version.Should().Be(version);
        tema.RangoEpisodios.Should().BeNull("el nombre legible no lleva el rango");
    }

    [Theory]
    [InlineData("notas.mp3")]
    [InlineData("Mi playlist - favoritas.mp3")]
    [InlineData("OP1 vX - algo.mp3")]
    [InlineData("OP1 v2 extra - algo.mp3")]
    public void TryParseNombreLegible_UnMp3CualquieraDelUsuario_NoSeConfunde(string archivo)
    {
        AnimeThemesDownloadService.TryParseNombreLegible(archivo, out _).Should().BeFalse();
    }

    [Fact]
    public void ElNombreLegible_YSuParseo_DanLaMismaClaveQueElTema()
    {
        var tema = Tema("ED", "ED1-TV", 2, "Título: con cosas / raras");

        AnimeThemesDownloadService.TryParseNombreCualquiera(tema.NombreArchivoLegible(), out var local).Should().BeTrue();

        local!.ClaveEstable().Should().Be(tema.ClaveEstable());
    }

    // === Descargas antiguas: siguen funcionando y se ponen al día ===

    [Fact]
    public void UnMp3Antiguo_SeRenombraAlNombreLegible()
    {
        Crear(21, "OP_OP1_v1_ep1-47.mp3", "we are");
        var tema = Tema("OP", "OP1", titulo: "We Are!", rango: "1-47");
        var sut = CrearSut();

        sut.ReconciliarDescargasLocales(21, [tema]).Should().Be(1);

        ArchivosDe(21).Should().Equal("OP1 - We Are!.mp3");
        File.ReadAllText(sut.ObtenerRutaLocalEsperada(21, tema)).Should().Be("we are");
    }

    [Fact]
    public void SiAnimeThemesCorrigeElTitulo_ElArchivoSeRenombra()
    {
        Crear(21, "OP1 - We Are.mp3");

        CrearSut().ReconciliarDescargasLocales(21, [Tema("OP", "OP1", titulo: "We Are!")]).Should().Be(1);

        ArchivosDe(21).Should().Equal("OP1 - We Are!.mp3");
    }

    [Fact]
    public void Reconciliar_NoTocaLosMp3QueNoSonDeLaApp()
    {
        Crear(21, "Mi mezcla - favoritas.mp3");
        Crear(21, "notas.mp3");

        CrearSut().ReconciliarDescargasLocales(21, [Tema("OP", "OP1", titulo: "We Are!")]).Should().Be(0);

        ArchivosDe(21).Should().BeEquivalentTo("Mi mezcla - favoritas.mp3", "notas.mp3");
    }

    [Fact]
    public void ListarDescargasLocales_ReconoceLosDosFormatosYIgnoraLoDemas()
    {
        Crear(21, "OP_OP1_v1_ep1-47.mp3");
        Crear(21, "ED2 v2 - Memories.mp3");
        Crear(21, "notas.mp3");

        var locales = CrearSut().ListarDescargasLocales(21);

        locales.Select(t => t.ClaveEstable()).Should().BeEquivalentTo("OP|OP1|1", "ED|ED2|2");
        locales.Single(t => t.Slug == "OP1").RangoEpisodios.Should().Be("1-47");
    }

    [Fact]
    public void ContarPendientes_CuentaLosNombresAntiguosYLosQueNoTienenEtiquetas()
    {
        Crear(21, "OP_OP1_v1_ep1-47.mp3");      // nombre antiguo
        Crear(21, "ED1 - Memories.mp3");        // legible pero sin etiquetas
        Crear(21, "notas.mp3");                 // no es de la app
        Crear(5, "OP1 - Algo.mp3");
        Directory.CreateDirectory(Path.Combine(_musica, "9"));   // carpeta vacía
        Directory.CreateDirectory(Path.Combine(_musica, "cosas")); // no es un anime
        var sut = CrearSut();

        sut.ContarPendientesDeOrganizar(21).Should().Be(2);
        sut.AnimesConDescargas().Should().BeEquivalentTo([5, 21]);
    }

    // === Etiquetar lo ya descargado (ffmpeg real) ===

    [Fact]
    public async Task Etiquetar_PoneTituloArtistaAlbumYPortada_SinTocarElAudio()
    {
        string ruta = CrearMp3SinEtiquetas(21, "OP_OP1_v1_ep1-47.mp3");
        CrearPortada(21);
        var tema = Tema("OP", "OP1", titulo: "We Are!", rango: "1-47");
        var sut = CrearSut();
        double duracionAntes = Duracion(ruta);
        AnimeThemesDownloadService.TieneTituloId3(ruta).Should().BeFalse();

        sut.ReconciliarDescargasLocales(21, [tema]);
        int etiquetados = await sut.EtiquetarDescargasLocalesAsync(21, [tema]);

        etiquetados.Should().Be(1);
        string final = sut.ObtenerRutaLocalEsperada(21, tema);
        Path.GetFileName(final).Should().Be("OP1 - We Are!.mp3");
        AnimeThemesDownloadService.TieneTituloId3(final).Should().BeTrue();
        string info = Sondear(final);
        info.Should().Contain("title=We Are!").And.Contain("artist=Artista").And.Contain("album=One Piece").And.Contain("codec_type=video");
        Duracion(final).Should().BeApproximately(duracionAntes, 0.1, "el audio se copia tal cual");
        Directory.GetFiles(Path.Combine(_musica, "21"), "*.part").Should().BeEmpty();
        sut.ContarPendientesDeOrganizar(21).Should().Be(0);

        (await sut.EtiquetarDescargasLocalesAsync(21, [tema])).Should().Be(0, "ya estaba etiquetado");
    }

    [Fact]
    public async Task Etiquetar_UnArchivoEnUso_SeDejaComoEstaYSeReintentaDespues()
    {
        string ruta = CrearMp3SinEtiquetas(21, "OP1 - We Are!.mp3");
        var tema = Tema("OP", "OP1", titulo: "We Are!");
        var sut = CrearSut();
        byte[] antes = File.ReadAllBytes(ruta);

        using (new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.Read)) // como cuando está sonando
        {
            (await sut.EtiquetarDescargasLocalesAsync(21, [tema])).Should().Be(0);
        }

        File.ReadAllBytes(ruta).Should().Equal(antes);
        Directory.GetFiles(Path.Combine(_musica, "21"), "*.part").Should().BeEmpty();
        (await sut.EtiquetarDescargasLocalesAsync(21, [tema])).Should().Be(1, "al soltarse se puede etiquetar");
    }

    [Fact]
    public void TieneTituloId3_ConArchivosRarosNoFalla()
    {
        CrearSut(); // la carpeta
        AnimeThemesDownloadService.TieneTituloId3(Crear(1, "vacio.mp3", "")).Should().BeFalse();
        AnimeThemesDownloadService.TieneTituloId3(Crear(1, "texto.mp3", "ID3 esto no es una cabecera")).Should().BeFalse();
        AnimeThemesDownloadService.TieneTituloId3(Path.Combine(_raiz, "no-existe.mp3")).Should().BeFalse();
    }

    // === "Organizar mi música" en toda la biblioteca ===

    [Fact]
    public async Task OrganizarTodo_RecorreLosAnimesPendientesYResume()
    {
        var themes = new Mock<IAnimeThemesService>();
        var descargas = new Mock<IAnimeThemesDownloadService>();
        var catalogo = new List<AnimeThemeInfo> { Tema("OP", "OP1", titulo: "We Are!") };
        descargas.Setup(d => d.AnimesConDescargas()).Returns([21, 5, 9]);
        descargas.Setup(d => d.ContarPendientesDeOrganizar(21)).Returns(3);
        descargas.Setup(d => d.ContarPendientesDeOrganizar(5)).Returns(1);
        descargas.Setup(d => d.ContarPendientesDeOrganizar(9)).Returns(0);
        themes.Setup(t => t.ObtenerTemasAsync(21, It.IsAny<CancellationToken>())).ReturnsAsync(catalogo);
        themes.Setup(t => t.ObtenerTemasAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(new List<AnimeThemeInfo>()); // ya no está en AnimeThemes
        descargas.Setup(d => d.ReconciliarDescargasLocales(21, catalogo)).Returns(2)
            .Callback(() => descargas.Setup(d => d.ContarPendientesDeOrganizar(21)).Returns(0));
        descargas.Setup(d => d.EtiquetarDescargasLocalesAsync(21, catalogo, It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var sut = new OrganizadorMusicaService(themes.Object, descargas.Object) { PausaTrasConsultarApi = TimeSpan.Zero };
        var avances = new List<(int, int)>();

        var pendientes = sut.ContarPendientes();
        var r = await sut.OrganizarTodoAsync(pendientes.Animes, new SincronoProgress(avances.Add), CancellationToken.None);

        pendientes.Canciones.Should().Be(4);
        pendientes.Animes.Should().BeEquivalentTo([21, 5]);
        r.Should().Be(new ResultadoOrganizarMusica(Renombradas: 2, Etiquetadas: 3, SinDatos: 1, Cancelado: false));
        avances.Should().Equal((0, 2), (1, 2), (2, 2));
        themes.Verify(t => t.ObtenerTemasAsync(9, It.IsAny<CancellationToken>()), Times.Never, "sin nada pendiente no se pregunta");
    }

    [Fact]
    public async Task OrganizarTodo_SePuedeCancelar()
    {
        var themes = new Mock<IAnimeThemesService>();
        var descargas = new Mock<IAnimeThemesDownloadService>();
        using var cts = new CancellationTokenSource();
        themes.Setup(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { cts.Cancel(); return new List<AnimeThemeInfo> { Tema("OP", "OP1") }; });
        var sut = new OrganizadorMusicaService(themes.Object, descargas.Object) { PausaTrasConsultarApi = TimeSpan.Zero };

        var r = await sut.OrganizarTodoAsync([1, 2, 3], null, cts.Token);

        r.Cancelado.Should().BeTrue();
        themes.Verify(t => t.ObtenerTemasAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Configuracion_SinNadaPendiente_SoloLoDice()
    {
        var organizador = new Mock<IOrganizadorMusicaService>();
        organizador.Setup(o => o.ContarPendientes()).Returns(new PendientesMusica(0, []));
        var dialogos = new Mock<IDialogService>();
        var vm = CrearConfiguracion(organizador.Object, dialogos.Object);

        await vm.OrganizarMusicaCommand.ExecuteAsync(null);

        dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        organizador.Verify(o => o.OrganizarTodoAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<IProgress<(int, int)>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Configuracion_ConfirmandoOrganizaYMuestraElResumen()
    {
        var organizador = new Mock<IOrganizadorMusicaService>();
        organizador.Setup(o => o.ContarPendientes()).Returns(new PendientesMusica(138, [21, 5]));
        organizador.Setup(o => o.OrganizarTodoAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<IProgress<(int, int)>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResultadoOrganizarMusica(136, 138, 2, false));
        var dialogos = new Mock<IDialogService>();
        dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        var vm = CrearConfiguracion(organizador.Object, dialogos.Object);

        await vm.OrganizarMusicaCommand.ExecuteAsync(null);

        dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("138") && m.Contains('2')), true, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("136") && m.Contains("138")), false, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        vm.OrganizandoMusica.Should().BeFalse();
    }

    private static ConfiguracionViewModel CrearConfiguracion(IOrganizadorMusicaService organizador, IDialogService dialogos)
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.ObtenerConfiguracion()).Returns(new AppSettings());
        var db = Mock.Of<IDatabaseService>();
        return new ConfiguracionViewModel(settings.Object, Mock.Of<IAuthService>(), db, dialogos, new CacheMaintenanceService(db),
            Mock.Of<IPluginService>(), organizadorMusica: organizador);
    }

    // === Utilidades con el ffmpeg embebido ===

    private sealed class SincronoProgress(Action<(int, int)> accion) : IProgress<(int, int)>
    {
        public void Report((int, int) value) => accion(value);
    }

    private static readonly string[] Silencio = ["-y", "-hide_banner", "-loglevel", "error"];

    private string CrearMp3SinEtiquetas(int id, string nombre)
    {
        string ruta = Path.Combine(_musica, id.ToString(), nombre);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        Ffmpeg(FfmpegLocator.Ffmpeg, [.. Silencio, "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-codec:a", "libmp3lame", "-q:a", "5", "-f", "mp3", ruta]);
        return ruta;
    }

    private void CrearPortada(int id)
    {
        Directory.CreateDirectory(_portadas);
        Ffmpeg(FfmpegLocator.Ffmpeg, [.. Silencio, "-f", "lavfi", "-i", "color=c=orange:s=100x150", "-frames:v", "1", Path.Combine(_portadas, id + ".jpg")]);
    }

    private static string Sondear(string ruta) =>
        Ffmpeg(FfmpegLocator.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type:format_tags=title,artist,album", "-of", "compact", ruta]).Replace("tag:", "");

    private static double Duracion(string ruta) =>
        double.Parse(Ffmpeg(FfmpegLocator.Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", ruta]).Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

    private static string Ffmpeg(string exe, string[] args)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string salida = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return salida;
    }
}
