using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Python;
using Xunit;

namespace AnimeLocalTracker.Tests.Services
{
    public class PythonBridgeServiceTests
    {
        [Fact]
        public async Task PythonBridge_IsAvailable_DeberiaResponderTrueSiExisteRuntime()
        {
            var bridge = new PythonBridgeService();
            bool available = await bridge.IsAvailableAsync();

            // Debe responder true ya sea por el binario compilado o por el script en tools/python/cli.py
            Assert.True(available);
        }

        [Fact]
        public async Task PythonBridge_ParseFilename_ConNombreCompletoDeFansub_DeberiaExtraerTituloYEpisodio()
        {
            var bridge = new PythonBridgeService();
            
            var result = await bridge.ExecuteCommandAsync<object, ParseFilenameResponse>(
                "parse-filename",
                new { filename = "[SubsPlease] Boku no Hero Academia - 159 (1080p) [F298F392].mkv" }
            );

            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.Equal("Boku no Hero Academia", result.AnimeTitle);
            Assert.Equal(159, result.EpisodeNumber);
            Assert.Equal("SubsPlease", result.ReleaseGroup);
            Assert.Equal("1080p", result.VideoResolution);
        }

        [Fact]
        public async Task PythonBridge_MatchTitle_ConErroresTipograficos_DeberiaEncontrarElMasCercano()
        {
            var bridge = new PythonBridgeService();
            
            var candidates = new[] { "Attack on Titan", "Demon Slayer", "Jujutsu Kaisen", "Boku no Hero Academia" };
            var result = await bridge.ExecuteCommandAsync<object, MatchTitleResponse>(
                "match-title",
                new { query = "Jujutsu Kaizen", candidates = candidates, threshold = 80.0 }
            );

            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.Match);
            Assert.Equal("Jujutsu Kaisen", result.Match.MatchedTitle);
            Assert.True(result.Match.Score >= 80.0);
        }

        [Fact]
        public async Task PeticionCancelada_NoDejaSuRespuestaParaLaSiguiente()
        {
            // Caso real: cambiar de episodio a mitad del análisis dejaba la respuesta en el canal del daemon y la petición siguiente la
            // leía como propia (el opening/ending de un episodio se guardaba en otro).
            string dir = Path.Combine(Path.GetTempPath(), "alt_bridge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string plugin = Path.Combine(dir, "plugin_eco.py");
            File.WriteAllText(plugin, "import time\ndef lento(valor, segundos):\n    time.sleep(segundos)\n    return valor\ndef eco(valor):\n    return valor\n");
            try
            {
                var bridge = new PythonBridgeService();
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
                {
                    Func<Task> lenta = () => bridge.ExecuteCommandAsync<object, EcoResponse>("run-plugin",
                        new { plugin_path = plugin, func_name = "lento", args = new { valor = "lenta", segundos = 2 } }, cts.Token);
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(lenta);
                }

                await Task.Delay(2500); // la respuesta de la lenta ya se habría escrito
                for (int i = 0; i < 2; i++)
                {
                    var r = await bridge.ExecuteCommandAsync<object, EcoResponse>("run-plugin",
                        new { plugin_path = plugin, func_name = "eco", args = new { valor = "rapida" + i } });
                    Assert.NotNull(r);
                    Assert.Equal("rapida" + i, r!.Result);
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Theory]
        [InlineData("{\"success\": true, \"id\": 7}", 7, false)]
        [InlineData("{\"success\": true, \"id\": 6}", 7, true)]
        [InlineData("{\"success\": true}", 7, false)] // daemon viejo, sin id
        [InlineData("no es json", 7, false)]
        public void EsRespuestaDeOtraPeticion_ComparaElId(string linea, long id, bool esperado)
        {
            Assert.Equal(esperado, PythonBridgeService.EsRespuestaDeOtraPeticion(linea, id));
        }

        private class EcoResponse
        {
            public bool Success { get; set; }
            public string? Result { get; set; }
        }

        private class ParseFilenameResponse
        {
            public bool Success { get; set; }
            public string? AnimeTitle { get; set; }
            public int? EpisodeNumber { get; set; }
            public int? SeasonNumber { get; set; }
            public string? ReleaseGroup { get; set; }
            public string? VideoResolution { get; set; }
        }

        private class MatchTitleResponse
        {
            public bool Success { get; set; }
            public MatchDetail? Match { get; set; }
        }

        private class MatchDetail
        {
            public string? MatchedTitle { get; set; }
            public double Score { get; set; }
            public int Index { get; set; }
        }
    }
}
