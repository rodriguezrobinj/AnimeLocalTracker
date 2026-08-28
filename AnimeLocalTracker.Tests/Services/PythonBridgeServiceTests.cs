using System.IO;
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
