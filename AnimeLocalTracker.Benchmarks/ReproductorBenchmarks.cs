using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AnimeLocalTracker.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class ReproductorBenchmarks : IDisposable
{
    private ReproductorViewModel _vm = null!;
    private List<EpisodioItem> _episodios100 = null!;
    private List<EpisodioItem> _episodios1000 = null!;
    private double[] _saltosAleatorios = null!;

    [GlobalSetup]
    public void Setup()
    {
        _vm = new ReproductorViewModel(new DummyDatabaseService(), new DummyTrackingService(), new DummyAuthService());
        _vm.TotalSeconds = 1440; // 24 minutos (duración estándar de episodio de anime)

        _episodios100 = Enumerable.Range(1, 100)
            .Select(i => new EpisodioItem { NumeroEpisodio = i, RutaCompleta = $"C:\\Anime\\Ep_{i:D3}.mkv" })
            .ToList();

        _episodios1000 = Enumerable.Range(1, 1000)
            .Select(i => new EpisodioItem { NumeroEpisodio = i, RutaCompleta = $"C:\\Anime\\Ep_{i:D4}.mkv" })
            .ToList();

        var rand = new Random(42);
        _saltosAleatorios = Enumerable.Range(0, 100).Select(_ => rand.NextDouble() * 1440).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Dispose();
    }

    // CA1001: el benchmark posee el ReproductorViewModel (IDisposable)
    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { _vm?.Dispose(); } catch { }
    }

    [Benchmark(Description = "Seeking continuo segundo a segundo (1440 segundos / 24 min)")]
    public void SeekingContinuoSegundoASegundo()
    {
        for (int sec = 1; sec <= 1440; sec++)
        {
            _vm.SeekCommand.Execute((double)sec);
        }
    }

    [Benchmark(Description = "Seeking aleatorio multi-punto (100 saltos en 24 min)")]
    public void SeekingAleatorioMultiPunto()
    {
        for (int i = 0; i < _saltosAleatorios.Length; i++)
        {
            _vm.SeekCommand.Execute(_saltosAleatorios[i]);
        }
    }

    [Benchmark(Description = "Resolución y búsqueda de Anterior/Siguiente (Colección 100 episodios)")]
    public void ResolucionEpisodios100Items()
    {
        _vm.CargarVideo("C:\\Anime\\Ep_050.mkv", 1, "Anime 100", 50, _episodios100);
        _ = _vm.ObtenerSiguienteEpisodio();
        _ = _vm.ObtenerAnteriorEpisodio();
    }

    [Benchmark(Description = "Resolución y búsqueda de Anterior/Siguiente (Colección 1,000 episodios)")]
    public void ResolucionEpisodios1000Items()
    {
        _vm.CargarVideo("C:\\Anime\\Ep_0500.mkv", 1, "Anime 1000", 500, _episodios1000);
        _ = _vm.ObtenerSiguienteEpisodio();
        _ = _vm.ObtenerAnteriorEpisodio();
    }

    [Benchmark(Description = "Formateo y composición de tiempo formateado (1,000 ticks)")]
    public void FormateoTiempoTicks()
    {
        string duracionTotal = "24:00";
        for (int i = 0; i < 1000; i++)
        {
            var t = TimeSpan.FromSeconds(i);
            string actual = t.ToString(t.Hours > 0 ? @"hh\:mm\:ss" : @"mm\:ss");
            _ = $"{actual} / {duracionTotal}";
        }
    }

    [Benchmark(Description = "Evaluación de umbral de Auto-Tracking al 90% (1,000 ticks)")]
    public bool EvaluacionAutoTrackingTicks()
    {
        double totalSeconds = 1440;
        bool alcanzado = false;
        for (int sec = 0; sec < 1000; sec++)
        {
            double porcentaje = sec / totalSeconds;
            if (porcentaje >= 0.90)
            {
                alcanzado = true;
            }
        }
        return alcanzado;
    }

    private class DummyDatabaseService : IDatabaseService
    {
        public Task InicializarBaseDatosAsync() => Task.CompletedTask;
        public Task CrearBackupRotativoAsync(int maxCopias = 5, string? backupDir = null) => Task.CompletedTask;
        public Task GuardarAnimeAsync(AnimeItem anime) => Task.CompletedTask;
        public Task<List<AnimeItem>> ObtenerTodosLosAnimesAsync() => Task.FromResult(new List<AnimeItem>());
        public Task EliminarAnimeAsync(AnimeItem anime) => Task.CompletedTask;
        public Task EliminarRegistroEpisodioAsync(int aniListId, int numeroEpisodio) => Task.CompletedTask;
        public Task<bool> ExportarCopiaSeguridadAsync(string rutaDestino) => Task.FromResult(true);
        public Task<bool> RestaurarCopiaSeguridadAsync(string rutaOrigen) => Task.FromResult(true);
        public Task<int> ExportarBibliotecaJsonAsync(string rutaDestino) => Task.FromResult(0);
        public Task<int> ImportarBibliotecaJsonAsync(string rutaOrigen) => Task.FromResult(0);
        public Task GuardarRegistroEpisodioAsync(RegistroEpisodio registro) => Task.CompletedTask;
        public Task GuardarRegistrosEpisodioBulkAsync(IEnumerable<RegistroEpisodio> registros) => Task.CompletedTask;
        public Task<List<RegistroEpisodio>> ObtenerRegistrosPorAnimeAsync(int aniListId) => Task.FromResult(new List<RegistroEpisodio>());
        public Task<List<RegistroEpisodio>> ObtenerTodosLosRegistrosAsync() => Task.FromResult(new List<RegistroEpisodio>());
        public Task<List<RegistroEpisodio>> ObtenerEpisodiosNoSincronizadosAsync() => Task.FromResult(new List<RegistroEpisodio>());
        public Task MarcarEpisodiosSincronizadosAsync(IEnumerable<int> ids) => Task.CompletedTask;
        public Task ActualizarAnimeAsync(AnimeItem anime) => Task.CompletedTask;
    }

    private class DummyTrackingService : IAnimeTrackingService
    {
        public Task<List<AniListMedia>> BuscarAnimePorTituloAsync(string titulo) => Task.FromResult(new List<AniListMedia>());
        public Task<bool> ActualizarProgresoAsync(int mediaId, int episodio, string token) => Task.FromResult(true);
        public Task<AniListMedia?> ObtenerAnimePorIdAsync(int id) => Task.FromResult<AniListMedia?>(null);
        public Task<Dictionary<int, AniListMedia>> ObtenerAnimesPorIdsLoteAsync(IEnumerable<int> ids, string? token = null) => Task.FromResult(new Dictionary<int, AniListMedia>());
        public Task<AniListMediaList?> ObtenerSeguimientoUsuarioAsync(int mediaId, string token) => Task.FromResult<AniListMediaList?>(null);
        public Task<bool> GuardarSeguimientoUsuarioAsync(int mediaId, string estado, int progreso, float puntaje, DateTime? fechaInicio, DateTime? fechaFin, string token) => Task.FromResult(true);
        public Task<AniListUser?> ObtenerPerfilUsuarioAsync(string token) => Task.FromResult<AniListUser?>(null);
        public Task<List<AniListMedia>> BuscarAnimesEnVivoAsync(string busqueda, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(new List<AniListMedia>());
        public Task<List<AniListMedia>> ObtenerAnimesTendenciaAsync(System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(new List<AniListMedia>());
        public Task<List<AiringEpisode>> ObtenerCalendarioEmisionAsync(List<int> mediaIds, long inicioSemana, long finSemana) => Task.FromResult(new List<AiringEpisode>());
    }

    private class DummyAuthService : IAuthService
    {
        public string? Token => null;
        public bool EstaAutenticado() => false;
        public string ObtenerTokenGuardado() => string.Empty;
        public Task<bool> IniciarSesionAsync() => Task.FromResult(false);
        public string? ObtenerToken() => null;
        public void CerrarSesion() { }
    }
}
