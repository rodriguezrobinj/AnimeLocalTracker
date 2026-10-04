using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace AnimeLocalTracker.Tests
{
    public static class TestInitializer
    {
        [ModuleInitializer]
        public static void Initialize()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests", "Logs");
            Environment.SetEnvironmentVariable("ANIMELOCALTRACKER_LOG_DIR", tempDir);

            // Las miniaturas que crean las pruebas no deben caer en la carpeta de datos real del usuario.
            AnimeLocalTracker.Services.Python.PythonEpisodeEnricher.CarpetaMiniaturas =
                Path.Combine(Path.GetTempPath(), "AnimeLocalTrackerTests", "Thumbnails");
        }
    }
}
