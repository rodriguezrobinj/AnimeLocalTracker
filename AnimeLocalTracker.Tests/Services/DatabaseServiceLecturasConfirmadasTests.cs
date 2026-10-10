using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Las lecturas iban por la misma conexión que las escrituras y veían lo que una transacción abierta llevaba escrito sin
/// confirmar: si esa transacción fallaba, la pantalla se quedaba con datos que nunca existieron. Ahora leen por su propia
/// conexión: solo ven lo confirmado y no esperan a que termine un guardado largo.
/// </summary>
public class DatabaseServiceLecturasConfirmadasTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Lecturas_{Guid.NewGuid():N}");
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceLecturasConfirmadasTests()
    {
        Directory.CreateDirectory(_carpeta);
        _rutaDb = Path.Combine(_carpeta, "biblioteca.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        SQLiteAsyncConnection.ResetPool();
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>Cada lectura y si devuelve lo confirmado (lo sembrado), no lo que la transacción ajena lleva a medias.</summary>
    private static readonly Dictionary<string, Func<DatabaseService, Task<bool>>> Lecturas = new()
    {
        ["ObtenerTodosLosAnimes"] = async s => await s.ObtenerTodosLosAnimesAsync() is [{ Titulo: "Semilla" }],
        ["ObtenerAnimesLigeros"] = async s => await s.ObtenerAnimesLigerosAsync() is [{ Titulo: "Semilla" }],
        ["ObtenerAnimePorId"] = async s => (await s.ObtenerAnimePorIdAsync(1))?.Titulo == "Semilla",
        ["ExisteAnime"] = async s => !await s.ExisteAnimeAsync(2),
        ["ObtenerConservarVideos"] = async s => !await s.ObtenerConservarVideosAsync(1),
        ["ObtenerRegistrosPorAnime"] = async s => (await s.ObtenerRegistrosPorAnimeAsync(1)).Count == 1,
        ["ObtenerTodosLosRegistros"] = async s => (await s.ObtenerTodosLosRegistrosAsync()).Count == 1,
        ["ObtenerEpisodiosVistosPorAnime"] = async s => (await s.ObtenerEpisodiosVistosPorAnimeAsync()).GetValueOrDefault(1) == 1,
        ["ObtenerAjusteAudio"] = async s => (await s.ObtenerAjusteAudioAsync(1, 1))?.Volumen == 40,
        ["ObtenerDescargasHistorial"] = async s => (await s.ObtenerDescargasHistorialAsync()).Count == 1,
    };

    public static TheoryData<string> NombresDeLecturas => new(Lecturas.Keys);

    private async Task SembrarAsync()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Semilla" });
        await _sut.GuardarRegistroEpisodioAsync(new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true });
        await _sut.GuardarAjusteAudioAsync(new AjusteAudio { AniListId = 1, NumeroEpisodio = 1, Volumen = 40 });
        await _sut.GuardarDescargaHistorialAsync(new DescargaHistorial { AniListId = 1, NumeroEpisodio = 1, Completada = true, FechaUtc = DateTime.UtcNow });
    }

    [Theory]
    [MemberData(nameof(NombresDeLecturas))]
    public async Task UnaLecturaDuranteUnaTransaccionAbierta_SoloVeLoConfirmado_YNoEsperaAQueTermine(string nombre)
    {
        await SembrarAsync();

        // Otro guardado va por la mitad: ha cambiado todo, pero aún no ha confirmado (y al final falla).
        using var dentro = new ManualResetEventSlim();
        using var seguir = new ManualResetEventSlim();
        var ajena = new SQLiteAsyncConnection(_rutaDb).RunInTransactionAsync(db =>   // la conexión de escritura del servicio
        {
            db.Execute("UPDATE AnimeItem SET Titulo = 'sin confirmar', ConservarVideos = 1");
            db.Insert(new AnimeItem { AniListId = 2, Titulo = "sin confirmar" });
            db.Execute("DELETE FROM RegistroEpisodio");
            db.Execute("DELETE FROM AjusteAudio");
            db.Execute("DELETE FROM DescargaHistorial");
            dentro.Set();
            seguir.Wait(TimeSpan.FromSeconds(15));
            throw new InvalidOperationException("la transacción ajena falla");
        });
        dentro.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        try
        {
            var lectura = Lecturas[nombre](_sut);
            (await Task.WhenAny(lectura, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(lectura, "leer no espera a que termine el guardado");
            (await lectura).Should().BeTrue($"{nombre} no debe ver lo que otra transacción aún no ha confirmado");
        }
        finally
        {
            seguir.Set();
            await ajena.Invoking(t => t).Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task UnaLecturaVeLoQueSeAcabaDeGuardar()
    {
        await SembrarAsync();
        (await _sut.ObtenerAnimePorIdAsync(1))!.Titulo.Should().Be("Semilla");   // la conexión de lectura ya está abierta

        for (int i = 0; i < 50; i++)
        {
            await _sut.ActualizarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = $"Vuelta {i}" });
            (await _sut.ObtenerAnimePorIdAsync(1))!.Titulo.Should().Be($"Vuelta {i}");
        }
    }
}
