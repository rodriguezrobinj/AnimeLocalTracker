using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Orquestador multi-fuente (Fase A): prueba los proveedores en orden de
/// registro (prioridad) con degradación por salud — un proveedor con N fallos
/// consecutivos entra en cooldown y se reintenta al expirar. Así la app deja de
/// depender de una sola fuente: si la primaria falla, la siguiente asume.
/// </summary>
public class OrquestadorMultiProveedor : IVideoSourceResolver
{
    private readonly List<EstadoProveedor> _proveedores;
    private readonly int _maxFallosConsecutivos;
    private readonly TimeSpan _cooldown;

    private sealed class EstadoProveedor
    {
        public EstadoProveedor(IProveedorVideo proveedor) => Proveedor = proveedor;

        public IProveedorVideo Proveedor { get; }
        public int FallosConsecutivos { get; set; }
        public DateTime? CooldownHasta { get; set; }
    }

    /// <summary>Orden de sitios elegido por el usuario (lista separada por comas con los nombres de proveedor), leído en cada búsqueda.</summary>
    private readonly Func<string?>? _ordenProveedores;

    /// <param name="ordenProveedores">Devuelve el orden elegido en Configuración ("JKAnime,AnimeAV1") o null. Se consulta en cada
    /// búsqueda, así un cambio se aplica sin reiniciar la app. Los no listados van después, en el orden de registro.</param>
    public OrquestadorMultiProveedor(
        IEnumerable<IProveedorVideo> proveedores,
        int maxFallosConsecutivos = 3,
        TimeSpan? cooldown = null,
        Func<string?>? ordenProveedores = null)
    {
        _proveedores = proveedores.Select(p => new EstadoProveedor(p)).ToList();
        _maxFallosConsecutivos = Math.Max(1, maxFallosConsecutivos);
        _cooldown = cooldown ?? TimeSpan.FromMinutes(5);
        _ordenProveedores = ordenProveedores;
    }

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, int? aniListId = null, string? audioPreferido = null, string? servidorPreferido = null, CancellationToken cancellationToken = default)
    {
        var titulosLista = titulos.ToList();
        foreach (var estado in ProveedoresAProbar())
        {
            if (cancellationToken.IsCancellationRequested) return null;

            try
            {
                var url = await estado.Proveedor.BuscarUrlEpisodioAsync(titulosLista, numeroEpisodio, aniListId, audioPreferido, servidorPreferido, cancellationToken);
                if (!string.IsNullOrEmpty(url))
                {
                    RegistrarExito(estado);
                    return url;
                }
                // "No está" (episodio aún no publicado, anime que el sitio no tiene) no es un fallo del
                // proveedor: contarlo como tal dejaba a AnimeAV1 —el único— en pausa 5 minutos tras tres
                // episodios inexistentes, y las descargas que sí existían fallaban al instante.
            }
            catch (Exception ex)
            {
                RegistrarFallo(estado, ex.Message);
            }
        }
        return null;
    }

    public async Task<string?> GetVideoUrlAsync(string pageUrl, CancellationToken cancellationToken = default)
    {
        foreach (var estado in ProveedoresAProbar())
        {
            try
            {
                var url = await estado.Proveedor.GetVideoUrlAsync(pageUrl, cancellationToken);
                if (!string.IsNullOrEmpty(url)) return url;
            }
            catch
            {
                // El siguiente proveedor (o null si ninguno reconoce la página)
            }
        }
        return null;
    }

    /// <summary>Los proveedores sanos; si todos están en pausa, se prueban igual (no probar ninguno es peor).</summary>
    private IEnumerable<EstadoProveedor> ProveedoresAProbar()
    {
        var sanos = _proveedores.Where(EstaSaludable).ToList();
        var lista = sanos.Count > 0 ? sanos : _proveedores;

        // Los que el usuario ordenó, primero y en ese orden; el resto (plugins…) detrás, en el de registro (OrderBy es estable).
        var elegidos = Models.OrdenProveedores.Dividir(_ordenProveedores?.Invoke()).ToList();
        if (elegidos.Count == 0) return lista;
        return lista.OrderBy(e =>
        {
            int puesto = elegidos.FindIndex(n => n.Equals(e.Proveedor.Nombre, StringComparison.OrdinalIgnoreCase));
            return puesto < 0 ? int.MaxValue : puesto;
        }).ToList();
    }

    private bool EstaSaludable(EstadoProveedor e)
        => !e.CooldownHasta.HasValue || e.CooldownHasta.Value <= DateTime.UtcNow;

    private void RegistrarExito(EstadoProveedor e)
    {
        e.FallosConsecutivos = 0;
        e.CooldownHasta = null;
    }

    private void RegistrarFallo(EstadoProveedor e, string motivo)
    {
        e.FallosConsecutivos++;
        if (e.FallosConsecutivos >= _maxFallosConsecutivos)
        {
            e.CooldownHasta = DateTime.UtcNow.Add(_cooldown);
            e.FallosConsecutivos = 0;
            AppLogger.Warn("OrquestadorMultiProveedor",
                $"Proveedor '{e.Proveedor.Nombre}' degradado tras {_maxFallosConsecutivos} fallos consecutivos ({motivo}); cooldown {_cooldown.TotalMinutes:F0} min.");
        }
    }
}
