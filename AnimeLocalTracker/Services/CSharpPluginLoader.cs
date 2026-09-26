using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Carga plugins C# "drop-in": cualquier .dll en la carpeta de plugins que contenga una clase
/// pública, concreta, con constructor sin parámetros, que implemente <see cref="IProveedorVideo"/>
/// se instancia y se suma a la lista real que usa OrquestadorMultiProveedor — no es un sistema de
/// plugins paralelo/decorativo, extiende el mismo punto que ya prueban los proveedores nativos.
///
/// Confianza total: un plugin C# corre con los mismos permisos que la app (a diferencia de los
/// plugins Python, que corren en el proceso aparte del daemon). Lo que SÍ se garantiza es que un
/// plugin roto no degrade el resto de la app (p. ej. la reproducción de video):
///  - cada .dll se carga en su propio <see cref="AssemblyLoadContext"/>, no en el contexto por
///    defecto de la app (a diferencia de Assembly.LoadFrom);
///  - la carga y el constructor corren en un hilo aparte con tiempo máximo — si el plugin se
///    cuelga, se descarta y se sigue sin él;
///  - cualquier fallo se aísla y se registra, nunca se propaga.
/// </summary>
public static class CSharpPluginLoader
{
    private static readonly TimeSpan TiempoMaximoPorDefecto = TimeSpan.FromSeconds(5);

    /// <param name="esConfiable">SEC-01: si se indica, solo se cargan los .dll para los que devuelve true (la app pasa
    /// aquí "plugins activados Y archivo aprobado con su huella SHA-256"). Null = cargar todos (pruebas).</param>
    public static List<IProveedorVideo> CargarProveedoresVideo(string carpetaPlugins, TimeSpan? tiempoMaximoPorPlugin = null, Func<string, bool>? esConfiable = null)
    {
        var resultado = new List<IProveedorVideo>();

        if (string.IsNullOrWhiteSpace(carpetaPlugins) || !Directory.Exists(carpetaPlugins))
        {
            return resultado;
        }

        var limite = tiempoMaximoPorPlugin ?? TiempoMaximoPorDefecto;

        foreach (string rutaDll in Directory.GetFiles(carpetaPlugins, "*.dll"))
        {
            if (esConfiable != null && !esConfiable(rutaDll))
            {
                AppLogger.Info("CSharpPluginLoader", $"Plugin '{Path.GetFileName(rutaDll)}' omitido: plugins desactivados o archivo sin confianza (Configuración → Plugins).");
                continue;
            }

            resultado.AddRange(CargarConLimite(rutaDll, limite));
        }

        return resultado;
    }

    private static List<IProveedorVideo> CargarConLimite(string rutaDll, TimeSpan limite)
    {
        string nombreDll = Path.GetFileName(rutaDll);

        // LongRunning = hilo dedicado en segundo plano: si el plugin se cuelga, el hilo abandonado
        // no bloquea el cierre de la app ni ocupa el ThreadPool.
        var tarea = Task.Factory.StartNew(
            () => CargarDesdeDll(rutaDll),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            if (!tarea.Wait(limite))
            {
                AppLogger.Warn("CSharpPluginLoader", $"El plugin '{nombreDll}' no terminó de cargar en {limite.TotalSeconds:0.#}s; se ignora.");
                return new List<IProveedorVideo>();
            }

            return tarea.Result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("CSharpPluginLoader", $"No se pudo cargar el plugin '{nombreDll}'", ex.GetBaseException());
            return new List<IProveedorVideo>();
        }
    }

    private static List<IProveedorVideo> CargarDesdeDll(string rutaDll)
    {
        var proveedores = new List<IProveedorVideo>();
        var asm = new ContextoPlugin(rutaDll).LoadFromAssemblyPath(rutaDll);

        foreach (var tipo in asm.GetExportedTypes())
        {
            if (tipo.IsAbstract || tipo.IsInterface) continue;
            if (!typeof(IProveedorVideo).IsAssignableFrom(tipo)) continue;
            if (tipo.GetConstructor(Type.EmptyTypes) == null) continue;

            if (Activator.CreateInstance(tipo) is IProveedorVideo proveedor)
            {
                proveedores.Add(proveedor);
                AppLogger.Info("CSharpPluginLoader", $"Plugin cargado: {proveedor.Nombre} ({Path.GetFileName(rutaDll)})");
            }
        }

        return proveedores;
    }

    /// <summary>
    /// Contexto de carga aislado por plugin. Las dependencias propias del plugin se resuelven junto
    /// a su .dll (vía .deps.json si existe); todo lo demás — incluido el ensamblado de la app, del
    /// que sale <see cref="IProveedorVideo"/> — cae al contexto por defecto, así los tipos coinciden.
    /// </summary>
    private sealed class ContextoPlugin : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolutor;

        public ContextoPlugin(string rutaDll) : base(Path.GetFileNameWithoutExtension(rutaDll))
        {
            _resolutor = new AssemblyDependencyResolver(rutaDll);
        }

        protected override Assembly? Load(AssemblyName nombre)
        {
            if (nombre.Name == typeof(IProveedorVideo).Assembly.GetName().Name) return null;

            string? ruta = _resolutor.ResolveAssemblyToPath(nombre);
            return ruta != null ? LoadFromAssemblyPath(ruta) : null;
        }
    }
}
