using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using FluentAssertions;
using SQLite;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>Migración v22: une los episodios con filas repetidas sin perder nada y deja un índice único.</summary>
public class DatabaseServiceDeduplicarRegistrosTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Dedup_{Guid.NewGuid():N}");
    private readonly string _rutaDb;

    public DatabaseServiceDeduplicarRegistrosTests()
    {
        Directory.CreateDirectory(_carpeta);
        _rutaDb = Path.Combine(_carpeta, "biblioteca.db");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        SQLiteAsyncConnection.ResetPool();
        try { Directory.Delete(_carpeta, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>Copias que deja la migración antes de unir filas (el nombre lleva la fecha).</summary>
    private string[] Copias => Directory.Exists(Path.Combine(_carpeta, "Backups"))
        ? Directory.GetFiles(Path.Combine(_carpeta, "Backups"), "biblioteca.antes-de-v22.*.db")
        : [];

    /// <summary>Deja una base como la de antes de v22: sin índice único, con esas filas y en la versión 21.</summary>
    private async Task CrearBaseAntiguaAsync(params RegistroEpisodio[] filas)
    {
        using (var sut = new DatabaseService(_rutaDb)) await sut.InicializarBaseDatosAsync();
        SQLiteAsyncConnection.ResetPool();
        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.Execute("DROP INDEX IF EXISTS IX_RegistroEpisodio_AnimeEp;");
        conexion.Execute("CREATE INDEX IX_RegistroEpisodio_AnimeEp ON RegistroEpisodio(AniListId, NumeroEpisodio);");
        foreach (var fila in filas) conexion.Insert(fila);
        conexion.Execute("PRAGMA user_version = 21;");
    }

    private async Task<List<RegistroEpisodio>> MigrarYLeerAsync()
    {
        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        return await sut.ObtenerTodosLosRegistrosAsync();
    }

    private static readonly DateTime Antes = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Despues = new(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);

    // ── Reglas de unión (puras, sin base de datos) ──

    [Fact]
    public void Union_VistoSiAlgunaFilaLoEstaba_YFavoritoSiAlgunaLoEra()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, VistoLocal = false, FavoritoLocal = true },
            new RegistroEpisodio { Id = 2, VistoLocal = true, FavoritoLocal = false },
        ]);

        unida.Id.Should().Be(1, "sobrevive la fila más antigua");
        unida.VistoLocal.Should().BeTrue("nunca se pierde un 'visto'");
        unida.FavoritoLocal.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // una vista no se envió a AniList: se sigue enviando
    [InlineData(false, false, false)]
    public void Union_SincronizadoSoloSiTodasLasFilasVistasLoEstaban(bool primeraSync, bool segundaSync, bool esperado)
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, VistoLocal = true, SincronizadoEnNube = primeraSync },
            new RegistroEpisodio { Id = 2, VistoLocal = true, SincronizadoEnNube = segundaSync },
        ]);

        unida.SincronizadoEnNube.Should().Be(esperado);
    }

    [Fact]
    public void Union_UnaFilaVistaSinEnviarYOtraSinVerYEnviada_SigueSinEnviarse()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, VistoLocal = false, SincronizadoEnNube = true },
            new RegistroEpisodio { Id = 2, VistoLocal = true, SincronizadoEnNube = false },
        ]);

        unida.VistoLocal.Should().BeTrue();
        unida.SincronizadoEnNube.Should().BeFalse("el 'visto' que falta por enviar no se da por enviado por culpa de la otra fila");
    }

    [Fact]
    public void Union_ProgresoYFechaSonLosDeLaReproduccionMasReciente()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, ProgresoSegundos = 900, TotalSegundos = 1400, UltimaReproduccion = Despues },
            new RegistroEpisodio { Id = 2, ProgresoSegundos = 100, TotalSegundos = 1400, UltimaReproduccion = Antes },
        ]);

        unida.ProgresoSegundos.Should().Be(900);
        unida.UltimaReproduccion.Should().Be(Despues);
    }

    [Fact]
    public void Union_SinFechas_NoLasInventaYTomaElMayorProgreso()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, ProgresoSegundos = 50 },
            new RegistroEpisodio { Id = 2, ProgresoSegundos = 700, TotalSegundos = 1400 },
        ]);

        unida.UltimaReproduccion.Should().BeNull("el historial es visionado real: no se fabrica una fecha");
        unida.ProgresoSegundos.Should().Be(700);
        unida.TotalSegundos.Should().Be(1400);
    }

    [Fact]
    public void Union_ArchivoYDatosTecnicos_LosDeLaFilaMasNuevaQueLosTenga()
    {
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { Id = 1, RutaArchivo = @"C:\viejo\ep.mp4", Resolucion = "1280x720", CodecVideo = "h264", RutaMiniatura = @"C:\t\a.jpg" },
            new RegistroEpisodio { Id = 2, RutaArchivo = @"C:\nuevo\ep.mp4", Resolucion = "", CodecVideo = null, RutaMiniatura = null, Es10Bit = true },
        ]);

        unida.RutaArchivo.Should().Be(@"C:\nuevo\ep.mp4");
        unida.Resolucion.Should().Be("1280x720", "la fila más nueva no la tenía");
        unida.CodecVideo.Should().Be("h264");
        unida.RutaMiniatura.Should().Be(@"C:\t\a.jpg");
        unida.Es10Bit.Should().BeTrue();
    }

    // ── La migración ──

    [Fact]
    public async Task Migracion_UneLasFilasRepetidas_ConservaElRestoYDejaIndiceUnico()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true, UltimaReproduccion = Despues, ProgresoSegundos = 30, TotalSegundos = 30 },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, RutaArchivo = @"C:\Anime\1\Episodio 01.mp4", Resolucion = "1920x1080" },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 2, VistoLocal = true },
            new RegistroEpisodio { AniListId = 2, NumeroEpisodio = 1, FavoritoLocal = true });

        var filas = await MigrarYLeerAsync();

        filas.Should().HaveCount(3, "solo se unió el episodio repetido");
        var unida = filas.Single(r => r.AniListId == 1 && r.NumeroEpisodio == 1);
        unida.VistoLocal.Should().BeTrue();
        unida.UltimaReproduccion.Should().Be(Despues);
        unida.RutaArchivo.Should().EndWith("Episodio 01.mp4");
        unida.Resolucion.Should().Be("1920x1080");
        filas.Single(r => r.AniListId == 1 && r.NumeroEpisodio == 2).VistoLocal.Should().BeTrue("los demás no se tocan");
        filas.Single(r => r.AniListId == 2).FavoritoLocal.Should().BeTrue();

        using var conexion = new SQLiteConnection(_rutaDb);
        conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().BeGreaterThanOrEqualTo(22);
        var duplicar = () => conexion.Insert(new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        duplicar.Should().Throw<SQLiteException>("el índice único impide volver a crear un duplicado");
    }

    [Fact]
    public async Task Migracion_UnGrupoDeTresFilas_QuedaEnUna()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 4 },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 4, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 4, FavoritoLocal = true });

        var filas = await MigrarYLeerAsync();

        var unica = filas.Should().ContainSingle().Subject;
        unica.VistoLocal.Should().BeTrue();
        unica.FavoritoLocal.Should().BeTrue();
    }

    [Fact]
    public async Task Migracion_ConDuplicados_HaceUnaCopiaConLasFilasOriginales()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });

        await MigrarYLeerAsync();

        Copias.Should().ContainSingle("antes de unir filas se guarda una copia de la base");
        using var copia = new SQLiteConnection(Copias[0]);
        copia.ExecuteScalar<int>("SELECT COUNT(*) FROM RegistroEpisodio;").Should().Be(2, "la copia conserva las dos filas tal como estaban");
    }

    [Fact]
    public void Union_LasFilasMasNuevasSonLasDeLaListaNoLasDeId()
    {
        // Un lote importado trae todos los Id a 0: el orden de la lista es el de antigüedad.
        var unida = DatabaseService.UnirFilasRepetidas([
            new RegistroEpisodio { RutaArchivo = @"C:\viejo\ep.mp4", ProgresoSegundos = 10 },
            new RegistroEpisodio { RutaArchivo = @"C:\nuevo\ep.mp4", ProgresoSegundos = 10 },
        ]);

        unida.RutaArchivo.Should().Be(@"C:\nuevo\ep.mp4");
    }

    [Fact]
    public async Task Migracion_SiYaHabiaUnaCopiaPrevia_NoLaPisa()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        await MigrarYLeerAsync();
        SQLiteAsyncConnection.ResetPool();
        var primera = Copias.Single();

        // Se restaura algo con duplicados y v22 vuelve a correr.
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        await MigrarYLeerAsync();

        Copias.Should().HaveCount(2).And.Contain(primera, "la copia de la primera vez sigue ahí");
    }

    [Fact]
    public async Task Migracion_SiNoSePuedeHacerLaCopia_LaAppAbreSinUnirNiBorrarNada_YLoReintentaDespues()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        // Un archivo llamado "Backups" impide crear la carpeta de copias (como un disco lleno o una carpeta bloqueada).
        string bloqueo = Path.Combine(_carpeta, "Backups");
        File.WriteAllText(bloqueo, "x");

        var filas = await MigrarYLeerAsync();

        filas.Should().HaveCount(2, "sin copia previa no se borra ninguna fila");
        SQLiteAsyncConnection.ResetPool();
        using (var conexion = new SQLiteConnection(_rutaDb))
            conexion.ExecuteScalar<int>("PRAGMA user_version;").Should().Be(21, "la migración queda pendiente");

        File.Delete(bloqueo);
        SQLiteAsyncConnection.ResetPool();
        (await MigrarYLeerAsync()).Should().ContainSingle().Which.VistoLocal.Should().BeTrue();
        Copias.Should().ContainSingle();
    }

    [Fact]
    public async Task Migracion_SinDuplicados_NoHaceCopiaNiCambiaNada()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 2 });

        var filas = await MigrarYLeerAsync();

        filas.Should().HaveCount(2);
        Copias.Should().BeEmpty("no había nada que unir");
    }

    [Fact]
    public async Task Migracion_SeEjecutaUnaSolaVez_AlReabrirNoCambiaNada()
    {
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        var primera = await MigrarYLeerAsync();
        SQLiteAsyncConnection.ResetPool();

        var segunda = await MigrarYLeerAsync();

        segunda.Select(r => (r.Id, r.VistoLocal)).Should().Equal(primera.Select(r => (r.Id, r.VistoLocal)));
    }

    [Fact]
    public async Task RestaurarUnaCopiaAnteriorAV22ConDuplicados_LasUneAlReabrir()
    {
        // Una copia de seguridad hecha antes de esta migración, con un episodio repetido.
        await CrearBaseAntiguaAsync(
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true },
            new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1 });
        string copiaAntigua = Path.Combine(_carpeta, "copia-antigua.db");
        File.Copy(_rutaDb, copiaAntigua);
        SQLiteAsyncConnection.ResetPool();
        File.Delete(_rutaDb);
        foreach (var sufijo in new[] { "-wal", "-shm" }) { try { File.Delete(_rutaDb + sufijo); } catch { /* ignore */ } }

        using var sut = new DatabaseService(_rutaDb);
        await sut.InicializarBaseDatosAsync();
        (await sut.RestaurarCopiaSeguridadAsync(copiaAntigua)).Should().BeTrue();

        (await sut.ObtenerTodosLosRegistrosAsync()).Should().ContainSingle().Which.VistoLocal.Should().BeTrue();
    }
}
