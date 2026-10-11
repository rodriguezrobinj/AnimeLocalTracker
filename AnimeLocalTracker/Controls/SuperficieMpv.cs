using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Native;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Wpf;

namespace AnimeLocalTracker.Controls;

/// <summary>
/// Donde se ve el video del motor nuevo: un lienzo OpenGL que WPF compone como una imagen más (así los controles van encima como
/// WPF normal, sin segunda ventana). libmpv avisa de que hay fotograma y se pinta en el siguiente paso de composición.
/// Con un motor que no sea <see cref="MotorMpv"/> no hace nada.
/// Hay una por motor (<see cref="Para"/>) y vive lo que él: las vistas se la pasan (<see cref="MoverA"/>) en vez de crear otra.
/// </summary>
public sealed class SuperficieMpv : Grid, IDisposable
{
    public static readonly DependencyProperty MotorProperty = DependencyProperty.Register(
        nameof(Motor), typeof(IMotorVideo), typeof(SuperficieMpv),
        new PropertyMetadata(null, (d, e) => ((SuperficieMpv)d).CambiarMotor(e.NewValue as MotorMpv)));

    public IMotorVideo? Motor
    {
        get => (IMotorVideo?)GetValue(MotorProperty);
        set => SetValue(MotorProperty, value);
    }

    private static readonly ConditionalWeakTable<MotorMpv, SuperficieMpv> PorMotor = new();

    /// <summary>Diagnóstico: con valor 1 el lienzo se da por no disponible, para probar el modo de compatibilidad en un equipo que sí tiene OpenGL.</summary>
    private const string VariableSinOpenGl = "ANIMELOCALTRACKER_SIN_OPENGL";

    /// <summary>
    /// La superficie de ese motor. Es una sola: pasa de la vista de la ventana principal a la del mini reproductor y vuelve.
    /// Null si este equipo no puede crear el lienzo OpenGL (máquina virtual, escritorio remoto, controlador antiguo): se le avisa al
    /// motor, y el reproductor sigue con el clásico.
    /// </summary>
    public static SuperficieMpv? Para(MotorMpv motor)
    {
        if (PorMotor.TryGetValue(motor, out var existente)) return existente;
        try
        {
            if (Environment.GetEnvironmentVariable(VariableSinOpenGl) == "1")
                throw new NotSupportedException($"OpenGL apagado con {VariableSinOpenGl}");
            var nueva = new SuperficieMpv { Motor = motor };
            PorMotor.Add(motor, nueva);
            return nueva;
        }
        catch (Exception ex)
        {
            AvisarSinSalida(motor, $"no se pudo crear el lienzo OpenGL: {ex.Message}");
            return null;
        }
    }

    /// <summary>Después, no ahora: quien avisa está a medio cargar una vista o a medio pintar, y la respuesta cierra este motor.</summary>
    private static void AvisarSinSalida(MotorMpv motor, string motivo) =>
        Dispatcher.CurrentDispatcher.BeginInvoke(() => { if (!motor.Desechado) motor.AvisarSinSalida(motivo); });

    /// <summary>
    /// La saca de donde esté y la pone en <paramref name="hueco"/>. Se mueve el mismo lienzo en vez de crear otro porque así el pintor
    /// de libmpv sigue vivo: soltarlo apaga el video y tarda 1 s en volver (medido). Por eso salir del árbol visual no suelta nada.
    /// </summary>
    public void MoverA(Decorator hueco)
    {
        if (ReferenceEquals(Parent, hueco)) return;
        if (Parent is Decorator anterior) anterior.Child = null;
        hueco.Child = this;
    }

    private readonly GLWpfControl _lienzo = new();
    private MotorMpv? _motor;
    private nint _pintor;
    private bool _pintorRechazado;
    private int _pintadoPedido;

    // Vivos mientras exista el pintor: libmpv guarda los punteros.
    private ObtenerFuncionGl? _obtenerFuncion;
    private AvisoDePintado? _aviso;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ObtenerFuncionGl(nint contexto, [MarshalAs(UnmanagedType.LPStr)] string nombre);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AvisoDePintado(nint contexto);

    public SuperficieMpv()
    {
        Children.Add(_lienzo);
        _lienzo.Render += Pintar;
        _lienzo.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = false });
        Loaded += (_, _) => PedirPintado();
        SizeChanged += (_, _) => PedirPintado(); // en pausa nadie avisa: al cambiar de tamaño hay que repintar el mismo fotograma
    }

    private void CambiarMotor(MotorMpv? motor)
    {
        SoltarPintor();
        if (_motor != null) _motor.Desechando -= Dispose;
        _motor = motor;
        if (_motor != null) _motor.Desechando += Dispose;
        PedirPintado();
    }

    /// <summary>Desde cualquier hilo: pide un pintado en el hilo de la interfaz, sin acumular peticiones.</summary>
    private void PedirPintado()
    {
        if (Interlocked.Exchange(ref _pintadoPedido, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _pintadoPedido, 0);
            _lienzo.InvalidateVisual();
        });
    }

    /// <summary>Con el contexto OpenGL del lienzo ya activo: la primera vez crea el pintor de libmpv; después pinta el fotograma.</summary>
    private void Pintar(TimeSpan _)
    {
        var motor = _motor;
        if (motor == null || motor.Desechado || _pintorRechazado) return;

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
                _pintorRechazado = true; // no se reintenta en cada fotograma: el reproductor pasa al motor clásico
                AvisarSinSalida(motor, $"libmpv no pudo pintar en la ventana: {LibMpv.TextoDeError(resultado)}");
                return false;
            }
            LibMpv.mpv_render_context_set_update_callback(_pintor, Marshal.GetFunctionPointerForDelegate(_aviso), 0);
            AppLogger.Debug("SuperficieMpv", $"Pintor de libmpv creado ({_lienzo.FrameBufferWidth}x{_lienzo.FrameBufferHeight}).");
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
        _lienzo.Dispose();
    }

    private void SoltarPintor()
    {
        if (_pintor == 0) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(SoltarPintor); return; }

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
