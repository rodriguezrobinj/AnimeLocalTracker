using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Caché temporal de vistas previas de openings/endings: rutas, guardar (mover), limpiar y fallos de red.
/// Usa carpetas temporales propias: las pruebas nunca tocan la carpeta de datos del usuario.</summary>
public class AnimeThemesVistaPreviaServiceTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"AnimeTracker_VistaPrevia_{Guid.NewGuid():N}");
    private readonly string _musica;
    private readonly string _previas;
    private readonly Mock<IHttpClientFactory> _http = new();
    private readonly AnimeThemeInfo _tema = new() { Slug = "OP1", Tipo = "OP", Version = 1, AudioUrlOgg = "https://a.animethemes.moe/op.ogg" };

    public AnimeThemesVistaPreviaServiceTests()
    {
        _musica = Path.Combine(_raiz, "Music");
        _previas = Path.Combine(_raiz, "MusicPreviews");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true); } catch { /* ignore */ }
    }

    private AnimeThemesDownloadService CrearSut() => new(_http.Object, _musica, _previas);

    private string RutaPrevia(int id = 7) => Path.Combine(_previas, id.ToString(), _tema.NombreArchivoLocal());
    private string RutaGuardada(int id = 7) => Path.Combine(_musica, id.ToString(), _tema.NombreArchivoLocal());

    private void CrearArchivo(string ruta, string contenido = "mp3")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, contenido);
    }

    private sealed class Manejador(HttpStatusCode codigo) : HttpMessageHandler
    {
        public int Llamadas { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Llamadas++;
            return Task.FromResult(new HttpResponseMessage(codigo));
        }
    }

    // === Rutas ===

    [Fact]
    public void ObtenerRutaVistaPrevia_SinArchivo_DeberiaDevolverNulo()
    {
        CrearSut().ObtenerRutaVistaPrevia(7, _tema).Should().BeNull();
    }

    [Fact]
    public void ObtenerRutaVistaPrevia_ConArchivo_DeberiaDevolverSuRutaDentroDeLaCacheTemporal()
    {
        CrearArchivo(RutaPrevia());

        var ruta = CrearSut().ObtenerRutaVistaPrevia(7, _tema);

        ruta.Should().Be(RutaPrevia());
        ruta.Should().StartWith(_previas + Path.DirectorySeparatorChar)
            .And.NotStartWith(_musica + Path.DirectorySeparatorChar, "\"MusicPreviews\" empieza por \"Music\" pero es otra carpeta");
    }

    [Fact]
    public void UnaVistaPrevia_NoDeberiaContarComoDescargada()
    {
        CrearArchivo(RutaPrevia());

        CrearSut().EstaDescargado(7, _tema).Should().BeFalse("la vista previa no es una descarga guardada");
    }

    [Fact]
    public void ObtenerRutaLocalEsperada_DeberiaSeguirUsandoLaCarpetaDeMusica()
    {
        CrearSut().ObtenerRutaLocalEsperada(7, _tema).Should().Be(RutaGuardada());
    }

    // === Guardar (mover) ===

    [Fact]
    public void GuardarVistaPrevia_DeberiaMoverElArchivoALaCarpetaDeMusica()
    {
        CrearArchivo(RutaPrevia(), "contenido-original");

        bool ok = CrearSut().GuardarVistaPrevia(7, _tema);

        ok.Should().BeTrue();
        File.Exists(RutaPrevia()).Should().BeFalse("se mueve, no se copia");
        File.ReadAllText(RutaGuardada()).Should().Be("contenido-original");
    }

    [Fact]
    public void GuardarVistaPrevia_DespuesDeGuardar_ElTemaDebeContarComoDescargado()
    {
        CrearArchivo(RutaPrevia());
        var sut = CrearSut();

        sut.GuardarVistaPrevia(7, _tema);

        sut.EstaDescargado(7, _tema).Should().BeTrue();
        sut.ObtenerRutaVistaPrevia(7, _tema).Should().BeNull();
    }

    [Fact]
    public void GuardarVistaPrevia_SinVistaPrevia_DeberiaDevolverFalso()
    {
        CrearSut().GuardarVistaPrevia(7, _tema).Should().BeFalse();
        File.Exists(RutaGuardada()).Should().BeFalse();
    }

    [Fact]
    public void GuardarVistaPrevia_SiYaEstabaGuardado_NoDeberiaSobrescribirYSiBorrarLaCopiaTemporal()
    {
        CrearArchivo(RutaPrevia(), "temporal");
        CrearArchivo(RutaGuardada(), "ya-guardado");

        bool ok = CrearSut().GuardarVistaPrevia(7, _tema);

        ok.Should().BeTrue();
        File.ReadAllText(RutaGuardada()).Should().Be("ya-guardado");
        File.Exists(RutaPrevia()).Should().BeFalse();
    }

    [Fact]
    public void GuardarVistaPrevia_ConElArchivoAbiertoPorOtroProceso_DeberiaDevolverFalsoSinLanzar()
    {
        CrearArchivo(RutaPrevia());
        using var bloqueo = new FileStream(RutaPrevia(), FileMode.Open, FileAccess.Read, FileShare.None);

        var act = () => CrearSut().GuardarVistaPrevia(7, _tema);

        act.Should().NotThrow();
        CrearSut().GuardarVistaPrevia(7, _tema).Should().BeFalse("Windows no deja mover un archivo en uso");
    }

    // === Limpieza ===

    [Fact]
    public void LimpiarVistasPrevias_DeberiaVaciarLaCacheSinTocarLaMusicaGuardada()
    {
        CrearArchivo(RutaPrevia(7));
        CrearArchivo(RutaPrevia(8));
        CrearArchivo(RutaGuardada(9));

        CrearSut().LimpiarVistasPrevias();

        Directory.Exists(_previas).Should().BeFalse();
        File.Exists(RutaGuardada(9)).Should().BeTrue("las descargas guardadas no son temporales");
    }

    [Fact]
    public void LimpiarVistasPrevias_SinCarpeta_NoDeberiaFallar()
    {
        var act = () => CrearSut().LimpiarVistasPrevias();

        act.Should().NotThrow();
    }

    // === Preparar ===

    [Fact]
    public async Task PrepararVistaPrevia_SiYaEstaPreparada_DeberiaDevolverlaSinBajarNada()
    {
        CrearArchivo(RutaPrevia());
        double ultimo = -1;

        var ruta = await CrearSut().PrepararVistaPreviaAsync(7, _tema, new Progress<double>(p => ultimo = p), CancellationToken.None);

        ruta.Should().Be(RutaPrevia());
        _http.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
        await Task.Delay(50);
        ultimo.Should().Be(1.0);
    }

    [Fact]
    public async Task PrepararVistaPrevia_SiLaDescargaFalla_DeberiaDevolverNuloYNoDejarBasura()
    {
        var manejador = new Manejador(HttpStatusCode.NotFound);
        _http.Setup(f => f.CreateClient("Downloader")).Returns(new HttpClient(manejador));

        var ruta = await CrearSut().PrepararVistaPreviaAsync(7, _tema, null, CancellationToken.None);

        ruta.Should().BeNull();
        manejador.Llamadas.Should().Be(1);
        File.Exists(RutaPrevia()).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(RutaPrevia())!).Should().BeTrue();
        Directory.GetFiles(Path.GetDirectoryName(RutaPrevia())!).Should().BeEmpty("no debe quedar ningún .ogg.tmp");
    }

    [Fact]
    public async Task PrepararVistaPrevia_NoDeberiaEscribirNadaEnLaCarpetaDeMusica()
    {
        _http.Setup(f => f.CreateClient("Downloader")).Returns(new HttpClient(new Manejador(HttpStatusCode.InternalServerError)));

        await CrearSut().PrepararVistaPreviaAsync(7, _tema, null, CancellationToken.None);

        Directory.Exists(_musica).Should().BeFalse();
    }

    [Fact]
    public async Task PrepararVistaPrevia_CancelandoAntesDeEmpezar_DeberiaDevolverNulo()
    {
        _http.Setup(f => f.CreateClient("Downloader")).Returns(new HttpClient(new Manejador(HttpStatusCode.OK)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ruta = await CrearSut().PrepararVistaPreviaAsync(7, _tema, null, cts.Token);

        ruta.Should().BeNull();
    }

    [Fact]
    public async Task DescargarYConvertir_SiFalla_DeberiaInformarDelInicioYDevolverNulo()
    {
        _http.Setup(f => f.CreateClient("Downloader")).Returns(new HttpClient(new Manejador(HttpStatusCode.NotFound)));
        var avances = new System.Collections.Concurrent.ConcurrentBag<double>();

        var ruta = await CrearSut().DescargarYConvertirAsync(7, _tema, new Progress<double>(avances.Add), CancellationToken.None);

        ruta.Should().BeNull();
        File.Exists(RutaGuardada()).Should().BeFalse();
    }
}
