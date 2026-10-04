# Auditoría de modularización y simplificación (2026-10-04)

**Objetivo:** localizar (1) funciones repetidas que se pueden compartir, (2) estilos, plantillas y animaciones repetidos en las vistas y (3) lógica que hoy resulta más complicada de lo necesario. **Solo se mide y se propone: no se cambió código.**

**Método:** análisis estático de todo `AnimeLocalTracker/` (53 389 líneas de C# en 281 archivos y 13 005 de XAML), con tres scripts guardados en `evidencia/`:

- `audit_xaml.py`: bloques `Style`/`ControlTemplate`/`Storyboard` idénticos, colores literales frente a la paleta de `App.xaml`, patrones repetidos.
- `audit_cs.py`: fragmentos clonados (ventana de 6 líneas significativas, ignorando textos y números) y conteo de patrones.
- `audit_complejidad.py`: ramas, longitud y anidamiento por método; campos, banderas y dependencias por clase.

**Límites:** son medidas estáticas. Un clon con nombres de variables distintos no se detecta; los ahorros son estimaciones; no se ejecutó la app. `LocalizationService.cs` (diccionario de textos) queda fuera del análisis de clones. No se repiten los temas de las auditorías de septiembre (seguridad, rendimiento, distribución).

---

## 1. Veredicto

1. **El copia-pega literal es bajo:** ≈550 líneas clonadas de 53 000 (alrededor del 1 %), la mitad dentro de un mismo archivo. No hay un problema general de código duplicado.
2. **Lo que se repite son patrones**, no bloques: el mismo gesto escrito a mano en muchos sitios (formatear un tamaño, lanzar un proceso, saltar al hilo de la interfaz, reaccionar al cambio de idioma, declarar una pestaña).
3. **En las vistas, la paleta existe pero no se usa del todo:** el 58 % de los colores escritos a mano ya tienen pincel en `App.xaml`.
4. **La complejidad está concentrada:** el 94 % de los métodos tiene menos de 60 líneas. Los focos son el reproductor, las descargas, la navegación por pestañas y la pantalla de Configuración.

## 2. Vistas: estilos, plantillas y animaciones

| # | Hallazgo | Evidencia | Propuesta | Riesgo |
|---|---|---|---|---|
| V1 | Colores literales que ya tienen pincel | 705 literales fuera de `App.xaml` (158 colores); **411 usos (58 %)** son de un color con pincel: `#60A5FA`×73 → `Brush.Accent`, `#1E293B`×54 → `Brush.Surface3`, `#9CA3AF`×49 → `Brush.TextSecondary`, `#262D38`×43 → `Brush.Border`, `#94A3B8`×41 → `Brush.TextTertiary` | Sustitución mecánica por `{StaticResource Brush.X}`. Dar pincel a los tres más usados sin él: `#6B7280`×30 (la regla del repo lo desaconseja para texto pequeño: revisar esos 30), `#1B222C`×18, `#121212`×17 | Bajo |
| V2 | Binding de localización escrito entero 848 veces | `{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}, Mode=OneWay}`; 211 de ellos sin `Mode=OneWay`. No hay ninguna extensión de marcado propia | Extensión `{loc:T Clave}` (unas 15 líneas de C#) que devuelve ese mismo binding, siempre unidireccional. Convierte la regla 1 de `wpf-mvvm.md` en algo que no se puede olvidar | Medio (cambio masivo; hay que verificar el cambio de idioma en caliente) |
| V3 | Barra lateral: el mismo botón escrito 10 veces | `MainWindow.xaml` líneas 71–420: 10 bloques de 33 líneas (rectángulo indicador + botón + estilo + icono con disparador), solo cambian el icono, el texto y la propiedad `EsXActivo` | Una plantilla y una lista de pestañas (ver L1). ≈300 líneas menos | Medio |
| V4 | Botón solo-icono con el texto repetido | 66 botones con `ToolTip` y `AutomationProperties.Name` con el mismo binding; `Cursor="Hand"` escrito 91 veces | Estilo base de botón de icono que copie el `ToolTip` al nombre accesible y ponga el cursor | Bajo |
| V5 | Estilos y plantillas idénticos entre vistas | Contenedor de fila transparente (`ListBoxItem`) en 5 vistas + variantes ×3 y ×4; tarjeta de `DetalleView` ≈ `GaleriaTarjeta` (19 + 21 + 13 líneas); plantilla de `ActualizacionesView` ≈ `HistorialView` (18 líneas). ≈190 líneas sobrantes | Moverlos a `App.xaml` o a `FichaEstilos.xaml` con clave | Bajo |
| V6 | Tipografía a mano | 457 `FontSize` y 321 `FontWeight` literales frente a 125 usos de los 7 estilos `AppText.*`; tamaños como 12.5, 11.5 y 10.5 conviven con 12, 11 y 10 | Completar la escala `AppText.*` y aplicarla al tocar cada vista (no como cambio masivo) | Bajo, gradual |
| V7 | Animaciones sin vocabulario común | 30 `Storyboard`, 60 `DoubleAnimation`; duraciones `0:0:0.2`×19 y `0:0:0.12`×18 repetidas; 16 curvas escritas en línea; 7 animaciones creadas en code-behind; los 4 menús del reproductor repiten borde, sombra y flecha | Recursos `Anim.Rapida`/`Anim.Normal` y una curva común en `App.xaml`; un estilo de "menú flotante" para los 4 `Popup` | Bajo |
| V8 | Dos vistas con paleta propia escrita a mano | `EstadisticasView` (129 literales) y `AnimeWrappedCardView` (104) | Un diccionario de recursos local por vista con nombres | Bajo |

Lo que está bien: solo hay 14 bloques `Style`/`ControlTemplate`/`Storyboard` idénticos entre archivos, ningún `DataTemplate` duplicado y una sola animación con `RepeatBehavior="Forever"`.

## 3. Funciones repetidas (C#)

| # | Hallazgo | Evidencia | Propuesta | Riesgo |
|---|---|---|---|---|
| F1 | Formato de tamaños | `FormatearTamano` definida 4 veces (`EpisodioItem`, `ISelectorTorrentService`, `DescargaHistorialItemViewModel`, `TemaAnimeItem`) más ≈30 cálculos con 1024 en línea (14 en `DownloadService`) | Una sola `Core/Formato.Tamano(long)` | Bajo |
| F2 | Lanzar procesos | 20 `new ProcessStartInfo` en 15 archivos. Dos familias: abrir una URL o carpeta (≈9, en ViewModels) y ejecutar ffmpeg/ffprobe drenando la salida (5 servicios; `AudioDurationService` ≈ `FotogramasClaveService`, 13 líneas clonadas) | `Shell.Abrir(ruta)` y `ProcesoExterno.EjecutarAsync(exe, args, limite, ct)` | Bajo |
| F3 | Reacción al cambio de idioma | `Receive(IdiomaCambiadoMensaje)` en 13 ViewModels con el mismo cuerpo (salto al hilo de interfaz + refrescar) | Clase base o ayudante de suscripción | Bajo |
| F4 | Salto al hilo de la interfaz | `Application.Current.Dispatcher` 56 veces en 21 archivos y `CheckAccess()` 12 veces; ya existe un `EnUi` privado en `DescargasViewModel` | Hacerlo común (`Core/HiloUi`) | Bajo |
| F5 | Reinicio de `CancellationTokenSource` | El trío cancelar/liberar/crear aparece 15 veces en 6 archivos | Un método `Reemplazar(ref cts)` | Bajo |
| F6 | Guardar una preferencia | Patrón leer-comparar-asignar-guardar repetido (`GuardarModoNochePreferencia`, `GuardarUsarMiEstiloEnAss`…): 53 lecturas y 14 guardados en 16 archivos | `ISettingsService.ActualizarAsync(Action<AppSettings>)` | Bajo |
| F7 | `catch` vacíos | 86 en 27 archivos, 22 de ellos en `ReproductorViewModel` | Un ayudante que registre en nivel detallado en vez de tragar el error en silencio. No tocar los 365 `catch` que ya registran | Bajo |
| F8 | Clones dentro de un archivo | `AgregarAnimeViewModel` 290 ≈ 353 (20 líneas); `AniListTrackingService` 875 ≈ 942 (20 + 11) y 709 ≈ 787 (9); `DatabaseService` 700 ≈ 790 (12); `ConfiguracionViewModel` 941 ≈ 977 (9); `UpdateService` 110 ≈ 130 (10) | Extraer un método privado en cada caso | Bajo |
| F9 | P/Invoke repartido | `MonitorFromWindow` y `GetMonitorInfo` declarados dos veces; 11 `[DllImport]` conviven con 9 `[LibraryImport]` | Un `Native/User32.cs` con `[LibraryImport]` | Bajo |
| F10 | Historial y Actualizaciones en paralelo | 21 líneas clonadas entre los ViewModels, 7 entre los ítems, plantillas idénticas en las vistas | Base común para los dos "feeds de episodios" | Medio |

## 4. Lógica que se complica

| # | Foco | Evidencia | Propuesta | Riesgo |
|---|---|---|---|---|
| L1 | **Añadir una pestaña exige tocar 7 sitios** | 14 clases `NavegarMensaje_*`, 16 propiedades `EsXActivo`, 10 comandos `NavegarX`, 11 métodos `ObtenerX()`, 10 botones de 33 líneas. `ui-wpf-vistas.md` lo describe como "cadena completa (no saltarse eslabones)" | Una tabla de pestañas (clave, icono, texto, tipo de ViewModel, si se precalienta), un comando `Navegar(pestaña)` y una propiedad `PestañaActiva`. La barra lateral pasa a ser una lista. Añadir una pestaña: una fila y su `DataTemplate` | Medio |
| L2 | **`ReproductorViewModel`** | 2 888 líneas, 89 campos, **39 banderas booleanas**, constructor de 21 parámetros, 363 ramas, 22 `catch` vacíos. Ocho banderas describen juntas en qué punto del arranque está (`_autoPlayEjecutado`, `_haCompletadoOpen`, `_arranqueRegistrado`, `_reanudacionPendiente`, `_imagenLista`, `_ocultarVideoInicio`, `_finDeEpisodioProcesado`, `_reabiertoPorProcesador`) | (a) Una fase explícita (`Abriendo → EsperandoImagen → Reanudando → Reproduciendo → Terminado`) en lugar de las ocho banderas. (b) Sacar a sub-ViewModels los subtítulos (7 banderas, incluidas las 4 del dibujo ASS añadidas hoy) y el ecualizador (3 banderas y las bandas) | Alto: por tramos y con las pruebas existentes |
| L3 | Vista del reproductor | 1 208 líneas de code-behind; 4 menús con 3 manejadores casi iguales cada uno (11 métodos) pese a existir ya la tabla `_menus`; 4 parejas `Enlazar…`/`…_PropertyChanged` con la misma forma | Un manejador por tipo que busque el menú en la tabla; un ayudante de suscripción para las parejas | Medio |
| L4 | Pantalla de Configuración | 53 propiedades que son espejo de `AppSettings`, con 31 + 28 asignaciones de ida y vuelta | Editar una copia de `AppSettings` enlazada directamente, y guardar esa copia | Medio |
| L5 | Descargas | `DownloadService`: 2 223 líneas; tres métodos de 134–192 líneas con anidamiento 5–6 (`EjecutarBucleDescargaAsync`, `DownloadSegmentedParallelAsync`, `DownloadSequentialAsync`) | Extraer reintentos y progreso a piezas compartidas. Esta zona acumula muchas correcciones de estabilidad: solo con pruebas delante, y no es prioritario si no hay fallos | Alto |
| L6 | Métodos largos sueltos | `AuthService.IniciarSesionAsync` (194 líneas, anidamiento 6), `CreateOptimizedPlayer` (156), `ActualizacionesViewModel.CargarActualizacionesAsync` (142), `PythonFileScannerService.EscanearEpisodiosAsync` (anidamiento 7), `FirmaTitulo.Interpretar` (51 ramas) | Partir en pasos con nombre, sin cambiar el comportamiento | Bajo–medio |

## 5. Lo que no conviene tocar

- **Las 63 interfaces con una sola implementación:** existen para las pruebas con Moq (2 048 métodos de prueba). Aquí no sobran.
- **El diccionario de `LocalizationService`** (2 738 líneas): son datos, no lógica.
- **Los `catch` que ya registran el error** (365): fundirlos escondería contexto.
- **PERF-02 y los parches de Flyleaf:** ya investigados en septiembre.

## 6. Orden propuesto

| Fase | Contenido | Por qué en ese orden |
|---|---|---|
| 1. Mecánica y segura | V1, V4, V5, F1, F2, F4, F5, F8, F9 | Mucho volumen, casi sin riesgo; deja ayudantes que usan las fases siguientes |
| 2. Convenciones | V2 (`{loc:T}`), F3, F6, F7, V7 | Cambios amplios pero uniformes |
| 3. Navegación | L1 + V3, y F10 | El mayor ahorro de "pasos para hacer algo"; toca la ventana principal |
| 4. Reproductor y Configuración | L2, L3, L4 | Lo más delicado; por tramos |
| Solo si hace falta | L5, L6, V6, V8 | Riesgo alto o beneficio gradual |

Ahorro estimado: ≈900–1 100 líneas de XAML y ≈400–600 de C# en las fases 1–3, más la reducción de `ReproductorViewModel` en la fase 4. Cada fase es un cambio de varios módulos: lleva su propio plan y la verificación de `repo-build-test`.

## 7. Estado de la fase 1 (aplicada el 2026-10-04, sin commit)

| Punto | Estado | Detalle |
|---|---|---|
| V1 colores | **Hecho** | 398 literales sustituidos por su pincel en 12 vistas, solo en propiedades de tipo pincel (`Foreground`, `Background`, `BorderBrush`, `Fill`, `Stroke`) y solo con colores idénticos al de la paleta. Quedan fuera los diccionarios de recursos (`Themes/`, `FichaEstilos`, `GaleriaTarjeta`, `MinijuegosEstilos`): desde ahí no está garantizado que `App.xaml` ya esté cargado |
| V5 estilos | **Hecho** | En `App.xaml`: `AppFilaTransparente`, `AppFilaTransparenteSinFoco`, `AppPageMargen` (11 copias en 7 vistas) y `AppChipFiltro` (chips de Actualizaciones, Historial y Logros). El chip de Descargas difiere de verdad (letra 12,5 y otro color al pasar el ratón) y se queda. La tarjeta de la ficha frente a la de la galería no se tocó: una vive en un diccionario de recursos y la otra en una vista |
| F1 tamaños | **Hecho** | `Core/Formato.Tamano`: GB con un decimal, MB con un decimal por debajo de 10 y sin él por encima, KB y B enteros, separador del idioma de la app. Sustituye las cuatro `FormatearTamano` y el formateo del visor de registros. **Cambia textos visibles**: el historial de descargas pasa de "2.4 MB" a "2,4 MB" en español; un archivo de 2 MB en la ficha pasa de "2 MB" a "2,0 MB"; una canción de más de 10 MB pierde el decimal; una de menos de 1 MB se muestra en KB. Corrección al punto F1 de arriba: los "≈30 cálculos con 1024" eran casi todos límites y mensajes de registro, no formato para pantalla |
| F2 procesos | **Hecho** | `Core/Shell.Abrir` (10 aperturas de URL/carpeta) y `Core/ProcesoExterno.EjecutarAsync` (subtítulos, duración de audio, fotogramas clave y verificación de integridad). La verificación de integridad ahora también mata ffprobe si se cancela; antes lo dejaba corriendo. La conversión de música conserva su código: espera a que el proceso termine tras cancelarlo |
| F4 hilo de interfaz | **Hecho en parte** | `Core/HiloUi.Ejecutar` sustituye 7 saltos con la forma exacta y el `EnUi` privado de `DescargasViewModel`. Los demás usos de `Dispatcher` llevan prioridades o esperas propias y no se tocaron |
| F5 cancelación | **Hecho** | `Core/Cancelacion.Reemplazar/Detener` sustituye 13 sitios |
| F8 clones internos | **Hecho en parte** | `DatabaseService.FusionarRegistro` y `AgregarAnimeViewModel.MostrarResultados`. El de `UpdateService` y los de `ConfiguracionViewModel` no eran clones (diálogos con textos distintos que el detector normaliza). El ciclo de petición de `AniListTrackingService` se repite en 14 métodos, pero cada uno tiene su manejo de errores y caché: es una refactorización propia, no un clon |
| F9 P/Invoke | **Hecho** | `Views/MonitorDeVentana` (con `[LibraryImport]`) sustituye las dos copias de `MonitorFromWindow`/`GetMonitorInfo` |
| V4 botón de icono | Sin hacer | No hay una forma de quitar la repetición del `ToolTip` y el nombre accesible que sea más simple que lo que hay |

Resultado: build con 0 errores y 0 advertencias; 2 733 pruebas en verde (21 nuevas de formato, cancelación y procesos; 3 de formato se movieron de sitio). Verificado en la app real con perfil aislado: las pestañas, la ficha y el reproductor cargan y se ven igual; la ventana maximiza sin tapar la barra de tareas; la extracción de subtítulos y la lectura de fotogramas clave siguen funcionando; 0 errores en el registro. La réplica de recursos de las pruebas de vistas (`WpfHostFixture`) necesitó las claves nuevas de `App.xaml`.

## 8. Estado de la fase 2 (aplicada el 2026-10-04, sin commit)

| Punto | Estado | Detalle |
|---|---|---|
| V2 `{loc:T}` | **Hecho** | `Services/TExtension.cs`: `{loc:T Clave}` devuelve el mismo binding de siempre, siempre unidireccional. Sustituidos los 848 bindings en 27 archivos. Quedan 5 con forma de elemento (`<Binding …>` dentro de bindings múltiples), que no admiten la extensión |
| F3 cambio de idioma | **Hecho** | 8 de los 13 `Receive(IdiomaCambiadoMensaje)` solo saltaban al hilo de interfaz y refrescaban: ahora son `HiloUi.Ejecutar(Refrescar…)`. Los otros 5 hacen trabajo propio. De paso se quitaron tres copias privadas de `EnHiloDeInterfaz` (ficha, reproductor, vista de detalle) |
| F6 guardar preferencias | **Hecho en parte** | `ISettingsService.ActualizarAsync(cambio)` (método de extensión: la interfaz no cambia y las pruebas con Moq siguen igual). Convertidos 5 sitios. No se convirtieron los que comparan antes de guardar para no escribir en disco sin necesidad (modo noche, "Usar mi estilo", idioma de audio) ni el guardado completo de Configuración |
| V7 animaciones | **Hecho** | En `App.xaml`: `Anim.Rapida` (0,12 s), `Anim.Normal` (0,2 s), `Anim.Suave`, `Anim.SuaveEntrada`, `Anim.SuaveFuerte`; 42 usos en 6 archivos. `PlayerMenuPanel` unifica el panel de los 4 menús del reproductor. Fuera: los diccionarios de recursos (`CustomComboBox`, `GaleriaTarjeta`) y las 7 animaciones creadas en code-behind |
| F7 `catch` vacíos | **Descartado** | Revisados: son casi todos `catch (OperationCanceledException) { }` o accesos al reproductor que puede estar cerrándose. Un ayudante que los registre solo añadiría ruido. La cifra de "86" del punto F7 incluía esos casos legítimos |

Resultado: build con 0 errores y 0 advertencias; 2 737 pruebas en verde (4 nuevas). Verificado en la app real con perfil aislado: textos en todas las pestañas, cambio de idioma en caliente español → inglés → español (incluidos los textos que construyen los ViewModels), menús del reproductor y reproducción con subtítulos ASS; 0 errores en el registro.
