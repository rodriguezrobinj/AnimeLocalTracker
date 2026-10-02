using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

public sealed class EliminacionAnimeTests : IDisposable
{
    private readonly Mock<IDialogService> _dialogos = new();
    private readonly Mock<IDatabaseService> _database = new();
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "alt-eliminacion-" + Guid.NewGuid().ToString("N"));

    public EliminacionAnimeTests()
    {
        Directory.CreateDirectory(_raiz);
        _database.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_raiz)) Directory.Delete(_raiz, recursive: true);
    }

    private void ElUsuarioResponde(bool respuesta) =>
        _dialogos.Setup(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(respuesta);

    private string CrearCarpeta(string nombre)
    {
        string ruta = Path.Combine(_raiz, nombre);
        Directory.CreateDirectory(ruta);
        File.WriteAllText(Path.Combine(ruta, "01.mkv"), "x");
        return ruta;
    }

    private void VerificarPreguntas(int veces) =>
        _dialogos.Verify(d => d.MostrarDialogoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(veces));

    private void VerificarAvisos(int veces) =>
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(veces));

    // === Qué carpetas se pueden borrar ===

    [Fact]
    public void CarpetaSegura_UnaCarpetaSoloDeEseAnime_SePuedeBorrar() =>
        EliminacionAnime.CarpetaSeguraParaBorrar(@"D:\Anime\Bleach", OtrasCarpetas).Should().BeTrue();

    // CA1861: arrays constantes reutilizados como campos estáticos
    private static readonly string?[] OtrasCarpetas = { @"D:\Anime\Naruto", null, "" };
    private static readonly string?[] CarpetaDeNombreParecido = { @"D:\Anime\Bleach TYBW" };

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"D:")]
    public void CarpetaSegura_LaRaizDeUnaUnidad_NoSeBorra(string carpeta) =>
        EliminacionAnime.CarpetaSeguraParaBorrar(carpeta, Array.Empty<string?>()).Should().BeFalse();

    [Theory]
    [InlineData(@"D:\Anime\Bleach")]            // otra temporada en la misma carpeta
    [InlineData(@"d:\anime\bleach\")]           // la misma, escrita distinto
    [InlineData(@"D:\Anime\Bleach\Temporada 2")] // otra temporada dentro
    public void CarpetaSegura_SiOtroAnimeUsaEsaCarpetaOUnaDeDentro_NoSeBorra(string carpetaDelOtro) =>
        EliminacionAnime.CarpetaSeguraParaBorrar(@"D:\Anime\Bleach", new[] { carpetaDelOtro }).Should().BeFalse();

    [Fact]
    public void CarpetaSegura_UnaCarpetaConNombreParecido_NoCuentaComoCompartida() =>
        EliminacionAnime.CarpetaSeguraParaBorrar(@"D:\Anime\Bleach", CarpetaDeNombreParecido).Should().BeTrue();

    [Fact]
    public void CarpetaSegura_LaCarpetaDelUsuarioOUnaQueLaContiene_NoSeBorra()
    {
        string perfil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        EliminacionAnime.CarpetaSeguraParaBorrar(perfil, Array.Empty<string?>()).Should().BeFalse();
        EliminacionAnime.CarpetaSeguraParaBorrar(Path.GetDirectoryName(perfil)!, Array.Empty<string?>()).Should().BeFalse();
    }

    // === El flujo completo ===

    [Fact]
    public async Task ConfirmarYEliminar_SiElUsuarioCancela_NoEliminaNada()
    {
        ElUsuarioResponde(false);
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", RutaCarpeta = CrearCarpeta("Bleach") };

        bool eliminado = await EliminacionAnime.ConfirmarYEliminarAsync(anime, _dialogos.Object, _database.Object);

        eliminado.Should().BeFalse();
        _database.Verify(d => d.EliminarAnimeAsync(It.IsAny<AnimeItem>()), Times.Never);
        Directory.Exists(anime.RutaCarpeta).Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmarYEliminar_SinCarpeta_NoPreguntaPorLosArchivos()
    {
        ElUsuarioResponde(true);
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", RutaCarpeta = "" };

        bool eliminado = await EliminacionAnime.ConfirmarYEliminarAsync(anime, _dialogos.Object, _database.Object);

        eliminado.Should().BeTrue();
        _database.Verify(d => d.EliminarAnimeAsync(anime), Times.Once);
        VerificarPreguntas(1);
    }

    [Fact]
    public async Task ConfirmarYEliminar_ConCarpetaPropia_LaBorraSiElUsuarioQuiere()
    {
        ElUsuarioResponde(true);
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", RutaCarpeta = CrearCarpeta("Bleach") };

        await EliminacionAnime.ConfirmarYEliminarAsync(anime, _dialogos.Object, _database.Object);

        Directory.Exists(anime.RutaCarpeta).Should().BeFalse();
        VerificarPreguntas(2);
        VerificarAvisos(0);
    }

    [Fact]
    public async Task ConfirmarYEliminar_SiOtroAnimeComparteLaCarpeta_LaConservaYAvisa()
    {
        ElUsuarioResponde(true);
        string carpeta = CrearCarpeta("Bleach");
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", RutaCarpeta = carpeta };
        _database.Setup(d => d.ObtenerAnimesLigerosAsync()).ReturnsAsync(new List<AnimeItem>
        {
            anime,
            new() { AniListId = 2, Titulo = "Bleach TYBW", RutaCarpeta = carpeta }
        });

        bool eliminado = await EliminacionAnime.ConfirmarYEliminarAsync(anime, _dialogos.Object, _database.Object);

        eliminado.Should().BeTrue();
        Directory.Exists(carpeta).Should().BeTrue();
        VerificarPreguntas(1);
        VerificarAvisos(1);
    }

    [Fact]
    public async Task ConfirmarYEliminar_SiLaCarpetaNoSePuedeBorrar_Avisa()
    {
        ElUsuarioResponde(true);
        string carpeta = CrearCarpeta("Bleach");
        var anime = new AnimeItem { AniListId = 1, Titulo = "Bleach", RutaCarpeta = carpeta };

        // Un video abierto (como en el reproductor) impide borrar la carpeta.
        using (new FileStream(Path.Combine(carpeta, "01.mkv"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await EliminacionAnime.ConfirmarYEliminarAsync(anime, _dialogos.Object, _database.Object);
        }

        Directory.Exists(carpeta).Should().BeTrue();
        VerificarAvisos(1);
    }
}
