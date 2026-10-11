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

        [Fact]
        public async Task UnaPeticionLenta_NoHaceEsperarALaSiguiente_YCancelarlaNoReiniciaElDaemon()
        {
            // Con el canal de un solo carril, resolver un servidor esperaba a lo que el daemon tuviera entre manos, y cancelar
            // cualquier petición lo reiniciaba (~1 s en el que las demás se quedaban sin daemon).
            string dir = Path.Combine(Path.GetTempPath(), "alt_bridge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string plugin = Path.Combine(dir, "plugin_eco.py");
            File.WriteAllText(plugin, "import time\ndef lento(valor, segundos):\n    time.sleep(segundos)\n    return valor\ndef eco(valor):\n    return valor\n");
            try
            {
                var bridge = new PythonBridgeService();
                Assert.True(await bridge.IsAvailableAsync());
                int? daemon = PythonBridgeService.PidDelDaemon; // null sin ejecutable empaquetado (CI): cada petición va en su proceso

                using var cts = new CancellationTokenSource();
                var lenta = bridge.ExecuteCommandAsync<object, EcoResponse>("run-plugin",
                    new { plugin_path = plugin, func_name = "lento", args = new { valor = "lenta", segundos = 30 } }, cts.Token);

                var rapida = await bridge.ExecuteCommandAsync<object, EcoResponse>("run-plugin",
                    new { plugin_path = plugin, func_name = "eco", args = new { valor = "rapida" } });

                Assert.Equal("rapida", rapida?.Result);
                Assert.False(lenta.IsCompleted, "la rápida no debía esperar a que terminara la lenta");

                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lenta);
                Assert.Equal(daemon, PythonBridgeService.PidDelDaemon);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private class EcoResponse
        {
            public bool Success { get; set; }
            public string? Result { get; set; }
        }
    }
}
