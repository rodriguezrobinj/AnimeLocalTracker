# Regla: Arquitectura WPF y MVVM

1. **Bindings de Localización Seguros:**
   - Los enlaces a textos traducidos `{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}}` deben ser siempre unidireccionales.
   - En propiedades con enlace bidireccional por defecto (como `Run.Text`), es mandatorio añadir `Mode=OneWay` para evitar excepciones en ejecución.
   - **Texto dinámico (código, no XAML):** diálogos, mensajes de estado y propiedades calculadas de modelos usan `LocalizationService.T("Clave")`; nunca cadenas literales en español ni `$"Episodio {n}"` sueltos. Antes de dar por traducida una etiqueta, buscar el literal en el código.
   - **Cambio de idioma en caliente:** un ViewModel NO se suscribe directamente a `LocalizationService.Instance` (fuga entre pruebas y condiciones de carrera); se suscribe con `WeakReferenceMessenger` a `IdiomaCambiadoMensaje`. Los `ComboBox` con listas de strings necesitan recarga explícita al cambiar de idioma.
   - **Datos mostrados vs. datos guardados:** la traducción es solo de presentación (p. ej. géneros de AniList vía `TraducirGenero()`); el valor almacenado y filtrado siempre es el original. Las fechas se formatean con `LocalizationService.Cultura`, no con `CurrentCulture`.
2. **Generadores de Código MVVM:**
   - Utilizar las anotaciones de `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`).
   - No generar código manual innecesario para `INotifyPropertyChanged` o `ICommand`.
3. **Manejo Asíncrono Estricto:**
   - Prohibido el uso de `async void`, salvo en manejadores de eventos nativos de la vista (event handlers XAML) siempre protegidos por un bloque `try-catch`.
   - En ViewModels, todos los comandos asíncronos deben devolver `Task` (ejemplo: `[RelayCommand] private async Task CargarEpisodiosAsync()`).
   - Trabajo de disco o de red (p. ej. `File.Exists` en un bucle sobre la biblioteca) no se hace de forma síncrona en el hilo de interfaz: va en `Task.Run`/`await`.
4. **Respeto al Hilo de Interfaz (UI Thread):**
   - Cualquier actualización de colecciones observables (`ObservableCollection`) o controles visuales debe enviarse al despachador principal (`Application.Current.Dispatcher`, preferiblemente `InvokeAsync`).
   - Los mensajes de `WeakReferenceMessenger` pueden llegar desde hilos de fondo: el receptor que toque la UI hace él mismo el salto al despachador.
5. **Animaciones acotadas:**
   - Evitar `RepeatBehavior="Forever"` en elementos permanentes o repetidos (tarjetas de lista, shimmer): acotar las repeticiones o detener la animación al terminar la carga.
