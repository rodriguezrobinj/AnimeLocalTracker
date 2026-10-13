using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Reports;

namespace AnimeLocalTracker.Benchmarks;

public class BenchmarkMetricRecord
{
    public string BenchmarkName { get; set; } = string.Empty;
    public double MeanNanoseconds { get; set; }
    public double AllocatedBytes { get; set; }
    public double OperationsPerSecond { get; set; }
    public string FormattedTime { get; set; } = string.Empty;
}

public class BenchmarkRunRecord
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string MachineName { get; set; } = Environment.MachineName;
    public string Framework { get; set; } = ".NET 8.0 Windows";
    public string BenchmarkCategory { get; set; } = string.Empty;
    public List<BenchmarkMetricRecord> Metrics { get; set; } = new();
}

public class BenchmarkMetricComparison
{
    public string BenchmarkName { get; set; } = string.Empty;
    public double BaselineMeanNs { get; set; }
    public double CurrentMeanNs { get; set; }
    public double LatencyDeltaPercent { get; set; }
    public double BaselineAllocatedBytes { get; set; }
    public double CurrentAllocatedBytes { get; set; }
    public double MemoryDeltaPercent { get; set; }
    public string Status { get; set; } = "ESTABLE"; // MEJORA, REGRESIÓN, ESTABLE
}

public class BenchmarkComparisonResult
{
    /// <summary>La ejecución anterior más reciente (null si es la primera).</summary>
    public BenchmarkRunRecord? BaselineRun { get; set; }
    /// <summary>Cuántas ejecuciones anteriores entran en la referencia (la mediana de sus tiempos).</summary>
    public int BaselineRunCount { get; set; }
    public BenchmarkRunRecord CurrentRun { get; set; } = null!;
    public List<BenchmarkMetricComparison> MetricComparisons { get; set; } = new();
}

public static class BenchmarkHistoryManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Cambio de tiempo (en %) a partir del cual un benchmark se marca como mejora o regresión. 5 en el equipo de desarrollo, donde
    /// la máquina es siempre la misma. El flujo de GitHub lo sube con ANIMELOCALTRACKER_BENCH_UMBRAL: allí cada ejecución cae en una
    /// máquina distinta y el mismo código varía entre un 7 y un 26 % (ejecuciones del 21-sep, 28-sep y 12-oct de 2026).
    /// </summary>
    public static double UmbralPorcentaje =>
        double.TryParse(Environment.GetEnvironmentVariable("ANIMELOCALTRACKER_BENCH_UMBRAL"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double umbral) && umbral > 0 ? umbral : 5.0;

    public static string GetDefaultHistoryDirectory()
    {
        // OPS-01: la ruta se puede fijar con la variable ANIMELOCALTRACKER_BENCH_HISTORY
        // (el script run_benchmarks_and_reports.ps1 la define en la raíz del repo; antes el
        // historial se escribía en bin\ y el script lo buscaba en la raíz: nunca coincidían).
        string? envDir = Environment.GetEnvironmentVariable("ANIMELOCALTRACKER_BENCH_HISTORY");
        string dir = !string.IsNullOrWhiteSpace(envDir)
            ? Path.GetFullPath(envDir)
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BenchmarkHistory");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    public static List<BenchmarkRunRecord> LoadHistory(string category, string? historyDir = null)
    {
        historyDir ??= GetDefaultHistoryDirectory();
        string filePath = Path.Combine(historyDir, $"{category}_history.json");

        if (!File.Exists(filePath)) return new List<BenchmarkRunRecord>();

        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<BenchmarkRunRecord>>(json) ?? new List<BenchmarkRunRecord>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BenchmarkHistory] Advertencia al leer historial: {ex.Message}");
            return new List<BenchmarkRunRecord>();
        }
    }

    public static void SaveRun(BenchmarkRunRecord run, string? historyDir = null)
    {
        historyDir ??= GetDefaultHistoryDirectory();
        string filePath = Path.Combine(historyDir, $"{run.BenchmarkCategory}_history.json");

        var history = LoadHistory(run.BenchmarkCategory, historyDir);
        history.Add(run);

        string json = JsonSerializer.Serialize(history, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public static BenchmarkRunRecord ExtractFromSummary(Summary summary, string category)
    {
        var run = new BenchmarkRunRecord
        {
            BenchmarkCategory = category,
            Timestamp = DateTime.UtcNow
        };

        foreach (var report in summary.Reports)
        {
            if (report.ResultStatistics == null) continue;

            string name = report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo;
            double meanNs = report.ResultStatistics.Mean;
            double allocated = (double)(report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? 0L);
            double opsPerSec = meanNs > 0 ? (1_000_000_000.0 / meanNs) : 0;

            run.Metrics.Add(new BenchmarkMetricRecord
            {
                BenchmarkName = name,
                MeanNanoseconds = meanNs,
                AllocatedBytes = allocated,
                OperationsPerSecond = opsPerSec,
                FormattedTime = FormatNanoseconds(meanNs)
            });
        }

        return run;
    }

    /// <summary>Cuántas ejecuciones anteriores, como mucho, forman la referencia con la que se compara.</summary>
    public const int EjecucionesDeReferencia = 5;

    private static double Mediana(IEnumerable<double> valores)
    {
        var v = valores.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>
    /// Compara con la MEDIANA de las últimas ejecuciones, no solo con la anterior: en GitHub cada ejecución cae en una máquina
    /// distinta y una sola lenta o rápida daba falsas alarmas (un bucle de 1 µs sin cambios de código: 1,68, 1,40, 1,25 y 1,68 µs
    /// en cuatro ejecuciones seguidas). Con una sola ejecución anterior es lo mismo que antes. Un cambio real sigue marcándose
    /// durante varias ejecuciones, hasta que pasa a ser lo habitual.
    /// </summary>
    public static BenchmarkComparisonResult CompareWithPrevious(BenchmarkRunRecord currentRun, string? historyDir = null)
    {
        var history = LoadHistory(currentRun.BenchmarkCategory, historyDir);
        var anteriores = history.Where(h => h.RunId != currentRun.RunId).TakeLast(EjecucionesDeReferencia).ToList();

        var result = new BenchmarkComparisonResult
        {
            BaselineRun = anteriores.LastOrDefault(),
            BaselineRunCount = anteriores.Count,
            CurrentRun = currentRun
        };

        foreach (var currentMetric in currentRun.Metrics)
        {
            var previas = anteriores.SelectMany(r => r.Metrics.Where(m => m.BenchmarkName == currentMetric.BenchmarkName).Take(1)).ToList();
            var prevMetric = previas.Count == 0 ? null : new BenchmarkMetricRecord
            {
                MeanNanoseconds = Mediana(previas.Select(m => m.MeanNanoseconds)),
                AllocatedBytes = Mediana(previas.Select(m => m.AllocatedBytes))
            };

            var comp = new BenchmarkMetricComparison
            {
                BenchmarkName = currentMetric.BenchmarkName,
                CurrentMeanNs = currentMetric.MeanNanoseconds,
                CurrentAllocatedBytes = currentMetric.AllocatedBytes
            };

            if (prevMetric != null)
            {
                comp.BaselineMeanNs = prevMetric.MeanNanoseconds;
                comp.BaselineAllocatedBytes = prevMetric.AllocatedBytes;

                if (prevMetric.MeanNanoseconds > 0)
                {
                    comp.LatencyDeltaPercent = ((currentMetric.MeanNanoseconds - prevMetric.MeanNanoseconds) / prevMetric.MeanNanoseconds) * 100.0;
                }

                if (prevMetric.AllocatedBytes > 0)
                {
                    comp.MemoryDeltaPercent = ((currentMetric.AllocatedBytes - prevMetric.AllocatedBytes) / prevMetric.AllocatedBytes) * 100.0;
                }

                if (comp.LatencyDeltaPercent <= -UmbralPorcentaje)
                {
                    comp.Status = "⚡ MEJORA";
                }
                else if (comp.LatencyDeltaPercent >= UmbralPorcentaje)
                {
                    comp.Status = "⚠️ REGRESIÓN";
                }
                else
                {
                    comp.Status = "✅ ESTABLE";
                }
            }
            else
            {
                comp.Status = "🆕 NUEVO";
            }

            result.MetricComparisons.Add(comp);
        }

        return result;
    }

    public static string GenerateMarkdownReport(BenchmarkComparisonResult comparison)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Reporte de Rendimiento y Comparativa Histórica: {comparison.CurrentRun.BenchmarkCategory}");
        sb.AppendLine();
        sb.AppendLine($"- **Fecha de Ejecución:** {comparison.CurrentRun.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- **ID Ejecución Actual:** `{comparison.CurrentRun.RunId}`");
        sb.AppendLine($"- **Equipo:** `{comparison.CurrentRun.MachineName}` ({comparison.CurrentRun.Framework})");
        
        if (comparison.BaselineRun != null)
        {
            sb.AppendLine($"- **Comparado contra:** la mediana de las últimas {comparison.BaselineRunCount} ejecuciones (la más reciente: `{comparison.BaselineRun.RunId}`, {comparison.BaselineRun.Timestamp:yyyy-MM-dd HH:mm:ss} UTC)");
        }
        else
        {
            sb.AppendLine("- **Estado:** Primera ejecución registrada (Línea Base inicial).");
        }
        sb.AppendLine();

        sb.AppendLine("| Benchmark | Latencia de Referencia | Latencia Actual | Delta Latencia | Memoria Ant. | Memoria Act. | Delta Memoria | Estado |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");

        foreach (var m in comparison.MetricComparisons)
        {
            string baselineLat = m.BaselineMeanNs > 0 ? FormatNanoseconds(m.BaselineMeanNs) : "N/A";
            string currentLat = FormatNanoseconds(m.CurrentMeanNs);
            string deltaLat = m.BaselineMeanNs > 0 ? $"{(m.LatencyDeltaPercent >= 0 ? "+" : "")}{m.LatencyDeltaPercent:F2}%" : "N/A";
            string baselineMem = m.BaselineMeanNs > 0 ? $"{m.BaselineAllocatedBytes} B" : "N/A";
            string currentMem = $"{m.CurrentAllocatedBytes} B";
            string deltaMem = m.BaselineMeanNs > 0 ? $"{(m.MemoryDeltaPercent >= 0 ? "+" : "")}{m.MemoryDeltaPercent:F2}%" : "N/A";

            sb.AppendLine($"| **{m.BenchmarkName}** | {baselineLat} | {currentLat} | {deltaLat} | {baselineMem} | {currentMem} | {deltaMem} | {m.Status} |");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    public static void PrintConsoleComparison(BenchmarkComparisonResult comparison)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================================================================");
        Console.WriteLine($" HISTORIAL Y COMPARATIVA DE RENDIMIENTO: {comparison.CurrentRun.BenchmarkCategory.ToUpperInvariant()}");
        Console.WriteLine("==========================================================================================");
        Console.WriteLine($" Ejecución: {comparison.CurrentRun.RunId} ({comparison.CurrentRun.Timestamp:yyyy-MM-dd HH:mm:ss} UTC)");
        if (comparison.BaselineRun != null)
        {
            Console.WriteLine($" Comparativa vs: mediana de las últimas {comparison.BaselineRunCount} ejecuciones (la más reciente: {comparison.BaselineRun.RunId}, {comparison.BaselineRun.Timestamp:yyyy-MM-dd HH:mm:ss} UTC)");
        }
        Console.WriteLine("------------------------------------------------------------------------------------------");
        Console.WriteLine($"{"Benchmark",-42} | {"Actual",-12} | {"Delta",-10} | {"Memoria",-10} | {"Estado",-10}");
        Console.WriteLine("------------------------------------------------------------------------------------------");

        foreach (var m in comparison.MetricComparisons)
        {
            string curTime = FormatNanoseconds(m.CurrentMeanNs);
            string delta = m.BaselineMeanNs > 0 ? $"{(m.LatencyDeltaPercent >= 0 ? "+" : "")}{m.LatencyDeltaPercent:F1}%" : "N/A";
            string mem = $"{m.CurrentAllocatedBytes} B";
            Console.WriteLine($"{Truncate(m.BenchmarkName, 42),-42} | {curTime,-12} | {delta,-10} | {mem,-10} | {m.Status,-10}");
        }
        Console.WriteLine("==========================================================================================");
        Console.WriteLine();
    }

    public static string FormatNanoseconds(double ns)
    {
        if (ns < 1_000) return $"{ns:F2} ns";
        if (ns < 1_000_000) return $"{ns / 1_000.0:F2} µs";
        if (ns < 1_000_000_000) return $"{ns / 1_000_000.0:F2} ms";
        return $"{ns / 1_000_000_000.0:F2} s";
    }

    private static string Truncate(string val, int max) => val.Length <= max ? val : val[..(max - 3)] + "...";
}
