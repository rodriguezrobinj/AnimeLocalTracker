using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>
/// Orquestador multi-fuente (Fase A): fallback entre proveedores y degradación
/// por salud — la app deja de depender de una sola fuente.
/// </summary>
public class OrquestadorMultiProveedorTests
{
    private sealed class ProveedorStub : IProveedorVideo
    {
        private readonly Func<Task<string?>> _resolver;
        private readonly Func<Task<string?>>? _pageResolver;
        public int Llamadas;

        public ProveedorStub(string nombre, Func<Task<string?>> resolver, Func<Task<string?>>? pageResolver = null)
        {
            Nombre = nombre;
            _resolver = resolver;
            _pageResolver = pageResolver;
        }

        public string Nombre { get; }
        public Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, CancellationToken ct = default)
        {
            Llamadas++;
            return _resolver();
        }

        public Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken ct = default)
            => _pageResolver != null ? _pageResolver() : Task.FromResult<string?>(null);
    }

    private static readonly string[] Titulos = { "Anime" };

    [Fact]
    public async Task BuscarUrlEpisodioAsync_PrimerProveedorExitoso_DeberiaUsarloSinTocarElSegundo()
    {
        // Arrange
        var p1 = new ProveedorStub("P1", () => Task.FromResult<string?>("https://p1.com/v.mp4"));
        var p2 = new ProveedorStub("P2", () => Task.FromResult<string?>("https://p2.com/v.mp4"));
        var orquestador = new OrquestadorMultiProveedor(new IProveedorVideo[] { p1, p2 });

        // Act
        var url = await orquestador.BuscarUrlEpisodioAsync(Titulos, 1);

        // Assert
        url.Should().Be("https://p1.com/v.mp4");
        p2.Llamadas.Should().Be(0, "el segundo proveedor solo se prueba si el primero falla");
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_PrimeroSinResultado_DeberiaCaerAlSegundo()
    {
        // Arrange
        var p1 = new ProveedorStub("P1", () => Task.FromResult<string?>(null));
        var p2 = new ProveedorStub("P2", () => Task.FromResult<string?>("https://p2.com/v.mp4"));
        var orquestador = new OrquestadorMultiProveedor(new IProveedorVideo[] { p1, p2 });

        // Act
        var url = await orquestador.BuscarUrlEpisodioAsync(Titulos, 1);

        // Assert
        url.Should().Be("https://p2.com/v.mp4");
        p2.Llamadas.Should().Be(1);
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_PrimeroLanzaExcepcion_DeberiaCaerAlSegundo()
    {
        // Arrange
        var p1 = new ProveedorStub("P1", () => throw new InvalidOperationException("fuente caída"));
        var p2 = new ProveedorStub("P2", () => Task.FromResult<string?>("https://p2.com/v.mp4"));
        var orquestador = new OrquestadorMultiProveedor(new IProveedorVideo[] { p1, p2 });

        // Act
        var url = await orquestador.BuscarUrlEpisodioAsync(Titulos, 1);

        // Assert: la excepción del proveedor no tumba la resolución
        url.Should().Be("https://p2.com/v.mp4");
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_ConFallosConsecutivos_DeberiaDegradarAlProveedor()
    {
        // Arrange: cooldown largo para que la degradación persista durante el test
        var p1 = new ProveedorStub("P1", () => Task.FromResult<string?>(null));
        var p2 = new ProveedorStub("P2", () => Task.FromResult<string?>("https://p2.com/v.mp4"));
        var orquestador = new OrquestadorMultiProveedor(new IProveedorVideo[] { p1, p2 },
            maxFallosConsecutivos: 2, cooldown: TimeSpan.FromHours(1));

        // Act: 3 intentos (el 3º debe saltarse P1 por estar en cooldown)
        await orquestador.BuscarUrlEpisodioAsync(Titulos, 1);
        await orquestador.BuscarUrlEpisodioAsync(Titulos, 1);
        await orquestador.BuscarUrlEpisodioAsync(Titulos, 1);

        // Assert: P1 se probó 2 veces (antes de degradarse) y P2 las 3
        p1.Llamadas.Should().Be(2, "con 2 fallos consecutivos P1 entra en cooldown");
        p2.Llamadas.Should().Be(3);
    }

    [Fact]
    public async Task BuscarUrlEpisodioAsync_TrasCooldownExpirado_DeberiaReintentarAlProveedor()
    {
        // Arrange: cooldown nulo → el proveedor se recupera al instante
        var p1 = new ProveedorStub("P1", () => Task.FromResult<string?>(null));
        var p2 = new ProveedorStub("P2", () => Task.FromResult<string?>("https://p2.com/v.mp4"));
        var orquestador = new OrquestadorMultiProveedor(new IProveedorVideo[] { p1, p2 },
            maxFallosConsecutivos: 1, cooldown: TimeSpan.Zero);

        // Act: varios intentos
        for (int i = 0; i < 4; i++)
        {
            await orquestador.BuscarUrlEpisodioAsync(Titulos, 1);
        }

        // Assert: con cooldown cero P1 siempre se reintenta (4 llamadas)
        p1.Llamadas.Should().Be(4);
    }

    [Fact]
    public async Task GetVideoUrlAsync_PrimerProveedorQueReconoceLaPagina_DeberiaUsarlo()
    {
        // Arrange
        var p1 = new ProveedorStub("P1", () => Task.FromResult<string?>(null),
            () => Task.FromResult<string?>("https://p1.com/directo.mp4"));
        var p2 = new ProveedorStub("P2", () => Task.FromResult<string?>(null));
        var orquestador = new OrquestadorMultiProveedor(new IProveedorVideo[] { p1, p2 });

        // Act
        var url = await orquestador.GetVideoUrlAsync("https://pagina.com/episodio");

        // Assert
        url.Should().Be("https://p1.com/directo.mp4");
    }
}
