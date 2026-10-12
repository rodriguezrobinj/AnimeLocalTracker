using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Native;
using GLFW = OpenTK.Windowing.GraphicsLibraryFramework.GLFW;
using OpenTK.Wpf;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// Donde se ve el video de libmpv, compuesto por WPF como una imagen más (así los controles van encima como WPF normal, sin segunda
/// ventana). libmpv avisa de que hay fotograma y se pinta en el siguiente paso de composición. Hay dos formas de pintar: un lienzo
/// OpenGL (la normal) o, en el modo de compatibilidad, una imagen de WPF que libmpv rellena por software.
/// Hay una por motor (<see cref="Para"/>) y vive lo que él: las vistas se la pasan (<see cref="MoverA"/>) en vez de crear otra.
/// </summary>
public sealed class SuperficieMpv : Grid, IDisposable
{
    private static readonly ConditionalWeakTable<MotorMpv, SuperficieMpv> PorMotor = new();

    /// <summary>Diagnóstico: con valor 1 el lienzo se da por no disponible, para probar el modo de compatibilidad en un equipo que sí tiene OpenGL.</summary>
    private const string VariableSinOpenGl = "ANIMELOCALTRACKER_SIN_OPENGL";

    /// <summary>La superficie de ese motor. Es una sola: pasa de la vista de la ventana principal a la del mini reproductor y vuelve.</summary>
    public static SuperficieMpv Para(MotorMpv motor)
    {
        if (PorMotor.TryGetValue(motor, out var existente)) return existente;
        var nueva = new SuperficieMpv(motor);
        PorMotor.Add(motor, nueva);
        return nueva;
    }

    /// <summary>Después, no ahora: quien avisa está a medio cargar una vista o a medio pintar, y la respuesta cierra este motor.</summary>
    private static void AvisarSinSalida(MotorMpv motor, string motivo) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(() => { if (!motor.Desechado) motor.AvisarSinSalida(motivo); });

    /// <summary>
    /// La saca de donde esté y la pone en <paramref name="hueco"/>. Se mueve la misma superficie en vez de crear otra porque así el
    /// pintor de libmpv sigue vivo: soltarlo apaga el video y tarda 1 s en volver (medido). Por eso salir del árbol visual no suelta nada.
    /// </summary>
    public void MoverA(Decorator hueco)
    {
        if (ReferenceEquals(Parent, hueco)) return;
        if (Parent is Decorator anterior) anterior.Child = null;
        hueco.Child = this;
    }

    private MotorMpv? _motor;
    private int _pintadoPedido;

    // Con OpenGL.
    private GLWpfControl? _lienzo;
    private nint _pintor;
    private bool _pintorRechazado;

    // Por software.
    private PintorPorSoftware? _porSoftware;
    private Image? _imagen;
    private WriteableBitmap? _mapa;

    // Vivos mientras exista el pintor: libmpv guarda los punteros.
    private ObtenerFuncionGl? _obtenerFuncion;
    private AvisoDePintado? _aviso;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ObtenerFuncionGl(nint contexto, [MarshalAs(UnmanagedType.LPStr)] string nombre);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvisoDePintado(nint contexto);

    private SuperficieMpv(MotorMpv motor)
    {
        _motor = motor;
        motor.Desechando += Dispose;
        Loaded += (_, _) => PedirPintado();
        SizeChanged += (_, _) => PedirPintado(); // en pausa nadie avisa: al cambiar de tamaño hay que repintar el mismo fotograma

        if (motor.PintaPorSoftware) PasarASoftware("lo pide el modo de compatibilidad de Configuración", esFallo: false);
        else if (Environment.GetEnvironmentVariable(VariableSinOpenGl) == "1") PasarASoftware($"OpenGL apagado con {VariableSinOpenGl}", esFallo: false);
        else
        {
            try
            {
                _lienzo = new GLWpfControl();
                Children.Add(_lienzo);
                _lienzo.Render += PintarEnElLienzo;
                _lienzo.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = false });
            }
            catch (Exception ex)
            {
                // Máquina virtual, escritorio remoto, controlador antiguo.
                PasarASoftware($"no se pudo crear el lienzo OpenGL: {ex.Message}", esFallo: true);
            }
        }
    }

    /// <summary>
    /// Quita el lienzo, si lo había, y pone una imagen que libmpv rellena por software. Como aún no había pintor, el archivo no se ha
    /// cargado: el episodio empieza donde tenía que empezar. Solo si tampoco así se puede se avisa al motor de que no hay salida.
    /// </summary>
    private void PasarASoftware(string motivo, bool esFallo)
    {
        if (_motor is not { Desechado: false } motor || _porSoftware != null) return;

        if (_lienzo != null)
        {
            Children.Remove(_lienzo);
            try { _lienzo.Dispose(); }
            catch (Exception ex) { AppLogger.Debug("SuperficieMpv", $"No se pudo soltar el lienzo OpenGL: {ex.Message}"); }
            _lienzo = null;
        }

        string mensaje = $"El video se pinta por software, sin escalado inteligente: {motivo}.";
        if (esFallo) AppLogger.Warn("SuperficieMpv", mensaje);
        else AppLogger.Info("SuperficieMpv", mensaje);

        motor.PintaPorSoftware = true;
        _imagen = new Image { Stretch = Stretch.Fill };
        Children.Add(_imagen);
        _porSoftware = PintorPorSoftware.Crear(motor, PedirPintado);
        if (_porSoftware == null) AvisarSinSalida(motor, $"{motivo}, y libmpv tampoco pudo pintar por software");
        else PedirPintado();
    }

    /// <summary>Desde cualquier hilo: pide un pintado en el hilo de la interfaz, sin acumular peticiones.</summary>
    private void PedirPintado()
    {
        if (Interlocked.Exchange(ref _pintadoPedido, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _pintadoPedido, 0);
            if (_porSoftware != null) PintarEnLaImagen();
            else _lienzo?.InvalidateVisual();
        });
    }

    /// <summary>libmpv pinta en la memoria de la imagen, al tamaño en píxeles de la zona de video (con tope: ver <see cref="PintorPorSoftware.Tamano"/>).</summary>
    private void PintarEnLaImagen()
    {
        if (_motor is not { Desechado: false } motor || _porSoftware == null || _imagen == null) return;

        var ppp = VisualTreeHelper.GetDpi(this);
        var (ancho, alto) = PintorPorSoftware.Tamano(ActualWidth * ppp.DpiScaleX, ActualHeight * ppp.DpiScaleY);
        if (_mapa == null || _mapa.PixelWidth != ancho || _mapa.PixelHeight != alto)
            _imagen.Source = _mapa = new WriteableBitmap(ancho, alto, 96, 96, PixelFormats.Bgr32, null);

        _mapa.Lock();
        try
        {
            if (_porSoftware.Pintar(_mapa.BackBuffer, ancho, alto, _mapa.BackBufferStride)) motor.ContarFotograma();
            _mapa.AddDirtyRect(new Int32Rect(0, 0, ancho, alto));
        }
        finally
        {
            _mapa.Unlock();
        }
    }

    /// <summary>Con el contexto OpenGL del lienzo ya activo: la primera vez crea el pintor de libmpv; después pinta el fotograma.</summary>
    private void PintarEnElLienzo(TimeSpan _)
    {
        var motor = _motor;
        if (motor == null || motor.Desechado || _lienzo == null || _pintorRechazado) return;

        if (_pintor == 0 && !CrearPintor(motor)) return;

        bool hayFotogramaNuevo = (LibMpv.mpv_render_context_update(_pintor) & LibMpv.HayFotogramaNuevo) != 0;
        var fbo = new LibMpv.Fbo { Id = _lienzo.Framebuffer, Ancho = _lienzo.FrameBufferWidth, Alto = _lienzo.FrameBufferHeight };
        nint pFbo = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpv.Fbo>());
        nint pVoltear = Marshal.AllocHGlobal(sizeof(int));
        nint pBloquear = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.StructureToPtr(fbo, pFbo, false);
            Marshal.WriteInt32(pVoltear, 1);
            Marshal.WriteInt32(pBloquear, 0); // no parar el hilo de la interfaz esperando al momento exacto del fotograma
            LibMpv.mpv_render_context_render(_pintor,
            [
                new() { Tipo = LibMpv.ParametroFbo, Datos = pFbo },
                new() { Tipo = LibMpv.ParametroVoltearY, Datos = pVoltear },
                new() { Tipo = LibMpv.ParametroBloquearHastaSuMomento, Datos = pBloquear },
                default
            ]);
            if (hayFotogramaNuevo) motor.ContarFotograma();
        }
        finally
        {
            Marshal.FreeHGlobal(pFbo);
            Marshal.FreeHGlobal(pVoltear);
            Marshal.FreeHGlobal(pBloquear);
        }
    }

    private bool CrearPintor(MotorMpv motor)
    {
        _obtenerFuncion = (_, nombre) => GLFW.GetProcAddress(nombre);
        _aviso = _ => PedirPintado();
        nint pInicio = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpv.InicioOpenGl>());
        nint api = Marshal.StringToHGlobalAnsi("opengl");
        try
        {
            Marshal.StructureToPtr(new LibMpv.InicioOpenGl { ObtenerFuncion = Marshal.GetFunctionPointerForDelegate(_obtenerFuncion) }, pInicio, false);
            int resultado = LibMpv.mpv_render_context_create(out _pintor, motor.Manejador,
            [
                new() { Tipo = LibMpv.ParametroTipoApi, Datos = api },
                new() { Tipo = LibMpv.ParametroInicioGl, Datos = pInicio },
                default
            ]);
            if (resultado < 0)
            {
                _pintor = 0;
                _pintorRechazado = true; // no se reintenta en cada fotograma
                string motivo = $"libmpv no pudo pintar en el lienzo OpenGL: {LibMpv.TextoDeError(resultado)}";
                // Después, no ahora: se está a medio pintar en el lienzo que hay que quitar.
                Dispatcher.BeginInvoke(() => PasarASoftware(motivo, esFallo: true));
                return false;
            }
            LibMpv.mpv_render_context_set_update_callback(_pintor, Marshal.GetFunctionPointerForDelegate(_aviso), 0);
            AppLogger.Debug("SuperficieMpv", $"Pintor de libmpv creado ({_lienzo!.FrameBufferWidth}x{_lienzo.FrameBufferHeight}).");
            try
            {
                // Con el contexto OpenGL del lienzo activo: qué tarjeta lo pinta (para el informe de video).
                motor.TarjetaDetectada(
                    OpenTK.Graphics.OpenGL4.GL.GetString(OpenTK.Graphics.OpenGL4.StringName.Renderer),
                    OpenTK.Graphics.OpenGL4.GL.GetString(OpenTK.Graphics.OpenGL4.StringName.Vendor));
            }
            catch (Exception ex)
            {
                AppLogger.Debug("SuperficieMpv", $"No se pudo leer el nombre de la tarjeta gráfica: {ex.Message}");
            }
            motor.PintorCreado();
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pInicio);
            Marshal.FreeHGlobal(api);
        }
    }

    /// <summary>El motor se va a cerrar: suelta el pintor (libmpv exige que sea antes), sale de la vista que la aloje y suelta el lienzo OpenGL.</summary>
    public void Dispose()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Dispose); return; }

        SoltarPintor();
        if (_motor != null) _motor.Desechando -= Dispose;
        _motor = null;
        if (Parent is Decorator hueco) hueco.Child = null;
        _lienzo?.Dispose();
    }

    private void SoltarPintor()
    {
        _porSoftware?.Dispose();
        _porSoftware = null;
        if (_pintor == 0) return;

        nint pintor = _pintor;
        _pintor = 0;
        _motor?.PintorSoltado();
        try
        {
            LibMpv.mpv_render_context_set_update_callback(pintor, 0, 0);
            LibMpv.mpv_render_context_free(pintor);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SuperficieMpv", $"No se pudo soltar el pintor de libmpv: {ex.Message}");
        }
        _obtenerFuncion = null;
        _aviso = null;
    }
}
