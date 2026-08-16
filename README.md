# AnimeLocalTracker 🎬✨

**AnimeLocalTracker** es una aplicación moderna de escritorio desarrollada en C# (WPF) diseñada para ofrecer la experiencia definitiva al gestionar tu colección de anime descargado localmente. Diseñada con principios limpios (MVVM) y un enfoque total en la estética y el rendimiento.

![AnimeLocalTracker Preview](https://via.placeholder.com/800x450.png?text=AnimeLocalTracker+Preview) <!-- Reemplaza esto con un screenshot real -->

## 🌟 Características Principales

* **Integración Total con AniList:** Conecta tu cuenta para sincronizar progreso, puntajes y estados en tiempo real (OAuth2 seguro).
* **Escaneo Inteligente:** Detecta automáticamente tus episodios locales descargados.
* **Diseño Premium:** Interfaz de usuario inmersiva con efectos de *glassmorphism*, desenfoque dinámico, modo oscuro avanzado y transiciones a 60 FPS aceleradas por hardware (MaterialDesignInXAML).
* **Reproducción a un Clic:** Integra tu reproductor de video favorito (como PotPlayer) para ver tus episodios al instante.
* **Buscador en Vivo:** Busca y añade animes directamente desde la base de datos de AniList sin fricción.
* **Gestión de Episodios:** Marca como vistos, no vistos o favoritos localmente con sincronización a la nube.

## 🛠️ Tecnologías

* **C# / .NET 8** - Core del sistema.
* **WPF (Windows Presentation Foundation)** - Renderizado gráfico.
* **MVVM Community Toolkit** - Arquitectura purista y reactiva.
* **MaterialDesignInXaml** - Componentes de diseño moderno (Material Design 3).
* **Entity Framework Core / SQLite** - Base de datos local ultrarrápida.
* **GraphQL** - Consumo dinámico de la API oficial de AniList.

## 🚀 Instalación

1. Dirígete a la sección de [Releases](../../releases) del repositorio.
2. Descarga el instalador `Setup_AnimeTracker_vX.X.X.exe`.
3. Ejecútalo y sigue las instrucciones en pantalla.
4. (Opcional) Instala PotPlayer o tu reproductor de preferencia para la mejor experiencia de visualización local.

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
