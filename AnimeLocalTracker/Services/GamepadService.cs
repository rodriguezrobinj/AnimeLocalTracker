using System;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows;
using Vortice.XInput;
using Forms = System.Windows.Forms;

namespace AnimeLocalTracker.Services;

public class GamepadService : IGamepadService
{
    private DispatcherTimer? _timer;
    private Gamepad _previousGamepadState;
    
    // Cooldown para el stick para no moverse demasiado rápido (15 frames = 250ms a 60fps)
    private int _stickCooldown = 0;

    public void Iniciar()
    {
        if (_timer != null) return;

        _timer = new DispatcherTimer(DispatcherPriority.Input);
        _timer.Interval = TimeSpan.FromMilliseconds(16); // ~60 FPS
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    public void Detener()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        // Revisamos el controlador 0 (Jugador 1)
        if (Vortice.XInput.XInput.GetState(0, out State state))
        {
            ProcesarEntrada(state.Gamepad);
            _previousGamepadState = state.Gamepad;
        }
    }

    private void ProcesarEntrada(Gamepad gamepad)
    {
        if (_stickCooldown > 0)
            _stickCooldown--;

        // Comprobamos qué botones han sido presionados en este frame
        bool pressedA = FuePresionado(gamepad, _previousGamepadState, GamepadButtons.A);
        bool pressedB = FuePresionado(gamepad, _previousGamepadState, GamepadButtons.B);
        
        bool pressedUp = FuePresionado(gamepad, _previousGamepadState, GamepadButtons.DPadUp);
        bool pressedDown = FuePresionado(gamepad, _previousGamepadState, GamepadButtons.DPadDown);
        bool pressedLeft = FuePresionado(gamepad, _previousGamepadState, GamepadButtons.DPadLeft);
        bool pressedRight = FuePresionado(gamepad, _previousGamepadState, GamepadButtons.DPadRight);

        // Soporte para Stick Izquierdo
        short deadzone = 20000;
        if (_stickCooldown == 0)
        {
            if (gamepad.LeftThumbY > deadzone) { pressedUp = true; _stickCooldown = 15; }
            else if (gamepad.LeftThumbY < -deadzone) { pressedDown = true; _stickCooldown = 15; }
            
            if (gamepad.LeftThumbX > deadzone) { pressedRight = true; _stickCooldown = 15; }
            else if (gamepad.LeftThumbX < -deadzone) { pressedLeft = true; _stickCooldown = 15; }
        }

        // Acciones de Navegación
        if (pressedUp) MoverFoco(FocusNavigationDirection.Up);
        else if (pressedDown) MoverFoco(FocusNavigationDirection.Down);
        else if (pressedLeft) MoverFoco(FocusNavigationDirection.Left);
        else if (pressedRight) MoverFoco(FocusNavigationDirection.Right);

        // Acción A -> Enter
        if (pressedA)
        {
            SimularTecla("{ENTER}");
        }

        // Acción B -> Escape (Salir/Atrás)
        if (pressedB)
        {
            SimularTecla("{ESC}");
        }
    }

    private bool FuePresionado(Gamepad actual, Gamepad anterior, GamepadButtons boton)
    {
        return actual.Buttons.HasFlag(boton) && !anterior.Buttons.HasFlag(boton);
    }

    private void MoverFoco(FocusNavigationDirection direccion)
    {
        if (Keyboard.FocusedElement is UIElement focusedElement)
        {
            focusedElement.MoveFocus(new TraversalRequest(direccion));
        }
        else
        {
            // Si no hay nada enfocado, intentamos enfocar la ventana principal
            Application.Current.MainWindow?.Focus();
        }
    }

    private void SimularTecla(string tecla)
    {
        // Aseguramos que se envía a la ventana de la aplicación
        if (Application.Current.MainWindow?.IsActive == true)
        {
            Forms.SendKeys.Send(tecla);
        }
    }
}
