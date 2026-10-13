using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Running;

namespace AnimeLocalTracker.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("=========================================================");
        Console.WriteLine(" ANIME LOCAL TRACKER - SUITE DE BENCHMARKS Y RENDIMIENTO ");
        Console.WriteLine("=========================================================");
        Console.WriteLine("1. Reproductor (Seeking 1s-1s, Navegación, AutoTracking)");
        Console.WriteLine("2. Base de Datos SQLite (Transacciones Bulk, Consultas)");
        Console.WriteLine("3. File Scanner (Extracción de Episodios Regex)");
        Console.WriteLine("4. Ejecutar Todos y Generar Historial Completo");
        Console.WriteLine("=========================================================");

        string opcion = "1";
        if (args.Length > 0)
        {
            opcion = args[0];
        }
        else
        {
            Console.Write("Selecciona una opción [1-4] (por defecto 1): ");
            var input = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(input)) opcion = input.Trim();
        }

        switch (opcion)
        {
            case "1":
                EjecutarYRegistrar<ReproductorBenchmarks>("Reproductor");
                break;
            case "2":
                EjecutarYRegistrar<DatabaseBenchmarks>("Database");
                break;
            case "3":
                EjecutarYRegistrar<FileScannerBenchmarks>("FileScanner");
                break;
            case "4":
                EjecutarYRegistrar<ReproductorBenchmarks>("Reproductor");
                EjecutarYRegistrar<DatabaseBenchmarks>("Database");
                EjecutarYRegistrar<FileScannerBenchmarks>("FileScanner");
                break;
            case "comprobar":
                ComprobarComparativa();
                break;
            default:
                Console.WriteLine("Opción no reconocida. Ejecutando benchmarks del Reproductor por defecto...");
                EjecutarYRegistrar<ReproductorBenchmarks>("Reproductor");
                break;
        }
    }

    /// <summary>
    /// Comprobación rápida de la comparativa con el historial (argumento "comprobar"): no ejecuta ningún benchmark. Usa un
    /// historial inventado en una carpeta temporal, con los tiempos que dio GitHub para un bucle sin cambios de código.
    /// </summary>
    private static void ComprobarComparativa()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"bench-comprobar-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("ANIMELOCALTRACKER_BENCH_UMBRAL", "30");
        try
        {
            static BenchmarkRunRecord Ejecucion(params (string Nombre, double Ns)[] metricas)
            {
                var run = new BenchmarkRunRecord { BenchmarkCategory = "Prueba" };
                foreach (var (nombre, ns) in metricas) run.Metrics.Add(new BenchmarkMetricRecord { BenchmarkName = nombre, MeanNanoseconds = ns });
                return run;
            }
            string Estado(BenchmarkRunRecord actual, string nombre) =>
                BenchmarkHistoryManager.CompareWithPrevious(actual, dir).MetricComparisons.Single(m => m.BenchmarkName == nombre).Status;
            static void Exigir(bool condicion, string que)
            {
                if (!condicion) throw new InvalidOperationException("Comprobación fallida: " + que);
            }

            // Una ejecución vieja muy lenta, que debe quedar fuera de la ventana de las 5 últimas.
            BenchmarkHistoryManager.SaveRun(Ejecucion(("bucle", 9000), ("saltos", 9000)), dir);
            foreach (double ns in new[] { 1680.0, 1400, 1250, 1300, 1250 })
                BenchmarkHistoryManager.SaveRun(Ejecucion(("bucle", ns), ("saltos", 183_000)), dir);

            // Tras 1250 llega 1680 (+34 % sobre la anterior): es la máquina, no el código. Mediana de las 5 últimas: 1300 → +29 %.
            var actual = Ejecucion(("bucle", 1680), ("saltos", 358_000), ("recien", 10));
            Exigir(Estado(actual, "bucle").Contains("ESTABLE"), "una máquina lenta suelta no es una regresión");
            Exigir(Estado(actual, "saltos").Contains("REGRESI"), "duplicar el tiempo sí es una regresión");
            Exigir(Estado(actual, "recien").Contains("NUEVO"), "un benchmark sin historial es nuevo");
            Exigir(BenchmarkHistoryManager.CompareWithPrevious(actual, dir).BaselineRunCount == BenchmarkHistoryManager.EjecucionesDeReferencia,
                "solo cuentan las últimas ejecuciones");
            Exigir(Estado(Ejecucion(("saltos", 120_000)), "saltos").Contains("MEJORA"), "bajar un tercio es una mejora");
            Console.WriteLine("Comparativa con el historial: correcta.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void EjecutarYRegistrar<T>(string categoria)
    {
        Console.WriteLine($"\n[Iniciando Benchmark: {categoria}]...");
        var summary = BenchmarkRunner.Run<T>();

        try
        {
            var runRecord = BenchmarkHistoryManager.ExtractFromSummary(summary, categoria);
            var comparison = BenchmarkHistoryManager.CompareWithPrevious(runRecord);

            // Guardar registro actual en el historial
            BenchmarkHistoryManager.SaveRun(runRecord);

            // Imprimir comparativa en consola
            BenchmarkHistoryManager.PrintConsoleComparison(comparison);

            // Guardar reporte Markdown en disco
            string reportDir = BenchmarkHistoryManager.GetDefaultHistoryDirectory();
            string mdContent = BenchmarkHistoryManager.GenerateMarkdownReport(comparison);
            string mdPath = Path.Combine(reportDir, $"{categoria}_latest_report.md");
            File.WriteAllText(mdPath, mdContent);
            Console.WriteLine($"[Reporte Markdown guardado en]: {mdPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error al procesar historial]: {ex.Message}");
        }
    }
}
