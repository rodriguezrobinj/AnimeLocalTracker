using System;
using System.IO;
using AnimeLocalTracker.Services.Python;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// En un build publicado, lo que la app ejecuta (daemon y plugin de audio) sale solo de su propia instalación: una carpeta
/// "tools\python" creada más arriba en el disco no puede colar su script.
/// </summary>
public sealed class LocalizadorHerramientasPythonTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "ALT_localizador_" + Guid.NewGuid().ToString("N"));
    private readonly string _instalacion;
    private readonly string _directorioActual;

    public LocalizadorHerramientasPythonTests()
    {
        _instalacion = Path.Combine(_raiz, "AnimeLocalTracker", "current");
        _directorioActual = Path.Combine(_raiz, "otra");
        Directory.CreateDirectory(_instalacion);
        Directory.CreateDirectory(_directorioActual);
    }

    public void Dispose()
    {
        try { Directory.Delete(_raiz, recursive: true); } catch { /* limpieza best-effort */ }
    }

    private static string Crear(params string[] partes)
    {
        string ruta = Path.Combine(partes);
        Directory.CreateDirectory(Path.GetDirectoryName(ruta)!);
        File.WriteAllText(ruta, "x");
        return ruta;
    }

    [Fact]
    public void Daemon_EnBuildPublicadoSinEjecutable_NoUsaScriptsDeCarpetasSuperioresNiDelDirectorioActual()
    {
        Crear(_raiz, "tools", "python", "cli.py");
        Crear(_directorioActual, "tools", "python", "cli.py");
        Crear(_directorioActual, "AnimeLocalTracker", "Tools", "AnimeTrackerTools.exe");

        LocalizadorHerramientasPython.Daemon(_instalacion, _directorioActual, desarrollo: false).Should().Be((null, null));
    }

    [Fact]
    public void Daemon_ConElEjecutableEmpaquetado_LoUsa()
    {
        string ejecutable = Crear(_instalacion, "Tools", "AnimeTrackerTools", "AnimeTrackerTools.exe");
        Crear(_raiz, "tools", "python", "cli.py");

        LocalizadorHerramientasPython.Daemon(_instalacion, _directorioActual, desarrollo: false).Should().Be((ejecutable, null));
    }

    [Fact]
    public void Daemon_EnDesarrolloSinEjecutable_UsaElScriptDelRepositorio()
    {
        string script = Crear(_raiz, "tools", "python", "cli.py");

        LocalizadorHerramientasPython.Daemon(_instalacion, _directorioActual, desarrollo: true).Should().Be((null, script));
    }
}
