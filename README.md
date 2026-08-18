# AnimeLocalTracker 🎬✨

**AnimeLocalTracker** es una aplicación moderna de escritorio desarrollada en C# (WPF) diseñada para ofrecer la experiencia definitiva al gestionar tu colección de anime descargado localmente. Diseñada con principios limpios (MVVM) y un enfoque total en la estética, inmersión y rendimiento.

![AnimeLocalTracker Preview](https://via.placeholder.com/800x450.png?text=AnimeLocalTracker+Preview) <!-- Reemplaza esto con un screenshot real -->

## 🌟 Características Principales

* **Integración Total con AniList:** Conecta tu cuenta mediante OAuth2 seguro para sincronizar progreso, puntajes y estados en tiempo real.
* **Escaneo Inteligente:** Detecta automáticamente tus episodios locales descargados en tus carpetas.
* **Reproductor de Video Nativo (Flyleaf):** Ya no dependes de reproductores externos. Disfruta de un reproductor integrado con aceleración por hardware, una interfaz de superposición inmersiva al estilo Netflix/Crunchyroll, controles de volumen fluidos, y atajos rápidos (F11 para pantalla completa real).
* **Auto-Tracking Híbrido:** Olvídate de marcar episodios manualmente. Cuando el video alcance el **90%** de su duración, la aplicación automáticamente actualizará la base de datos local y sincronizará tu cuenta de AniList en vivo. ¡Y la UI se refresca sin fricción!
* **Diseño Premium:** Interfaz de usuario inmersiva con efectos de *glassmorphism*, desenfoque dinámico, modo oscuro avanzado y transiciones a 60 FPS aceleradas por hardware (MaterialDesignInXAML).
* **Buscador en Vivo:** Busca y añade animes directamente desde la base de datos de AniList al instante.
* **Calendario de Emisión:** Rastrea cuándo salen los nuevos episodios de la semana.

## 🛠️ Tecnologías

* **C# / .NET 8** - Core moderno y de alto rendimiento.
* **WPF (Windows Presentation Foundation)** - Renderizado gráfico.
* **MVVM Community Toolkit** - Arquitectura purista y reactivamente rápida gracias a la generación de código.
* **MaterialDesignInXaml** - Componentes de diseño (Material Design 3).
* **Flyleaf (DirectX/FFmpeg)** - Motor de renderizado de video empotrado ultrarrápido y potente.
* **Entity Framework Core / SQLite** - Base de datos local para acceso sin conexión y almacenamiento híbrido.
* **GraphQL** - Consumo dinámico de la API oficial de AniList.

## 🚀 Instalación

1. Dirígete a la sección de [Releases](../../releases) del repositorio.
2. Descarga el instalador `Setup_AnimeTracker_vX.X.X.exe`.
3. Ejecútalo y sigue las instrucciones en pantalla.
4. Conecta tu cuenta de AniList directamente desde la app. ¡Eso es todo!

## 🏗️ Compilar desde el código fuente

Si prefieres compilar la aplicación tú mismo:

```bash
git clone https://github.com/rodriguezrobinj/AnimeLocalTracker.git
cd AnimeLocalTracker
dotnet build -c Release
```

## 🤝 Contribuciones
¡Las contribuciones son bienvenidas! Si tienes alguna idea, mejora de rendimiento, o encuentras un bug, no dudes en abrir un Issue o un Pull Request.

## 📝 Licencia
Distribuido bajo la licencia MIT. Consulta el archivo `LICENSE` para más información.
