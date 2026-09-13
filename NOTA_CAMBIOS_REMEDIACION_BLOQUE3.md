# NOTA DE CAMBIOS — Remediación del Bloque 3 explicada con ejemplos de uso

> Explicación orientada a usuario/equipo de los cambios de la remediación del Bloque 3
> (auditoría de Rendimiento y Calidad/DevOps). Cada sección describe el escenario dentro
> de la aplicación y el comportamiento "antes → después". Complementa a
> `AUDITORIA_BLOQUE3_RENDIMIENTO_DEVOPS.md` (hallazgos técnicos con `file:line`).

---

## 1. La ficha de un anime carga más ligero (PERF-002/003)

**Escenario:** abres la Galería, el Calendario o recibes notificaciones con una
biblioteca de cientos de animes (cada uno con una sinopsis HTML de hasta ~20 KB).

- **Antes:** el Calendario y el notificador de episodios cargaban la **biblioteca
  completa, incluida la sinopsis de cada anime** (que solo se muestra en la ficha de
  detalle): cientos de KB de HTML que no se usaban viajando desde SQLite en cada vista.
- **Después:** esas vistas usan una **proyección ligera** (título, portada, ruta,
  estado… sin sinopsis). La ficha de detalle sigue cargando la fila completa.
- **Además (PERF-003):** al añadir un anime desde el buscador, la comprobación "¿ya está
  en tu biblioteca?" era un `SELECT *` + un `File.Exists` por cada anime guardado; ahora
  es un **`COUNT(*)`** por id. Con 500 animes en biblioteca: antes ~500 accesos a disco
  por cada alta; ahora uno solo.

---

## 2. Importar/exportar la biblioteca ya no congela la ventana (PERF-007)

**Escenario:** exportas tu biblioteca a JSON (o importas un backup de 50 MB).

- **Antes:** la serialización/lectura+parseo corría en el **hilo de la interfaz**: con
  una biblioteca grande, la ventana podía quedarse "congelada" unos segundos durante el
  guardado/importación.
- **Después:** el trabajo pesado (leer el archivo y serializar/parsear) corre en el
  **thread pool**; la ventana sigue respondiendo y solo ves el diálogo cuando termina.

---

## 3. Sincronización y categorización por lotes (PERF-005/006)

**Escenario:** pulsas "Actualizar" en la Galería (sincroniza con AniList), marcas 30
animes en selección múltiple para cambiar su estado, o entras en la ficha de un anime
de 300 capítulos y se generan miniaturas/metadatos.

| Acción | Antes | Después |
|---|---|---|
| Actualizar biblioteca | 1 escritura en BD **por anime** (desde el hilo de UI) | Detecta cambios reales y persiste **una sola transacción** al final |
| Categorizar seleccionados (multiselección) | 1 UPDATE por anime | Un solo UPDATE masivo |
| Enriquecer episodios (miniaturas + ffprobe) | **1 SELECT + 1 escritura por episodio** (300 capítulos ≈ 600 operaciones individuales) | Se acumula en memoria y persiste con el **bulk en lotes de 20** + vaciado final (~15 transacciones) |

Las miniaturas siguen apareciendo en la ficha **una a una** mientras se generan (el
cambio es solo en cómo se guardan).

---

## 4. La ficha de episodios se repinta por ráfagas (PERF-001)

**Escenario:** en la ficha de un anime con cientos de episodios se van generando
miniaturas de fondo, una cada ~1 segundo.

- **Antes:** cada miniatura terminada repintaba la **lista completa de episodios** (con
  la lista virtualizada ya activa, el coste era el refresco O(N) por episodio → O(N²)
  total durante el enriquecimiento).
- **Después:** los repintados están **coalescidos**: si varias miniaturas terminan casi a
  la vez (o mientras el hilo de UI está ocupado), solo se repinta **una vez por ráfaga**.

---

## 5. Portadas corruptas (PERF-008)

**Escenario:** el archivo `Covers\16498.jpg` quedó a medio escribir (corte de luz,
disco lleno…) y ya no se puede decodificar.

- **Antes:** cada vez que visitabas la galería, la app intentaba decodificarlo, fallaba y
  **re-descargaba de la red… una y otra vez en cada visita** (doble decode fallido por
  visita).
- **Después:** la primera vez que falla el decode, la app **borra el archivo corrupto**.
  La siguiente visita re-descarga una única vez y lo deja sano.

---

## 6. La cola de sincronización ya no se barre entera (PERF-010)

**Escenario:** bibliotecas muy grandes (100 000+ registros) con episodios pendientes de
sincronizar.

- **Antes:** la consulta "¿qué episodios faltan por sincronizar?" hacía un **barrido de
  toda la tabla** (sin índice).
- **Después:** existe el índice `(VistoLocal, SincronizadoEnNube)` (migración v2 de la
  base de datos): la consulta es un rango directo. Tu base existente **migra sola al
  arrancar**; no hay que hacer nada.

---

## 7. Benchmarks de rendimiento: por fin se ejecutan (OPS-001)

**Para el equipo de desarrollo:**

- **Antes:** el proyecto de benchmarks (reproductor/DB/scanner) **existía pero nunca se
  ejecutaba**: no había ni una medición guardada, y el historial se escribía en
  `bin\Release\…` mientras el script lo buscaba en la raíz del repo (nunca coincidían).
- **Después:** la ruta del historial está unificada (variable
  `ANIMELOCALTRACKER_BENCH_HISTORY`) y hay un workflow de GitHub Actions
  (`benchmarks.yml`) que ejecuta los 9 benchmarks de forma **manual o semanal**, genera
  los reportes con comparación ±5% contra el historial y los sube como artefacto.
  Primera ejecución recomendada desde *Actions → Benchmarks → Run workflow* para
  generar la línea base.

---

## 8. Calidad y observabilidad (OPS-004/005/008)

| Cambio | Antes | Después |
|---|---|---|
| **`app.log` al arrancar** | No se sabía qué versión produjo un log | La primera línea indica **versión y arquitectura** (p. ej. "versión 1.0.5.0, x64") |
| **CI — clippy (Rust)** | El núcleo Rust solo se auditaba por vulnerabilidades (cargo audit), sin control de estilo | `cargo clippy -- -D warnings` **bloquea el CI** (se corrigieron los 2 avisos existentes en el FFI) |
| **CI — cobertura** | Umbral solo de líneas (45%) | Umbral de **líneas (45%) y ramas (30%)** |

---

## Checklist de verificación sugerida

1. Exporta tu biblioteca a JSON con 300+ animes → la ventana no se congela (PERF-007).
2. "Actualizar" en la Galería con red lenta → el progreso se ve igual, pero la BD se
   escribe una sola vez al final (PERF-006).
3. Abre la ficha de un anime de 200+ capítulos sin miniaturas → aparecen una a una y la
   UI responde (PERF-001/005).
4. Corrompe a propósito un `Covers\{id}.jpg` → la app lo borra y lo re-descarga una sola
   vez (PERF-008).
5. Consulta `%LocalAppData%\AnimeLocalTrackerData\Logs\app.log` → la primera línea tiene
   versión y arquitectura (OPS-005).

*Documento informativo; no sustituye a los informes de auditoría (AUDITORIA_BLOQUE1…4)
que contienen el detalle técnico con `file:line`.*
