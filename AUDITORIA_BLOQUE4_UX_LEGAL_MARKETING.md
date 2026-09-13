# INFORME DE AUDITORÍA — AnimeLocalTracker · Bloque 4: UX/Accesibilidad, Legal/Privacidad y Marketing (mínimo)

| | |
|---|---|
| **Proyecto** | AnimeLocalTracker v1.0.5 (desktop Windows, .NET 8 WPF) |
| **Fecha** | 2026-09-02 |
| **Versión del informe** | 1.0 (entrega final del plan por bloques) |
| **Alcance cubierto** | Bloque 4: §4.7 UX/UI y accesibilidad (WCAG 2.1 AA aplicable a WPF), §4.9 Legal/privacidad (buenas prácticas; sin marco legal formal definido por el cliente) y §4.8 Marketing/SEO mínimo (repo GitHub; producto sin presencia web) |
| **Confidencialidad** | Interno — uso exclusivo del equipo del proyecto |
| **Equipo firmante** | Especialista UX/UI y accesibilidad · Analista de privacidad · Marketing digital (nota mínima) |
| **Metodología** | Auditoría **estática** de XAML/C# con `file:line`; cálculos de contraste WCAG realizados por el equipo (fórmula de luminancia relativa); verificaciones por grep (`AutomationProperties`: 0 usos; `TabIndex`: 0 usos; `LocalizationService.Instance`: 93 refs, solo en Configuración/Estadísticas/MainWindow). Sin pruebas con usuarios ni lectores de pantalla en runtime. El análisis legal es **orientativo y no constituye asesoría jurídica**. |

---

## 1. Resumen ejecutivo

**Madurez UX/UI: 2.9/5 · Privacidad (buenas prácticas): 3.3/5.** Visualmente el producto es consistente y moderno (paleta tokenizada, escala tipográfica, MaterialDesign como base), pero la capa de accesibilidad es **prácticamente inexistente**: cero `AutomationProperties`, cero `TabIndex`, 6 de 10 vistas sin ningún texto localizado y **9 pares de color que fallan WCAG AA** en texto pequeño (incluidos botones primarios y KPIs). En privacidad, el producto cumple lo que promete (local-first real, sin telemetría — verificado por grep), pero **no existe borrado total de datos, ni política de privacidad en la app, ni consentimiento previo informado al login**, y el README promete un archivo `LICENSE` que **no existe en el repo** (enlace 404).

### Top 5 riesgos (Bloque 4)

| # | Riesgo | ID | Impacto |
|---|---|---|---|
| 1 | **0** `AutomationProperties` y **0** `TabIndex`: lectores de pantalla y teclado no pueden operar la app (botones de ventana, reproductor, descargas, búsqueda) | UX-001/004/005 | **Alto** (exclusión de usuarios con discapacidad) |
| 2 | Contraste AA fallido en texto pequeño (KPIs `#6B7280` a 9-10 px = 3.68-3.88:1; blanco sobre `#3B82F6`/`#F43F5E`/`#4CAF50` = 2.7-3.7:1 en botones y badges) | UX-002/003 | **Alto** |
| 3 | Diálogos modales sin gestión de foco y overlay sin trampa de foco; resultados de búsqueda inalcanzables por teclado | UX-005 | **Alto** |
| 4 | Desinstalar/borrar la app **no elimina los datos** (`%LocalAppData%\AnimeLocalTrackerData` queda para siempre) y no hay opción de purga en la app | PRI-001 | **Alto** |
| 5 | El ajuste UI "Umbral para marcar como visto" **no gobierna el auto-marcado** (fijo 90 %) — misma promesa rota que FUN-003; agravado aquí con el feedback del toast en pantalla completa | UX-009 | **Medio** |

**Méritos verificados:** paleta tokenizada real con escala tipográfica (`App.xaml:59-175`); cambio de idioma en caliente ES/EN bien implementado donde existe; confirmaciones destructivas en las acciones correctas (eliminar anime ×2, restaurar backup); estados de carga/progreso presentes en escaneo y descargas; auto-tracking con feedback toast + actualización visual del episodio; README transparente y **veraz** (local-first sin telemetría — confirmado en código).

---

## 2. Accesibilidad (§4.7) — WCAG 2.1 AA aplicado a WPF

### 2.1 Nombres accesibles y lectores de pantalla (UX-001 — Alto)

**Evidencia:** grep `AutomationProperties|AutomationPeer|LiveSetting|HelpText` en todo `AnimeLocalTracker/`: **0 resultados** (verificado). Sin `AutomationProperties.Name`, un lector de pantalla anuncia los icon-buttons como "botón" sin función:

| Vista:línea | Control | Nombre accesible |
|---|---|---|
| `MainWindow.xaml:332-340` | Minimizar / Maximizar / Cerrar | Ninguno (sin ToolTip ni AutomationProperties) |
| `ReproductorView.xaml:220-223,319-321` | Volver, silenciar/volumen | Ninguno |
| `DescargasView.xaml:115-130` | Pausar/reanudar por descarga | Ninguno (icono dinámico Pause/Play) |
| `GaleriaView.xaml:62-72`, `DetalleView.xaml:553-558,501-506` | Multi-selección, menú de descargado, favorito | Solo ToolTip en ES (que Narrator no anuncia por defecto) |

Los toasts no son *live regions* (`AutomationProperties.LiveSetting=Polite` no existe): ni errores ni auto-tracking se anuncian.

### 2.2 Contraste — tabla calculada (UX-002/003 — Alto)

Metodología: luminancia relativa WCAG `L = 0.2126·R + 0.7152·G + 0.0722·B` (componentes lineales). Nota: el rojo `#e50914` del branding **no existe en la app** (solo en el badge del README); los rojos reales de la paleta se analizaron en su lugar.

| Texto | Fondo | Ratio | AA 4.5:1 (normal) | Uso (evidencia) |
|---|---|---|---|---|
| `#E2E8F0` | `#14181E` | 14.45 | ✅ | Texto primario sobre tarjetas |
| `#9CA3AF` | `#121212` | 7.38 | ✅ | Labels/secundarios (`ConfiguracionView.xaml:57`) |
| `#9CA3AF` | `#1E293B` | 5.76 | ✅ | Texto sobre chips |
| **`#6B7280`** | `#121212` | **3.88** | ❌ | **10 px bold, KPIs** (`EstadisticasView.xaml:85,95,105,115,177,193,209,225`) |
| **`#6B7280`** | `#14181E` | **3.68** | ❌ | Calendario 11 px (`CalendarioView.xaml:30`); título "visto" en Detalle (`DetalleView.xaml:611`) con `Opacity 0.6` → **2.13** (peor caso) |
| **White** | `#3B82F6` (primario) | **3.68** | ❌ | Botones 12-14 px (`App.xaml:169-171`; `ConfiguracionView.xaml:82,441`) y badge 10 px (`MainWindow.xaml:220`) |
| **White** | `#F43F5E` (Rose) | **3.67** | ❌ | Badge "N Nuevos" 11 px (`GaleriaView.xaml:614-619`) |
| **White** | `#4CAF50` / `#9E9E9E` | **2.78 / 2.68** | ❌ | Chips de estado (`GaleriaView.xaml:606-610`) |
| `#F87171` | `#121212` | 6.77 | ✅ | Acciones destructivas |
| `#93C5FD`, `#FECACA`/`#3A1416`, `#D1D5DB` | oscuros | 10.4-12.7 | ✅ | Acentos y banners |

**Lectura (desde UX):** los fallos se concentran en texto **pequeño (9-14 px)** con el token `Brush.TextTertiary #6B7280` y en **blanco sobre acentos saturados**. Corrección de bajo riesgo: subir `TextTertiary` a `#9CA3AF` (cumple 4.5:1 en todos los fondos oscuros usados: 5.76-7.38:1) y oscurecer fondos de acento (`#2563EB`, `#E11D48`, `#16A34A`) o aceptar solo 3:1 para texto ≥18.66 px/14 px bold y documentarlo.

### 2.3 Navegación por teclado y foco (UX-004/005 — Alto)

- `TabIndex`: **0 usos** (verificado). Resultados de búsqueda (`AgregarAnimeView.xaml:150-167`, `Focusable=False` en `:152`) y tarjetas de galería (`GaleriaView.xaml:520-536`) sin estado de foco → **inalcanzables/imperceptibles por teclado**.
- Reproductor: todos los controles `Focusable="False"` (`ReproductorView.xaml:220-507`) → solo operable por atajos (documentados de forma parcial y **hardcodeada**: "Espacio", "(S)", "(N)/(P)" en `ReproductorViewModel.cs:709-716,1188` — si el usuario reasigna teclas, los hints mienten; 6 de 12 atajos configurables, `ConfiguracionView.xaml:406-434`).
- Diálogo custom (`MainWindow.xaml:344-369`) y editor de seguimiento (`DetalleView.xaml:662-851`) **no mueven el foco** al abrir ni lo devuelven al cerrar; sin Esc-para-cancelar; el Tab puede llegar a controles bajo el overlay.
- `FocusVisualStyle="{x:Null}"` en `Themes/CustomComboBox.xaml:16,115` y `DetalleView.xaml:671` (foco visible eliminado).
- Positivos: F11 con manejo de doble disparo (`MainWindow.xaml.cs:204-210`), foco devuelto a la ventana al cerrar el player (`ReproductorViewModel.cs:1294-1305`), sin animaciones estroboscópicas (>3 destellos/s) — solo shimmer 1.5 s y fades lentos.

### 2.4 Escalado y preferencias

- WPF escala por DPI de forma nativa, pero **no hay `app.manifest`** → la app es system-DPI aware, no PerMonitorV2: arrastrar entre monitores de distinto DPI produce bitmap-stretching. Sin consulta a la preferencia "reduce motion" ni a "high contrast" de Windows.
- Solo tema oscuro (`App.xaml:10`, `BaseTheme="Dark"`), sin opción de tema claro.

### 2.5 Consistencia del sistema de diseño (UX-018 — Bajo/Medio)

- **Lo bueno:** paleta de 16 brushes tokenizados + escala tipográfica + `AppCard/AppBadge/AppPageRoot/AppEmptyTitle` + estilos de botón (`App.xaml:59-175`); iconos PackIcon consistentes; 2 temas custom (`MinimalScrollBar`, `CustomComboBox`).
- **La realidad:** los recursos **no se usan de forma sistemática**:
  - Fila de ajustes `#14181E/#262D38/CornerRadius=10/MinHeight=56/Padding=16,8` repetida **9×** inline (`ConfiguracionView.xaml:216,231,246,261,284,306,330,345,364`) — no existe "AppSettingRow".
  - `AppCard` definido pero **sin uso**: las cards usan `#121212` inline (`ConfiguracionView.xaml:49,203,453`, `AcercaDeView.xaml:46,84,121`).
  - Margen de página con trigger Topmost duplicado en 5 vistas en vez de `AppPageRoot`.
  - **Paleta duplicada fuera de tokens:** 3 verdes distintos (`#4CAF50/#10B981/#34D399`), 2 familias de rojos (`#FF5252/#E53935` vs `Brush.Danger #F87171`), contadores de hex literales: DetalleView 113, EstadisticasView 102, ConfiguracionView 87.
  - Tipografía: tamaños literales `10.5/11.5/12.5/13.5` conviven con la escala `AppText.*`; subtítulos en `Trebuchet MS` (única desviación tipográfica).
  - Estados vacíos sin plantilla única (3 patrones distintos).
- **Recomendación:** inventario visual + migración a tokens en 2-3 pases por vista (esfuerzo M), empezando por Configuración/Estadísticas.

### 2.6 Heurísticas y user journey (UX-009 a UX-017)

**Visibilidad de estado:** escaneo y descargas con progreso real ✅; **sync automático invisible** (corre cada 5 min sin indicador; solo log) ❌; import/export/restore sin progreso (solo diálogo final) ⚠️.

**Prevención de errores:** confirmaciones en eliminar anime (×2) y restore ✅; **cancelar descargas sin confirmación** (pierde archivo parcial) ⚠️; **eliminar de biblioteca disfrazado de corazón** "Biblioteca" con tooltip "Clic para eliminar" (`DetalleView.xaml:70-79`) ❌ (patrón de estado presentado como destructivo); confirmación de restore **antes** de elegir archivo (`ConfiguracionViewModel.cs:370-381`) — orden invertido.

**Fricciones de journey (con evidencia):**
- **(a) Primera ejecución:** sin onboarding; la carpeta por defecto `Videos\Anime` (`AppSettings.cs:9`) puede no existir y no se avisa; el CTA del estado vacío lleva a buscar anime, no a elegir carpeta (`GaleriaView.xaml:479-490`) — un usuario nuevo puede tardar en descubrir Configuración.
- **(b) Login AniList:** 1 clic → navegador, **sin copy previo de qué se sincroniza**; timeout de 2 min sin feedback in-app.
- **(c) Auto-track:** toast 3.5 s fácil de perder en pantalla completa; umbral UI (95 %) no gobierna el auto-mark (90 % fijo — ref. FUN-003).
- **(d) Descarga:** 1 clic sin diálogo de fuente/calidad/tamaño; errores crudos `ex.Message` al fallar la reproducción (`DetalleViewModel.cs:832`) y stderr de ffmpeg en diálogo de captura (`ReproductorViewModel.cs:1003-1008`).
- **(e) Buscar/añadir:** flujo limpio con debounce, dedupe y CTA "En tu biblioteca" ✅.
- **(f) Restaurar backup:** orden invertido de confirmación (ver arriba).

### 2.7 Localización (UX-016 — Bajo pero amplio)

- Mecanismo sólido: diccionarios C# ES/EN en `LocalizationService` (~215 claves/idioma), cambio **en caliente** sin reinicio (`ConfiguracionViewModel.cs:56-63`).
- **Cobertura real:** 93 refs a `LocalizationService.Instance` **concentradas en 3 archivos** (ConfiguracionView, EstadisticasView, MainWindow — verificado por grep). **Galería, Detalle, Calendario, Agregar, AcercaDe, Descargas y Reproductor: 0 bindings localizados** (galería 34 literales ES, detalle 34, calendario 23 — incluye días LUNES…DOMINGO, detalle 15 tooltips ES). Con el idioma EN activo, **8 de 10 vistas y todos los toasts/diálogos de VM siguen en español**.
- Claves huérfanas: el fallback muestra la propia clave (`LocalizationService.cs:291-294`), así que el riesgo no es visible hasta usarla — el problema son los literales, no las claves.

---

## 3. Legal y privacidad (§4.9) — buenas prácticas (orientativo)

### 3.1 Inventario de datos

| Dato | Dónde | ¿Personal? | Notas |
|---|---|---|---|
| Bibliotecas + sinopsis + géneros | `biblioteca.db` `AnimeItem` | Parcial (títulos de series) | Sinopsis HTML íntegra |
| Historial de visionado con timestamps | `RegistroEpisodio` (`UltimaReproduccion`, `ProgresoSegundos/TotalSegundos`) | **Sí** | En claro |
| Rutas locales absolutas (pueden contener el nombre de usuario Windows) | `AnimeItem.RutaCarpeta`, `RegistroEpisodio.RutaArchivo`, `settings.json`, logs | **Sí** | `RutaArchivo` puede revelar el perfil |
| Token OAuth AniList | `anilist_token.txt` | Sí | **DPAPI CurrentUser** ✅ |
| A terceros | AniList (progreso, estado, puntuación, fechas; lee `Viewer{name avatar}`) · AniSkip (solo `malId`+ep+duración, sin cuenta) · animeav1/mp4upload (solo UA/Referer) · GitHub (updates) | — | Minimización correcta; la app **no sube archivos** (verificado) y **no descarga la lista completa del usuario** |

### 3.2 Verificación de promesas del README

| Promesa (`README.md:128-133`) | Veredicto |
|---|---|
| "Nunca sube, rastrea ni comparte tus archivos locales" | ✅ Cierto (ninguna subida de archivos en el código) |
| "Sin telemetría invasiva; solo tú y tu cuenta AniList controlan tu historial" | ✅ Cierto (0 hits de analytics/telemetry/Sentry/AppInsights) |
| "100 % código abierto bajo MIT" | ⚠️ El archivo `LICENSE` **no existe** en el repo (verificado `Test-Path`: False) → badge y enlace 404, ambigüedad legal real (MKT-001) |

### 3.3 Checklist de buenas prácticas

| Práctica | Estado | Evidencia |
|---|---|---|
| Minimización de datos en red | ✅ | Solo IDs/progreso a AniList; MAL ID a AniSkip |
| Sin telemetría | ✅ | Verificado por grep |
| Token cifrado en reposo | ✅ | DPAPI `CurrentUser` (`AuthService.cs:34,199`) |
| Transparencia (README veraz) | ✅ | Coincide con el código |
| Backups íntegros y no corruptibles | ✅ | `VACUUM INTO` + `integrity_check` |
| **Consentimiento informado previo al login** | ❌ | Sin copy de alcance antes del OAuth; sin parámetro `scope` (`AuthService.cs:66`) |
| **Borrado total de datos / desinstalación** | ❌ | Velopack no borra `%LocalAppData%\AnimeLocalTrackerData` (intencional, `AppDataPaths.cs:8-11`); sin opción de purga en la app |
| **Política de privacidad en la app** | ❌ | No existe (AcercaDe solo versión/licencia/repo) |
| Advertencia de contenido/edad (anime puede incluir contenido no apto para menores) | ❌ | Sin verificación de edad ni aviso |
| Exportación completa (derecho de portabilidad) | ✅ | JSON + backup .db (`ConfiguracionViewModel.cs:332-358`) |
| Cifrado en reposo de DB/logs | ⚠️ | En claro (local-first; documentar el riesgo; DB con historial = dato personal) |
| Retención acotada | ⚠️ | 5 backups rotativos; log 5 MB + 1 rotación (≈10 MB máx) |
| Alcance del logout | ⚠️ | Solo local; el acceso AniList sigue válido (ref. SEC-002 Bloque 1) |

### 3.4 Hallazgos PRI priorizados

| ID | Sev. | Hallazgo → recomendación |
|---|---|---|
| PRI-001 | **Alto** | Los datos sobreviven a la desinstalación y no hay purga → añadir "Borrar todos mis datos" (doble confirmación: token, DB, portadas, miniaturas, logs, backups) y documentarlo en la UI de Configuración y en el README |
| PRI-002 | Medio | Sin consentimiento previo al login (alcance OAuth no explicado; sin `scope` explícito) → pantalla previa que enumere "se leerá/escribirá tu progreso, estado, puntuación y fechas en AniList"; revisar si la API de AniList admite scopes restringidos |
| PRI-003 | Medio | Sin política de privacidad en la app → sección en AcercaDe (o enlace) con inventario de datos, terceros y retención; párrafo de menores |
| PRI-004 | Medio | Historial con timestamps + rutas locales en claro → documentar; opcional: cifrar DB (SQLCipher) o al menos excluir rutas de logs (ref. SEC-012) |
| PRI-005 | Bajo | README no matiza que progreso/estado/puntuación sí viajan a AniList → precisar en la sección Privacidad |

**Nota legal:** análisis orientativo basado en buenas prácticas (no se definió marco legal formal). Si la audiencia incluye la UE (GDPR), el historial de visionado con timestamps es dato personal y aplicarían derechos de acceso/borrado que las funciones de export ya cubren parcialmente; si incluye menores, la advertencia de contenido pasa a ser recomendable. No sustituye asesoría jurídica.

---

## 4. Marketing/SEO — nota mínima (§4.8)

Producto desktop sin web ni tienda: el punto de conversión es el repositorio GitHub.

| ID | Hallazgo (evidencia) → recomendación |
|---|---|
| MKT-001 | `LICENSE` prometido en `README.md:9,151` y **ausente del repo** (404) → crear el archivo MIT real |
| MKT-002 | README sin una sola imagen de producto → capturas/GIF de galería y reproductor (mayor gap de conversión) |
| MKT-003 | Releases siempre **pre-release** con notas automáticas sin curar (`release.yml:166-168`) → CHANGELOG curado + publicar estable cuando corresponda |
| MKT-004 | Futura landing: diferenciar "local-first, sin telemetría, Windows" con datos estructurados `SoftwareApplication` + OpenGraph |
| MKT-005 | La privacidad verificada (sin telemetría) es el argumento de venta central → página/README de privacidad con el inventario real de §3 |

Sin datos de mercado: no se inventan métricas de competencia.

---

## 5. Plan de remediación (Bloque 4)

| Fase | Ítems | Esfuerzo |
|---|---|---|
| **Quick wins (≤1 semana)** | UX-002/003 (subir `TextTertiary` a `#9CA3AF` + oscurecer acentos; 4.5:1 en un commit), UX-006 (tooltips desde config de atajos), PRI-005/MKT-001 (LICENSE + matiz README) | S |
| **Corto plazo (2-4 semanas)** | UX-001 (AutomationProperties.Name en icon-buttons), UX-004/005 (foco en galería/búsqueda/diálogos), UX-011/012/013 (destructivos claros + orden de restore), UX-009 (unificar umbral — con FUN-003), PRI-002 (consentimiento pre-login) | M |
| **Mediano plazo (1-2 meses)** | UX-016 (localizar 8 vistas + toasts: esfuerzo real de i18n), UX-008 (player accesible), UX-019 (live regions), PRI-001 (borrado total), PRI-003 (política en app) | L |
| **Largo plazo** | UX-018 (migración total a tokens), PerMonitorV2 (`app.manifest`), tema claro, "reduce motion" | M |

---

## 6. Cierre del plan por bloques — Radar de madurez integrado (9 áreas)

> Escala 0-5 del equipo; n/e = no evaluado en profundidad. Áreas 4.2-4.6 se evalúan en los informes de Bloque 2 y 3.

```
Seguridad (4.1)              ████████████████████░░ 3.8   Bloque 1
Arquitectura (4.3)           ██████████████████░░░░ 3.5   Bloque 1
Funcionalidad (4.2)          ████████████████░░░░░░ 3.2   Bloque 2
Integraciones (4.5)          █████████████████░░░░░ 3.4   Bloque 2
Rendimiento (4.4)            ███████████████░░░░░░░ 3.0   Bloque 3
Calidad/DevOps (4.6)         ██████████████████░░░░ 3.6   Bloque 3
UX/UI y accesibilidad (4.7)  ██████████████░░░░░░░░ 2.9   Bloque 4
Marketing/SEO (4.8)          ████████████░░░░░░░░░░ 2.4   Bloque 4 (solo repo; sin web)
Legal/privacidad (4.9)       ████████████████░░░░░░ 3.3   Bloque 4 (buenas prácticas)
```

**Prioridad global de remediación (cruzando los 4 bloques):**
1. **Pérdida de datos**: FUN-001 (sync regresivo), FUN-002 (200+errors), FUN-004 (reset a 0) — Bloque 2.
2. **Accesibilidad básica**: UX-001/002/003/004/005 (un sprint concentrado) — Bloque 4.
3. **Gate de rendimiento**: OPS-001 (benchmarks en CI) + PERF-001/002 — Bloque 3.
4. **Configuraciones que mienten**: FUN-003/FUN-005 + UX-009 (unificar fuentes de verdad) — Bloques 2/4.
5. **Daemon**: PERF-004/INT-004 (timeout + backoff + onefile) — Bloques 2/3.
6. **Privacidad**: PRI-001 (borrado total) y PRI-002 (consentimiento) — Bloque 4.

---

## 7. Limitaciones y no verificado

1. Auditoría estática: sin pruebas con Narrator/lectores, sin medición de foco runtime, sin DPI 150 % multi-monitor (falta `app.manifest` — inferido, no probado), sin "reduce motion"/"high contrast".
2. Contraste sobre imágenes reales (títulos sobre portadas/backdrops, `GaleriaView.xaml:623-643`) no calculable estáticamente.
3. GitHub (about, stars, releases históricas, existencia del LICENSE en el remoto) no auditable desde el checkout local.
4. Sin tests con usuarios: las fricciones de journey son heurísticas, no validadas.
5. El análisis legal es orientativo y no constituye asesoría jurídica; sin marco legal formal definido, se evaluó contra buenas prácticas generales.

*Firmado por UX/UI, privacidad y marketing. Sin cambios sobre el código: solo propuestas. Con este informe se completa el plan de auditoría por bloques (1-4) confirmado en la Fase 0.*
