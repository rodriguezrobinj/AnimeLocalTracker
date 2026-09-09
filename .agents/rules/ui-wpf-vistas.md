# Regla: Vistas WPF canónicas (UI de listas, navegación y accesibilidad)

Patrón canónico para crear o modificar vistas/pestañas. Su objetivo: **consistencia visual y cero iteraciones de "arregla el layout"** (lo que más tokens cuesta en UI).

1. **Nueva pestaña = cadena completa (no saltarse eslabones):**
   `NavegarMensaje_*` (Messages) → `INavigationService.ObtenerX()` (interfaz + impl) → singleton en DI (`App.xaml.cs`) → `DataTemplate` en `App.xaml` → en `MainViewModel`: receptor `IRecipient<>`, flag `EsXActivo` (+ `NotifyPropertyChangedFor`), comando de navegación que **carga datos al entrar** → botón en `MainWindow.xaml` (Rectangle indicador + Button con `ToolTip` localizado `Nav_X`).
2. **Listas grandes = ListBox virtualizado (nunca `ItemsControl` dentro de `ScrollViewer`):**
   - Propiedades obligatorias: `VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`, `ScrollViewer.CanContentScroll=True`, `ScrollViewer.VerticalScrollBarVisibility=Auto`.
   - `ItemContainerStyle` transparente con `ContentPresenter` (sin selección visual) si las filas no son seleccionables.
   - Si la lista alterna **cabeceras de grupo y tarjetas** (Hoy/Ayer/fecha), usar **plantillas implícitas por tipo** en `ListBox.Resources`: `DataTemplate DataType="{x:Type sys:String}"` (cabecera) + `DataTemplate DataType="{x:Type vm:Item}"` (tarjeta). NUNCA dos DataTemplates dentro de `ListBox.ItemTemplate` (MC3089). Añadir `xmlns:sys="clr-namespace:System;assembly=System.Runtime"`.
   - El ViewModel expone `ItemsAgrupados` (ObservableCollection<object>) construido con `GroupBy` conservando el orden desc.
3. **Estados de la vista:** spinner circular (`MaterialDesignCircularProgressBar`, `EstaCargando`), estado vacío con icono + título + CTA (`AppEmptyTitle/AppEmptyText`), y búsqueda sin resultados si aplica.
4. **Accesibilidad y estilo:**
   - Botones solo-icono: `ToolTip` + `AutomationProperties.Name` (mismo texto) y `Cursor="Hand"`.
   - Colores SIEMPRE de la paleta de `App.xaml` (`Brush.*`) o recursos `AppText.*`; no inventar literales nuevos (los existentes ya cuentan con contraste AA; texto pequeño nunca `#6B7280` sobre oscuro, usar `#94A3B8`; blanco sobre acentos claros → oscurecer a `#2563EB`/`#E11D48` o texto oscuro).
   - Comandos desde un DataTemplate: `{Binding DataContext.XCommand, RelativeSource={RelativeSource AncestorType=UserControl}}`.
5. **Imágenes:** para fallback de miniatura/portada poner el placeholder DEBAJO de la `Image` (visible si falla la carga); **no usar `IsAsync` en `Image`** (da MC3072 en algunos SDKs) y no hacer `File.Exists` en getters de binding (resolver al construir el ítem).
6. **Botones de estado de descarga:** replicar el patrón de `DetalleView` (círculo 34×34: `DownloadCircleOutline` cuando está libre; al descargar, aro base `#33FFFFFF` + spinner indeterminado (en cola) + `MaterialDesignCircularProgressBar` determinada con `DownloadProgress`). El VM se suscribe a `DescargaProgresoMensaje` (marshal al hilo de UI) para actualizar en vivo.
