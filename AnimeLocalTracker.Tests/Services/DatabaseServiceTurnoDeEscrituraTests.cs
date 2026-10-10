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
/// La base usa una sola conexión y sqlite-net solo hace esperar su turno a las transacciones: una escritura suelta se colaba
/// dentro de la transacción que otro hilo tuviera abierta. Si esa transacción fallaba, la escritura (que ya había devuelto
/// éxito) se deshacía con ella; y si caía entre la lectura y la escritura de un guardado por lotes, el lote la pisaba.
/// </summary>
public class DatabaseServiceTurnoDeEscrituraTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"AnimeTracker_Turno_{Guid.NewGuid():N}");
    private readonly string _rutaDb;
    private readonly DatabaseService _sut;

    public DatabaseServiceTurnoDeEscrituraTests()
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

    /// <summary>Cada escritura suelta del servicio y la consulta que da 1 si quedó guardada.</summary>
    private static readonly Dictionary<string, (Func<DatabaseService, Task> Escribir, string QuedoGuardada)> Escrituras = new()
    {
        ["GuardarAnime"] = (s => s.GuardarAnimeAsync(new AnimeItem { AniListId = 50, Titulo = "Nuevo" }),
            "SELECT COUNT(*) FROM AnimeItem WHERE AniListId = 50"),
        ["EliminarAnime"] = (s => s.EliminarAnimeAsync(new AnimeItem { AniListId = 1 }),
            "SELECT (SELECT COUNT(*) FROM AnimeItem WHERE AniListId = 1) + (SELECT COUNT(*) FROM RegistroEpisodio WHERE AniListId = 1) = 0"),
        ["EliminarRegistroEpisodio"] = (s => s.EliminarRegistroEpisodioAsync(1, 1),
            "SELECT COUNT(*) = 0 FROM RegistroEpisodio WHERE AniListId = 1 AND NumeroEpisodio = 1"),
        ["ConservarRegistroTrasEliminarArchivo"] = (s => s.ConservarRegistroTrasEliminarArchivoAsync(1, 1),
            "SELECT COUNT(*) FROM RegistroEpisodio WHERE AniListId = 1 AND NumeroEpisodio = 1 AND RutaArchivo = ''"),
        ["ActualizarAnime"] = (s => s.ActualizarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Cambiado" }),
            "SELECT COUNT(*) FROM AnimeItem WHERE AniListId = 1 AND Titulo = 'Cambiado'"),
        ["ActualizarAnimes"] = (s => s.ActualizarAnimesAsync(new[] { new AnimeItem { AniListId = 1, Titulo = "Cambiados" } }),
            "SELECT COUNT(*) FROM AnimeItem WHERE AniListId = 1 AND Titulo = 'Cambiados'"),
        ["GuardarConservarVideos"] = (s => s.GuardarConservarVideosAsync(1, true),
            "SELECT COUNT(*) FROM AnimeItem WHERE AniListId = 1 AND ConservarVideos = 1"),
        ["GuardarPartidaMinijuego"] = (s => s.GuardarPartidaMinijuegoAsync(new PartidaMinijuego { JuegoId = "adivina", Puntos = 7 }),
            "SELECT COUNT(*) FROM PartidaMinijuego WHERE JuegoId = 'adivina'"),
        ["GuardarAjusteAudio"] = (s => s.GuardarAjusteAudioAsync(new AjusteAudio { AniListId = 1, NumeroEpisodio = 1, Volumen = 40 }),
            "SELECT COUNT(*) FROM AjusteAudio WHERE AniListId = 1 AND Volumen = 40"),
        ["GuardarPreferenciaEmision"] = (s => s.GuardarPreferenciaEmisionAsync(new PreferenciaEmision { AniListId = 1, Avisar = true }),
            "SELECT COUNT(*) FROM PreferenciaEmision WHERE AniListId = 1 AND Avisar = 1"),
        ["GuardarDatosExtra"] = (s => s.GuardarDatosExtraAsync(new DatosExtraAnime { AniListId = 1, Formato = "TV" }),
            "SELECT COUNT(*) FROM DatosExtraAnime WHERE AniListId = 1 AND Formato = 'TV'"),
        ["GuardarProximaEmision"] = (s => s.GuardarProximaEmisionAsync(new ProximaEmisionLocal { AniListId = 1, Episodio = 5 }),
            "SELECT COUNT(*) FROM ProximaEmisionLocal WHERE AniListId = 1 AND Episodio = 5"),
        ["GuardarSeguimientoLocal"] = (s => s.GuardarSeguimientoLocalAsync(new SeguimientoLocal { AniListId = 1, Progreso = 3 }),
            "SELECT COUNT(*) FROM SeguimientoLocal WHERE AniListId = 1 AND Progreso = 3"),
        ["GuardarMediaAnimeAv1"] = (s => s.GuardarMediaAnimeAv1Async(new MediaAnimeAv1Verificado { AniListId = 1, Slug = "serie", VerificadoUtc = DateTime.UtcNow }),
            "SELECT COUNT(*) FROM MediaAnimeAv1Verificado WHERE AniListId = 1 AND Slug = 'serie'"),
        ["EliminarDescargaHistorial"] = (async s => await s.EliminarDescargaHistorialAsync((await s.ObtenerDescargasHistorialAsync()).First(d => d.Completada).Id),
            "SELECT COUNT(*) = 0 FROM DescargaHistorial WHERE Completada = 1"),
        ["LimpiarDescargasHistorial"] = (s => s.LimpiarDescargasHistorialAsync(),
            "SELECT COUNT(*) = 0 FROM DescargaHistorial"),
    };

    public static TheoryData<string> NombresDeEscrituras => new(Escrituras.Keys);

    private async Task SembrarAsync()
    {
        await _sut.InicializarBaseDatosAsync();
        await _sut.GuardarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Semilla" });
        await _sut.GuardarRegistroEpisodioAsync(new RegistroEpisodio { AniListId = 1, NumeroEpisodio = 1, RutaArchivo = @"C:\Anime\ep1.mkv" });
        await _sut.GuardarDescargaHistorialAsync(new DescargaHistorial { AniListId = 1, NumeroEpisodio = 1, Completada = true, FechaUtc = DateTime.UtcNow });
        await _sut.GuardarDescargaHistorialAsync(new DescargaHistorial { AniListId = 1, NumeroEpisodio = 2, Completada = false, FechaUtc = DateTime.UtcNow });
    }

    /// <summary>La misma conexión que usa el servicio: sqlite-net reparte una sola por archivo, con su turno de transacciones.</summary>
    private SQLiteAsyncConnection ConexionCompartida() => new(_rutaDb);

    [Theory]
    [MemberData(nameof(NombresDeEscrituras))]
    public async Task UnaEscrituraLanzadaDuranteUnaTransaccionAjenaQueFalla_NoSePierdeConElla(string nombre)
    {
        await SembrarAsync();
        var (escribir, quedoGuardada) = Escrituras[nombre];

        // Otro guardado (una sincronización, una importación) tiene su transacción abierta y va a fallar.
        using var dentro = new ManualResetEventSlim();
        using var seguir = new ManualResetEventSlim();
        var ajena = ConexionCompartida().RunInTransactionAsync(db =>
        {
            db.Execute("UPDATE AnimeItem SET Sinopsis = 'a medias' WHERE AniListId = 1");
            dentro.Set();
            seguir.Wait(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("la transacción ajena falla");
        });
        dentro.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var escritura = escribir(_sut);
        await Task.WhenAny(escritura, Task.Delay(100));   // sin turno, aquí ya se habría ejecutado dentro de la transacción ajena
        seguir.Set();
        await ajena.Invoking(t => t).Should().ThrowAsync<InvalidOperationException>();
        await escritura;

        SQLiteAsyncConnection.ResetPool();
        using var c = new SQLiteConnection(_rutaDb);
        c.ExecuteScalar<int>(quedoGuardada).Should().Be(1, $"{nombre} devolvió éxito: no puede deshacerse porque falle otro guardado");
    }

    [Fact]
    public async Task VaciarBibliotecaMientrasOtrosGuardan_NoDejaLoBorradoRecuperableDentroDelArchivo()
    {
        // La compactación que quita del archivo lo borrado no admite ir dentro de una transacción: suelta, caía dentro de la de
        // otro guardado, fallaba en silencio y los títulos seguían legibles en el archivo.
        const string marcador = "TituloPrivadoQueNoDebeQuedar";
        await SembrarAsync();
        for (int i = 1; i <= 200; i++)
            await _sut.GuardarDescargaHistorialAsync(new DescargaHistorial { AniListId = 1, NumeroEpisodio = i, AnimeTitulo = marcador, FechaUtc = DateTime.UtcNow });

        using var parar = new CancellationTokenSource();
        var compartida = ConexionCompartida();
        var guardando = Task.Run(async () =>
        {
            while (!parar.IsCancellationRequested)
                await compartida.RunInTransactionAsync(db => { db.Execute("UPDATE AnimeItem SET Sinopsis = 'x' WHERE AniListId = 1"); Thread.Sleep(15); });
        });
        await Task.Delay(50);

        await _sut.VaciarBibliotecaAsync();
        parar.Cancel();
        await guardando;

        foreach (var archivo in new[] { _rutaDb, _rutaDb + "-wal" }.Where(File.Exists))
        {
            using var flujo = new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var contenido = new byte[flujo.Length];
            flujo.ReadExactly(contenido);
            System.Text.Encoding.UTF8.GetString(contenido).Should().NotContain(marcador, $"'{Path.GetFileName(archivo)}' no debe conservar lo borrado");
        }
    }

    [Fact]
    public async Task ActivarConservarVideosMientrasSeActualizaElAnime_NoSeApaga()
    {
        // Actualizar el anime leía "Conservar los videos" y después escribía la fila entera, en dos pasos: si el usuario activaba
        // el interruptor justo en medio, la fila se escribía con el valor viejo y la protección se apagaba sola.
        await SembrarAsync();
        int apagados = 0;
        for (int vuelta = 0; vuelta < 300; vuelta++)
        {
            await _sut.GuardarConservarVideosAsync(1, false);
            await Task.WhenAll(
                Task.Run(() => _sut.ActualizarAnimeAsync(new AnimeItem { AniListId = 1, Titulo = "Semilla" })),
                Task.Run(() => _sut.GuardarConservarVideosAsync(1, true)));
            if (!await _sut.ObtenerConservarVideosAsync(1)) apagados++;
        }

        apagados.Should().Be(0);
    }
}
