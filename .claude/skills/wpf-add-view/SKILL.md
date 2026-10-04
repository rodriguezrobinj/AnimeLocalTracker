---
name: wpf-add-view
description: Procedimiento canónico para añadir una nueva pestaña/vista a AnimeLocalTracker (fila en la tabla de pestañas → DI → DataTemplate), y el patrón de listas grandes virtualizadas. Usar al crear una pestaña/pantalla nueva o al portar una lista existente a ListBox virtualizado.
---

# Añadir una vista/pestaña nueva — AnimeLocalTracker

La app navega cambiando `INavigationService.VistaActual` (un `ObservableObject`); `App.xaml` resuelve la vista correcta vía `DataTemplate` por tipo de ViewModel. Las pestañas de la barra lateral salen de una tabla: **añadir una pestaña no toca `MainWindow.xaml`, `MainViewModel` ni `NavigationService`.**

## Los tres pasos (ejemplo real: Historial)

1. **Fila en la tabla** — `AnimeLocalTracker/ViewModels/Pestana.cs`, en `Pestanas`, y añadirla a `Pestanas.Todas` en la posición que deba ocupar en la barra:
   ```csharp
   public static readonly Pestana Historial = new("Historial", "History", "Nav_Historial", false, [typeof(HistorialViewModel)]);
   ```
   Clave, icono (nombre de `PackIconKind`), clave de texto (`Nav_X` en `LocalizationService`, ES y EN), si va en el grupo de abajo, y los tipos de ViewModel que la dejan marcada. El primero es la vista que se abre; los demás solo la marcan (la Biblioteca incluye la ficha; Configuración, el visor de registros).
2. **DI** en `App.xaml.cs` — registra el ViewModel (`AddSingleton` si mantiene estado entre visitas tipo lista cacheada, `AddTransient` si debe recargar cada vez).
3. **`DataTemplate` VM→Vista** en `App.xaml`:
   ```xml
   <DataTemplate DataType="{x:Type vm:HistorialViewModel}">
       <views:HistorialView />
   </DataTemplate>
   ```

Con eso la barra lateral ya muestra el botón (con su `ToolTip`, nombre accesible e indicador de activa) y `NavigationService` sabe abrirla.

- **Cargar datos al entrar:** el ViewModel implementa `IAlEntrarEnPestana` (no lo hagas en el constructor: así recarga cada vez que se navega a ella, no solo la primera):
  ```csharp
  public async Task AlEntrarAsync()
  {
      if (NecesitaRecargar()) await CargarHistorialAsync();
  }
  ```
- **Navegar a una pestaña desde código:** `Pestanas.Historial.Abrir();` (envía `NavegarMensaje_Pestana`). Dentro de una clase que ya tenga un miembro llamado `Pestanas` (como `DescargasViewModel`), usa el nombre completo `AnimeLocalTracker.ViewModels.Pestanas`.
- **Que salga un número sobre el botón** (como las descargas en curso): `PestanaItemViewModel.Insignia`, que `MainViewModel` rellena.
- **Precalentado:** si la pestaña es de uso frecuente, añádela a la lista de `MainWindow.PrecalentarPestanasAsync` con `() => navegacion.ObtenerVista(Pestanas.X)`.
- **Vistas que no son pestañas** (la ficha, el reproductor, el visor de registros) conservan su propio mensaje `NavegarMensaje_*` y su receptor en `NavigationService`.
- **Pruebas de vistas:** si la vista usa una clave nueva de `App.xaml`, añádela también a `AnimeLocalTracker.Tests/Views/WpfHostFixture.cs`.

## La vista de una pestaña se conserva entre visitas

La zona de contenido de `MainWindow` es `Controls/AnfitrionVistas` (no un `ContentControl`): la vista de cada pestaña se construye **una vez** y después solo se muestra u oculta (cambiar de pestaña pasó de 105-440 ms a 3-20 ms). Consecuencias al escribir una vista:

- **`Loaded`/`Unloaded` NO llegan al entrar/salir de la pestaña** (solo la primera vez / al cerrar la app). Lo que deba pasar al salir —cerrar un `Popup` con `StaysOpen=True`, cerrar un panel, parar audio o un temporizador— o al volver, va en `IsVisibleChanged` (ejemplos: `GaleriaView`, `DescargasView`, `AdivinaOpEdView`).
- El estado propio de la vista (desplazamiento, sección elegida, texto no enlazado) **se conserva** entre visitas.
- Si el ViewModel se crea nuevo en cada visita (como `DetalleViewModel`), márcalo con `IVistaReutilizable`: hay una sola vista para todos y pasa de un ViewModel al siguiente. En `DataContextChanged` la vista debe reiniciar su estado propio (ver `DetalleView.ReiniciarEstadoDeLaVista`).
- Secciones internas (subpestañas): mismo patrón. Para alternar entre varias vistas dentro de una pestaña usa otro `AnfitrionVistas` (ver `MinijuegosView`); para una sección que se quiera construir por adelantado, ocúltala con `BoolToVisOculto` (Hidden) en vez de `BoolToVis` (Collapsed no se mide). Mide el cambio con `MedidorRendimiento.MedirCambio("…")` justo antes de provocarlo.
- `IsVisibleChanged` también llega cuando se oculta la ventana entera (bandeja): si lo que paras es algo que debe seguir ahí (música), comprueba antes `Window.GetWindow(this)?.IsVisible`.
- Pestaña de uso frecuente: añádela a la lista de `MainWindow.PrecalentarPestanasAsync` para que se construya en reposo tras abrir la app. Eso crea su ViewModel al arrancar: su constructor no debe hacer trabajo caro en el hilo de la interfaz.
- Para comprobar el coste: el registro deja `[Perf] Navegación A → B: colocada a los X ms, interfaz libre a los Y ms` en cada cambio (DEBUG por debajo de 250 ms, INFO por encima).

## Listas grandes: SIEMPRE `ListBox` virtualizado

Nunca `ItemsControl` dentro de `ScrollViewer` para listas que pueden crecer (biblioteca, historial, descargas) — sin virtualización, WPF crea un contenedor visual por cada item y la lista se pone lenta/consume memoria con colecciones grandes.

Propiedades obligatorias en el `ListBox`:
```xml
VirtualizingPanel.IsVirtualizing="True"
VirtualizingPanel.VirtualizationMode="Recycling"
ScrollViewer.CanContentScroll="True"
ScrollViewer.VerticalScrollBarVisibility="Auto"
```
- Si las filas no son seleccionables: `ItemContainerStyle` transparente con `ContentPresenter` (sin resaltado de selección).
- Si la lista alterna **cabeceras de grupo y tarjetas** (p. ej. Historial: "Hoy"/"Ayer" + items), usar **plantillas implícitas por tipo** en `ListBox.Resources`: un `DataTemplate DataType="{x:Type sys:String}"` para la cabecera + otro `DataTemplate DataType="{x:Type vm:Item}"` para la tarjeta. Nunca dos `DataTemplate` dentro de `ListBox.ItemTemplate` directamente (error `MC3089`). Requiere `xmlns:sys="clr-namespace:System;assembly=System.Runtime"`.
- El ViewModel expone la colección agrupada como `ObservableCollection<object>` construida con `GroupBy` conservando el orden.

## Estados de la vista (checklist)

- **Cargando:** `MaterialDesignCircularProgressBar` + flag `EstaCargando`.
- **Vacío:** icono + título + texto de ayuda (claves `AppEmptyTitle`/`AppEmptyText` reutilizables) + CTA si aplica.
- **Sin resultados de búsqueda:** si la vista tiene buscador, un estado separado del "vacío real".

## Accesibilidad y estilo (no inventar)

- Botones solo-icono: siempre `ToolTip` + `AutomationProperties.Name` (mismo texto, localizado) + `Cursor="Hand"`.
- Colores SIEMPRE de la paleta ya definida en `App.xaml` (`Brush.*`, `AppText.*`) — nunca un literal `#RRGGBB` nuevo. Ya están ajustados a contraste AA: texto pequeño sobre fondo oscuro nunca `#6B7280` (usar `#94A3B8`), texto sobre acentos claros oscurecer el fondo (`#2563EB`/`#E11D48`) en vez de aclarar el texto.
- Comando invocado desde dentro de un `DataTemplate` (p. ej. botón en una tarjeta de lista): `{Binding DataContext.XCommand, RelativeSource={RelativeSource AncestorType=UserControl}}` (el `DataContext` del item NO es el ViewModel de la vista).

## Imágenes/miniaturas

- Placeholder de fallback SIEMPRE **debajo** de la `Image` en el árbol visual (se ve si la imagen real falla en cargar), nunca oculto condicionalmente por binding.
- **No usar `IsAsync=True` en el binding de `Image.Source`** — produce `MC3072` en algunos SDKs.
- No hacer `File.Exists` dentro del getter de un binding (bloquea el hilo de UI); resolver la ruta una vez al construir el item/ViewModel.

## Localización

Toda cadena visible al usuario va en `LocalizationService.cs`, con clave en ambos diccionarios (ES y EN) — nunca texto literal en el XAML salvo contenido que no se traduce (números, nombres propios). Patrón: `{loc:T Clave}` (`Services/TExtension.cs`), que crea el enlace al diccionario siempre unidireccional; no escribir a mano el binding largo `{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}}` (ver `wpf-mvvm.md`, punto 1).
