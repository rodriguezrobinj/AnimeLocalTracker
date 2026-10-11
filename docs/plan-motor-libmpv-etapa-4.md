# Plan de implementación: motor libmpv, etapa 4 (Anime4K)

> **Para quien lo ejecute:** SUB-SKILL OBLIGATORIO: `superpowers:executing-plans`, tarea a tarea, en esta misma sesión (en este repo no se usan subagentes salvo que el usuario lo pida). Los pasos usan casillas (`- [ ]`) para llevar la cuenta.

**Objetivo:** que, con `"MotorVideo": "Mpv"`, el ajuste «Escalado inteligente» (Automático, Activado, Desactivado) gobierne Anime4K: se enciende en el nivel que aguanta el equipo, se nota en la imagen y, en Automático, se apaga solo en ese episodio si empieza a perder fotogramas. Sin pantallas ni ajustes nuevos. Con Flyleaf, el ajuste sigue haciendo lo de hoy.

**Arquitectura:** los shaders de Anime4K (MIT) van dentro del repositorio y se copian junto a la app. `MotorMpv.EscaladoInteligente` los carga o los quita con `glsl-shaders`. La política —cuándo encender, qué nivel, cuándo apagar— vive en `MotorVideo` y en un vigilante puro (`VigilanteEscalado`), y el reproductor la aplica en su bucle de progreso con un dato nuevo del motor (fotogramas perdidos). El contrato dice de qué clase es el escalado de cada motor para que el reproductor no pregunte qué motor es.

**Tecnologías:** C# .NET 8, WPF, libmpv 0.41, Anime4K v4.0.1 (GLSL para mpv), xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-motor-libmpv.md`, sección 6. Etapas anteriores: `docs/plan-motor-libmpv-etapa-3.md`.

## Lo que ya está medido (prototipo, Intel HD 620, 1366×768, Youjo Senki 02 a 1080p)

| Nivel | Fotogramas perdidos en 25 s | Pintados por segundo | Memoria |
|---|---|---|---|
| Sin Anime4K | 0 | 24,0 | 241 MB |
| Ligero (variantes S) | 0 | 24,0 | 392 MB |
| Medio (variantes M) | 237 | 14,6 | 456 MB |
| Muy alto (VL) | 556 | 4,0 | 604 MB |

- El nivel ligero es el único que aguanta esta gráfica integrada. Cuesta unos **150 MB más de memoria**.
- El contador `frame-drop-count` de libmpv delata la sobrecarga con claridad (9 por segundo con el nivel medio; 0 con el ligero).
- **Los shaders de ampliación se saltan solos** cuando el video no se amplía al menos un 20 % en pantalla (lo llevan escrito en su cabecera). En una pantalla de 1366×768, un video de 720p o 1080p no se amplía: de Anime4K actúa la pasada de «restaurar» (líneas más limpias), no la de ampliar. La ampliación entra con videos de menos de ~640 píxeles de alto, o en pantallas más grandes.

## Decisiones de esta etapa

1. **Automático con libmpv = encendido en cualquier tarjeta**, vigilado. (Con Flyleaf, Automático sigue siendo «solo tarjeta dedicada de 2 GB o más»: su escalado es otro y no se puede vigilar.)
2. **Nivel:** ligero salvo con tarjeta NVIDIA o AMD, que usan el medio. No se distingue una AMD integrada de una dedicada: si el nivel medio no aguanta, en Automático se apaga (no baja a ligero). Variable de diagnóstico `ANIMELOCALTRACKER_ANIME4K_NIVEL` (`Ligero`/`Medio`) para forzar el nivel al investigar un equipo.
3. **Apagado automático:** se mira el contador cada 2 s; con 4 ventanas seguidas perdiendo 3 fotogramas o más (≥ 6 % durante 8 s) se apaga hasta el siguiente episodio y se anota en el registro. Un salto o un tirón suelto no lo dispara. En «Activado» no se apaga nunca.
4. **Textos de Configuración sin cambios** en esta etapa: la descripción de «Escalado inteligente» sigue hablando del de NVIDIA/Intel porque el motor nuevo está oculto; se reescribe en la etapa 5, con el selector de motor.
5. **Créditos:** «Anime4K» entra en la lista de tecnologías de «Acerca de» (licencia MIT; cada shader lleva su aviso de licencia en la cabecera).

## Restricciones globales

- **Sin commit ni push** hasta que el usuario lo ordene. Sin ramas, worktrees ni subagentes.
- **Con el ajuste por defecto (`"Flyleaf"`) no cambia nada.** `ReproductorView` no se modifica. El reproductor decide por lo que dice el contrato del motor, nunca por `UsaMotorMpv`.
- **Frontera nativa:** `[LibraryImport]`, UTF-8, `LibMpv.Disponible`. Números hacia libmpv con `CultureInfo.InvariantCulture`.
- Los shaders son archivos de solo lectura junto a la app: nada se escribe en la carpeta de instalación.
- **Textos visibles:** claves ES y EN (skill `localizacion`). **Verificación:** `repo-build-test`; en la app, `perfil-aislado`, nunca más del 85 % de un episodio.
- Mensajes, comentarios y nombres en español.

## Foco de revisión

1. **Faltan los shaders en la instalación** (copia incompleta): pedir el escalado no lanza, el motor dice que no lo tiene puesto y el informe dice «apagado». → Tarea 2, `SinLosArchivos_ElEscaladoNoSeEnciende`.
2. **El contador de perdidos baja** (archivo nuevo, libmpv lo pone a cero): el vigilante empieza de nuevo, no cuenta en negativo ni apaga. → Tarea 3, `SiElContadorBaja_EmpiezaDeNuevo`.
3. **Un tirón suelto** (salto, compilación de shaders al abrir): una o dos ventanas malas no apagan. → Tarea 3, `UnTironSuelto_NoApaga`.
4. **Modo «Activado» con el equipo sin poder:** no se apaga solo (el usuario lo pidió así). → Tarea 4, `EnActivado_NoSeApagaAunquePierdaFotogramas`.
5. **Ajuste con un valor raro** (`null`, vacío, texto desconocido en `settings.json`): se trata como Automático. → Tarea 1, `UsarEscaladoInteligente_*`.

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
|---|---|---|
| `AnimeLocalTracker/Shaders/Anime4K/*.glsl` (5 archivos, 112 KB) | Crear | `Clamp_Highlights`, `Restore_CNN_S`, `Upscale_CNN_x2_S`, `Restore_CNN_M`, `Upscale_CNN_x2_M` |
| `AnimeLocalTracker/AnimeLocalTracker.csproj` | Modificar | Copiar `Shaders\**` a la salida |
| `AnimeLocalTracker/Services/IMotorVideo.cs` | Modificar | `ClaseDeEscalado`, `FotogramasPerdidos` |
| `AnimeLocalTracker/Services/MotorVideo.cs` | Modificar | Política: encender, nivel, lista de shaders |
| `AnimeLocalTracker/Services/VigilanteEscalado.cs` | Crear | Cuándo apagar (puro) |
| `AnimeLocalTracker/Services/MotorFlyleaf.cs`, `MotorMpv.cs` | Modificar | Implementar el contrato; cargar los shaders |
| `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` | Modificar | Vigilar en el bucle de progreso |
| `AnimeLocalTracker/Services/LocalizationService.cs`, `Views/AcercaDeView.xaml` | Modificar | Crédito de Anime4K |
| `AnimeLocalTracker.Tests/…` | Modificar/crear | Pruebas |

---

## Tarea 1: shaders, contrato y política

**Archivos:** los cinco `.glsl`, `AnimeLocalTracker.csproj`, `IMotorVideo.cs`, `MotorVideo.cs`, `MotorFlyleaf.cs`, `MotorMpv.cs` (provisional), `MotorDePrueba.cs`, `RendimientoVideoTests.cs`.

```csharp
// IMotorVideo.cs
/// <summary>Cómo mejora la imagen cada motor: con el escalado del controlador de la tarjeta (solo algunas, no se puede medir su coste)
/// o con sombreadores propios (en cualquier tarjeta, y se ve cuándo no dan abasto).</summary>
public enum ClaseEscalado { PorControlador, PorSombreadores }
public enum NivelEscalado { Ligero, Medio }

// en la interfaz
ClaseEscalado ClaseDeEscalado { get; }
/// <summary>Fotogramas que el motor no llegó a pintar a tiempo desde que abrió el archivo. 0 si el motor no lo sabe.</summary>
long FotogramasPerdidos { get; }

// MotorVideo.cs
public static bool UsarEscaladoInteligente(string? modo, FabricanteGrafica fabricante, long memoriaMb,
    ClaseEscalado clase = ClaseEscalado.PorControlador) => modo switch
{
    ModosRendimientoVideo.Activado => true,
    ModosRendimientoVideo.Desactivado => false,
    // Con sombreadores funciona en cualquier tarjeta y el reproductor lo apaga si pierde fotogramas (VigilanteEscalado).
    _ => clase == ClaseEscalado.PorSombreadores || (fabricante is FabricanteGrafica.Nvidia or FabricanteGrafica.Intel && memoriaMb >= 2048)
};

/// <summary>El nivel de Anime4K que suele aguantar cada tarjeta: el medio pierde fotogramas en una integrada de Intel (medido).</summary>
public static NivelEscalado NivelAnime4K(FabricanteGrafica fabricante) =>
    fabricante is FabricanteGrafica.Nvidia or FabricanteGrafica.Amd ? NivelEscalado.Medio : NivelEscalado.Ligero;

/// <summary>Los shaders de un nivel, en el orden en que se aplican.</summary>
public static IReadOnlyList<string> SombreadoresAnime4K(NivelEscalado nivel)
{
    string v = nivel == NivelEscalado.Medio ? "M" : "S";
    return ["Anime4K_Clamp_Highlights.glsl", $"Anime4K_Restore_CNN_{v}.glsl", $"Anime4K_Upscale_CNN_x2_{v}.glsl"];
}
```

- `MotorFlyleaf`: `ClaseDeEscalado => PorControlador`, `FotogramasPerdidos => 0`. `MotorMpv` (provisional): `PorSombreadores`, `0`. `MotorDePrueba`: las dos con `set`.
- `csproj`: `<None Include="Shaders\**"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></None>`.

- [ ] **Paso 1: pruebas que fallan** (`RendimientoVideoTests`): `UsarEscaladoInteligente_ConSombreadores_AutomaticoEnciendeEnCualquierTarjeta` (Intel integrada con 0 MB, desconocida); `…_DesactivadoNuncaYActivadoSiempre`; `…_UnValorRaroEsAutomatico` (`null`, `""`, `"cualquiera"`); las pruebas existentes de la clase por controlador no se tocan; `NivelAnime4K_*`; `SombreadoresAnime4K_ListaLosTresDeCadaNivelEnOrden`; `LosShadersDeAnime4K_EstanJuntoALaApp` (los cinco existen en `AppContext.BaseDirectory\Shaders\Anime4K` y empiezan por el aviso `// MIT License`).
- [ ] **Paso 2:** verlas fallar. **Paso 3:** copiar los cinco shaders del paquete de Anime4K v4.0.1 e implementar. **Paso 4:** compilar (0 advertencias) y `--filter "FullyQualifiedName~RendimientoVideo"`.

## Tarea 2: `MotorMpv` carga y quita los shaders

**Archivos:** `MotorMpv.cs`, `MotorMpvTests.cs`.

```csharp
private volatile bool _escalado;
/// <summary>Junto a la app. Las pruebas la cambian para simular una instalación incompleta.</summary>
internal string CarpetaDeSombreadores { get; set; } = Path.Combine(AppContext.BaseDirectory, "Shaders", "Anime4K");

public ClaseEscalado ClaseDeEscalado => ClaseEscalado.PorSombreadores;
public long FotogramasPerdidos => _hayArchivo && long.TryParse(Leer("frame-drop-count"), out long n) ? n : 0;

public bool EscaladoInteligente
{
    get => _escalado;
    set
    {
        if (value == _escalado) return;
        Ordenar("change-list", "glsl-shaders", "clr", "");
        _escalado = value && CargarSombreadores();
    }
}

/// <summary>False si falta algún archivo (instalación incompleta): mejor sin escalado que con medio juego de shaders.</summary>
private bool CargarSombreadores()
{
    var rutas = MotorVideo.SombreadoresAnime4K(NivelPedido()).Select(n => Path.Combine(CarpetaDeSombreadores, n)).ToList();
    if (!rutas.All(File.Exists))
    {
        AppLogger.Warn("MotorMpv", $"Faltan los shaders de Anime4K en '{CarpetaDeSombreadores}': el escalado inteligente no se enciende.");
        return false;
    }
    return rutas.All(r => Ordenar("change-list", "glsl-shaders", "append", r));
}
```

- `NivelPedido()`: la variable `ANIMELOCALTRACKER_ANIME4K_NIVEL` si vale `Ligero` o `Medio`; si no, `MotorVideo.NivelAnime4K(TarjetaEnUso?.Fabricante ?? Desconocido)`. El nivel elegido se anota en el registro al encender.

- [ ] **Paso 1: pruebas que fallan** (libmpv real sin pantalla): `EncenderElEscalado_CargaLosTresShadersDelNivelLigero_YApagarloLosQuita` (`LeerPropiedad("glsl-shaders")` contiene `Anime4K_Restore_CNN_S.glsl` y, tras apagar, queda vacío; `EscaladoInteligente` lo refleja); `ConTarjetaNvidia_ElNivelEsElMedio` (tras `TarjetaDetectada("GeForce RTX 3060", "NVIDIA Corporation")`, contiene `_M.glsl`); `SinLosArchivos_ElEscaladoNoSeEnciende` (carpeta vacía → `false`, sin lanzar, `glsl-shaders` vacío); `LosFotogramasPerdidos_SinArchivoSonCero`.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** `--filter "FullyQualifiedName~MotorMpv"`.

## Tarea 3: el vigilante

**Archivos:** `Services/VigilanteEscalado.cs`, `AnimeLocalTracker.Tests/Services/VigilanteEscaladoTests.cs`.

```csharp
/// <summary>
/// Decide cuándo el escalado inteligente no da abasto: varias ventanas seguidas perdiendo fotogramas. Un salto o la compilación de los
/// shaders al abrir dan un tirón suelto, que no cuenta. Medido en una Intel HD 620: el nivel medio pierde 9 por segundo; el ligero, 0.
/// </summary>
public sealed class VigilanteEscalado
{
    public TimeSpan Ventana { get; init; } = TimeSpan.FromSeconds(2);
    public int PerdidosPorVentana { get; init; } = 3;
    public int VentanasSeguidas { get; init; } = 4;

    private TimeSpan _inicio;
    private long _perdidosAlInicio = -1;
    private int _malas;

    public void Reiniciar() { _perdidosAlInicio = -1; _malas = 0; }

    /// <returns>True cuando toca apagarlo.</returns>
    public bool Anotar(long perdidos, TimeSpan ahora)
    {
        if (_perdidosAlInicio < 0 || perdidos < _perdidosAlInicio) // primera muestra, o archivo nuevo (el contador volvió a cero)
        {
            (_inicio, _perdidosAlInicio, _malas) = (ahora, perdidos, 0);
            return false;
        }
        if (ahora - _inicio < Ventana) return false;

        _malas = perdidos - _perdidosAlInicio >= PerdidosPorVentana ? _malas + 1 : 0;
        (_inicio, _perdidosAlInicio) = (ahora, perdidos);
        return _malas >= VentanasSeguidas;
    }
}
```

- [ ] **Paso 1: pruebas que fallan:** `SinPerdidas_NuncaApaga`; `CuatroVentanasSeguidasPerdiendo_Apaga` (y no antes de la cuarta); `UnTironSuelto_NoApaga` (dos malas, una buena, dos malas → no); `SiElContadorBaja_EmpiezaDeNuevo`; `TrasReiniciar_EmpiezaDeNuevo`.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** `--filter "FullyQualifiedName~VigilanteEscalado"`.

## Tarea 4: el reproductor vigila y apaga

**Archivos:** `ReproductorViewModel.cs`, `ReproductorConMotorTests.cs`.

- En `AplicarRendimientoSegunEquipo`: `MotorVideo.UsarEscaladoInteligente(modo, tarjeta.Fabricante, tarjeta.MemoriaMb, motor.ClaseDeEscalado)`; el informe publica `motor.EscaladoInteligente` (lo que quedó puesto de verdad, no lo pedido); `_vigilarEscalado = motor.EscaladoInteligente && motor.ClaseDeEscalado == ClaseEscalado.PorSombreadores && modo no es Activado`; `VigilanteEscalado.Reiniciar()`.
- En `RastrearProgresoAsync`, en cada vuelta mientras reproduce: `if (_vigilarEscalado && VigilanteEscalado.Anotar(motor.FotogramasPerdidos, _relojVigilante.Elapsed)) ApagarEscaladoPorRendimiento(motor);`
- `ApagarEscaladoPorRendimiento`: `motor.EscaladoInteligente = false`, `_vigilarEscalado = false`, aviso en el registro («El escalado inteligente perdía fotogramas: se apaga en este episodio») y se vuelve a publicar el informe con el escalado apagado.
- `internal VigilanteEscalado VigilanteEscalado { get; set; } = new();` para que las pruebas usen ventanas de milisegundos.

- [ ] **Paso 1: pruebas que fallan** (motor de prueba con `ClaseDeEscalado = PorSombreadores` y `TarjetaEnUso` de Intel con 0 MB): `ConSombreadores_EnAutomaticoSeEnciendeAunqueLaTarjetaSeaIntegrada`; `EnAutomatico_SiPierdeFotogramasDeFormaSostenida_SeApagaYLoDiceElInforme`; `EnActivado_NoSeApagaAunquePierdaFotogramas`; `AlCambiarDeEpisodio_VuelveAIntentarlo`; con el motor clásico (`PorControlador`) las pruebas de hoy no cambian.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** compilar (0 advertencias) y `--filter "FullyQualifiedName~ReproductorConMotor|FullyQualifiedName~RendimientoVideo"`.

## Tarea 5: crédito de Anime4K

**Archivos:** `LocalizationService.cs` (`Acerca_Tecnologias` en ES y EN: añadir «Anime4K»), `AcercaDeView.xaml` (una ficha `Anime4K` junto a `libmpv`), `VistasSinTextoFijoTests.cs` (`"Anime4K"` entre los nombres propios permitidos), `README.md` (una línea en los créditos: Anime4K, licencia MIT).

- [ ] **Paso 1:** aplicar. **Paso 2:** compilar (0 advertencias) y `--filter "FullyQualifiedName~VistasSinTextoFijo|FullyQualifiedName~Localizacion"`.

## Tarea 6: verificación final

- [ ] **Paso 1:** compilar la app (0 advertencias).
- [ ] **Paso 2: en la app**, perfil aislado con rutas reales, `MotorVideo=Mpv`, registro de libmpv:
  1. **Automático** (el valor por defecto): la línea «Video: …» dice «escalado sí»; libmpv tiene cargados los tres shaders del nivel ligero; 45 s sin tocar con la ventana a la vista: 24 fotogramas por segundo y 0 perdidos; memoria.
  2. **Se nota:** la misma imagen en pausa con «Desactivado» y con «Activado» (dos arranques, mismo instante), recorte ampliado de las dos capturas lado a lado. Si con los videos de la biblioteca (720p y 1080p en pantalla de 768) la diferencia no se aprecia, decirlo tal cual en el informe.
  3. **Se apaga solo:** con `ANIMELOCALTRACKER_ANIME4K_NIVEL=Medio` (el que esta gráfica no aguanta) y Automático: a los pocos segundos el registro dice que se apaga, libmpv se queda sin shaders y los fotogramas vuelven a 24 por segundo. Con «Activado» y el mismo nivel forzado: no se apaga.
  4. **Episodio siguiente** tras un apagado: vuelve a intentarlo.
  5. **AV1** (por procesador) con el nivel ligero: fotogramas por segundo y perdidos.
  6. **Con Flyleaf:** el ajuste sigue haciendo lo de hoy (en esta Intel integrada, «escalado no» en Automático) y no se carga nada de libmpv.
  - Cerrar por PID, borrar el perfil, 0 diferencias en los datos reales y en las carpetas de video.
- [ ] **Paso 3:** suite completa una vez con el código final.
- [ ] **Paso 4:** informe al usuario (qué se verificó y qué no, cuánto se nota, memoria) y esperar la orden para el commit.

## Lo que esta etapa deja para las siguientes

- Etapa 5: motor por defecto y selector «Motor de video»; reescribir la descripción de «Escalado inteligente»; ocultar «Tarjeta gráfica» y el borde de la caja con el motor nuevo; modo de compatibilidad sin OpenGL.
- Etapa 6: retirada de Flyleaf.
- Bajar de nivel (medio → ligero) antes de apagar: hoy en Automático se apaga directamente; se añade si aparece un equipo donde haga falta.

---

## Notas de ejecución (2026-10-11)

**Lo medido en la app contradijo al prototipo y cambió una decisión.** En el arnés, el nivel ligero no perdía fotogramas a 1080p. En la app (Intel HD 620, 1366×768, Youjo Senki a 1080p), sí:

| Caso (90 s sin tocar) | Fotogramas pintados | Perdidos | Memoria |
|---|---|---|---|
| Sin escalado | 2229 | 2 | 496 MB |
| Nivel ligero forzado («Activado»), shaders originales | 1474 | 749 (8 por segundo) | 631 MB |
| Automático, shaders originales | — | ~85 en 10 s y el vigilante lo apagó (dos episodios) | 490 MB |
| Automático, con la condición añadida | 2219 | 4 | 520 MB |
| Automático, con la condición, AV1 por procesador (60 s) | 1512 | 10 | 585 MB |

- La causa: «restaurar» y «recortar brillos» corrían siempre, aunque el video no se ampliara (los de ampliar ya se saltaban solos). En el arnés la ventana no hacía nada más; en la app el hilo de la interfaz y la composición de WPF compiten con ellos.
- **Cambio:** esos tres archivos llevan ahora, en cada pasada, la misma condición `//!WHEN` que los de ampliación traen de origen: todo el juego de Anime4K actúa solo si el video se agranda al menos un 20 % en pantalla (`AnimeLocalTracker/Shaders/Anime4K/LEEME.md`; prueba `LosShadersQueNoAmplian_SoloActuanSiElVideoSeAgranda`). Es lo que dice la descripción del ajuste («al ampliar videos de menor resolución») y lo que ya hacía el escalado de Flyleaf.
- **Consecuencia en este equipo:** con videos de 720p y 1080p en una pantalla de 768 de alto, Anime4K no hace nada (la imagen es idéntica: diferencia 0,10 de 765) y no cuesta fotogramas. Con un video de 480p sí actúa (diferencia media 5,82, máxima 138: bordes más limpios y menos halo de color; el cambio es sutil a simple vista).
- El apagado automático quedó verificado en la app antes del cambio: se apagó a los 10 s en dos episodios seguidos y el siguiente volvió a intentarlo. Después del cambio no se puede provocar con la biblioteca actual (no hay videos que se amplíen).
- La línea de salida del registro detallado dice ahora los fotogramas perdidos y si el escalado seguía encendido.
- La prueba del reproductor no comprueba el informe publicado (`MotorVideo.UltimoInforme` es estático y lo comparten pruebas en paralelo).

No verificado en la app: el nivel medio (no hay tarjeta dedicada), un video que se amplíe (ninguno en la biblioteca; visto solo en el arnés con un recorte de 480p) y «Activado» sin apagado con la condición puesta.
