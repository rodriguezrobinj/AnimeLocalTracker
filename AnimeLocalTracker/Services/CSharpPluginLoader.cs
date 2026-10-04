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

    /// <param name="esConfiable">SEC-01: recibe el nombre del archivo y su CONTENIDO; si se indica, solo se carga lo que
    /// devuelva true (la app pasa aquí "plugins activados Y contenido aprobado con su huella SHA-256"). Se carga
    /// exactamente el contenido comprobado, y la misma regla vale para las bibliotecas de las que dependa el plugin: una
    /// .dll dejada a su lado es código igual que él. Null = cargar todo (pruebas).</param>
    public static List<IProveedorVideo> CargarProveedoresVideo(string carpetaPlugins, TimeSpan? tiempoMaximoPorPlugin = null, Func<string, byte[], bool>? esConfiable = null)
    {
        var resultado = new List<IProveedorVideo>();

        if (string.IsNullOrWhiteSpace(carpetaPlugins) || !Directory.Exists(carpetaPlugins))
        {
            return resultado;
        }

        var limite = tiempoMaximoPorPlugin ?? TiempoMaximoPorDefecto;

        foreach (string rutaDll in Directory.GetFiles(carpetaPlugins, "*.dll"))
        {
            byte[] contenido;
            try
            {
                contenido = File.ReadAllBytes(rutaDll);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("CSharpPluginLoader", $"No se pudo leer el plugin '{Path.GetFileName(rutaDll)}': {ex.Message}");
                continue;
            }

            if (esConfiable != null && !esConfiable(Path.GetFileName(rutaDll), contenido))
            {
                AppLogger.Info("CSharpPluginLoader", $"Plugin '{Path.GetFileName(rutaDll)}' omitido: plugins desactivados o archivo sin confianza (Configuración → Plugins).");
                continue;
            }

            resultado.AddRange(CargarConLimite(rutaDll, contenido, limite, esConfiable));
        }

        return resultado;
    }

    private static List<IProveedorVideo> CargarConLimite(string rutaDll, byte[] contenido, TimeSpan limite, Func<string, byte[], bool>? esConfiable)
    {
        string nombreDll = Path.GetFileName(rutaDll);

        // LongRunning = hilo dedicado en segundo plano: si el plugin se cuelga, el hilo abandonado
        // no bloquea el cierre de la app ni ocupa el ThreadPool.
        var tarea = Task.Factory.StartNew(
            () => CargarDesdeDll(rutaDll, contenido, esConfiable),
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

    private static List<IProveedorVideo> CargarDesdeDll(string rutaDll, byte[] contenido, Func<string, byte[], bool>? esConfiable)
    {
        var proveedores = new List<IProveedorVideo>();
        // Desde los bytes ya comprobados, no volviendo a abrir la ruta: entre la comprobación y la carga el archivo podía cambiarse.
        var asm = new ContextoPlugin(rutaDll, esConfiable).LoadFromStream(new MemoryStream(contenido));

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
        private readonly Func<string, byte[], bool>? _esConfiable;

        public ContextoPlugin(string rutaDll, Func<string, byte[], bool>? esConfiable) : base(Path.GetFileNameWithoutExtension(rutaDll))
        {
            _resolutor = new AssemblyDependencyResolver(rutaDll);
            _esConfiable = esConfiable;
        }

        protected override Assembly? Load(AssemblyName nombre)
        {
            if (nombre.Name == typeof(IProveedorVideo).Assembly.GetName().Name) return null;

            string? ruta = _resolutor.ResolveAssemblyToPath(nombre);
            if (ruta == null) return null;

            // Una biblioteca junto al plugin corre con los mismos permisos que él: necesita su propia aprobación (en
            // Configuración → Plugins aparece como un archivo más) y se carga de los bytes comprobados. Sin ella no se
            // carga desde aquí; si la app ya trae esa biblioteca, el plugin usa la de la app.
            byte[] contenido = File.ReadAllBytes(ruta);
            if (_esConfiable != null && !_esConfiable(Path.GetFileName(ruta), contenido))
            {
                AppLogger.Warn("CSharpPluginLoader", $"La biblioteca '{Path.GetFileName(ruta)}' que usa el plugin '{Name}' no tiene confianza: no se carga desde la carpeta de plugins.");
                return null;
            }

            return LoadFromStream(new MemoryStream(contenido));
        }
    }
}
