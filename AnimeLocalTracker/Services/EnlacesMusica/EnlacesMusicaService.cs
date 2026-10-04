using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AnimeLocalTracker.Models;

namespace AnimeLocalTracker.Services.EnlacesMusica;

public interface IEnlacesMusicaService
{
    /// <summary>Enlaces de todos los proveedores para este anime (los que fallan o no aplican, se omiten).</summary>
    IReadOnlyList<EnlaceMusica> ObtenerEnlaces(AnimeItem anime);

    /// <summary>Abre el enlace en el navegador. False si la dirección no es segura o no se pudo abrir.</summary>
    bool Abrir(EnlaceMusica enlace);
}

public sealed class EnlacesMusicaService : IEnlacesMusicaService
{
    private readonly IReadOnlyList<IProveedorEnlacesMusica> _proveedores;
    private readonly Action<string> _abrirUrl;

    public EnlacesMusicaService(IEnumerable<IProveedorEnlacesMusica> proveedores)
        : this(proveedores, url => Core.Shell.Abrir(url))
    {
    }

    /// <param name="abrirUrl">Solo para pruebas: qué hacer con la URL (por defecto, abrirla en el navegador).</param>
    internal EnlacesMusicaService(IEnumerable<IProveedorEnlacesMusica> proveedores, Action<string> abrirUrl)
    {
        _proveedores = proveedores.ToList();
        _abrirUrl = abrirUrl;
    }

    public IReadOnlyList<EnlaceMusica> ObtenerEnlaces(AnimeItem anime)
    {
        var resultado = new List<EnlaceMusica>();
        if (anime == null) return resultado;

        foreach (var proveedor in _proveedores)
        {
            try
            {
                var enlace = proveedor.ObtenerEnlace(anime);
                if (enlace == null) continue;

                if (!EsUrlSegura(enlace.Url))
                {
                    AppLogger.Warn("EnlacesMusicaService", $"El proveedor '{proveedor.Id}' devolvió una URL no permitida y se ignora.");
                    continue;
                }
                if (resultado.Any(e => e.ProveedorId == enlace.ProveedorId)) continue;

                resultado.Add(enlace);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("EnlacesMusicaService", $"El proveedor '{proveedor.Id}' falló y se omite: {ex.Message}");
            }
        }

        return resultado;
    }

    public bool Abrir(EnlaceMusica enlace)
    {
        if (enlace == null || !EsUrlSegura(enlace.Url)) return false;

        try
        {
            _abrirUrl(enlace.Url);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("EnlacesMusicaService", $"No se pudo abrir el enlace '{enlace.Url}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Solo https, sin usuario/contraseña embebidos: un proveedor no puede hacer que la app lance otra cosa.</summary>
    internal static bool EsUrlSegura(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo);
}
