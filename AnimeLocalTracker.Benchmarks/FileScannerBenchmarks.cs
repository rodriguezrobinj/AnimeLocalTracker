using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using AnimeLocalTracker.Services;

namespace AnimeLocalTracker.Benchmarks;

[MemoryDiagnoser]
public class FileScannerBenchmarks
{
    private readonly string[] _nombresDeArchivo = new[]
    {
        "[Erai-raws] Boku no Hero Academia - 138 [1080p][Multiple Subtitle].mkv",
        "Naruto Shippuden Ep 05.mp4",
        "One Piece E1071.mkv",
        "Bleach - Episode 02.avi",
        "Death Note Episodio 15 [720p].mkv",
        "Dragon Ball Z Cap 01",
        "Jujutsu Kaisen Capitulo 24",
        "Shingeki no Kyojin - 87 (1080p).mkv",
        "Solo Leveling 12.mkv",
        "Frieren 04.mp4",
        "Movie Name 1080p.mkv",
        "Anime sin numeros.mkv"
    };

    [Benchmark(Description = "ExtraerNumeroEpisodio - Múltiples formatos")]
    public void ExtraerNumeroEpisodioBenchmark()
    {
        foreach (var nombre in _nombresDeArchivo)
        {
            FileScannerService.ExtraerNumeroEpisodio(nombre);
        }
    }
}
