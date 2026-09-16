---
Proyecto: AnimeLocalTracker
Fase: 4 - Diseño y UX/UI
Fecha de auditoría: 2026-09-14
Hora de inicio: 23:59:00
Duración de la fase: 20:00
---

# Fase 4 — Diseño y UX/UI

## Metodología y desviaciones documentadas

- **No se instaló Accessibility Insights for Windows** (app de Microsoft Store, orientada a uso
  interactivo por un humano, difícil de pilotar de forma no interactiva y con instalación que
  requeriría `winget`/Store en la sesión del usuario). En su lugar se usó directamente la API de
  **UI Automation de .NET** (`System.Windows.Automation`, la misma tecnología subyacente que usa
  Accessibility Insights) vía PowerShell para recorrer el árbol de automatización de la ventana
  principal y detectar controles interactivos sin nombre accesible — cubre el chequeo automático
  más importante de Accessibility Insights ("Empty name") con evidencia real y reproducible.
- **No se probaron 3 niveles de escalado DPI del sistema (100/150/200%).** Cambiar el DPI a nivel
  de sistema operativo afecta a *todas* las ventanas abiertas del usuario (no solo la app bajo
  prueba) y en Windows moderno normalmente requiere cerrar sesión para aplicarse por completo —
  se consideró una acción demasiado invasiva sobre el escritorio real del usuario para
  ejecutarla sin confirmación explícita, por lo que se omitió. **Se marca explícitamente como "no
  evaluado"** en vez de asumir un resultado, siguiendo la Regla 6 del alcance. Alternativa
  recomendada para una corrida futura: usar una máquina virtual o un segundo monitor con
  escalado distinto, o el override de "Cambiar la configuración de alta resolución de pantalla"
  específico del `.exe` (Propiedades → Compatibilidad), que sí es no-invasivo por ser
  local a la app.
- Sí se ejecutó la app compilada localmente (Debug) y se navegó/capturó evidencia visual real
  (ventana normal, maximizada, redimensionada manualmente) — cumple parcialmente la Regla 5.

## 1. Evidencia visual capturada

| Archivo | Estado |
|---|---|
| `evidencia/01_ventana_principal_normal_100pct.png` | Ventana normal, tamaño por defecto (1366×728), vista "Mi Colección" |
| `evidencia/02_ventana_maximizada.png` | Ventana maximizada |
| `evidencia/03_ventana_redimensionada_estrecha.png` | Ventana redimensionada manualmente a 760×620 |

Nota: la app abrió directamente sobre una biblioteca local ya poblada del propio entorno del
usuario (168 animes en "Mi Colección"), no un estado vacío/onboarding — útil porque expone el
layout real bajo carga de contenido, pero significa que no se evaluó en esta corrida el estado
vacío ("sin animes añadidos aún") ni el flujo de onboarding inicial.

En la primera captura las carátulas aparecen como bloques grises (carga asíncrona de imágenes
aún en curso a los ~3 s de arranque); en las capturas siguientes (varios segundos después) todas
las carátulas ya están renderizadas — comportamiento esperado de un caché bi-capa
RAM+Disco como describe el README, no un bug.

## 2. HALLAZGO: ruptura de layout en la barra de pestañas al redimensionar la ventana

**Severidad: Media.** Comparando `02_ventana_maximizada.png` contra
`03_ventana_redimensionada_estrecha.png` (mismo estado de la app, solo cambia el ancho de
ventana a 760px): la fila de pestañas de filtro ("Todos / Viendo / Completados / Planeando", a la
derecha del buscador) **no se reordena ni se colapsa a un control compacto** al reducirse el
ancho disponible — el texto de las pestañas se solapa visualmente con el borde del campo de
búsqueda/botón "Filtros" y las pestañas "Completados"/"Planeando" quedan casi completamente
recortadas fuera del área visible, sin scroll horizontal ni menú "más opciones" de respaldo. Esto
confirma exactamente el tipo de problema que el alcance de esta fase pedía verificar
("especialmente en la galería visual de pósteres y fichas de detalle") — aquí aparece en la
cabecera, no en la galería de pósteres en sí (la grilla de pósteres sí se reflowea
correctamente, ver §3).

**Recomendación (quick win):** aplicar un `WrapPanel` o un patrón de colapso a menú desplegable
("⋮ Más filtros") para la fila de pestañas cuando el `ActualWidth` del contenedor cae por debajo
de un umbral, siguiendo el mismo patrón responsive que ya parece aplicarse correctamente a la
grilla de pósteres.

## 3. Grilla de pósteres — reflow correcto bajo redimensionado

A diferencia de la barra de pestañas, la grilla de tarjetas de anime sí se adapta correctamente:
pasa de 6 columnas (maximizada) a 3 columnas (760px de ancho) sin recortar contenido ni romper el
aspect ratio de las carátulas — comportamiento responsive correcto, ningún hallazgo aquí.

## 4. HALLAZGO DE ACCESIBILIDAD: 32% de los controles interactivos principales sin nombre accesible

**Severidad: Media-Alta (bloqueante para usuarios de lector de pantalla).**

Recorrido del árbol de UI Automation sobre la ventana principal (`System.Windows.Automation`,
`TreeScope.Descendants`, filtrado a `Button`/`MenuItem`/`TabItem`/`ListItem`/`Hyperlink`):

- **115** elementos de control totales detectados en la vista "Mi Colección".
- **43** de ellos son controles interactivos evaluables (botones, pestañas, etc.).
- **14 de 43 (32.6%)** tienen la propiedad `Name` de UI Automation **vacía**.

De los 14 sin nombre, **9 corresponden exactamente a las tarjetas de póster de anime** (tamaño
178×265, coincide con las tarjetas visibles en la captura de pantalla — cada tarjeta clicable es
un `Button` en XAML cuyo único contenido visual es la imagen de carátula, sin
`AutomationProperties.Name` enlazado al título del anime). Los otros 5 corresponden a controles
de la cabecera (probablemente "Qué veo hoy" — 135×40, "Conectar" — 117×40, el badge de versión
"v1.0.5" — 61×22, y dos botones más de 90×44 y sin identificar en la zona de la cabecera).

**Impacto real:** un usuario de Narrator (o cualquier lector de pantalla) que navegue la galería
con Tab/flechas escuchará únicamente **"Botón"** repetido 168 veces (una por cada anime de la
colección del usuario), sin poder distinguir un título de otro sin activar cada tarjeta — esto
hace la función central de la app (navegar la colección) esencialmente inutilizable con lector de
pantalla, pese a que MaterialDesignInXaml (el sistema de diseño usado) sí soporta
`AutomationProperties.Name` de forma nativa en sus plantillas de `Button`.

**Recomendación (quick win, bajo esfuerzo):** enlazar `AutomationProperties.Name="{Binding
Titulo}"` (o equivalente) en la plantilla del botón de tarjeta de anime en la vista de galería, y
lo mismo para los 5 controles de cabecera identificados. Dado que ya existe un sistema de
localización centralizado (`LocalizationService`, confirmado en Fases 0-1), este fix se puede
aplicar de forma consistente en ambos idiomas (ES/EN) reutilizando las claves ya existentes para
el texto visible de cada control.

**No evaluado en esta corrida (fuera del árbol de la ventana principal):** navegación completa por
teclado (Tab order, atajos), los **controles overlay del reproductor de video** (mencionados
explícitamente en el alcance como "punto ciego típico de accesibilidad") — no se abrió una sesión
de reproducción real en esta fase (ver Fase 3 §4, requiere un archivo de video de prueba), por lo
que este punto de atención específico del brief queda pendiente para una corrida con un fixture
de video disponible.

## 5. Glassmorphism / blur dinámico — impacto en GPUs de gama baja

**No evaluado con medición de frames** (requeriría una GPU integrada de gama baja disponible y
`PresentMon`/contadores GPU en ejecución durante interacción activa con la UI, fuera del
presupuesto de tiempo de esta corrida). Observación de código: no se auditó en esta fase cuántos
elementos `BlurEffect`/acrílico dinámico están activos simultáneamente en pantalla — recomendación
para una corrida futura con acceso a una GPU integrada real: perfilar con `dotnet-trace` +
proveedor ETW de Direct3D mientras se hace scroll rápido sobre la galería de 168 elementos (el
escenario más exigente ya confirmado disponible en los datos reales del usuario).

## 6. Consistencia del sistema de diseño (MaterialDesignInXaml) — revisión visual rápida

A partir de las 3 capturas: paleta oscura consistente (fondo `#0d1117`-like, acentos rojo/azul
coherentes con el badge del README `color=e50914`), tipografía y espaciado uniforme entre
tarjetas, estados ("Finalizado"/"En Emisión"/"N Nuevos") con contraste de color suficiente a
simple vista. No se hizo una auditoría exhaustiva de recursos XAML duplicados/hardcodeados
(requeriría revisar los diccionarios de recursos completos, no solo la evidencia visual) — se
marca como **fuera de profundidad de esta corrida**, recomendado como seguimiento con más tiempo
asignado.

## 7. Resumen de hallazgos

| # | Hallazgo | Severidad |
|---|---|---|
| 1 | Barra de pestañas de filtro rompe/se solapa al redimensionar ventana a <~800px | Media |
| 2 | 32.6% de controles interactivos principales sin nombre accesible (9 son las tarjetas de póster) | **Media-Alta** |
| 3 | Grilla de pósteres se adapta correctamente al redimensionado | Ninguna (positivo) |
| 4 | Escalado DPI 150%/200% | No evaluado — acción invasiva sobre el escritorio del usuario |
| 5 | Overlay de controles del reproductor de video | No evaluado — requiere fixture de video (ver Fase 3) |
| 6 | Rendimiento del efecto glassmorphism en GPU de gama baja | No evaluado — requiere GPU integrada + herramienta de perfilado gráfico |
