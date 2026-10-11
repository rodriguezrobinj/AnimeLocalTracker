# Plan de implementación: motor libmpv, etapa 5 (motor predeterminado)

> **Para quien lo ejecute:** SUB-SKILL OBLIGATORIO: `superpowers:executing-plans`, tarea a tarea, en esta misma sesión (en este repo no se usan subagentes salvo que el usuario lo pida). Los pasos usan casillas (`- [ ]`) para llevar la cuenta.

**Objetivo:** que libmpv sea el motor con el que se abre un episodio si no se dice otra cosa; que en Configuración → Reproducción se pueda elegir «Motor de video: Nuevo / Clásico»; y que en un equipo donde el motor nuevo no pueda pintar (sin OpenGL: máquina virtual, escritorio remoto, controlador antiguo) el episodio se abra solo con el clásico. Además, dejar medido si libmpv puede pintar por software, que es lo que decidirá si Flyleaf se puede retirar en la etapa 6.

**Arquitectura:** el ajuste `MotorVideo` deja de estar oculto y su valor por defecto pasa a `"Mpv"`. La elección sigue en `MotorVideo.ElegirMotor`, que ahora también sabe si el lienzo OpenGL falló en esta sesión. Si el lienzo falla al abrir un episodio, el reproductor cambia al motor clásico sobre la marcha (la plantilla de `App.xaml` ya elige la vista por `UsaMotorMpv`, que pasa a ser observable). Configuración gana una fila y adapta tres cosas al motor elegido.

**Tecnologías:** C# .NET 8, WPF, libmpv 0.41, OpenTK.GLWpfControl 4.3.6, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-motor-libmpv.md`, secciones 5 (modo de compatibilidad), 7 y 10. Etapas anteriores: planes 2 a 4 con sus notas de ejecución.

## Esta etapa es el punto de no retorno práctico

Hasta aquí todo se revertía quitando una línea de `settings.json`. A partir de esta etapa, quien instale la versión siguiente verá sus episodios con libmpv sin haber hecho nada. Se puede volver al clásico desde Configuración (y Flyleaf sigue instalado hasta la etapa 6), pero ya no es un experimento.

**Lo que se sabe que es distinto con el motor nuevo** (aceptado en las etapas 3 y 4): no se dibuja el borde de la caja de los subtítulos; el tamaño de la letra es proporcional a la ventana; la captura incluye los subtítulos; no se elige tarjeta gráfica; no hay precarga del episodio siguiente; el escalado inteligente solo actúa si el video se amplía; gasta unos 40–60 MB más de memoria mientras reproduce.

**Lo que nunca se ha probado en la app con el motor nuevo** y esta etapa prueba antes de darlo por bueno (tarea 5): que un episodio llegue a su final (marcar visto, cuenta atrás, episodio siguiente), un archivo con subtítulos SRT sueltos y el inicio tras «Continuar» desde Historial con el motor por defecto. **Lo que sigue sin poder probarse aquí:** la captura de fotograma (escribe en Imágenes y en el portapapeles: la cubre una prueba sin pantalla) y cambiar el tamaño del mini reproductor arrastrando un borde.

## Decisiones de esta etapa

1. **Valor por defecto `"Mpv"`.** Un `settings.json` sin la clave (todas las instalaciones de hoy) pasa al motor nuevo al actualizar. Quien tenga `"Flyleaf"` escrito a mano se queda con el clásico.
2. **Si falta `libmpv-2.dll`** (instalación incompleta) se usa el clásico, como hoy, y la fila de Configuración lo dice.
3. **Modo de compatibilidad = motor clásico** mientras exista Flyleaf: si el lienzo OpenGL no arranca, el episodio se reabre con Flyleaf en el acto, se anota en el registro y el resto de la sesión ya abre con el clásico sin intentarlo de nuevo. No se cambia el ajuste guardado (puede ser un escritorio remoto ocasional).
4. **Configuración:** con «Nuevo» se ocultan «Tarjeta gráfica» y los dos controles del borde de la caja de subtítulos (no hacen nada con ese motor), y la descripción de «Escalado inteligente» habla de Anime4K. El cambio de motor se aplica al abrir el siguiente episodio.
5. **Salida por software:** solo se mide (arnés desechable y, si los números lo merecen, una prueba dentro de la app con una variable de diagnóstico). No se implementa como modo de compatibilidad en esta etapa. Lo aprendido en la etapa 4 obliga a no fiarse del arnés para dar algo por bueno.
6. **Publicar la versión, el aviso de la GPL en la página de la release y archivar el paquete de libmpv** quedan en manos del propietario; el plan deja escrito el texto del aviso.

## Restricciones globales

- **Sin commit ni push** hasta que el usuario lo ordene. Sin ramas, worktrees ni subagentes.
- `ReproductorView` (la vista clásica) no se modifica. El reproductor no pregunta qué motor es salvo para elegir cuál crear.
- **Textos visibles nuevos:** claves ES y EN en `LocalizationService`, `{loc:T}` en XAML (skill `localizacion`). Colores de la paleta. Botones y desplegables con `AutomationProperties.Name`.
- **En la app nunca se pulsa «Guardar Preferencias»** (escribe en el registro real de Windows): lo que guarda Configuración se cubre con pruebas del ViewModel; las opciones se preparan en el `settings.json` del perfil antes de lanzar.
- **Verificación de C#:** `repo-build-test`. **En la app:** `perfil-aislado`. El final de episodio se prueba solo con **episodios de mentira de 30 s** en la biblioteca de prueba del perfil (rutas reescritas), nunca con los reales.
- Mensajes, comentarios y nombres en español.

## Foco de revisión

1. **`settings.json` con un valor raro en `MotorVideo`** (`null`, vacío, `"mpv"` en minúsculas, `"Nuevo"`, basura): todo lo que no sea `"Flyleaf"` es el motor nuevo; `"flyleaf"` en minúsculas es el clásico. → Tarea 1, `ElegirMotor_*`.
2. **El lienzo falla a mitad de sesión** (segundo episodio, mini reproductor): el reproductor pasa al clásico sin cerrar la app ni perder el punto del episodio. → Tarea 2, `SiElLienzoFalla_ElEpisodioSeReabreConElMotorClasicoDondeIba`.
3. **El lienzo falla y tampoco hay motor clásico** (entorno sin video): aviso de error normal, sin bucle de reintentos. → Tarea 2, `SiElLienzoFallaYNoHayMotorClasico_AvisaUnaVez`.
4. **Cambiar el motor en Configuración con un episodio en el mini reproductor:** el que está sonando sigue con su motor; el siguiente usa el nuevo valor. → Tarea 1, `ElMotorSeEligeAlCrearElReproductor_NoAMitadDeEpisodio`.
5. **Configuración cargada con `"Flyleaf"` y sin biblioteca libmpv:** el desplegable muestra «Clásico», no se queda vacío, y guardar no cambia el valor. → Tarea 3, `SinLibmpv_ConfiguracionMuestraElClasicoYNoLoCambiaAlGuardar`.

---

## Mapa de archivos

| Archivo | Acción | Responsabilidad |
|---|---|---|
| `AnimeLocalTracker/Models/AppSettings.cs` | Modificar | `MotorVideo = "Mpv"` |
| `AnimeLocalTracker/Services/MotorVideo.cs` | Modificar | `ElegirMotor`, `LienzoNoDisponible`, constantes de motor |
| `AnimeLocalTracker/ViewModels/ReproductorViewModel.cs` | Modificar | Elegir motor; `PasarAlMotorClasico` |
| `AnimeLocalTracker/Controls/SuperficieMpv.cs`, `Views/ReproductorMpvView.xaml.cs` | Modificar | Crear el lienzo sin tumbar la app; avisar al reproductor |
| `AnimeLocalTracker/ViewModels/ConfiguracionViewModel.cs`, `Views/ConfiguracionView.xaml` | Modificar | Fila «Motor de video»; adaptar tres controles |
| `AnimeLocalTracker/Services/LocalizationService.cs` | Modificar | Textos ES/EN |
| `README.md`, `docs/` | Modificar | Motor por defecto; texto del aviso para la release; medición de la salida por software |
| `AnimeLocalTracker.Tests/…` | Modificar | Pruebas |

---

## Tarea 1: libmpv por defecto

**Archivos:** `AppSettings.cs`, `MotorVideo.cs`, `ReproductorViewModel.cs`, `RendimientoVideoTests.cs`, `ReproductorConMotorTests.cs`.

```csharp
// MotorVideo.cs
public static class MotoresDeVideo
{
    public const string Nuevo = "Mpv", Clasico = "Flyleaf";
    /// <summary>Lo que hay escrito en settings.json → uno de los dos valores. Todo lo que no sea el clásico es el nuevo.</summary>
    public static string Normalizar(string? ajuste) =>
        string.Equals(ajuste?.Trim(), Clasico, StringComparison.OrdinalIgnoreCase) ? Clasico : Nuevo;
}

/// <summary>En esta sesión el lienzo OpenGL del motor nuevo no arrancó: se deja de intentar hasta reiniciar la app.</summary>
public static bool LienzoNoDisponible { get; internal set; }

/// <summary>True si toca el motor nuevo: no se pidió el clásico, la biblioteca está en la instalación y el lienzo no ha fallado.</summary>
public static bool ElegirMotor(string? ajuste, bool mpvDisponible) =>
    mpvDisponible && !LienzoNoDisponible && MotoresDeVideo.Normalizar(ajuste) == MotoresDeVideo.Nuevo;
```

- `AppSettings.MotorVideo = MotoresDeVideo.Nuevo`.
- `ReproductorViewModel`: `pideMpv` pasa a ser `MotoresDeVideo.Normalizar(ajuste) == Nuevo`; el aviso «el ajuste pide libmpv pero su biblioteca no está» se queda.

- [ ] **Paso 1: pruebas que fallan:** `ElegirMotor_TodoLoQueNoSeaElClasicoEsElNuevo` (teoría con `null`, `""`, `"Mpv"`, `"mpv"`, `"Nuevo"`, `"x"` → nuevo; `"Flyleaf"`, `" flyleaf "` → clásico); `ElegirMotor_SinBibliotecaOConElLienzoRoto_EsElClasico`; `AppSettings_PorDefectoPideElMotorNuevo`; `ElMotorSeEligeAlCrearElReproductor_NoAMitadDeEpisodio`. Se actualiza la teoría existente `ConElAjusteEnMpvPeroSinBiblioteca_SeUsaFlyleaf`.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** compilar (0 advertencias) y `--filter "FullyQualifiedName~RendimientoVideo|FullyQualifiedName~ReproductorConMotor"`.

## Tarea 2: modo de compatibilidad

**Archivos:** `SuperficieMpv.cs`, `ReproductorMpvView.xaml.cs`, `ReproductorViewModel.cs`, `ReproductorConMotorTests.cs`.

- `SuperficieMpv.Para(motor)` devuelve `null` si el lienzo no se puede crear (`GLWpfControl.Start` lanza) o si la variable de diagnóstico `ANIMELOCALTRACKER_SIN_OPENGL=1` está puesta; lo anota como aviso. Si `CrearPintor` falla (libmpv no acepta el contexto), avisa por el evento `SinSalida` del motor en vez de reintentar en cada fotograma.
- `ReproductorMpvView.AlojarSuperficie`: si no hay superficie, o llega `SinSalida`, llama a `vm.PasarAlMotorClasico("motivo")`.
- `ReproductorViewModel`:

```csharp
[ObservableProperty] private bool _usaMotorMpv; // antes de solo lectura: la plantilla de App.xaml elige la vista con ella

/// <summary>Fábrica del motor clásico para el modo de compatibilidad. Las pruebas la sustituyen.</summary>
internal Func<AjustesInicialesMotor, IMotorVideo?> FabricaMotorClasico { get; set; } = MotorFlyleaf.Crear;

/// <summary>
/// El motor nuevo no puede pintar en este equipo (sin OpenGL): el episodio se reabre con el clásico donde iba y el resto de la
/// sesión ya no lo intenta. No toca el ajuste guardado.
/// </summary>
public void PasarAlMotorClasico(string motivo)
{
    if (!UsaMotorMpv) return; // ya se pasó (el aviso puede llegar dos veces)
    AppLogger.Warn("ReproductorViewModel", $"El motor nuevo no puede pintar en este equipo ({motivo}): se sigue con el motor clásico.");
    MotorVideo.LienzoNoDisponible = true;

    double segundos = Math.Max(CurrentSeconds, _posicionInicioSegundos);
    var anterior = Motor;
    if (anterior != null) anterior.AperturaTerminada -= AlTerminarApertura;
    Motor = null;
    anterior?.Dispose();

    _fabricaMotor = FabricaMotorClasico;
    UsaMotorMpv = false;                 // la plantilla cambia a la vista clásica
    AsegurarMotorInicializado();
    if (Motor == null) { NotificarFalloAlAbrir(LocalizationService.T("Player_ErrorSinVideo")); return; }

    _posicionInicioSegundos = segundos;  // se reanuda donde iba
    _haCompletadoOpen = false;
    Motor.Abrir(_rutaVideo);
}
```

  (Los nombres exactos de los campos de reanudación se toman del código al implementar; `_fabricaMotor` deja de ser de solo lectura.)
- Clave nueva `Player_ErrorSinVideo` (ES: «Este equipo no puede mostrar video con ninguno de los dos motores.»; EN equivalente) solo si no existe ya un texto que sirva.

- [ ] **Paso 1: pruebas que fallan** (motor de prueba como «nuevo», otro como «clásico» en `FabricaMotorClasico`; `UsaMotorMpv` se fuerza con un constructor interno o propiedad interna para pruebas): `SiElLienzoFalla_ElEpisodioSeReabreConElMotorClasicoDondeIba` (el motor nuevo se desecha, el clásico abre la misma ruta, `UsaMotorMpv` pasa a false y `MotorVideo.LienzoNoDisponible` a true; al llegar su primer fotograma salta al punto donde iba); `ElAvisoDosVeces_SoloCambiaUnaVez`; `SiElLienzoFallaYNoHayMotorClasico_AvisaUnaVez`. Cada prueba restaura `MotorVideo.LienzoNoDisponible = false` al terminar.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** compilar (0 advertencias) y `--filter "FullyQualifiedName~ReproductorConMotor"`.

## Tarea 3: Configuración

**Archivos:** `ConfiguracionViewModel.cs`, `ConfiguracionView.xaml`, `LocalizationService.cs`, pruebas de Configuración y de vistas.

- `ConfiguracionViewModel`: `[ObservableProperty] string _motorVideo` (se carga con `MotoresDeVideo.Normalizar` y se guarda tal cual); `bool MotorNuevoDisponible => LibMpv.Disponible` (inyectable para pruebas); `bool UsaMotorNuevo => MotorNuevoDisponible && MotorVideo == Nuevo` (avisa al cambiar `MotorVideo`). Sin biblioteca, se muestra el clásico pero **no se pisa** el valor guardado.
- `ConfiguracionView.xaml`, grupo de rendimiento de video, primera fila:

```xml
<ctl:SettingRow Title="{loc:T Cfg_MotorVideo}" Description="{loc:T Cfg_MotorVideoSub}">
    <ComboBox SelectedValue="{Binding MotorVideo}" SelectedValuePath="Tag" Height="36" MinWidth="230"
              IsEnabled="{Binding MotorNuevoDisponible}" AutomationProperties.Name="{loc:T Cfg_MotorVideo}">
        <ComboBoxItem Content="{loc:T Cfg_MotorVideo_Nuevo}" Tag="Mpv"/>
        <ComboBoxItem Content="{loc:T Cfg_MotorVideo_Clasico}" Tag="Flyleaf"/>
    </ComboBox>
</ctl:SettingRow>
```

  - «Tarjeta gráfica» (fila y su divisor) y los controles «Grosor del borde» / «Color del borde» de la caja de subtítulos: `Visibility` ligada a `UsaMotorNuevo` con el conversor inverso que ya use la vista.
  - Descripción de «Escalado inteligente»: `Cfg_EscaladoInteligenteSub` con el clásico y `Cfg_EscaladoInteligenteSubMpv` con el nuevo (disparador de datos sobre `UsaMotorNuevo`).
- Textos (ES; el inglés equivalente en la otra mitad):
  - `Cfg_MotorVideo`: «Motor de video»
  - `Cfg_MotorVideoSub`: «El motor nuevo (libmpv) dibuja los subtítulos con su estilo original y deja usar Anime4K. El clásico (Flyleaf) sigue disponible por si algo no va bien. Se aplica al abrir el siguiente episodio.»
  - `Cfg_MotorVideoSubNoDisponible`: «El motor nuevo no está en esta instalación: se usa el clásico.»
  - `Cfg_MotorVideo_Nuevo`: «Nuevo (libmpv)» · `Cfg_MotorVideo_Clasico`: «Clásico (Flyleaf)»
  - `Cfg_EscaladoInteligenteSubMpv`: «Mejora la nitidez al ampliar videos de menor resolución que la pantalla (Anime4K). Automático: se apaga solo si el equipo no da abasto.»

- [ ] **Paso 1: pruebas que fallan:** `Configuracion_CargaYGuardaElMotorDeVideo` (carga `"Flyleaf"` → muestra clásico; se elige nuevo → `GuardarConfiguracionAsync` recibe `"Mpv"`); `Configuracion_SinClaveEnElArchivo_MuestraElNuevo`; `SinLibmpv_ConfiguracionMuestraElClasicoYNoLoCambiaAlGuardar`; `UsaMotorNuevo_AvisaAlCambiarElMotor`; las pruebas de textos existentes (claves en las dos mitades, vistas sin texto fijo) cubren las claves y el XAML nuevos.
- [ ] **Paso 2:** verlas fallar. **Paso 3:** implementar. **Paso 4:** compilar (0 advertencias) y `--filter "FullyQualifiedName~Configuracion|FullyQualifiedName~Localiz|FullyQualifiedName~Vistas"`.

## Tarea 4: medir la salida por software (para decidir la etapa 6)

Desechable, fuera del repo (arnés del prototipo): libmpv con la interfaz de pintado por software (`MPV_RENDER_API_TYPE_SW`) sobre un `WriteableBitmap` del tamaño de la ventana.

- [ ] Medir 45 s sin tocar, a 1366×705, con Youjo Senki (1080p H.264) y Tokyo Revengers (1080p AV1): fotogramas pintados por segundo, perdidos, uso de procesador y memoria. Compararlo con el lienzo OpenGL en el mismo arnés.
- [ ] Anotar el resultado en `docs/investigacion-motor-libmpv.md` (sección 5, «Modo de compatibilidad») con la advertencia de que es una medida de arnés, y la recomendación para la etapa 6: retirar Flyleaf con este modo como respaldo, o mantener Flyleaf.

## Tarea 5: verificación en la app

- [ ] **Paso 1:** compilar la app (0 advertencias).
- [ ] **Paso 2: con los datos de siempre** (perfil aislado con rutas reales, `settings.json` **sin** la clave `MotorVideo`): un episodio abre con libmpv sin haber tocado nada; pausa, salto, subtítulos, mini reproductor y `Esc` (repaso de las etapas 2 y 3); «Continuar» desde Historial.
- [ ] **Paso 3: Configuración → Reproducción** (sin pulsar «Guardar Preferencias»): captura de la fila nueva; al cambiar el desplegable a «Clásico» aparecen «Tarjeta gráfica» y el borde de la caja y cambia la descripción del escalado; al volver a «Nuevo» se ocultan. Nombres accesibles presentes. En inglés (idioma del perfil cambiado en su `settings.json`) los textos salen traducidos.
- [ ] **Paso 4: con `"MotorVideo": "Flyleaf"`** en el perfil: abre con el clásico y no carga libmpv.
- [ ] **Paso 5: modo de compatibilidad**, con `ANIMELOCALTRACKER_SIN_OPENGL=1`: el episodio se abre (con el clásico) y el registro lo explica una vez; el episodio siguiente abre directamente con el clásico; nada se cierra.
- [ ] **Paso 6: final de episodio con episodios de mentira.** Perfil con rutas reescritas; en su biblioteca de prueba se generan con el FFmpeg de la app tres clips de 30 s en las rutas que la base copiada espera para tres episodios seguidos de un anime (ninguno el último), uno de ellos con un `.srt` suelto al lado. Con libmpv: el primero llega al final → se marca visto en la base del perfil → cuenta atrás → abre el segundo; el segundo muestra el subtítulo SRT con el estilo de Configuración. Se comprueba en el registro y en la base del perfil.
- [ ] **Paso 7:** cerrar por PID, borrar perfiles, 0 diferencias en los datos reales y en las carpetas de video.
- [ ] **Paso 8:** suite completa una vez con el código final.

## Tarea 6: documentación

- [ ] `README.md`: el reproductor usa libmpv por defecto y Flyleaf queda como motor clásico seleccionable.
- [ ] `docs/investigacion-motor-libmpv.md`: estado de las etapas y resultado de la tarea 4.
- [ ] Texto del aviso para la página de la release (en este plan, sección siguiente): lo publica el propietario.
- [ ] Informe al usuario y esperar la orden para el commit.

## Texto para la página de la release (lo publica el propietario)

> **Motor de video nuevo.** Desde esta versión los episodios se reproducen con mpv (libmpv), que dibuja los subtítulos con su estilo original y permite mejorar la imagen con Anime4K al ampliar videos de baja resolución. Si algo no va bien, el motor anterior sigue en Configuración → Reproducción → Motor de video → Clásico.
>
> El instalador crece unos 120 MB. Incluye mpv (compilación de shinchiro `20261011-git-accb7b2821`), distribuido bajo la GPL versión 2 o posterior; su código fuente está en https://github.com/mpv-player/mpv. Los shaders de Anime4K son de bloc97 (licencia MIT). El código de AnimeLocalTracker sigue bajo la licencia MIT.

## Lo que esta etapa deja para la siguiente

- Etapa 6: retirada de Flyleaf, solo después de una versión publicada y usada un tiempo sin volver al clásico, y según lo que diga la medición de la tarea 4.
- Pendiente de decisión del propietario: archivar el paquete de libmpv en una release del repositorio.

---

## Notas de ejecución (2026-10-11)

**Lo que cambió respecto al plan**

- **El aviso de «no puedo pintar» no pasa por la vista.** El plan decía que `ReproductorMpvView` llamara a `PasarAlMotorClasico`. Hay dos vistas posibles (ventana principal y mini reproductor) y el fallo puede darse dentro del pintado, donde solo se conoce el motor: `SuperficieMpv` avisa al motor (`MotorMpv.SinSalida`) y el reproductor se suscribe al crearlo (`CrearMotorNuevo`). El aviso se entrega en la siguiente vuelta del despachador, porque quien avisa está a medio cargar una vista o a medio pintar y la respuesta cierra ese motor.
- **La propiedad de Configuración se llama `MotorDeVideo`**, no `MotorVideo`: dentro de `ConfiguracionViewModel` chocaba con la clase estática `MotorVideo` (la que lista las tarjetas).
- **Configuración no carga libmpv para saber si está.** Pregunta por el archivo (`LibMpv.EnLaInstalacion`); `LibMpv.Disponible` carga 120 MB y, con el motor clásico elegido, entrar en Configuración habría cargado la biblioteca para nada.
- **Caso añadido:** si el fallo llega con el episodio a medias, el reproductor vuelve a tapar el video, lo reabre con el clásico y salta al punto donde iba (prueba `SiElLienzoFallaConElEpisodioAMedias_SigueDesdeEsePunto`). Las pistas de subtítulos se vuelven a evaluar con las del motor clásico.
- **Clave nueva `Player_ErrorSinVideo`:** el aviso genérico de «no se pudo abrir» dice «puede estar dañado o incompleto», que aquí sería falso.

**Quién cambia de motor al actualizar.** Ninguna versión publicada incluye el ajuste (se añadió el 2026-10-11 y la última versión, v1.1.0, es del 2026-09-14), así que ningún `settings.json` de usuario tiene la clave: todos pasan al motor nuevo. El `settings.json` real de esta máquina tampoco la tiene.

**Verificado en la app** (Intel HD 620, 1366×768, perfil aislado):

| Caso | Resultado |
|---|---|
| `settings.json` sin la clave, episodio real (25 s) | Abre con libmpv: 666 fotogramas, 6 perdidos, Anime4K ligero encendido, 521 MB |
| `"MotorVideo": "Flyleaf"` | Abre con el clásico; ningún módulo de libmpv cargado y ninguna línea suya en el registro; 417 MB |
| Configuración → Reproducción | Fila «Motor de video» con «Nuevo (libmpv)»; con «Nuevo» no existen «Tarjeta gráfica» ni los dos controles del borde y el escalado habla de Anime4K; al elegir «Clásico» aparecen los tres y vuelve el texto de NVIDIA/Intel; al volver a «Nuevo» desaparecen. En inglés: «Video engine», «New (libmpv)», textos traducidos. No se pulsó «Guardar Preferencias» |
| `ANIMELOCALTRACKER_SIN_OPENGL=1` | Un solo aviso en el registro; el episodio se reabrió con el clásico en el punto guardado (50,2 s) y se vio (523 fotogramas en 20 s); el episodio siguiente abrió directamente con el clásico, sin crear el motor nuevo; nada se cerró |
| Final de episodio (clips de 30 s en la biblioteca de prueba, umbral de visto al 50 %) | El episodio 1 llegó al final, se marcó visto en la base del perfil, salió la cuenta atrás («Siguiente episodio en · Episodio 3 · 2s» capturada al final del 2) y abrió el siguiente a los 5 s; lo mismo del 2 al 3 |
| SRT suelto junto al episodio 2 | «Pistas de subtítulos disponibles: 1» y el texto en pantalla con el estilo de Configuración |
| «Continuar» desde Historial | Abre con libmpv y reanuda en el punto guardado (8,0 s), imagen a los 541 ms |
| Salto y mini reproductor con el motor por defecto | Salto exacto recibido por libmpv; al navegar, el video pasa al mini reproductor y sigue |
| Datos reales | 0 diferencias en la carpeta de datos (salvo `biblioteca.db-shm`, que toca la copia de solo lectura al crear un perfil) |

**No verificado en la app:** la captura de fotograma (no se pulsa: escribe en Imágenes y en el portapapeles), redimensionar el mini reproductor arrastrando, y un equipo que de verdad no tenga OpenGL (el fallo se simuló con la variable de diagnóstico: que `GLWpfControl.Start` lance una excepción en ese equipo es lo esperado, no lo comprobado). La pausa no se repitió en esta etapa (verificada en la 2 y la 3; el código no cambió). No se hizo foto de antes y después de las carpetas de video: con rutas reales solo se reprodujeron 20–25 s de dos episodios. Las dos líneas que reinician los subtítulos en `PasarAlMotorClasico` se añadieron después de la prueba en la app del modo de compatibilidad; las cubre la suite.

**Límite conocido del modo de compatibilidad:** cubre que el lienzo no se pueda crear o que libmpv lo rechace. Si el lienzo se crea pero nunca llega a pintar, no hay cambio automático: salta el aviso normal de «el reproductor no respondió» y queda elegir «Clásico» en Configuración.

**Dos arreglos de la vista nueva, pedidos por el propietario antes de subir**

- **Los subtítulos ya no quedan detrás de la barra inferior.** libmpv los dibuja dentro del video y los controles van encima. Mientras los controles están a la vista, la vista sube los diálogos justo por encima de la barra de progreso (`ReproductorMpvView.ColocarSubtitulos` → `MotorMpv.ColocarSubtitulos` → `sub-pos`); al ocultarse vuelven a su sitio. Solo se mueven los diálogos de abajo: los carteles con posición propia no. Para que valga también con el estilo original de una pista ASS, `sub-ass-override` pasa de `no` a `scale` (el valor de fábrica de mpv): el estilo se conserva igual, pero con `no` libmpv ignora la posición. En la app (Youjo Senki, ASS): 90,1 % con los controles visibles y 100 % al ocultarse, con la letra del mismo tamaño en los dos casos. **Corrección a lo que se dijo al informar:** la vista clásica no los subía; allí también quedan detrás de la barra (captura de la etapa 3). No se toca: se retira en la etapa 6.
- **Los controles no se ocultaban con el ratón quieto sobre el video** (ni el cursor). Venía de la etapa 2 y salió al probar lo anterior: la capa de controles ocupa toda la vista y es transparente, así que «el ratón está sobre los controles» era cierto en cualquier punto del reproductor. Ahora solo cuenta la franja de arriba, la barra de abajo o el cajón. En la app: ratón quieto sobre el video → se ocultan a los 3 s; al moverlo vuelven; quieto sobre la barra inferior → siguen a la vista; de vuelta al video → se ocultan.

**Salida por software (tarea 4):** medida en un arnés; los números y la recomendación están en `docs/investigacion-motor-libmpv.md`, sección 5. En resumen: 24 fotogramas por segundo sin pérdidas en H.264 y AV1 a 1080p, 3 ms por fotograma y menos memoria que el lienzo; sirve como modo de compatibilidad para retirar Flyleaf, a falta de medirla dentro de la app.

Pruebas: 3217 de 3217 (17 nuevas). Compilación con 0 advertencias.
