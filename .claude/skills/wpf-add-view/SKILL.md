---
name: wpf-add-view
description: Procedimiento canónico para añadir una nueva pestaña/vista a AnimeLocalTracker (mensaje de navegación → NavigationService → DI → DataTemplate → botón en la sidebar), y el patrón de listas grandes virtualizadas. Usar al crear una pestaña/pantalla nueva o al portar una lista existente a ListBox virtualizado.
---

# Añadir una vista/pestaña nueva — AnimeLocalTracker

La app navega cambiando `INavigationService.VistaActual` (un `ObservableObject`); `App.xaml` resuelve la vista correcta vía `DataTemplate` por tipo de ViewModel. **No te saltes eslabones de la cadena** — es la fuente #1 de "no pasa nada al hacer clic" en este patrón.

## Cadena completa (ejemplo real: Historial)

1. **Mensaje de navegación** — `AnimeLocalTracker/Messages/NavegarMensajes.cs`:
   ```csharp
   public record NavegarMensaje_Historial();
   ```
2. **Contrato en `INavigationService`** — `AnimeLocalTracker/ViewModels/NavigationService.cs`:
   ```csharp
   bool EsHistorialActivo { get; }
   HistorialViewModel ObtenerHistorial();
   ```
3. **Implementación** en la misma clase `NavigationService` (que implementa `IRecipient<NavegarMensaje_Historial>`):
   ```csharp
   public bool EsHistorialActivo => VistaActual is HistorialViewModel;
   public HistorialViewModel ObtenerHistorial() => _serviceProvider.GetRequiredService<HistorialViewModel>();
   // Receive(NavegarMensaje_Historial) navega y (si aplica) carga datos al entrar
   ```
4. **DI** en `App.xaml.cs` — registra el ViewModel (`AddSingleton` si mantiene estado entre visitas tipo lista cacheada, `AddTransient` si debe recargar cada vez).
5. **`DataTemplate` VM→Vista** en `App.xaml`:
   ```xml
   <DataTemplate DataType="{x:Type vm:HistorialViewModel}">
       <views:HistorialView />
   </DataTemplate>
   ```
6. **En `MainViewModel`**: comando de navegación que envía el mensaje —
   ```csharp
   [RelayCommand]
   private void NavegarHistorial() => WeakReferenceMessenger.Default.Send(new NavegarMensaje_Historial());
   ```
   (si la vista necesita cargar datos al entrar, hazlo en el `Receive` de `NavigationService`, no en el constructor del ViewModel — así recarga cada vez que se navega a ella, no solo la primera).
7. **Botón en `MainWindow.xaml`** (sidebar): `Rectangle` indicador de activo (`Visibility` bindeado a `Navigation.EsXActivo`) + `Button` con `Command="{Binding NavegarXCommand}"`, `ToolTip`/`AutomationProperties.Name` localizados (clave `Nav_X` en `LocalizationService`, ES y EN).

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

Toda cadena visible al usuario va en `LocalizationService.cs`, con clave en ambos diccionarios (ES y EN) — nunca texto literal en el XAML salvo contenido que no se traduce (números, nombres propios). Patrón de binding: `{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}, Mode=OneWay}` — el `Mode=OneWay` es obligatorio en propiedades con binding bidireccional por defecto (como `Run.Text`) para evitar excepciones en runtime.
