# Plan: navegación por tabla de pestañas (fase 3 de la auditoría 2026-10-04)

**Objetivo:** que añadir una pestaña sea una fila en una tabla y su `DataTemplate`, en vez de los 7 eslabones de hoy; y que la barra lateral sea una lista en lugar de 10 botones copiados.

**Estado:** etapas A y B aplicadas el 2026-10-04 (ver la sección 9 del informe de la auditoría).

**Diseño de partida:** `docs/auditoria-2026-10-04/informe.md`, puntos L1 y V3. Sin commit, sin ramas ni subagentes; verificación con `repo-build-test` y `wpf-visual-verification` (perfil aislado).

## 1. Cómo está hoy (medido)

| Pieza | Cantidad | Dónde |
|---|---|---|
| Botón de la barra lateral | 10 bloques de 33 líneas (Descargas, 49: lleva la insignia con el número de descargas) | `MainWindow.xaml` 71–420 |
| Propiedad "esta pestaña está activa" | 11 `EsXActivo`, usadas solo por esos botones (3 veces cada una) y por 4 pruebas | `INavigationService` / `NavigationService` |
| Comando de navegación | 10 `NavegarXCommand`, todos de una línea: enviar su mensaje | `MainViewModel` |
| Mensaje por pestaña | 11 clases `NavegarMensaje_X` | `Messages/` |
| Receptor por pestaña | 11 `Receive(NavegarMensaje_X)`, cada uno con su lógica de entrada | `NavigationService` |
| `ObtenerX()` | 11 métodos en la interfaz; fuera de `NavigationService` solo los usa el precalentado | `INavigationService` |

Lo que cada pestaña hace al entrar no es uniforme: la Galería vuelve a la vista anterior si se venía del Calendario y refresca minijuegos; Calendario refresca estados y carga si está vacío; Descargas refresca; Estadísticas, Historial, Logros y Actualizaciones cargan si `NecesitaRecargar()`; el visor de registros añade lo nuevo o relee; Configuración y Acerca de solo se muestran. Además, dos botones se marcan con más de una vista: Biblioteca también con la ficha, y Configuración también con el visor de registros.

## 2. Diseño

### Etapa A: la barra lateral como lista (riesgo bajo)

- **`Pestana`** (`ViewModels/Pestana.cs`): registro con `Clave`, icono (`PackIconKind`), clave de texto (`Nav_X`), si va abajo, los tipos de ViewModel que la marcan como activa y el mensaje que la abre. **`Pestanas.Todas`**: la tabla, en el orden de la barra.
- **`PestanaItemViewModel`**: lo que pinta un botón: `Icono`, `Texto` (traducido; se refresca con `IdiomaCambiadoMensaje`), `EsActiva`, `Insignia`/`TieneInsignia` (solo Descargas) y `AbrirCommand`.
- **`MainViewModel`**: expone `PestanasSuperiores` y `PestanasInferiores`; recalcula `EsActiva` cuando cambia `Navigation.VistaActual`. Los 10 `NavegarXCommand` se reducen a los que se usan fuera de la barra (hoy, `NavegarDescargasCommand` y `NavegarAgregarAnime`).
- **`MainWindow.xaml`**: dos `ItemsControl` con una sola plantilla de botón (indicador, botón, icono, insignia). Unas 300 líneas menos.
- **`INavigationService`**: desaparecen las 11 `EsXActivo`; queda `bool EstaActiva(Pestana)`. Las 4 pruebas que las usan pasan a esa llamada.

Los mensajes y los receptores **no cambian** en esta etapa: el comportamiento al entrar en cada pestaña es exactamente el de hoy.

### Etapa B: un solo mensaje y la lógica de entrada en cada ViewModel (riesgo medio)

- **`NavegarMensaje_Pestana(Pestana)`** sustituye a las 11 clases de mensaje; se actualizan sus emisores (unos 20 sitios entre app y pruebas).
- **`IAlEntrarEnPestana { Task AlEntrarAsync(); }`**: lo implementan los ViewModels que hoy tienen lógica de entrada (Calendario, Descargas, Estadísticas, Historial, Logros, Actualizaciones, visor de registros; la Galería ya tiene `AlEntrarAsync`). La lógica se mueve tal cual desde `NavigationService`.
- **`NavigationService`**: un único `Receive(NavegarMensaje_Pestana)` que resuelve el ViewModel por su tipo, lo muestra y llama a `AlEntrarAsync` con el mismo `try/catch` de hoy. El caso especial de la Galería (volver al Calendario desde una ficha) se conserva como condición explícita. Desaparecen los 11 `ObtenerX()`; el precalentado recorre la tabla.
- **Reglas:** se reescriben el punto 1 de `.agents/rules/ui-wpf-vistas.md` y el skill `wpf-add-view`: "nueva pestaña = fila en `Pestanas.Todas` + `DataTemplate` + registro en DI (+ `IAlEntrarEnPestana` si carga datos al entrar)".

Lo que **no** entra: la ficha, el reproductor y la vuelta del reproductor (no son pestañas) conservan sus mensajes y su lógica.

## 3. Pruebas

- Nuevas (antes del código): la tabla tiene las 10 pestañas en el orden actual; `EstaActiva` marca Biblioteca con la ficha y Configuración con el visor de registros; el item refresca su texto al cambiar de idioma; la insignia de Descargas refleja `ConteoDescargasActivas`.
- Etapa B: una prueba por pestaña que comprueba que entrar hace lo mismo que hoy (carga solo si `NecesitaRecargar()`, etc.), y la vuelta de la ficha al Calendario.
- Existentes que se adaptan: 4 usos de `EsXActivo`, 9 usos de mensajes de pestaña en 3 archivos, 2 de `NavegarDescargasCommand`.
- En la app real (perfil aislado): recorrer las 10 pestañas, comprobar el indicador activo en ficha y visor de registros, la insignia de Descargas, el `ToolTip` en ambos idiomas, el registro `[Perf] Navegación A → B` sin regresión y el precalentado.

## 4. Riesgos

- **Rendimiento de navegación:** la barra pasa de botones fijos a plantillas; no debería notarse (10 elementos), pero se compara con los tiempos `[Perf]` actuales (2–15 ms).
- **Foco y teclado:** el orden de tabulación de la barra debe quedar igual.
- **Etapa B toca el núcleo de la navegación.** Se hace después de verificar la A, y cada pestaña con su prueba.

## 5. Decisiones que necesito

1. **Alcance:** ¿solo la etapa A (la barra como lista; el mayor ahorro con el menor riesgo), o A y B (añadir una pestaña pasa de 7 pasos a 2, a cambio de tocar el núcleo de la navegación y reescribir tu regla de "cadena completa")?
2. **Lógica de entrada (solo si hay etapa B):** ¿moverla a cada ViewModel con `IAlEntrarEnPestana`, como propongo, o dejarla en `NavigationService` como una tabla de funciones? La primera deja a `NavigationService` sin conocer cada pestaña; la segunda toca menos archivos.
