---
name: wpf-visual-verification
description: Cómo compilar, lanzar e interactuar con AnimeLocalTracker.exe para verificar cambios de UI/ventana manualmente (screenshots, clicks simulados, estado de ventana vía GetWindowRect), y las trampas conocidas del entorno (foco/z-order poco fiable, animaciones de WindowState, controles que se ocultan por inactividad). Usar tras cualquier cambio visual o de comportamiento de ventana antes de darlo por confirmado.
---

# Verificación visual manual — AnimeLocalTracker

Este proyecto no tiene UI automation tests; la verificación de cambios visuales/de ventana es manual, vía PowerShell. Este skill documenta el procedimiento y las trampas reales encontradas (no teóricas) al hacerlo en este entorno.

## Regla de oro

**`GetWindowRect` (vía P/Invoke) es más confiable que una captura de pantalla** para verificar tamaño/posición/estado de la ventana. En este entorno el compositor a veces deja el propio terminal cubriendo la pantalla en las capturas aunque la app siga recibiendo foco/clicks correctamente por debajo — si una captura "no muestra nada" o muestra el terminal, no asumas que la app dejó de responder: verifica con `GetWindowRect` antes de concluir que algo falló.

## 1. Compilar y lanzar

Usa el skill `repo-build-test` para compilar sin caer en el flake conocido. Luego:

```powershell
$exe = 'C:\Users\HP\RiderProjects\AnimeLocalTracker\AnimeLocalTracker\bin\Debug\net8.0-windows10.0.26100.0\AnimeLocalTracker.exe'
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5
Write-Output "PID=$($p.Id) HasExited=$($p.HasExited)"
```

Antes de relanzar tras recompilar, mata la instancia anterior — dos instancias compitiendo por el mismo `settings.json`/DB o por el mismo `MainWindowHandle` da resultados confusos:
```powershell
Get-Process AnimeLocalTracker -ErrorAction SilentlyContinue | Stop-Process -Force
```

## 2. Capturar pantalla

```powershell
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bmp.Save("$env:TEMP\screenshot.png")
```
Luego lee el PNG con la herramienta `Read` normal (no hace falta nada especial).

## 3. Simular clicks

**Cada llamada a PowerShell es un proceso nuevo: los tipos `Add-Type` NO persisten entre llamadas.** Redefine el tipo helper en el mismo script que lo usa, siempre:
```powershell
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MouseSimXYZ {   // nombre único por llamada si vas a redefinir varias veces en la sesión — PowerShell no permite recargar el mismo nombre de tipo
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    public const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    public const uint MOUSEEVENTF_LEFTUP = 0x04;
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
    }
}
'@
[MouseSimXYZ]::Click(600, 330)
```
Importante: usa un nombre de clase **distinto** cada vez que redefinas el tipo en la misma sesión (`MouseSim2`, `MouseSim3`, ...) — PowerShell no permite recargar un tipo con el mismo nombre y falla con "Cannot add type... already exists" o similar.

Estos clicks son inyección de hardware real (`mouse_event`), así que llegan a la ventana que esté en esa posición de pantalla **independientemente de si `SetForegroundWindow` tuvo éxito** — no necesitas foco "oficial" para que los clicks funcionen, solo coordenadas correctas.

## 4. Verificar estado de ventana programáticamente (más confiable que mirar la captura)

```powershell
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class WinInfoXYZ {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
'@
$proc = Get-Process AnimeLocalTracker
$rect = New-Object WinInfoXYZ+RECT
[WinInfoXYZ]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null
Write-Output "Left=$($rect.Left) Top=$($rect.Top) W=$($rect.Right-$rect.Left) H=$($rect.Bottom-$rect.Top)"
```

## Trampas conocidas (reales, no hipotéticas)

1. **`SetForegroundWindow` suele devolver `False` en este entorno** y no logra traer la ventana al frente para la captura. No pierdas tiempo con el truco de `AttachThreadInput`/`BringWindowToTop` — rara vez ayuda aquí. Prioriza verificar con `GetWindowRect` en vez de depender de que la captura se vea "bonita".
2. **Transiciones de `WindowState` (Maximized↔Normal) no son instantáneas.** Windows anima la transición (~1-2s). Si lees `GetWindowRect` inmediatamente después de un click que dispara un cambio de tamaño/estado (p. ej. entrar/salir de un modo compacto), puedes leer un frame intermedio con valores que no tienen sentido (ej. una posición/tamaño que no coincide con ningún estado lógico). Antes de diagnosticar un bug por una lectura rara, **espera 1-2s y vuelve a leer** — si el segundo valor es consistente y correcto, la primera lectura era solo la animación a medias, no un bug real.
3. **Los controles superpuestos del reproductor (barra de botones) se ocultan tras unos segundos de inactividad** (temporizador de fade-out, ver `ReproductorView` / `RegistrarActividad()`). Si vas a hacer click en un botón de esos controles, mueve el mouse primero (`SetCursorPos` sin click, o un `Move` corto) para "despertarlos" antes del click real — si esperaste varios segundos entre abrir el video y hacer click en un botón de sus controles, es probable que el click caiga sobre el video (que sigue ahí debajo) en vez del botón.
4. **Un `Stop-Process -Force` a mitad de una corrida de `dotnet test`** puede dejar el entorno de pruebas en mal estado para la siguiente corrida (ver skill `repo-build-test`, punto 5) — no mates procesos "para ver qué pasa", espera a que terminen o confirma con `Get-Process` que de verdad están colgados (sin producir output por varios minutos) antes de matarlos.
5. Si tras matar procesos y relanzar la app el log muestra `IOException: No se encuentra el recurso 'app.xaml'` al arrancar: es el mismo flake de build del SDK WPF (ver skill `repo-build-test`), no un bug de tu cambio — recompila limpio (`build-server shutdown` + borrar `obj`/`bin` + rebuild) antes de relanzar.

## Log de la app

`%LocalAppData%\AnimeLocalTrackerData\Logs\app.log` — útil para confirmar que un flujo llegó a ejecutarse sin depender de la captura de pantalla (p. ej. buscar el mensaje de log que tu código agrega en el punto que quieres verificar). Para ver solo las líneas nuevas de esta corrida, guarda el conteo de líneas (`(Get-Content $logPath).Count`) antes de interactuar y usa `Select-Object -Skip <n>` después.
