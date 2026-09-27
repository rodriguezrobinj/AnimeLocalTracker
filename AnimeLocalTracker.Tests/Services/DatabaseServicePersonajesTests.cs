using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Copia local de los personajes de AniList (migración v12, guardado por anime y borrado total). Usa una base temporal.</summary>
public class DatabaseServicePersonajesTests : IDisposable
{
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServicePersonajesTests()
    {
        _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Personajes_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    private static PersonajeAnime P(int animeId, int personajeId, string nombre) => new()
    {
        AnimeId = animeId, PersonajeId = personajeId, Nombre = nombre, NombreNativo = "夜神月", Alternativos = "Kira | Light Asahi",
        ImagenUrl = "https://s4.anilist.co/x.png", Genero = "Male", Edad = "17-23", Rol = "MAIN", Favoritos = 20349
    };

    [Fact]
    public async Task Migracion_CreaLasTablasYElIndiceUnico()
    {
        await _sut.InicializarBaseDatosAsync();

        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(12);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'PersonajeAnime';").Should().Be(1);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'PersonajesAnimeSync';").Should().Be(1);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_PersonajeAnime_Unico';").Should().Be(1);
    }

    [Fact]
    public async Task Guardar_YObtener_ConservaTodosLosCampos()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarPersonajesAsync(new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PersonajeAnime>>
        {
            [1535] = [P(1535, 80, "Light Yagami")]
        });

        var guardados = await _sut.ObtenerPersonajesAsync([1535]);
        var p = guardados.Should().ContainSingle().Subject;
        (p.AnimeId, p.PersonajeId, p.Nombre, p.NombreNativo).Should().Be((1535, 80, "Light Yagami", "夜神月"));
        (p.Alternativos, p.ImagenUrl, p.Genero, p.Edad, p.Rol, p.Favoritos).Should().Be(("Kira | Light Asahi", "https://s4.anilist.co/x.png", "Male", "17-23", "MAIN", 20349));
    }

    [Fact]
    public async Task Guardar_MarcaElAnimeComoConsultado_AunqueNoTengaPersonajes()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarPersonajesAsync(new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PersonajeAnime>> { [77] = [] });

        var marcas = await _sut.ObtenerMarcasPersonajesAsync([77, 78]);
        marcas.Should().ContainSingle().Which.AnimeId.Should().Be(77, "el 78 no se consultó");
        marcas[0].FechaUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Guardar_ReemplazaLosPersonajesDelAnime_SinTocarLosDeOtros()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarPersonajesAsync(new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PersonajeAnime>>
        {
            [1] = [P(1, 10, "Viejo Uno"), P(1, 11, "Viejo Dos")],
            [2] = [P(2, 20, "Otro anime")]
        });

        await _sut.GuardarPersonajesAsync(new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PersonajeAnime>>
        {
            [1] = [P(1, 12, "Nuevo")]
        });

        var todos = await _sut.ObtenerPersonajesAsync([1, 2]);
        todos.Where(p => p.AnimeId == 1).Select(p => p.Nombre).Should().Equal("Nuevo");
        todos.Where(p => p.AnimeId == 2).Select(p => p.Nombre).Should().Equal("Otro anime");
    }

    [Fact]
    public async Task Guardar_ElMismoPersonajeEnDosAnimes_SeGuardaEnCadaUno_PeroNoDuplicadoEnElMismo()
    {
        await _sut.InicializarBaseDatosAsync();

        await _sut.GuardarPersonajesAsync(new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PersonajeAnime>>
        {
            [1] = [P(1, 17, "Naruto"), P(1, 17, "Naruto repetido")],
            [2] = [P(2, 17, "Naruto")]
        });

        var todos = await _sut.ObtenerPersonajesAsync([1, 2]);
        todos.Should().HaveCount(2);
        todos.Count(p => p.AnimeId == 1).Should().Be(1, "el índice único (AnimeId, PersonajeId) descarta el repetido");
    }

    [Fact]
    public async Task Obtener_SinIds_DevuelveVacioSinConsultar()
    {
        await _sut.InicializarBaseDatosAsync();

        (await _sut.ObtenerPersonajesAsync([])).Should().BeEmpty();
        (await _sut.ObtenerMarcasPersonajesAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task VaciarBiblioteca_TambienBorraLosPersonajes()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarPersonajesAsync(new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<PersonajeAnime>> { [1] = [P(1, 10, "Uno")] });

        await _sut.VaciarBibliotecaAsync();

        (await _sut.ObtenerPersonajesAsync([1])).Should().BeEmpty();
        (await _sut.ObtenerMarcasPersonajesAsync([1])).Should().BeEmpty();
    }
}
