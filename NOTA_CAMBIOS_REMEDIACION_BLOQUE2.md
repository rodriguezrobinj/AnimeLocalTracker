# NOTA DE CAMBIOS — Remediación del Bloque 2 explicada con ejemplos de uso

> Explicación orientada a usuario/equipo de los cambios de la remediación del Bloque 2
> (auditoría de Funcionalidad e Integraciones). Cada sección describe un escenario dentro
> de la aplicación y el comportamiento "antes → después". Complementa a
> `AUDITORIA_BLOQUE2_FUNCIONALIDAD_INTEGRACIONES.md` (hallazgos técnicos con `file:line`).

---

## 1. Ajustes de Configuración que ahora funcionan de verdad

### a) "Porcentaje para marcar como visto" (FUN-003)

**Escenario:** vas a Configuración → Reproducción y cambias el porcentaje.

- **Antes:** el ajuste era **decorativo**: el auto-marcado ocurría siempre al 90% de la
  reproducción, pasaras lo que pasaras. Si ponías 100% esperando "solo al terminar",
  se seguía marcando al 90%.
- **Después:** el auto-marcado usa **el porcentaje que tú eliges** (de 1 a 100; el valor
  por defecto es 90%, como promete el README). Pones 100% → solo se marca al terminar;
  pones 80% → se marca al llegar al 80%.

> Nota: si ya tenías un valor guardado (p. ej. 95%), se conserva. El cambio de 95→90
> afecta solo a instalaciones nuevas o configuraciones sin valor previo.

### b) "Intervalo de sincronización" (FUN-005)

- **Antes:** sin importar lo que eligieras (1, 10, 60 minutos), la app sincronizaba con
  AniList **cada 5 minutos fijos**.
- **Después:** el intervalo se aplica **al arrancar** y **se reinicia en caliente al
  guardar** las preferencias. Pones 60 min → sincroniza cada hora.

### c) "Buscar actualizaciones al iniciar" (FUN-005)

- **Antes:** desactivar la casilla no hacía nada: la app siempre comprobaba
  actualizaciones en segundo plano.
- **Después:** si la desactivas, la app **deja de comprobar actualizaciones
  automáticamente** (el botón manual de Actualizaciones sigue disponible).

---

## 2. Reproducción y auto-tracking

### a) Un video truncado ya no se marca como visto (FUN-012)

**Escenario:** tu archivo `EP05.mkv` está cortado/corrupto y el reproductor "termina" a
los 3 minutos de un capítulo de 24.

- **Antes:** al llegar al "final" (aunque fuera prematuro) la app marcaba el episodio como
  visto y lo sincronizaba con AniList.
- **Después:** solo se marca como visto si se alcanzó el **final real del archivo**
  (≥ umbral configurado). El video truncado queda sin marcar.

### b) Archivos "especiales" sin número (FUN-004)

**Escenario:** en la carpeta del anime tienes `Specials.mkv` (sin número de episodio) y
aún no has visto el Episodio 3.

- **Antes:** "Qué veo hoy" podía elegir el `Specials.mkv` (su número era 0) por delante
  del episodio real; y si lo veías hasta el final, la app **ponía tu progreso de AniList
  a 0** (regresión de datos).
- **Después:** los archivos sin número **no son candidatos** de "Qué veo hoy" ni se
  sincronizan nunca; ver uno ya no puede tocar tu progreso en AniList.

### c) Reanudar un episodio cuyo archivo cambió (FUN-006)

**Escenario:** reemplazas `EP05.mkv` (50 min) por otro archivo de solo 5 min (misma
carpeta y nombre).

- **Antes:** la app reanudaba "desde el minuto 25" sobre el archivo nuevo (quedaba
  fuera de rango: el video saltaba al final o se comportaba raro).
- **Después:** si la ruta guardada **no coincide con el archivo que vas a abrir**, no se
  reanuda; y cualquier posición de reanudación se **acota a la duración real** del
  archivo. Empiezas desde el principio.

### d) "Reanudar" elige el episodio correcto (FUN-010)

**Escenario:** dejaste a medias el Episodio 1 (50%), el 2 (30%) y el 3 (70%), en ese
orden de visualización.

- **Antes:** "Reanudar" abría siempre el de **menor número** con progreso (el 1), aunque
  el último que viste fuera el 3.
- **Después:** se reanuda el **más recientemente reproducido** (el 3).

### e) Saltos de OP/ED y guardado de progreso (FUN-007 / FUN-011 / FUN-017)

- **FUN-007:** el salto automático de intro ya no puede **pasarse del final del video**
  (con timings incorrectos de AniSkip) — antes podía disparar un "terminado" falso.
- **FUN-011/017:** los guardados de progreso (cada 5 s) están **serializados**: si pausas
  justo al terminar un episodio, ya no hay carrera donde un guardado viejo pisara la
  marca de "visto" o notificara el progreso de otro episodio en la lista de Detalle.

---

## 3. Notificaciones de episodios nuevos (FUN-008)

**Escenario:** descargas/copias un capítulo nuevo a la carpeta del anime **con la app
abierta**.

- **Antes:** las notificaciones solo se buscaban **una vez al arrancar**: copiar archivos
  con la app abierta jamás producía el toast.
- **Después:** además del chequeo inicial, la app **revisa cada 30 minutos** mientras está
  abierta: copias el archivo y, como tarde, media hora después recibes el aviso
  "N episodios nuevos".

---

## 4. Descargas de episodios

### a) Pausar y reanudar contra servidores sin soporte de rangos (FUN-014)

**Escenario:** descargas un episodio de un servidor que **ignora** la petición de
reanudación; pausas y reanudas.

- **Antes:** la app seguía escribiendo a partir del archivo parcial mientras el servidor
  enviaba el archivo **desde el principio** → el archivo final quedaba **corrupto sin
  ningún aviso**.
- **Después:** si al reanudar el servidor responde "200" (cuerpo desde cero) en vez de
  "206" (partial), la app **borra el parcial y reinicia la descarga limpia**.

### b) Servidor que se queda mudo (FUN-015)

**Escenario:** la descarga avanza y de repente el servidor deja de enviar bytes (sin
cortar la conexión).

- **Antes:** la descarga quedaba **"colgada" indefinidamente**; solo la cortaba el
  usuario (pausar/cancelar).
- **Después:** si pasan **60 segundos sin recibir datos**, la descarga aborta con un
  error claro ("inactividad"), y en el modo segmentado **se conserva el estado** para
  poder reanudar después.

### c) Diagnóstico (FUN-016)

- **Antes:** el ciclo de vida de las descargas solo escribía a `Debug` (invisible en el
  `app.log` de las builds de release): un fallo no dejaba rastro.
- **Después:** inicio/fallo/error de cada descarga queda **registrado en `app.log`** con
  título y episodio, para poder diagnosticar "por qué falló".

---

## 5. Importar una biblioteca desde JSON (FUN-013)

**Escenario:** importas un JSON de biblioteca (exportado de esta u otra máquina).

- **Antes:**
  - Si el JSON traía el **mismo anime dos veces** (p. ej. con títulos distintos), la
    segunda entrada **pisaba a la primera en silencio** (perdías datos sin enterarte).
  - Si el archivo estaba **malformado** (no era un JSON real o tenía tipos inválidos),
    la app mostraba un error genérico "Error".
- **Después:**
  - Con duplicados se **avisa en el log** y se conserva la última entrada (regla clara,
    sin pérdida silenciosa).
  - Un JSON inválido muestra: *"El archivo seleccionado no es un JSON de biblioteca
    válido de AnimeLocalTracker"*.

---

## 6. Galería y base de datos (invisible pero importante)

### a) Carga de biblioteca sin escrituras innecesarias (FUN-018)

- **Antes:** cada vez que abrías la Galería, la app recalculaba estados y **escribía en
  la base de datos** (1 UPDATE por anime), aunque nada hubiera cambiado.
- **Después:** solo escribe cuando el estado **cambia de verdad**. Menos escrituras =
  menos desgaste del disco y arranques más ligeros con bibliotecas grandes.

### b) Descargar un episodio que ya habías visto (FUN-019)

**Escenario:** viste el Episodio 5 en otro momento (queda marcado como visto, sin
archivo local) y luego descargas el archivo desde la app.

- **Antes:** la descarga podía **resetear la marca de "visto"** (y el progreso) del
  episodio.
- **Después:** la descarga solo **añade los metadatos del archivo** (miniatura,
  resolución…): el episodio sigue marcado como visto y conserva su progreso.

---

## 7. Cambiar la carpeta base de animes (FUN-009)

**Escenario:** en Configuración → Almacenamiento eliges otra carpeta principal.

- **Antes:** el diálogo solo confirmaba la nueva ruta; el usuario podía creer que sus
  animes se habían movido (no es así: cada anime conserva su carpeta absoluta).
- **Después:** el diálogo **avisa explícitamente**: *"Los animes ya existentes conservan
  su carpeta actual: para que la app los encuentre, deberán estar (o copiarse) dentro de
  la nueva carpeta base."*

---

## 8. Integraciones (visible sobre todo en logs y en casos de red)

| Cambio | Antes | Después |
|---|---|---|
| **INT-001** — Actualizar biblioteca | Si un lote de animes fallaba al consultar AniList, **no quedaba rastro** de cuántos se quedaron sin refrescar | El log resume: "X/Y lotes fallidos; N/M animes disponibles" |
| **INT-004** — Daemon Python | Si el daemon no arrancaba a tiempo (onefile lento), se **descartaba para toda la sesión** (todo iba por procesos one-shot lentos) | Reintenta con **backoff de 10 min** y el handshake tolera hasta **20 s** |
| **INT-005** — AniSkip (saltos OP/ED) | Un fallo puntual bloqueaba el salto automático durante **15 min** | Se reintenta a los **5 min** |
| **INT-002** — Timeouts | AniSkip podía esperar hasta **100 s** por defecto | Timeout propio de **30 s** |

---

## Checklist de verificación manual sugerida

1. Configuración → pon "marcar como visto" al 100% → ve un capítulo hasta el final y
   otro hasta el 90%: solo el primero debe marcarse (FUN-003/012).
2. Copia un capítulo nuevo a una carpeta con la app abierta → en ≤30 min recibes el
   toast de episodios nuevos (FUN-008).
3. Pausa/reanuda una descarga de un servidor que no soporta rangos → el archivo final
   debe ser íntegro (FUN-014).
4. Descarga un episodio que ya estaba marcado como visto → sigue visto tras descargar
   (FUN-019).
5. Importa un JSON con el mismo anime repetido → aparece el último y hay aviso en el log
   (FUN-013).

*Documento informativo; no sustituye a los informes de auditoría (AUDITORIA_BLOQUE1…4)
que contienen el detalle técnico con `file:line`.*
