# Regla: Arquitectura WPF y MVVM

1. **Bindings de Localización Seguros:**
   - Los enlaces a textos traducidos `{Binding [Clave], Source={x:Static loc:LocalizationService.Instance}}` deben ser siempre unidireccionales.
   - En propiedades con enlace bidireccional por defecto (como `Run.Text`), es mandatorio añadir `Mode=OneWay` para evitar excepciones en ejecución.
2. **Generadores de Código MVVM:**
   - Utilizar las anotaciones de `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`).
   - No generar código manual innecesario para `INotifyPropertyChanged` o `ICommand`.
3. **Manejo Asíncrono Estricto:**
   - Prohibido el uso de `async void`, salvo en manejadores de eventos nativos de la vista (event handlers XAML) siempre protegidos por un bloque `try-catch`.
   - En ViewModels, todos los comandos asíncronos deben devolver `Task` (ejemplo: `[RelayCommand] private async Task CargarEpisodiosAsync()`).
4. **Respeto al Hilo de Interfaz (UI Thread):**
   - Cualquier actualización de colecciones observables (`ObservableCollection`) o controles visuales debe enviarse al despachador principal (`Application.Current.Dispatcher`).
