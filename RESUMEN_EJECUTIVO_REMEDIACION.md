# RESUMEN EJECUTIVO — Remediación completa de la auditoría AnimeLocalTracker (Bloques 1-4)

| | |
|---|---|
| **Proyecto** | AnimeLocalTracker v1.0.5 (desktop Windows, .NET 8 WPF + SQLite + Rust FFI + daemon Python) |
| **Fecha** | 2026-09-02 |
| **Alcance** | Remediación de los hallazgos de los Bloques 1-4 (Seguridad/Arquitectura · Funcionalidad/Integraciones · Rendimiento/DevOps · UX/Privacidad/Marketing) |
| **Estado** | 105 hallazgos auditados → **65 corregidos**, 6 con mitigación parcial/decisión, 34 diferidos con causa documentada |
| **Verificación** | **308/308 tests** en verde · **0 warnings** (.NET, TreatWarningsAsErrors) · `cargo clippy -D warnings` limpio · SCA NuGet **0 vulnerables** · pytest Python 4/4 · builds `--no-incremental` verificadas |
| **Confidencialidad** | Interno |

---

## 1. Cifras de la remediación

| Bloque | Áreas | Hallazgos | Corregidos | Parciales | Diferidos (con causa) |
|---|---|---|---|---|---|
| **Bloque 1** | Seguridad (§4.1) + Arquitectura (§4.3) | 28 (SEC ×13, ARC ×15) | **18** | 4 (SEC-002 revocación=limitación AniList; ARC-003/012/015) | 6 (SEC-010/011/013, ARC-007/009/013) |
| **Bloque 2** | Funcionalidad (§4.2) + Integraciones (§4.5) | 28 (FUN ×19, INT ×9) | **23** | — | 5 (INT-003/006/007/008/009: decisiones de contrato/producto) |
| **Bloque 3** | Rendimiento (§4.4) + DevOps (§4.6) | 19 (PERF ×11, OPS ×8) | **13** | 2 (OPS-002/006: smoke del binario y restore e2e) | 4 (PERF-009/011, OPS-003/007) |
| **Bloque 4** | UX (§4.7) + Legal (§4.9) + Marketing (§4.8) | 30 (UX ×20, PRI ×5, MKT ×5) | **11** | — | 19 (UX-004/006…020, MKT-002/004/005) |
| **Total** | 9 áreas | **105** | **65** | **6** | **34** |

> Método de conteo: un hallazgo cuenta como "corregido" cuando su remediación quedó en
> código con tests/verificación; "parcial" cuando la mitigación depende de una limitación
> externa (AniList) o de una decisión aceptada; "diferido" cuando requiere assets,
> validación visual, tooling de pipeline o una decisión de producto — todos con causa y
> recomendación en los informes correspondientes.

---

## 2. Lo más relevante por bloque (para usuarios y equipo)

**Bloque 1 — Seguridad y Arquitectura (18 corregidos).** Login OAuth endurecido (origen
exacto + token de un solo uso + auto-logout ante 401); descargas con tope de 35 GB y
redirecciones solo-https; portadas solo de la CDN de AniList y con firma de imagen;
logs sin rutas del perfil de usuario; navegación reescrita (NavigationService +
DataTemplates, sin ServiceLocator en VMs); migraciones versionadas de SQLite;
LibraryImport en el FFI; 0 `async void` en producción.

**Bloque 2 — Funcionalidad e Integraciones (23 corregidos).** Los 19 hallazgos FUN están
resueltos: sync sin regresión del progreso remoto, umbral de "visto" configurable real,
videos truncados que ya no se marcan, archivos sin número que ya no resetean AniList,
reanudación segura y del último episodio, notificador periódico, import JSON con dedupe,
descargas con watchdog y reanudación sin corrupción. El daemon Python se reintenta con
backoff (INT-004).

**Bloque 3 — Rendimiento y DevOps (13 corregidos).** Proyecciones ligeras sin sinopsis,
escrituras por lotes (enriquecimiento, actualización de biblioteca, categorización),
export/import fuera del hilo de UI, índice de la cola de sync (migración v2), refrescos
coalescidos en la ficha, portadas corruptas auto-limpiadas; benchmarks con historial
unificado y workflow manual/semanal; clippy -D warnings y umbral de ramas en CI; versión
en el log de arranque.

**Bloque 4 — UX, Privacidad y Marketing (11 corregidos).** Contraste AA en los puntos
críticos (texto terciario, botones primarios, chips y badges); nombres accesibles en
todos los botones solo-icono; diálogos con foco y Esc; **"Borrar todos mis datos"** con
doble confirmación; consentimiento previo al login de AniList; sección de privacidad en
Acerca de; matiz en el README sobre el alcance de la sincronización; `LICENSE` MIT y
`CHANGELOG.md` publicados.

---

## 3. Diferidos principales (dónde continuar)

| Ítem | Por qué queda diferido | Dónde retomarlo |
|---|---|---|
| ARC-003 (ImageSource fuera del modelo) | Exige wrapper visual o split de proyecto Domain + verificación visual | Iteración de arquitectura dedicada |
| UX-004 (galería/resultados por teclado) y UX-006…020 (i18n masivo, player con Tab, live regions…) | Rediseño de plantillas y trabajo de contenido; requieren verificación con usuarios/herramientas reales | Sprint de accesibilidad + proyecto i18n |
| SEC-010 (MaterialDesignThemes estable) | Upgrade de librería UI que exige validación visual manual | Con checklist visual en un ciclo de QA |
| SEC-011 / OPS-003 (firma en cliente, SBOM) | Requieren provisionar el certificado y tooling de pipeline | Cuando el cert esté disponible |
| MKT-002/004/005 (capturas, landing) | Requieren assets y decisión de producto | Con la estrategia de marketing |

---

## 4. Trazabilidad y evidencia

- Informes de auditoría: `AUDITORIA_BLOQUE1…4_*.md` (hallazgos, `file:line`, planes).
- Notas de cambios por bloque: `NOTA_CAMBIOS_REMEDIACION_BLOQUE1…4.md` (antes/después
  con ejemplos de uso).
- Commits: ~37 de remediación, conventional commits en español con los IDs de hallazgo
  (p. ej. `fix(seguridad): … (SEC-003)`).
- Suites: 308 tests .NET + 4 pytest Python; SCA NuGet/cargo/pip sin vulnerabilidades;
  gates CI: clippy, cobertura 45%/30%, SCA triple, pytest.

*Resumen de gestión; el detalle técnico vive en los informes por bloque. Este documento y
las notas asociadas no sustituyen asesoría legal (análisis de privacidad orientativo).*
