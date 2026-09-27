using System;
using System.IO;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Página de animeav1 verificada por anime (migración v14): se guarda y sobrevive a reabrir la base.</summary>
public class DatabaseServiceMediaAnimeAv1Tests : IDisposable
{
    private readonly string _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_MediaAv1_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SQLiteAsyncConnection.ResetPool();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    [Fact]
    public async Task Migracion_CreaLaTablaYSubeLaVersion()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(14);
        conexion.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='MediaAnimeAv1Verificado';").Should().Be(1);
    }

    [Fact]
    public async Task Guardar_SobreviveAReabrirLaBaseYSeSobrescribe()
    {
        using (var sut = new DatabaseService(_rutaDb))
        {
            await sut.InicializarBaseDatosAsync();
            await sut.GuardarMediaAnimeAv1Async(new MediaAnimeAv1Verificado { AniListId = 146065, Slug = "mushoku-tensei-3", MalId = 59193, VerificadoUtc = DateTime.UtcNow });
            await sut.GuardarMediaAnimeAv1Async(new MediaAnimeAv1Verificado { AniListId = 146065, Slug = "mushoku-tensei-iii", MalId = 59193, VerificadoUtc = DateTime.UtcNow });
        }

        using var reabierta = new DatabaseService(_rutaDb);
        await reabierta.InicializarBaseDatosAsync();
        var guardada = await reabierta.ObtenerMediaAnimeAv1Async(146065);

        guardada.Should().NotBeNull();
        guardada!.Slug.Should().Be("mushoku-tensei-iii");
        guardada.MalId.Should().Be(59193);
        (await reabierta.ObtenerMediaAnimeAv1Async(1)).Should().BeNull();
    }

    [Fact]
    public async Task Guardar_SinSlug_NoGuardaNada()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();

        await sut.GuardarMediaAnimeAv1Async(new MediaAnimeAv1Verificado { AniListId = 5, Slug = " " });

        (await sut.ObtenerMediaAnimeAv1Async(5)).Should().BeNull();
    }
}
