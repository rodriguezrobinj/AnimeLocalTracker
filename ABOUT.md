# Sobre AnimeLocalTracker 📘

**AnimeLocalTracker** nació de la necesidad de tener un control absoluto sobre el anime que descargamos y almacenamos localmente, sin perder las ventajas sociales y de seguimiento que ofrecen plataformas como AniList. 

## 🏗️ La Arquitectura (Under the Hood)

Esta aplicación no es solo una interfaz bonita; está construida pensando en el rendimiento extremo y las mejores prácticas de la industria para aplicaciones .NET de escritorio.

### Patrón MVVM (Model-View-ViewModel)
Hemos separado estrictamente la interfaz de usuario (XAML) de la lógica de negocio (C#) utilizando **CommunityToolkit.Mvvm**. No hay código "espagueti" en el *Code-Behind* de las vistas. Toda la comunicación se maneja a través de *Bindings* reactivos y `WeakReferenceMessenger` para el enrutamiento.

### Inyección de Dependencias
Implementamos un contenedor IoC (Inversion of Control) gestionado por `Microsoft.Extensions.DependencyInjection`. Todos nuestros servicios (`IAnimeTrackingService`, `IDatabaseService`, `IAuthService`, etc.) son inyectados limpiamente en los ViewModels, lo que hace que el código sea altamente modular, testeable y preparado para futuras expansiones (como integrar MyAnimeList u otras plataformas).

### Rendimiento Extremo (60 FPS)
* **Reutilización de Sockets HTTP:** Utilizamos `IHttpClientFactory` para evitar el agotamiento de sockets al hacer múltiples peticiones a la API de AniList, asegurando una conexión rápida y estable.
* **Procesamiento Asíncrono:** Tareas pesadas como el escaneo de miles de episodios o la decodificación de respuestas GraphQL se descargan de la UI mediante `Task.Run()`, manteniendo la interfaz responsiva en todo momento.
* **Virtualización Visual:** Utilizamos `VirtualizingWrapPanel` y carga de imágenes diferida (`IsAsync=True`) junto con `CacheMode="BitmapCache"` para garantizar que, incluso con galerías de 1000 animes y sus portadas en alta resolución, el scroll se mantenga a 60 FPS estables sin saturar la RAM.

## 🤝 Filosofía
Creemos en el software local, privado y veloz. Tu colección está en tu disco duro, nosotros solo te ayudamos a organizarla con estilo.
