---
Proyecto: AnimeLocalTracker
Fase: 6 - Distribución, Adopción y Verificación de Claims
Fecha de auditoría: 2026-09-14
Hora de inicio: 00:31:00
Duración de la fase: 10:00
---

# Fase 6 — Distribución, Adopción y Verificación de Claims

## 1. README como material de marketing — evaluación

El README (reescrito el mismo día de esta auditoría, commit `3d5cdef`) adopta un tono de
marketing premium explícito ("La revolución definitiva...", "Bienvenido al estándar premium").
Fortalezas: propuesta de valor clara desde el primer párrafo (comparación directa streaming vs.
archivos locales), badges informativos (build, tests, licencia), sección de privacidad destacada.

**Debilidad confirmada por inspección directa: el README no incluye ninguna captura de pantalla
de la aplicación**, pese a describir una experiencia visual ("glassmorphism", "portadas en alta
resolución", "modo oscuro profundo") como uno de sus principales argumentos de venta. Esta
auditoría generó 3 capturas reales en la Fase 4 (`evidencia/01_*.png`, `02_*.png`, `03_*.png`)
que podrían reutilizarse directamente como evidencia de que el producto cumple lo que promete
visualmente — su ausencia en el README es una oportunidad de adopción perdida y de bajo esfuerzo
para corregir.

## 2. Onboarding "30 segundos" — verificación de fricción no documentada

Flujo declarado: *Releases → descargar `Setup_AnimeTracker_vX.X.X.exe` → instalar con un clic →
vincular AniList → señalar carpeta de anime*.

**Fricción no documentada, confirmada en Fase 1:** el instalador **no está firmado
digitalmente** (`Get-AuthenticodeSignature` → `NotSigned`, ver `01_seguridad.md` §2). En la
práctica, esto dispara la advertencia de Windows SmartScreen ("Windows protegió su PC" / editor
no reconocido) en la primera ejecución para la inmensa mayoría de usuarios, requiriendo un clic
adicional en "Más información" → "Ejecutar de todas formas" que **no está mencionado en el
README**. Para un usuario no técnico (el público objetivo declarado: "entusiastas de anime", no
necesariamente desarrolladores), esta advertencia de seguridad del propio sistema operativo puede
leerse como una señal de desconfianza justo en el primer contacto con el producto — contradice
directamente la promesa de "30 segundos" y "un clic". Este es el mismo hallazgo de Fase 1 §2, aquí
evaluado desde el ángulo de conversión/adopción en vez de seguridad pura.

No se completó el resto del flujo de onboarding (vinculación real de OAuth con una cuenta de
AniList, selección de carpeta) en esta corrida por requerir credenciales/cuenta real de un
usuario y no ser una acción de solo-lectura — queda como **no evaluado end-to-end**, aunque el
código de `AuthService.cs` (revisado en Fase 1) confirma que el flujo técnico es sólido.

## 3. Verificación de claims de privacidad del README contra el código — Coincide

| Claim del README | Verificación en código |
|---|---|
| "Tus archivos... jamás salen de tu máquina" | Confirmado: ninguna llamada de red sube contenido de video/rutas de archivos a un servidor propio (no existe backend propio, solo AniList/AniSkip como consumidores de terceros, ver Fase 1 §4) |
| "El código se comunica con AniList exclusivamente para mantener tu perfil... solo si tú lo autorizas" | Confirmado: el flujo OAuth requiere acción explícita del usuario (`AuthService.cs`), y AniSkip solo recibe `malId`/número de episodio (metadata pública, no contenido del usuario) |
| "Sin telemetría invasiva" (string de la UI, `LocalizationService.cs:747`) | Confirmado en Fase 1: sin SDKs de APM/analytics en el código |
| "327/327 Tests Passing" (badge del README) | **Desviación menor**: el conteo real actual es 335/335 (Fase 3) — el badge no se actualiza automáticamente en cada commit, es un valor hardcodeado en el Markdown |

**Conclusión: no se encontró ningún claim de privacidad engañoso o falso.** La única discrepancia
detectada (conteo de tests) es inocua (subestima, no sobreestima, la calidad real) pero sigue
siendo una señal de que el README no se mantiene sincronizado automáticamente con el estado real
del proyecto — riesgo de que futuras desviaciones sí sean en la dirección contraria (sobreestimar)
sin que nadie lo note.

## 4. `CHANGELOG.md` — No existe en el repositorio

Se buscó explícitamente `CHANGELOG.md` (y variantes `CHANGELOG*`) en todo el árbol del repo
(excluyendo `obj`/`bin`/`node_modules`): **no se encontró ningún archivo de changelog
versionado**. El historial de cambios visible para un usuario final depende enteramente de las
"Release Notes" que Velopack/GitHub Releases genere automáticamente (no verificado el contenido
real de una release en esta fase) y del texto hardcodeado `_novedadesTexto` en
`AcercaDeViewModel.cs:25` (una única cadena estática de bullets genéricos, no un historial
versionado real). **Hallazgo (Severidad: Baja):** para un proyecto que se posiciona con
"Ingeniería de Software de Grado Empresarial", la ausencia de un `CHANGELOG.md` versionado es una
inconsistencia menor entre el discurso y la práctica — quick win de bajo esfuerzo (generar uno a
partir del historial de conventional commits ya en uso, confirmado en `AGENTS.md:30`).

## 5. Canal de feedback enlazado desde la UI — Parcialmente presente

`AcercaDeViewModel.cs:19,121` confirma que la vista "Acerca de" expone la URL del repositorio
(`https://github.com/rodriguezrobinj/AnimeLocalTracker`) y la abre en el navegador vía
`Process.Start`. **Matiz:** enlaza a la raíz del repositorio, no directamente a `/issues` o
`/discussions` — un usuario tiene que navegar un clic adicional dentro de GitHub para reportar un
problema. Quick win de esfuerzo mínimo: apuntar el enlace (o añadir uno adicional) directamente a
`.../issues/new`.

## 6. Resumen de hallazgos

| # | Hallazgo | Severidad |
|---|---|---|
| 1 | README sin capturas de pantalla pese a vender la experiencia visual como diferenciador | Baja — quick win (ya hay 3 capturas reales disponibles de esta auditoría) |
| 2 | Instalador sin firma → SmartScreen rompe la promesa de "30 segundos/un clic" | Media (mismo hallazgo raíz que Seguridad §2) |
| 3 | Claims de privacidad verificados contra el código — sin discrepancias engañosas | Ninguna (positivo) |
| 4 | Badge de tests desactualizado (327 vs. 335 reales) | Baja |
| 5 | `CHANGELOG.md` no existe en el repo | Baja — quick win |
| 6 | Enlace de feedback en "Acerca de" apunta a la raíz del repo, no a `/issues` directamente | Muy baja — quick win |
