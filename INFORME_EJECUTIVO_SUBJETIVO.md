# Informe ejecutivo — AnimeLocalTracker

**Fecha:** 14 de septiembre de 2026
**Autor:** Lectura independiente basada en el código, el README, `AGENTS.md`, las 4 auditorías por bloque (Seguridad/Arquitectura, Funcionalidad/Integraciones, Rendimiento/DevOps, UX/Legal/Marketing) y sus notas de remediación
**Naturaleza del documento:** Valoración **subjetiva** de gestión de producto e ingeniería. No sustituye asesoría legal ni una auditoría de seguridad formal.

---

## 1. Resumen ejecutivo

AnimeLocalTracker es un tracker de anime local para Windows (.NET 8 / WPF) que envuelve una colección de video en disco con la experiencia de una plataforma de streaming: reproductor propio sobre Flyleaf/DirectX 11, salto automático de openings/endings vía AniSkip, sincronización en vivo con AniList, galería con caché de portadas y un gestor de descargas integrado. Por debajo hay un stack poco habitual para un proyecto de este tamaño: un núcleo nativo en Rust vía FFI para parsing y fingerprinting, y un daemon Python empaquetado (PyInstaller) para ffmpeg/yt-dlp/OpenCV.

Mi valoración general, tras revisar tanto el código como el propio historial de auditoría del proyecto, es que **este es un proyecto personal llevado con una disciplina de ingeniería que normalmente solo se ve en equipos con proceso formal**: 327 tests, SCA bloqueante en CI, firma de código, actualizaciones delta con Velopack, y — algo poco frecuente — una auditoría externa completa (4 bloques, 105 hallazgos) que ya se tradujo en 65 correcciones reales sobre el código, verificadas con tests. Eso es la parte más fuerte de la historia.

La contrapartida es igual de real: la capa de presentación (WPF/MVVM) acumula deuda de arquitectura que va a encarecer cada función nueva si no se atiende pronto; la accesibilidad estaba (y en gran parte sigue estando) prácticamente ausente; la promesa de bilingüismo (ES/EN) no se cumple fuera de 2-3 pantallas; y hay una pieza operativa frágil — el daemon Python de 73 MB — cuyo fallo de arranque ya se observó en logs reales, no es solo una hipótesis de auditoría.

En síntesis: **el producto es sólido y honesto con el usuario (local-first real, sin telemetría, verificado por código), y el proceso de mejora continua ya está funcionando**; lo que falta es convertir ese impulso de "auditoría y corrección" en una cadencia sostenida de release, accesibilidad e internacionalización, en vez de dejarlas como una lista de pendientes que crece de bloque en bloque.

---

## 2. Estado actual, en una frase por bloque

| Bloque | Madurez (equipo auditor, 0-5) | Lectura subjetiva |
|---|---|---|
| Seguridad | 3.8 | Fundamentos correctos (DPAPI, TLS del sistema, SQL parametrizado, SCA en CI); los huecos que quedan son de "defensa en profundidad", no de vulnerabilidades explotables triviales. |
| Arquitectura | 3.5 | La lógica de dominio es limpia; la capa de presentación (VM de 586 líneas, ServiceLocator, doble registro de DI) es la que va a doler al crecer. |
| Funcionalidad | 3.2 | Las features prometidas existen y funcionan; el problema no era "qué falta" sino "qué miente" (ajustes que no hacían nada) y 3 rutas con pérdida real de datos del usuario — ya corregidas. |
| Integraciones | 3.4 | Buen manejo de degradación (AniSkip, proveedor de video, Polly con backoff); el daemon Python es el eslabón más frágil del sistema. |
| Rendimiento | 3.0 | No es que vaya lento: es que **nunca se había medido nada** hasta la remediación. La UI tiene puntos O(N²) reales en bibliotecas grandes. |
| Calidad/DevOps | 3.6 | Pipeline serio para un proyecto de este tamaño (SCA triple, Actions pinneadas, cobertura con gate); el gate de cobertura vive con un margen de menos de 2 puntos. |
| UX/Accesibilidad | 2.9 | El punto más débil del proyecto. Visualmente cuidado, pero la app era (y en su mayoría sigue siendo) inoperable con teclado o lector de pantalla. |
| Privacidad | 3.3 → mejor tras remediación | El producto cumple lo que promete (verificado por grep, no solo por el README); faltaban mecanismos de control al usuario (borrado, consentimiento), ya añadidos. |
| Marketing/SEO | 2.4 | No aplica gran cosa a un desktop sin web, pero el propio README —que es la única "landing page" del proyecto— no tenía ni una captura de pantalla. |

Nota metodológica: estas cifras son del propio equipo auditor (2026-09-02); las traigo aquí porque coinciden bastante con lo que yo mismo leo en el código y los documentos, y dan un punto de partida cuantitativo a esta lectura subjetiva.

---

## 3. Pros y contras (visión general)

### Lo que juega a favor

- **Disciplina de ingeniería fuera de lo común para un proyecto de este tamaño.** STRIDE, diagramas C4, SCA bloqueante en 3 ecosistemas (NuGet/cargo/pip), Actions pinneadas por SHA, firma de código y actualizaciones delta — es el tipo de rigor que normalmente se ve en producto con equipo dedicado de seguridad, no en un tracker de anime personal.
- **El proyecto se audita y se corrige a sí mismo.** 105 hallazgos → 65 corregidos con tests, 6 con mitigación documentada, 34 diferidos **con causa explícita** (no simplemente ignorados). Eso es un proceso de mejora continua real, no cosmético.
- **Cumple lo que promete en privacidad.** "Local-first, sin telemetría" no es solo un eslogan de README: se verificó por grep en el código (0 hits de analytics/Sentry/AppInsights) y ahora además hay borrado total de datos y consentimiento previo al login.
- **La ingeniería de dominio (no la UI) está muy bien resuelta.** Upserts idempotentes, backups atómicos con `VACUUM INTO` + `integrity_check`, migraciones versionadas, FFI Rust con `catch_unwind` y clamps, daemon Python sin `shell=True` — decisiones de nivel senior en las partes que no se ven.
- **Base de tests real y creciente** (265 → 308 → 327 según los distintos documentos), no solo declarativa: cubre concurrencia, upserts masivos y casos límite.

### Lo que juega en contra

- **La accesibilidad es, hoy, casi inexistente.** Cero `AutomationProperties`, cero `TabIndex` verificados en el código: un usuario que dependa de teclado o lector de pantalla no puede operar el reproductor, la galería ni los diálogos. Esto contradice de forma directa la narrativa de "plataforma de streaming premium" del propio README.
- **Deuda de arquitectura concentrada donde más cuesta tocarla.** `ServiceProvider` estático global, `MainViewModel` de 586 líneas resolviendo 13+ ViewModels y actuando de navegador/diálogo/buscador a la vez, fábrica manual de vistas con `new` en paralelo a un contenedor DI cuyas vistas registradas nunca se resuelven. No es un problema de "hoy no funciona"; es un problema de "cada feature nueva cuesta más que la anterior".
- **Triple motor de parsing de nombres de archivo** (regex C# → Rust anitomy → Python anitopy) para la misma tarea. Tres implementaciones que pueden divergir en el resultado y que triplican el coste de mantenimiento de algo tan central como "reconocer qué episodio es este archivo".
- **El daemon Python es el punto más frágil del sistema, y no es teórico:** el log real de producción muestra 2 de 2 sesiones con handshake fallido, degradando toda la sesión a arrancar un binario de 73 MB por cada comando (miniaturas, metadatos, descargas). Ya se corrigió con reintentos y backoff, pero la causa de fondo (onefile pesado) sigue ahí.
- **El bilingüismo es una promesa a medias.** La infraestructura de localización existe y funciona donde se usa, pero 8 de 10 vistas (galería, detalle, calendario, reproductor…) siguen con literales en español fijos. Con el idioma en inglés activo, la mayoría de la app sigue hablando español.
- **Cero mediciones de rendimiento hasta hace muy poco.** Existían benchmarks construidos con cuidado (BenchmarkDotNet, comparación ±5% contra historial) que **nunca se habían ejecutado ni una sola vez**, con una discrepancia de rutas que los habría hecho inútiles aunque se corrieran. Rendimiento se diseñó bien "a ojo", pero sin ningún dato real que lo confirme todavía.

---

## 4. Ventajas y desventajas por área

**Seguridad.** Ventaja: superficie de ataque bien entendida (STRIDE por flujo), invariantes de datos respetadas en código (nunca en el directorio de instalación, backups atómicos), sin banderas rojas de las graves (sin TLS bypass, sin `shell=True`, SQL 100% parametrizado). Desventaja: el flujo OAuth depende de un puerto fijo (5050) en loopback porque AniList no ofrece alternativa para apps nativas — es una limitación de plataforma aceptada y mitigada, no resuelta del todo, y probablemente nunca lo esté sin que AniList cambie su API.

**Arquitectura.** Ventaja: el dominio (parsing, reglas de sincronización, persistencia) está razonablemente desacoplado de la UI en las partes nuevas. Desventaja: la capa de presentación creció "orgánicamente" y hoy concentra casi toda la deuda técnica del proyecto en un puñado de archivos; es manejable ahora, pero escala mal si el proyecto sigue añadiendo pantallas al ritmo actual.

**Funcionalidad.** Ventaja: el diseño de flujos es más cuidadoso que la media (confirmaciones destructivas dobles, degradación elegante cuando un servicio externo falla, upserts idempotentes). Desventaja: hasta la remediación reciente, tres rutas distintas podían **perder o degradar datos reales del usuario en AniList** de forma silenciosa — el tipo de bug que rompe la confianza del usuario aunque el resto de la app funcione perfecto.

**Rendimiento.** Ventaja: las decisiones de diseño (WAL, índices compuestos, virtualización en galería, imágenes congeladas y limitadas a ~135 MB) son correctas para el tamaño actual de biblioteca. Desventaja: con series de 1000-3000 episodios había refresco cuadrático en la ficha de detalle, y la ausencia total de benchmarks ejecutados significa que cualquier afirmación de "rinde bien" era, hasta ahora, una suposición.

**UX/Accesibilidad.** Ventaja: sistema visual consistente (paleta tokenizada, Material Design, modo oscuro cuidado); en el propio working tree de hoy ya hay trabajo activo en esta dirección (navegación por teclado y foco visible añadidos a los resultados de búsqueda de `AgregarAnimeView`, siguiendo el mismo patrón que `GaleriaView`). Desventaja: sigue siendo, en conjunto, una app visualmente pulida pero mayormente cerrada para cualquiera que no use mouse y buena vista — lo corregido hasta ahora son focos puntuales, no una pasada completa; y varios contrastes de color siguen por debajo del mínimo WCAG AA en textos pequeños.

**Privacidad/Legal.** Ventaja: el discurso de privacidad es honesto y verificable, algo raro de encontrar y que podría ser un argumento de venta real. Desventaja: hasta la remediación no había forma de purgar los datos ni consentimiento explícito antes de conectar AniList — dos ausencias que en cualquier revisión de cumplimiento (GDPR u otra) habrían sido señaladas de inmediato.

**DevOps/Calidad.** Ventaja: pipeline con más controles automáticos (SCA triple, firma, dependabot en 4 ecosistemas) que la mayoría de proyectos comerciales pequeños. Desventaja: el gate de cobertura vive con menos de 2 puntos de margen (46,92% real vs 45% de umbral) — cualquier código nuevo sin tests puede tirar el pipeline sin que sea una señal de regresión real, solo de aritmética ajustada.

**Marketing.** Ventaja: ninguna necesaria más allá de un producto honesto — pero el README es literalmente la única vitrina del proyecto y no tenía ni una captura de pantalla ni el archivo de licencia que prometía (enlace roto), ya corregido. Desventaja persistente: los releases se publican siempre como *pre-release*, lo que puede desalentar a un usuario nuevo que busque "la versión estable".

---

## 5. Campos de mejora

Priorizados por impacto para el usuario final, no solo por severidad técnica:

1. **Accesibilidad básica (UX-001/002/004/005).** Nombres accesibles ya se añadieron en los controles auditados y el contraste ya se corrigió en los puntos señalados; el foco en resultados de búsqueda también se está cerrando ahora mismo (cambio sin commitear a fecha de este informe). Falta completar diálogos y reproductor. Es el campo de mejora con mayor impacto humano de todo el proyecto.
2. **Localización real (ARC-009/UX-016).** 8 de 10 vistas sin traducir es una brecha grande para un proyecto que se presenta en inglés en su propio README/badges.
3. **Consolidación del motor de parsing.** Definir Rust como motor canónico y dejar Python solo para lo que únicamente él sabe hacer (ffmpeg/yt-dlp/OpenCV) reduciría de tres implementaciones a una la lógica más central del producto.
4. **Robustez del daemon Python.** Evaluar `--onedir` en vez de `--onefile`, o un modo daemon persistente desde el arranque, para eliminar la causa raíz (extracción de 73 MB) en vez de solo mitigar el síntoma con reintentos.
5. **Refactor de la capa de presentación (ARC-002/003/004).** `NavigationService` + `DataTemplates` ya se implementó parcialmente; falta terminar de sacar el `ServiceProvider` estático de los ViewModels y desacoplar el modelo persistido de `ImageSource` de WPF.
6. **Cultura de medición de rendimiento.** Ahora que existe el workflow de benchmarks en CI, falta generar la primera línea base real y empezar a vigilar regresiones de verdad, no solo cobertura de tests.
7. **Higiene de release.** Publicar una versión marcada como estable (no *pre-release*) una vez que las correcciones actuales estén realmente probadas por usuarios, y curar las notas de cambio en vez de dejarlas autogeneradas.
8. **Marco legal mínimo.** Una advertencia de contenido/edad es razonable para un catálogo de anime (parte del contenido no es apto para todo público) — ya hay un primer texto de aviso de contenido añadido en `AcercaDeView` (sin commitear a fecha de este informe); falta decidir si debe mostrarse también de forma más visible (p. ej. al primer arranque) y no solo en "Acerca de".

---

## 6. Recomendaciones

**Ya (próximos días):**
- Cortar un tag de versión con todo lo remediado (hoy vive en `[No publicado]` del CHANGELOG) para que las correcciones de pérdida de datos y seguridad lleguen a quien ya usa la app, en vez de quedarse en el repositorio.
- Ejecutar el workflow de benchmarks una vez para tener, por fin, una línea base real contra la que comparar cualquier cambio futuro de rendimiento.

**Próximas 2-4 semanas:**
- Cerrar el bloque de accesibilidad por teclado (foco en diálogos y en resultados de búsqueda) — es lo que más rápido cambiaría quién puede usar la app.
- Decidir explícitamente el futuro del daemon Python: si se queda, invertir en que el arranque sea confiable (el log real ya mostró que falla); si se puede reducir su alcance, mejor aún.

**1-2 meses:**
- Proyecto dedicado de internacionalización: inventariar y migrar los literales de las 8 vistas restantes, no como tarea residual sino como una entrega con objetivo propio.
- Continuar el refactor de `MainViewModel`/`ServiceProvider` mientras el proyecto es todavía manejable — cuanto más crezca la app, más caro será separar navegación, diálogos y resolución de dependencias.

**Estratégico (sin fecha fija):**
- Convertir la honestidad en privacidad en un argumento de comunicación explícito: pocos proyectos pueden decir "verificamos por código que no hay telemetría", y hoy eso vive enterrado en una sección del README.
- Si el proyecto aspira a más usuarios, invertir el orden habitual: antes de sumar features nuevas, cerrar la brecha entre lo que el README promete (plataforma "premium", bilingüe, accesible) y lo que la app entrega hoy en esos tres frentes.

---

## 7. Conclusión

Mi lectura, con la información disponible, es que AnimeLocalTracker está mejor gestionado en su trastienda (seguridad, arquitectura de dominio, CI/CD, proceso de auditoría) que en su cara visible (accesibilidad, localización, pulido de la promesa "streaming premium"). Es exactamente el patrón que se espera de un proyecto construido por alguien con criterio de ingeniería fuerte trabajando en solitario o con apoyo de herramientas de IA para auditar y corregir: el código bajo el capó recibe un trato casi profesional, y las capas que requieren validación humana repetida — accesibilidad real, traducción completa, capturas de marketing — quedan un paso por detrás porque no se pueden resolver solo con disciplina técnica.

La buena noticia, y la razón por la que esta valoración es más optimista que crítica, es que el proyecto ya demostró que sabe auditarse y corregirse a sí mismo con seriedad (65 de 105 hallazgos resueltos con tests, no de palabra). El reto ahora no es técnico: es de secuencia y prioridad — llevar ese mismo rigor a la accesibilidad y a cerrar la brecha entre lo que el producto promete y lo que hoy entrega.

---

*Este informe es una valoración subjetiva de gestión de producto e ingeniería basada en el código y la documentación del propio proyecto (README, AGENTS.md, auditorías por bloque y sus notas de remediación) a fecha de hoy. No sustituye una auditoría de seguridad formal ni asesoría legal.*
