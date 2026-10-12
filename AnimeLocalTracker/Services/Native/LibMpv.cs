using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AnimeLocalTracker.Services.Native;

/// <summary>
/// Llamadas a libmpv-2.dll (el motor de video). La biblioteca va en la carpeta Mpv de la instalación y se carga la primera vez
/// que se pregunta por <see cref="Disponible"/>, al abrir el primer episodio. Nadie llama a nada de aquí sin comprobarlo antes.
/// </summary>
internal static partial class LibMpv
{
    private const string Dll = "libmpv-2";

    private static readonly Lazy<bool> Cargada = new(() =>
    {
        try
        {
            string ruta = Path.Combine(AppContext.BaseDirectory, "Mpv", "libmpv-2.dll");
            // Cargada por su ruta completa, las llamadas de abajo la encuentran por su nombre (Windows reutiliza el módulo ya cargado).
            return File.Exists(ruta) && NativeLibrary.TryLoad(ruta, out _);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("LibMpv", $"No se pudo cargar libmpv: {ex.Message}");
            return false;
        }
    });

    public static bool Disponible => Cargada.Value;

    // Identificadores de evento y de formato que usa la app (client.h).
    internal const int EventoFinDeArchivo = 7, EventoArchivoCargado = 8, EventoReinicioDeReproduccion = 21, EventoCambioDePropiedad = 22;
    internal const int FormatoBandera = 3, FormatoDoble = 5;
    internal const int FinPorError = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Evento { public int Id; public int Error; public ulong Usuario; public nint Datos; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventoPropiedad { public nint Nombre; public int Formato; public nint Datos; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventoFin { public int Razon; public int Error; }

    [LibraryImport(Dll)] internal static partial nint mpv_create();
    [LibraryImport(Dll)] internal static partial int mpv_initialize(nint ctx);
    [LibraryImport(Dll)] internal static partial void mpv_terminate_destroy(nint ctx);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_option_string(nint ctx, string nombre, string valor);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_set_property_string(nint ctx, string nombre, string valor);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] internal static partial nint mpv_get_property_string(nint ctx, string nombre);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)] internal static partial int mpv_observe_property(nint ctx, ulong usuario, string nombre, int formato);
    [LibraryImport(Dll)] internal static partial void mpv_free(nint datos);
    [LibraryImport(Dll)] internal static partial int mpv_command(nint ctx, nint[] argumentos);
    [LibraryImport(Dll)] internal static partial nint mpv_wait_event(nint ctx, double espera);
    [LibraryImport(Dll)] internal static partial void mpv_wakeup(nint ctx);
    [LibraryImport(Dll)] internal static partial nint mpv_error_string(int error);

    // ── Pintado (render.h, render_gl.h) ──
    internal const int ParametroTipoApi = 1, ParametroInicioGl = 2, ParametroFbo = 3, ParametroVoltearY = 4, ParametroBloquearHastaSuMomento = 12;
    // Pintado por software (API "sw"): tamaño (int[2]), formato de píxel (texto), bytes por fila (size_t*) y el búfer donde pinta.
    internal const int ParametroTamanoSw = 17, ParametroFormatoSw = 18, ParametroPasoSw = 19, ParametroPunteroSw = 20;
    internal const ulong HayFotogramaNuevo = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ParametroDePintado { public int Tipo; public nint Datos; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InicioOpenGl { public nint ObtenerFuncion; public nint Contexto; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Fbo { public int Id, Ancho, Alto, Formato; }

    [LibraryImport(Dll)] internal static partial int mpv_render_context_create(out nint pintor, nint ctx, ParametroDePintado[] parametros);
    [LibraryImport(Dll)] internal static partial int mpv_render_context_render(nint pintor, ParametroDePintado[] parametros);
    [LibraryImport(Dll)] internal static partial void mpv_render_context_set_update_callback(nint pintor, nint aviso, nint datos);
    [LibraryImport(Dll)] internal static partial ulong mpv_render_context_update(nint pintor);
    [LibraryImport(Dll)] internal static partial void mpv_render_context_free(nint pintor);

    /// <summary>Una propiedad como texto ("" si no existe o aún no tiene valor).</summary>
    internal static string Leer(nint ctx, string nombre)
    {
        nint texto = mpv_get_property_string(ctx, nombre);
        if (texto == 0) return string.Empty;
        try { return Marshal.PtrToStringUTF8(texto) ?? string.Empty; }
        finally { mpv_free(texto); }
    }

    /// <summary>Una orden de mpv ("loadfile", "seek"…). Devuelve su código de error (negativo si falla).</summary>
    internal static int Orden(nint ctx, params string[] argumentos)
    {
        var punteros = new nint[argumentos.Length + 1]; // termina en nulo
        try
        {
            for (int i = 0; i < argumentos.Length; i++) punteros[i] = Marshal.StringToCoTaskMemUTF8(argumentos[i]);
            return mpv_command(ctx, punteros);
        }
        finally
        {
            foreach (nint p in punteros) if (p != 0) Marshal.FreeCoTaskMem(p);
        }
    }

    internal static string TextoDeError(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? error.ToString();
}
