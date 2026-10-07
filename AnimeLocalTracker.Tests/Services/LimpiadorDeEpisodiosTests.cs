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
        IEnumerable<EpisodioItem>? extraEscaneados = null, bool confirma = true, bool conservarVideos = false)
    {
        var escaneados = conArchivo.Select(n => new EpisodioItem { NumeroEpisodio = n, RutaCompleta = Archivo(n) }).ToList();
        if (extraEscaneados != null) escaneados.AddRange(extraEscaneados);

        _ajustes.Setup(a => a.ObtenerConfiguracion()).Returns(new AppSettings { ModoEliminarTrasVer = modo, EpisodiosAConservar = conservar });
        _db.Setup(d => d.ObtenerAnimePorIdAsync(Id)).ReturnsAsync(new AnimeItem { AniListId = Id, Titulo = "Frieren", TotalEpisodios = total, RutaCarpeta = _carpeta, ConservarVideos = conservarVideos });
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

    [Theory]
    [InlineData(ModoEliminarTrasVerValores.Automatico)]
    [InlineData(ModoEliminarTrasVerValores.ConsumoLigero)]
    [InlineData(ModoEliminarTrasVerValores.AlCompletarSerie)]
    public async Task AnimeProtegido_NoSeBorraNadaEnNingunModo(string modo)
    {
        // 6 episodios vistos con archivo y el 6 es el último: cualquiera de los tres modos habría borrado algo.
        var sut = Preparar(modo, conArchivo: [1, 2, 3, 4, 5, 6], vistos: [1, 2, 3, 4, 5, 6], total: 6, conservar: 1, conservarVideos: true);

        await sut.AplicarTrasVerAsync(Id, 6);

        Enumerable.Range(1, 6).Should().OnlyContain(n => Existe(n), "el anime está protegido");
        VerificarRegistroConservado(); // ningún registro tocado
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SiSeProtegeMientrasEsperaLaConfirmacion_NoBorraNada()
    {
        var sut = Preparar(ModoEliminarTrasVerValores.AlCompletarSerie, conArchivo: [1, 2, 3], vistos: [1, 2, 3], total: 3);
        // El diálogo está abierto: el usuario activa "Conservar los videos" antes de pulsar Aceptar.
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<string>(), It.IsAny<string>()))
            .Returns(() =>
            {
                _db.Setup(d => d.ObtenerConservarVideosAsync(Id)).ReturnsAsync(true);
                return Task.FromResult(true);
            });

        await sut.AplicarTrasVerAsync(Id, 3);

        Enumerable.Range(1, 3).Should().OnlyContain(n => Existe(n), "la protección se activó antes de borrar");
        VerificarRegistroConservado();
    }

    [Fact]
    public async Task AnimeSinProteger_ConElMismoEscenario_SiBorra()
    {
        // Control: el mismo escenario sin la protección sí borra, así que la prueba anterior no pasa por casualidad.
        var sut = Preparar(ModoEliminarTrasVerValores.Automatico, conArchivo: [1, 2, 3], vistos: [1, 2, 3], conservarVideos: false);

        await sut.AplicarTrasVerAsync(Id, 3);

        Existe(3).Should().BeFalse();
    }
}
