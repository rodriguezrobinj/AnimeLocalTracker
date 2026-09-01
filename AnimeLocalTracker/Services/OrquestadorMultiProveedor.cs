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

    public OrquestadorMultiProveedor(
        IEnumerable<IProveedorVideo> proveedores,
        int maxFallosConsecutivos = 3,
        TimeSpan? cooldown = null)
    {
        _proveedores = proveedores.Select(p => new EstadoProveedor(p)).ToList();
        _maxFallosConsecutivos = Math.Max(1, maxFallosConsecutivos);
        _cooldown = cooldown ?? TimeSpan.FromMinutes(5);
    }

    public async Task<string?> BuscarUrlEpisodioAsync(IEnumerable<string> titulos, int numeroEpisodio, CancellationToken cancellationToken = default)
    {
        var titulosLista = titulos.ToList();
        foreach (var estado in _proveedores.Where(EstaSaludable))
        {
            if (cancellationToken.IsCancellationRequested) return null;

            try
            {
                var url = await estado.Proveedor.BuscarUrlEpisodioAsync(titulosLista, numeroEpisodio, cancellationToken);
                if (!string.IsNullOrEmpty(url))
                {
                    RegistrarExito(estado);
                    return url;
                }
                RegistrarFallo(estado, "sin resultado");
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
        foreach (var estado in _proveedores.Where(EstaSaludable))
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
