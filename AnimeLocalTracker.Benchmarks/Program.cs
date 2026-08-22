using System;
using System.IO;
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
            default:
                Console.WriteLine("Opción no reconocida. Ejecutando benchmarks del Reproductor por defecto...");
                EjecutarYRegistrar<ReproductorBenchmarks>("Reproductor");
                break;
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
