# INFORME DE AUDITORÍA — AnimeLocalTracker · Bloque 2: Funcionalidad e Integraciones

| | |
|---|---|
| **Proyecto** | AnimeLocalTracker v1.0.5 (desktop Windows, .NET 8 WPF) |
| **Fecha** | 2026-09-02 |
| **Versión del informe** | 1.0 (entrega parcial del plan por bloques) |
| **Alcance cubierto** | Bloque 2: §4.2 Funcionalidad y correcciones (trazabilidad, bugs, casos límite) y §4.5 Integraciones (AniList, AniSkip, daemon Python, descargas, Velopack, contratos e idempotencia) |
| **Confidencialidad** | Interno — uso exclusivo del equipo del proyecto |
| **Equipo firmante** | Analista de sistemas (trazabilidad) · Ingeniero full-stack (bugs) · Seguridad (solo cuando el defecto cruza a riesgo) |
| **Metodología** | Lectura estática de código con `file:line`; sin ejecución de la suite (los 265 tests no se corrieron en esta entrega); requisitos derivados de README.md + invariantes de AGENTS.md (no hay spec formal). Complementa al Bloque 1 (SEC/ARC) sin repetirlo. |

---

## 1. Resumen ejecutivo

**Madurez funcional: 3.2/5 · Integraciones: 3.4/5.** La funcionalidad prometida existe y está mayormente operativa, con una **calidad de diseño de flujos superior a la media** (confirmaciones destructivas, degradaciones elegantes, upsert idempotente). Pero la auditoría encontró **3 defectos con pérdida real de datos del usuario** en el ciclo sincronización/reproducción y **~7 configuraciones o promesas de UI que no gobiernan el comportamiento real** (umbral de visto, intervalo de sync, notificaciones, buscar actualizaciones al iniciar, carpeta base).

### Top 5 riesgos (Bloque 2)

| # | Riesgo | ID | Impacto | Nota ejecutiva |
|---|---|---|---|---|
| 1 | Sync **push-only sin leer el estado remoto**: si el usuario avanzó en la web mientras la app estuvo offline, la app **degrada el progreso AniList** (p. ej. 12→5) de forma permanente | FUN-001 | **Crítico** (pérdida de datos) | `SyncService.cs:62-70` hace SET absoluto; el método para leer el remoto existe pero no se usa en sync |
| 2 | Un **200 con `errors` GraphQL** en `ActualizarProgresoAsync` marca episodios como sincronizados **sin haberse sincronizado jamás** | FUN-002 | **Alto** | Único método de AniList que no inspecciona el body (`AniListTrackingService.cs:353-360`) |
| 3 | Archivo **sin número de episodio** ("Specials.mkv") se prioriza en "Qué veo hoy" y al verlo **resetea el progreso AniList a 0** (sin validación `episodio > 0`) | FUN-004 | **Alto** | `ReproductorViewModel.cs:1214-1221` |
| 4 | La UI promete **umbral de "marcado como visto" configurable** (95 %) pero el auto-mark está fijo en **90 %** en dos sitios distintos | FUN-003 | **Medio** | Promesa rota al usuario: `AppSettings.cs:17` vs `ReproductorViewModel.cs:1160` y `EpisodioItem.cs:123` |
| 5 | Daemon Python: **sin timeout por comando**, descarte permanente tras un handshake fallido y `detect-scenes` que puede bloquear la cola entera | INT-004 | **Medio** | Evidenciado en `app.log`: handshake fallido 2/2 sesiones → toda la sesión degradada a one-shot |

**Méritos verificados:** import/export JSON con tope 50 MB; backups atómicos con restore validado; upsert masivo anti-N+1 idempotente con tests; búsqueda con debounce + cancellation (sin carreras); cola de descargas FIFO con deduplicación `animeId_ep` y estado `.state` saneado; verificación anti-confusión MAL ID robusta (23 tests); degradación correcta de AniSkip sin romper reproducción; Polly con retry+jitter+circuit breaker solo donde debe (AniList/AniSkip).

---

## 2. Matriz de trazabilidad requisito → código → tests (sección 4.2)

> Requisitos derivados del README (sin spec formal). Estado: ✅ cumple · ⚠️ cumple con defecto (ref.) · ❌ hueco.

| ID | Requisito (README) | Código principal | Tests existentes | Estado |
|---|---|---|---|---|
| R1 | Reproductor nativo Flyleaf + auto-play | `ReproductorViewModel.cs:787-904,1081-1260` | `ReproductorViewModelTests` (23), `ReproductorStressTests` (5) | ⚠️ loop/Ended sin test (FUN-007/011/012) |
| R2 | Auto-tracking al 90 % con feedback | `ReproductorViewModel.cs:1157-1163,1214-1221` | 1 test happy-path | ⚠️ umbral fijo vs config (FUN-003); videos truncados (FUN-012) |
| R3 | Sync offline→online AniList | `SyncService.cs:28-92`, `DatabaseService.cs:497-525` | `SyncServiceTests` (3) | ❌ **pérdida de datos** (FUN-001/002) |
| R4 | Galería visual + caché de portadas | `GaleriaViewModel.cs`, `ImageCacheService.cs` | `GaleriaViewModelTests` (16), `ImageCacheServiceTests` (2) | ✅ (concurrencia sin test) |
| R5 | Ficha detalle enriquecida | `DetalleViewModel.cs` (~1144 lín.) | `DetalleViewModelTests` (3) | ⚠️ cobertura muy pobre (FUN-010, PERF-005) |
| R6 | Buscador en tiempo real + alta | `MainViewModel.cs:493-550`, `AgregarAnimeViewModel.cs:152-204` | 8 + 5 tests | ✅ debounce/duplicados correctos |
| R7 | Calendario semanal (hora Japón) | `CalendarioViewModel.cs`, `AniListTrackingService.cs:641-737` | 4 + 8 tests | ✅ sin test DST/límites |
| R8 | Gestor de descargas | `DownloadService.cs`, `OrquestadorMultiProveedor.cs` | 8 + 7 + 23 + 7 tests | ⚠️ FUN-014/015; `DownloadStateStore` sin test |
| R9 | AniSkip OP/ED | `AniSkipService.cs`, `SkipTimesCoordinator.cs` | `AniSkipServiceTests` (7) | ⚠️ Coordinator sin suite propia |
| R10 | Backups/restore | `DatabaseService.cs:94-253` | `DatabaseServiceUpsertTests` (parcial) | ✅ |
| R11 | Import/export JSON | `DatabaseService.cs:259-378` | 4 tests | ⚠️ FUN-013 (duplicados en JSON) |
| R12 | Notificaciones de episodios nuevos | `NewEpisodeNotifier.cs:43-90` | 3 tests | ⚠️ solo al arranque (FUN-008) |
| R13 | Organización/parsing de nombres | `FileScannerService.cs`, `PythonFileScannerService.cs`, Rust/Python | `FileScannerServiceTests` (1) | ⚠️ escaneo completo sin test (FUN-004) |
| R14 | Auto-actualizaciones Velopack | `UpdateService.cs` | 5 tests (modo dev) | ✅ |
| R15 | Configuraciones (idioma, atajos, umbral, intervalo) | `SettingsService.cs`, `ConfiguracionViewModel.cs` | `SettingsServiceTests` (7) | ❌ **3 ajustes muertos** (FUN-003/005, `BuscarActualizacionesAlIniciar`) |

**Tests dependientes de entorno (no deterministas):** `RustNativeTests` (se auto-saltan si falta la DLL) y `PythonBridgeServiceTests` (requieren runtime Python real).

---

## 3. Hallazgos de funcionalidad (FUN)

### FUN-001 — Sync push-only: degrada el progreso remoto más nuevo (Crítico)

- **Categoría:** Funcional — consistencia de datos (4.2).
- **Severidad:** **Crítico** · Probabilidad media (requiere ver en 2 dispositivos/offline+web) · Impacto alto (pérdida permanente de historial AniList).
- **Evidencia:** `SyncService.cs:57-72` — agrupa pendientes por `AniListId`, `maxEpisodio = grupo.Max(...)` **solo de datos locales** `:62`, y llama `ActualizarProgresoAsync` `:67`; si "ok" marca sincronizados `:70`. `AniListTrackingService.cs:336-360` — `SaveMediaListEntry(progress)` es un **SET absoluto**. El lector del estado remoto existe (`ObtenerSeguimientoUsuarioAsync`, `AniListTrackingService.cs:369-385`) pero **solo** lo usa el editor manual (`DetalleViewModel.cs:1047`).
- **Causa raíz:** sync diseñado como push unidireccional local→nube sin merge; supone que el cliente local es la única fuente de progreso.
- **Escenario:** marcar 1-5 sin red → ver 6-12 en anilist.co → reconectar → el ciclo de 5 min escribe `progress=5` (regresión permanente, luego se marcan como sincronizados y nunca se reintenta).
- **Recomendación:** antes de empujar, leer `mediaListEntry.progress` remoto y enviar `max(local, remoto)`; si el remoto es mayor, además **actualizar el estado local** (no marcar esos como sincronizados con progreso viejo).
- **Solución propuesta (antes → después):**
  ```csharp
  // SyncService.cs — Antes:
  bool ok = await _trackingService.ActualizarProgresoAsync(aniListId, maxEpisodio, token);
  // Después (dentro de ActualizarProgresoAsync o en SyncService):
  var remoto = await _trackingService.ObtenerSeguimientoUsuarioAsync(aniListId, token);
  int progresoFinal = Math.Max(maxEpisodio, remoto?.Progress ?? 0);
  if (progresoFinal > maxEpisodio) /* marcar locales como sincronizados hasta progresoFinal */;
  bool ok = await _trackingService.ActualizarProgresoAsync(aniListId, progresoFinal, token);
  ```
- **Validación:** test en `SyncServiceTests` con mock que devuelve progreso remoto 12 y pendientes locales hasta 5 → verificar que la mutation recibe 12 y que los episodios ≤12 quedan `SincronizadoEnNube=true`.

### FUN-002 — 200 con `errors` GraphQL marca episodios como sincronizados sin sincronizarlos (Alto)

- **Categoría:** Funcional — manejo de errores de integración.
- **Severidad:** **Alto** · Probabilidad media (token revocado/mediaId inválido) · Impacto alto (pérdida silenciosa de sincronización + logs engañosos).
- **Evidencia:** `AniListTrackingService.cs:353-360` — único método que retorna `true` con `response.IsSuccessStatusCode` **sin inspeccionar el body**; los otros 5 métodos del mismo servicio chequean `content.Contains("\"errors\"")` (p. ej. `:127-131,237-241,308-312,439-443,689-693`). `SyncService.cs:70` marca sincronizados a ciegas.
- **Causa raíz:** falta de validación simétrica del contrato GraphQL (HTTP 200 ≠ éxito).
- **Solución propuesta:** replicar el chequeo de `errors` antes de `return true` y añadir `AppLogger.Warn` con el mensaje del error.
- **Validación:** test con respuesta 200 `{"errors":[{"message":"Invalid token"}]}` → `ActualizarProgresoAsync` devuelve `false`; el ciclo de sync NO marca sincronizados y aplica backoff.

### FUN-003 — Umbral "marcado como visto" configurable ignorado: dos fuentes de verdad (Medio)

- **Evidencia:** `AppSettings.cs:17` (`UmbralMarcadoVisto = 95`), texto de UI "porcentaje a partir del cual se marca automáticamente" (`LocalizationService.cs:92`, `ConfiguracionView.xaml:313-314`) **vs** disparo real `porcentaje >= 0.90` fijo (`ReproductorViewModel.cs:1160`, verificado) **vs** `EpisodioItem.cs:123` con `0.95` fijo. El ajuste solo afecta a limpieza/reanudación (`PlaybackStateService.cs:39,53,67,86`).
- **Causa raíz:** el umbral se introdujo para una función (limpieza de progreso) y nunca se conectó al auto-marcado.
- **Recomendación:** usar `UmbralMarcadoVisto/100.0` en `RastrearProgresoAsync` y en `EpisodioItem`; extraer el cálculo a un método puro testeable.
- **Validación:** test del cálculo con umbral 100 → no marca al 90 %; con 85 → marca al 85 %.

### FUN-004 — Episodio sin número: "Qué veo hoy" lo elige y verlo resetea el progreso AniList a 0 (Alto)

- **Evidencia:** `FileScannerService.cs:91-113` deriva `NumeroEpisodio=0` si no hay dígitos; `GaleriaViewModel.cs:495-508` ordena ascendente y elige `FirstOrDefault(!vistos)` **sin filtrar 0** → `Specials.mkv` gana al episodio real; al terminar, `RealizarAutoTrackingAsync` → `MarcarComoVistoYSincronizarAsync(animeId, 0, …)` (`ReproductorViewModel.cs:1214-1221`) **sin validación `episodio > 0`** (a diferencia de `PlaybackStateService.cs:61`) → persiste ep-0 y empuja `progress=0` a AniList (`AniListTrackingService.cs:348`).
- **Causa raíz:** el parser produce 0 como "sin número" y los consumidores lo tratan como episodio válido.
- **Recomendación:** filtrar `NumeroEpisodio == 0` en "Qué veo hoy" y en el notificador; validar `episodio > 0` en `MarcarComoVistoYSincronizarAsync` y en `PlaybackStateService` (guard superior).
- **Validación:** test de Galería con archivo sin número + ep. 3 sin ver → nunca selecciona el 0; test de PlaybackStateService con episodio 0 → no persiste ni llama a la API.

### FUN-005 — `IntervaloSincronizacionMinutos`: ajuste muerto, el ciclo es siempre 5 min (Medio)

- **Evidencia:** expuesto y persistido (`AppSettings.cs:14`, `ConfiguracionViewModel.cs:93,244`, `ConfiguracionView.xaml:291-294`) pero el arranque usa el literal `TimeSpan.FromMinutes(5)` (`App.xaml.cs:335`, verificado) y `SyncService` no se suscribe a cambios de configuración.
- **Recomendación:** leer la config al iniciar e implementar re-arranque del ciclo (`DetenerSincronizacionPeriodica` + `IniciarSincronizacionPeriodica`) cuando se guarda; misma familia que `BuscarActualizacionesAlIniciar` (`AppSettings.cs:15`, sin consumidores — verificado).

### Resto de hallazgos (tabla compacta)

| ID | Sev. | Evidencia | Comportamiento | Corrección (1 línea) |
|---|---|---|---|---|
| FUN-006 | Bajo | `PlaybackStateService.cs:44-57`, `ReproductorViewModel.cs:1096-1121` | Reanuda con posición del archivo viejo si la ruta fue reemplazada; seek puede superar la duración nueva | Validar `RutaArchivo` (OrdinalIgnoreCase) o acotar `min(posición, duración-ε)` |
| FUN-007 | Medio | `ReproductorViewModel.cs:1175` (auto) vs `:1276` (manual, sí acota) | Auto-skip sin clamp al final: timings erróneos pueden disparar `Ended` y marcar visto | `Seek(Math.Min(skip.End+0.2, TotalSeconds))` |
| FUN-008 | Medio | `App.xaml.cs:362-374` único llamador de `BuscarYNotificarNuevosAsync` | Notificaciones de episodios nuevos **solo al arranque** | Disparar tras descarga completada y/o timer periódico |
| FUN-009 | Medio | `SettingsService.cs:163-187`, `DetalleViewModel.cs:1137-1142` | Cambiar la carpeta base no re-escanea ni reubica: animes y descargas siguen en la ruta vieja | Ofrecer re-escaneo/migración de rutas o aviso explícito del alcance |
| FUN-010 | Medio-Bajo | `DetalleViewModel.cs:894` (FirstOrDefault sobre lista 1..N) | "Reanudar" elige el episodio con progreso de **menor número**, ignora `UltimaReproduccion` | Ordenar por `UltimaReproduccion` desc |
| FUN-011 | Bajo | `ReproductorViewModel.cs:1154` (fire-and-forget cada 5 s), `:1214-1221` | Guardados asíncronos no serializados: el "forzar 0" al terminar puede pisar el "visto" | Serializar saves con semáforo o cancelar saves previos al marcar visto |
| FUN-012 | Medio | `ReproductorViewModel.cs:1214-1221` | `Ended` marca visto aunque el video sea truncado/corrupto (terminó antes del final real) | Exigir `porcentaje >= umbral` (o posición ≥ 95 % con duración > 0) en `Ended` |
| FUN-013 | Bajo | `DatabaseService.cs:318-322`; deserialización `:295-296` | Import JSON: AniListId duplicados se pisan en silencio; `JsonException` sin catch local llega al VM como "Error" | Dedupe con Warn + validación estructural con mensaje localizado |
| FUN-014 | Medio | `DownloadService.cs:663-679` | Reanudar en modo secuencial manda `Range` y, si el servidor responde 200 (lo ignora), hace **append → archivo corrupto** sin error | Exigir 206 al reanudar; si no, reiniciar descarga limpia |
| FUN-015 | Medio | `DownloadService.cs:602-636` (sin CTS de lectura) | Sin timeout de inactividad: servidor que corta bytes deja la descarga colgada (solo la corta el usuario) | Watchdog por segmento (cancelar si 60 s sin bytes) |
| FUN-016 | Bajo | `DownloadService.cs:346,372,383,387` (`Debug.WriteLine`); resolver `:429-466` | Ciclo de vida de descargas invisible en `app.log` release | Logs Info/Warn con IDs de corrida |
| FUN-017 | Bajo | `ReproductorViewModel.cs:1047` | Guardado fire-and-forget puede notificar el progreso del episodio viejo bajo el ID del nuevo | Snapshot de `_animeId/_episodio` al inicio del método |
| FUN-018 | Bajo | `GaleriaViewModel.cs:371-391` | Migración que escribe BD (UPDATE por anime) en `CargarBibliotecaAsync` | Ejecutar bajo transacción única y solo si hay cambios |
| FUN-019 | Info | `DetalleViewModel.cs:237-252` + `DatabaseService.cs:389-404` | Descargar un episodio con registro previo visto puede resetearlo a no-visto | No pisar `VistoLocal` en el merge post-descarga |

---

## 4. Hallazgos de integraciones (INT)

### INT-004 — Daemon Python: sin timeout por comando y descarte permanente tras handshake fallido (Medio)

- **Evidencia:** `PythonBridgeService.cs:239-253` (`_daemonDescartado` definitivo), `:293-300` (handshake con `protocolVersion` solo en modo daemon), `:180-229` (semáforo serializa la cola), `:213` (sin timeout propio: depende del `ct` del llamador). En el log real de la máquina (`%LocalAppData%\AnimeLocalTrackerData\Logs\app.log`): *"Fallo en handshake del daemon … no incluyó protocolVersion. Se usará modo one-shot"* **en 2/2 sesiones** (00:33 y 00:40). El binario PyInstaller onefile pesa ~73,4 MB → extracción + imports (opencv/numpy/pydantic/rapidfuzz) supera el handshake de 8 s (`:283`).
- **Impacto:** toda la sesión degrada a one-shot (spawn de 73 MB + imports por comando) y `detect-scenes` (OpenCV, hasta ~140 s) puede bloquear en cola a miniaturas/metadatos (ct solo cancela la espera, no la ejecución).
- **Recomendación:** (1) timeout por comando desde el host (p. ej. 60 s + kill de árbol); (2) reintento del daemon con backoff (no descarte permanente); (3) handshake ≥ 15 s o arranque en paralelo con la carga de biblioteca; (4) evaluar `--onedir`.
- **Validación:** test de integración que arranque el daemon real 2 veces seguidas tras fallo forzado → la 2ª debe reintentar; comando `detect-scenes` lento con timeout → el host lo mata y degrada sin colgar la cola.

### Tabla INT (resto)

| ID | Sev. | Evidencia | Hallazgo → recomendación |
|---|---|---|---|
| INT-001 | Medio | `AniListTrackingService.cs:230-234` | Lote de biblioteca (`Page.media(id_in)` de 50) **descarta en silencio los chunks que fallan** (`continue`) → animes sin refrescar sin aviso. Acumular fallos y exponerlos |
| INT-002 | Medio | `AniListTrackingService.cs:41-44` (60 s) vs AniSkip/Downloader sin configurar (default 100 s; `App.xaml.cs:116,152`) | Timeouts HTTP por servicio inconsistentes: definir política por tipo de llamada (AniSkip 30 s, descargas por cabeceras + watchdog FUN-015) |
| INT-003 | Bajo | `PythonBridgeService.cs:121-124` vs `:293-300` | One-shot **no valida `protocolVersion`**: un binario viejo produce errores genéricos. Validar también en one-shot |
| INT-005 | Bajo | `AniSkipService.cs:55-58,67-72` | 404 cacheado 30 min (bien) pero **429/fallo cacheado 15 min como vacío** (excesivo si el fallo es puntual) → ventana 2-5 min |
| INT-006 | Bajo | `cli.py:23-98` vs consumidores C# | Comandos del CLI sin consumidor (`match-title`, `parse-filename`, `fingerprint`, `find-duplicates`) → código muerto o feature no expuesta; decidir y retirar |
| INT-007 | Bajo | `PythonBridgeService.cs:143-149` (kill de árbol) | Tras cancelar `download-stream` quedan `.part`/`.state` huérfanos que la reanudación siguiente reutiliza por accidente → limpieza post-kill |
| INT-008 | Bajo | `AniListTrackingService.cs:373-385` + `SyncService` | No se descarga la lista AniList completa (sin `MediaListCollection`): el merge local←nube (estado/completadas) no existe → animes marcados COMPLETED en la web no bajan a la app; decidir dirección del sync (documentar o implementar pull) |
| INT-009 | Bajo | `App.xaml.cs:204-208` | "Multi-fuente" nominal: hay 1 solo `IProveedorVideo` registrado → el cooldown del orquestador no tiene a quién degradar. Al añadir proveedores, revisar salud por proveedor |

**Positivos de integración:** payloads a AniSkip sin datos de cuenta (solo MAL ID + nº de episodio + UA `AnimeLocalTracker/1.0`); verificación anti-confusión MAL ID en cascada con rapidfuzz (23 tests); Polly (retry 3 + jitter + `Retry-After` + circuit breaker 3/2 min) aplicado a AniList/AniSkip; queries GraphQL con variables; calendario paginado con `hasNextPage`; upsert y reanudación de descarga idempotentes; cola de descargas con dedupe por `animeId_ep`; kill garantizado del daemon (Job Object + `ProcessExit`, verificado Bloque 1).

---

## 5. Checklist §4.2/§4.5

| Práctica | Estado | Evidencia |
|---|---|---|
| Casos límite de progreso (0, duración 0, truncados) | ❌ | FUN-004/012 |
| Estados de sync consistentes (no marcar sin éxito) | ❌ | FUN-002 |
| Merge offline→online sin pérdida | ❌ | FUN-001 |
| Idempotencia de escrituras (upsert, reintentos, reanudación) | ✅ | `DatabaseService.cs:432-482`, `DownloadStateStore.cs:58-77` |
| Timeouts y cancelación en llamadas largas | ⚠️ | INT-002/004, FUN-015 |
| Degradación elegante de servicios auxiliares | ✅ | daemon→one-shot, AniSkip→local, proveedor→mensaje |
| Trazabilidad de operaciones críticas en logs | ⚠️ | FUN-016; sync sin ID de corrida |
| Tests de los caminos de error | ⚠️ | 0 tests para DownloadStateStore, escaneo, Coordinator, bucle 90 % |

---

## 6. Plan de remediación (Bloque 2)

| Fase | Ítems | Esfuerzo | Impacto |
|---|---|---|---|
| **Quick wins (≤1 semana)** | FUN-002 (chequeo errors), FUN-003 (umbral unificado), FUN-005 (intervalo real), FUN-007 (clamp auto-skip), FUN-019, INT-006 (retirar comandos muertos) | ~1-2 días | Alto: elimina silencios y promesas rotas |
| **Corto plazo (2-4 semanas)** | FUN-001 (merge con lectura remota + tests), FUN-004 (filtro ep-0 + validación), FUN-008, FUN-010, FUN-012, FUN-014, INT-001 (lote con errores visibles), INT-004 (timeout+backoff daemon) | 4-6 días | Alto: **pérdida de datos** resuelta |
| **Mediano plazo** | FUN-009 (relocalización de carpeta), FUN-006/011/013/015/016, INT-002/003/005/007/008 | 1-2 semanas | Medio: robustez y visibilidad |

**Comandos de reproducción (escenarios):**
1. FUN-001: app sin red → ver 5 caps → en anilist.co marcar hasta el 12 → reconectar → esperar ciclo (5 min) → comprobar progreso en AniList (regresión a 5). Verificar tras corrección que queda en 12.
2. FUN-002: revocar el acceso en anilist.co → marcar un episodio → el log dice "Sincronizado" sin haber sincronizado (tras fix: debe quedar pendiente y loguear Warn).
3. FUN-004: crear `Specials.mkv` en la carpeta de un anime con ep. 3 sin ver → "Qué veo hoy" (hoy elige Specials) → tras fix elige el 3.
4. INT-004: `python tools/python/cli.py --daemon` cronometrado en frío → si tarda > 8 s, el host degrada (reproduce el fallo del log).

**Validación global:** `powershell -ExecutionPolicy Bypass -File .\build.ps1 -RunTests` + nuevos tests citados en cada hallazgo.

---

## 7. Limitaciones y no verificado

1. Suite de 265 tests **no ejecutada** en esta entrega (verificación de regresión queda para la fase de correcciones).
2. Sin spec formal: la "matriz de requisitos" deriva del README; puede haber features intencionales no listadas.
3. Comportamiento runtime de Flyleaf ante archivo borrado/movido entre escaneo y reproducción no confirmado (solo `OpenCompleted` en `ReproductorViewModel.cs:350-381`).
4. Respuestas reales de AniList/AniSkip/animeav1 y del binario PyInstaller en producción no probadas en vivo.
5. Flujo end-to-end de Velopack (delta/rollback) no ejecutable estáticamente.
6. Análisis legal: no aplica en este bloque (Bloque 4).

*Firmado por el equipo: trazabilidad y hallazgos desde Analista de sistemas / Full-stack; riesgos cruzados con seguridad del Bloque 1 (SEC-002 se refuerza con FUN-002). Sin cambios sobre el código: solo propuestas.*
