using System;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Solo hay un hueco de aviso para toda la app. Estas pruebas fijan quién lo ocupa cuando llegan dos seguidos: antes el segundo
/// pisaba siempre al primero y el temporizador del primero apagaba al segundo antes de tiempo.
/// </summary>
[Collection("WpfSmoke")]
public class DialogServiceToastTests
{
    // Con la aplicación de pruebas viva: el servicio salta a su hilo de interfaz, como en la app.
    public DialogServiceToastTests(AnimeLocalTracker.Tests.Views.WpfHostFixture _) { }

    private static readonly TimeSpan Duracion = TimeSpan.FromMilliseconds(1000);

    private static DialogService Crear() => new() { DuracionToast = Duracion };

    private static async Task EsperarHasta(Func<bool> condicion, int ms = 5000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condicion() && DateTime.UtcNow < limite) await Task.Delay(20);
        condicion().Should().BeTrue($"la condición debía cumplirse en {ms} ms");
    }

    [Fact]
    public async Task ElTemporizadorDeUnAviso_NoApagaAlQueLlegoDespues()
    {
        var sut = Crear();
        sut.MostrarToast("Primero", "…");
        await EsperarHasta(() => sut.ToastTitulo == "Primero" && sut.ToastVisible);

        await Task.Delay(600);
        sut.MostrarToast("Segundo", "…");
        await EsperarHasta(() => sut.ToastTitulo == "Segundo");

        await Task.Delay(600); // el temporizador del primero ya saltó (1000 ms); al segundo le quedan 400
        sut.ToastVisible.Should().BeTrue("el segundo tiene su propia duración completa");
        await EsperarHasta(() => !sut.ToastVisible);
    }

    [Fact]
    public async Task UnAvisoDeFuera_EsperaAQueCaduqueElDelReproductor()
    {
        var sut = Crear();
        sut.MostrarToastReproductor("Auto-Tracking", "Episodio 5 marcado como visto.");
        await EsperarHasta(() => sut.ToastVisible);

        sut.MostrarToast("Descarga completada", "Frieren - Episodio 6");
        await Task.Delay(200);
        (sut.ToastTitulo, sut.ToastDelReproductor).Should().Be(("Auto-Tracking", true), "el del reproductor no se corta");

        await EsperarHasta(() => sut.ToastTitulo == "Descarga completada");
        (sut.ToastVisible, sut.ToastDelReproductor).Should().Be((true, false), "al caducar, sale el que esperaba");
        await EsperarHasta(() => !sut.ToastVisible);
    }

    [Fact]
    public async Task UnAvisoDelReproductor_NoEsperaAlDeFuera()
    {
        var sut = Crear();
        sut.MostrarToast("Descarga completada", "Frieren - Episodio 6");
        await EsperarHasta(() => sut.ToastVisible);

        sut.MostrarToastReproductor("Reanudar Reproducción", "Continuando desde 10:26");

        await EsperarHasta(() => sut.ToastTitulo == "Reanudar Reproducción" && sut.ToastDelReproductor);
    }
}
