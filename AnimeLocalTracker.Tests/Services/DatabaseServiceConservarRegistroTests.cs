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

/// <summary>Borrar el video de un episodio limpia lo del archivo en TODAS sus filas y conserva lo visto.</summary>
public class DatabaseServiceConservarRegistroTests : IDisposable
{
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceConservarRegistroTests()
    {
        _rutaDb = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Conservar_{Guid.NewGuid():N}.db");
        _sut = new DatabaseService(_rutaDb);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _sut.Dispose();
        try { if (File.Exists(_rutaDb)) File.Delete(_rutaDb); } catch { /* ignore */ }
    }

    [Fact]
    public async Task ConEpisodioDuplicado_LimpiaLasDosFilasYConservaLoVisto()
    {
        await _sut.InicializarBaseDatosAsync();
        var visto = new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc);

        // Las bases anteriores a v22 pueden tener dos filas del mismo episodio (una con lo visto y otra con la ruta, la miniatura y
        // los datos técnicos): se quita el índice único para simular una de ellas.
        using (var conexion = new SQLiteConnection(_rutaDb))
        {
            conexion.Execute("DROP INDEX IF EXISTS IX_RegistroEpisodio_AnimeEp;");
            conexion.Insert(new RegistroEpisodio { AniListId = 10, NumeroEpisodio = 1, VistoLocal = true, TotalSegundos = 30, UltimaReproduccion = visto });
            conexion.Insert(new RegistroEpisodio
            {
                AniListId = 10, NumeroEpisodio = 1, RutaArchivo = @"C:\Anime\Frieren\Episodio 01.mp4", RutaMiniatura = @"C:\Datos\Thumbnails\a.jpg",
                Resolucion = "320x240", CodecVideo = "h264", Fps = "25", Es10Bit = true,
            });
            conexion.Insert(new RegistroEpisodio { AniListId = 10, NumeroEpisodio = 2, RutaArchivo = @"C:\Anime\Frieren\Episodio 02.mp4" });
        }

        await _sut.ConservarRegistroTrasEliminarArchivoAsync(10, 1);

        var episodio1 = (await _sut.ObtenerRegistrosPorAnimeAsync(10)).Where(r => r.NumeroEpisodio == 1).ToList();
        episodio1.Should().HaveCount(2);
        episodio1.Should().OnlyContain(r => r.RutaArchivo == "" && r.RutaMiniatura == null && r.Resolucion == "" && r.CodecVideo == "" && r.Fps == "" && !r.Es10Bit);
        episodio1.Should().ContainSingle(r => r.VistoLocal && r.UltimaReproduccion != null, "borrar el archivo no borra que se vio");
        (await _sut.ObtenerRegistrosPorAnimeAsync(10)).Single(r => r.NumeroEpisodio == 2).RutaArchivo.Should().EndWith("Episodio 02.mp4", "otro episodio no se toca");
    }
}
