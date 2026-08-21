using BenchmarkDotNet.Running;

namespace AnimeLocalTracker.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<FileScannerBenchmarks>();
    }
}
