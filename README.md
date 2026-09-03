# AnimeLocalTracker 🎬✨
### *Tu colección local de anime, elevada al estándar de una plataforma de streaming premium.*

[![GitHub Release](https://img.shields.io/github/v/release/rodriguezrobinj/AnimeLocalTracker?style=for-the-badge&color=e50914)](https://github.com/rodriguezrobinj/AnimeLocalTracker/releases)
[![.NET 8](https://img.shields.io/badge/.NET-8.0_WPF-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![AniList API](https://img.shields.io/badge/AniList-GraphQL_Sync-02A9FF?style=for-the-badge&logo=anilist&logoColor=white)](https://anilist.co/)
[![Flyleaf Video Engine](https://img.shields.io/badge/Engine-Flyleaf_DirectX11-FF6B00?style=for-the-badge&logo=ffmpeg&logoColor=white)](https://github.com/FredTinc/Flyleaf)
[![Tests Passing](https://img.shields.io/badge/Tests-308%2F308_Passing-28a745?style=for-the-badge&logo=githubactions&logoColor=white)](https://github.com/rodriguezrobinj/AnimeLocalTracker)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=for-the-badge)](LICENSE)

---

## ⚡ El Problema vs. La Revolución AnimeLocalTracker

Coleccionas anime en tu disco duro porque valoras la **máxima fidelidad**: videos en 1080p/4K sin la agresiva compresión del streaming, pistas de audio duales y los subtítulos estilizados de tus fansubs favoritos.

Sin embargo, la experiencia tradicional de escritorio siempre ha estado rota:
* ❌ Abrir el navegador tras cada episodio para actualizar manualmente tu lista en AniList o MyAnimeList.
* ❌ Usar reproductores genéricos y toscos que no recuerdan tu progreso de forma visual.
* ❌ Perder tiempo saltando intros y endings manualmente con la barra de tiempo.
* ❌ Navegar por carpetas aburridas de Windows Explorer sin pósteres, sinopsis ni calificaciones.

**AnimeLocalTracker fusiona lo mejor de ambos mundos:** la libertad, calidad y privacidad de tus archivos locales con la elegancia, inmersión y automatización de un servicio de streaming de última generación.

---

## 🌟 Experiencia de Usuario & Características Principales

### 🍿 1. Reproductor Nativo de Élite (Flyleaf DirectX 11 + FFmpeg)
Olvida los reproductores externos o reproductores web lentos.
* **Aceleración por Hardware Pura:** Decodificación fluida a 60, 120 y 144+ FPS sin tirones, compatible con HEVC/H.265, AV1, VP9, 10-bit y audio espacial.
* **Subtítulos con Legibilidad Cinematográfica:** Renderizado optimizado con silueta gaussiana y sombra paralela de alto contraste para máxima legibilidad en pantallas 4K y paneles OLED.
* **Controles Overlay Estilo Netflix:** Interfaz superpuesta que aparece al mover el cursor y se desvanece suavemente durante la reproducción. Atajo rápido `F11` para inmersión total en pantalla completa.

### ⏩ 2. AniSkip & Auto-Play Inteligente (Maratones sin fricción)
* **Salto Automático de Openings y Endings:** Integración directa con la API de AniSkip para detectar y saltar intros (OP), outros (ED), recaps y escenas post-créditos con un solo clic o de forma 100% automática.
* **Binge-Watching Automatizado:** Al finalizar un episodio, la aplicación reproduce automáticamente el siguiente archivo de tu biblioteca sin que tengas que tocar el ratón o el teclado.

### 🔄 3. Auto-Tracking Invisible al 90%
* **Cero Clics, Cero Preocupaciones:** Tan pronto como alcanzas el **90%** de un episodio, AnimeLocalTracker actualiza instantáneamente tu base de datos local y sincroniza tu perfil de AniList en vivo a través de GraphQL.
* **Soporte Offline con Sincronización Automática:** Si ves anime sin conexión a internet, tu progreso se guarda localmente y se sincroniza en la nube en cuanto recuperas la conexión.

### 🎨 4. Galería Visual y Fichas de Detalle en Alta Definición
* **Glassmorphism & Material Design 3:** Interfaz moderna en modo oscuro con transparencias, desenfoques dinámicos y micro-animaciones a 60 FPS.
* **Caché Instantáneo (0 ms de latencia):** Navegación fluida por colecciones de cientos de animes con carga asíncrona de portadas optimizadas.
* **Fichas Completas:** Banners cinemáticos, sinopsis limpias, géneros, temporadas, puntajes mundiales y lista interactiva de episodios con estado de visto/descargado.

### 🔍 5. Exploración de Catálogo y Calendario Semanal
* **Buscador en Tiempo Real:** Busca y añade cualquier título de la base de datos de AniList al instante.
* **Sección de Tendencias:** Descubre lo más popular de la temporada actual y los clásicos mejor valorados de todos los tiempos.
* **Calendario de Emisión Semanal:** Cuenta regresiva y horarios de estreno sincronizados con la emisión en Japón.

### 📥 6. Gestor Integrado de Descargas
* Descarga episodios directamente desde la aplicación con monitoreo de velocidad en vivo, barra de progreso y notificaciones al completar.

### 🛡️ 7. UX Pulida al Milímetro y Estabilidad Inquebrantable
* **Memoria Visual Infalible:** ¿Dejaste un capítulo a la mitad? El sistema inteligente de tracking local-first recuerda tu posición exacta al milisegundo y lo refleja en la interfaz visualmente, sin errores.
* **Inmersión sin Fricciones:** Controles de volumen estilizados (sin bordes toscos), gestión rápida de subtítulos de grado cinematográfico, e inmersión en pantalla completa con atajos intuitivos (`F11` o botón dedicado). Todo está diseñado para que te olvides del software y te sumerjas en la historia.

---

## 🏗️ Arquitectura e Ingeniería de Alto Rendimiento

AnimeLocalTracker está construido con estándares de ingeniería de software empresarial:

| Componente | Tecnología | Beneficio Técnico |
| :--- | :--- | :--- |
| **Plataforma Core** | **.NET 8 (C# 12)** | Rendimiento nativo x64, gestión de memoria moderna y compilación optimizada. |
| **Capa Gráfica** | **WPF + MaterialDesignInXaml** | Renderizado acelerado por GPU con estilos vectoriales fluidos y escalado DPI perfecto. |
| **Arquitectura** | **MVVM (CommunityToolkit.Mvvm)** | Desacople estricto con generación de código en tiempo de compilación para cero sobrecarga. |
| **Motor de Video** | **FlyleafLib (DirectX 11 / FFmpeg)** | Reproducción nativa empotrada sin dependencias externas pesadas. |
| **Base de Datos** | **SQLite en Modo WAL** | Consultas con índices compuestos `(AniListId, NumeroEpisodio)`, PRAGMAs optimizados y transacciones masivas sin bloqueo. |
| **Sistema de Logs** | **System.Threading.Channels** | Logger asíncrono no bloqueante con escritura en lotes y rotación automática de 5 MB. |
| **Caché de Imágenes** | **Two-Tier Cache (RAM + Disco)** | Límite de consumo en memoria (~135 MB) y decodificación diferida para evitar OOM. |
| **Actualizador** | **Velopack** | Actualizaciones silenciosas en segundo plano sin instaladores intrusivos ni ventanas de UAC. |

---

## 🚀 Comienza en 30 Segundos

### Para Usuarios Finales
1. Dirígete a la pestaña de [**Releases**](../../releases) del repositorio.
2. Descarga la última versión de `Setup_AnimeTracker_vX.X.X.exe`.
3. Ejecuta el instalador (se instalará en segundos de forma limpia).
4. Abre la aplicación, vincula tu cuenta de **AniList** en un clic y selecciona la carpeta donde guardas tus animes. ¡A disfrutar!

---

### Para Desarrolladores

#### Requisitos Previos
* **Windows 10/11** (x64)
* **[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** o superior
* **Visual Studio 2022**, **JetBrains Rider** o **VS Code** con extensiones de C#

#### Clonación y Compilación
```bash
# 1. Clonar el repositorio
git clone https://github.com/rodriguezrobinj/AnimeLocalTracker.git
cd AnimeLocalTracker

# 2. Restaurar dependencias y compilar
dotnet build

# 3. Ejecutar la suite completa de pruebas unitarias
dotnet test --no-build

# 4. Iniciar la aplicación
dotnet run --project AnimeLocalTracker/AnimeLocalTracker.csproj
```

---

## 🧪 Pruebas y Calidad de Código

El proyecto cuenta con una suite rigurosa de pruebas unitarias e integración con **xUnit** y **FluentAssertions**, cubriendo:
* Semánticas de *upsert* masivo en base de datos y prevención de duplicados.
* Resiliencia ante concurrencia y conexiones múltiples.
* Manejo seguro de mensajes reactivos y estados en ViewModels.

```text
Serie de pruebas: AnimeLocalTracker.Tests.dll (net8.0)
Correctas: 308 | Fallidas: 0 | Omitidas: 0 | Duración: ~40s
```

> Además del gate de cobertura del CI (≥45 % líneas / ≥30 % ramas), el pipeline ejecuta
> SCA bloqueante (NuGet/cargo/pip), `clippy -D warnings`, tests pytest del daemon Python y
> benchmarks comparativos contra historial (workflow manual/semanal). El historial de
> cambios por versión se mantiene en [`CHANGELOG.md`](CHANGELOG.md).

---

## 🔒 Privacidad y Filosofía

Creemos firmemente en el software **local-first**, privado y ultrarrápido:
* **Tus archivos se quedan en tu máquina:** La aplicación nunca sube, rastrea ni comparte tus archivos locales.
* **Sin telemetría invasiva:** Solo tú y tu cuenta oficial de AniList tienen el control de tu historial.
* **100% Código Abierto:** Transparencia total bajo licencia permisiva.

> **Matiz (PRI-05):** cuando **conectas tu cuenta de AniList**, la app *sí* sincroniza con
> la nube tu **progreso, estado y puntuación** (es su función principal). Lo que nunca sale
> de tu equipo son tus archivos de video, la biblioteca local y tu historial de reproducción.

---

## 🤝 Contribuciones y Comunidad

¡Las contribuciones son lo que hace que la comunidad de código abierto sea un lugar increíble para aprender, inspirar y crear!

* 🐛 **¿Encontraste un bug?** Abre un [Issue](../../issues).
* 💡 **¿Tienes una idea de mejora?** Inicia una [Discusión](../../discussions).
* 🚀 **¿Quieres aportar código?** Los Pull Requests son bienvenidos.

Si este proyecto te ha sido útil o te gusta la propuesta, no olvides dejar una ⭐ **Star en GitHub** para apoyar su desarrollo continuo.

---

## 📝 Licencia

Distribuido bajo la Licencia **MIT**. Consulta el archivo [`LICENSE`](LICENSE) para más detalles.

<div align="center">
  <sub>Hecho con ❤️ para la comunidad de entusiastas del anime y el código limpio.</sub>
</div>
