# NOTA DE CAMBIOS — Remediación del Bloque 1 explicada con ejemplos de uso

> Explicación orientada a usuario/equipo de qué cambió con la remediación del Bloque 1
> (auditoría de Seguridad y Arquitectura). Cada sección describe un escenario dentro de
> la aplicación y el comportamiento "antes → después". Complementa a
> `AUDITORIA_BLOQUE1_SEGURIDAD_ARQUITECTURA.md` (hallazgos técnicos con `file:line`).

---

## 1. Conectar tu cuenta de AniList (login)

**Escenario:** pulsas "CONECTAR" en la Galería, se abre el navegador y la app espera en `http://localhost:5050/callback`.

| | Antes | Después |
|---|---|---|
| Origen del navegador | Se aceptaba cualquier petición cuyo Referer *empezara* por `http://localhost:5050` → un sitio trampa alojado en un dominio como `localhost:5050.evil.com` podía colarse (SEC-005) | Solo se acepta el origen **exacto** `http://localhost:5050` (esquema+host+puerto comparados de verdad) |
| Reintentos del token | Si el token capturado se reenviaba dos veces (replay), la app lo procesaba igual | El POST del token se acepta **una sola vez por intento** de login; una segunda entrega se rechaza con error 410 (SEC-001) |
| Token caducado/revocado | La app seguía mostrando "conectado" y fallaba en silencio cada 5 min | Ante el primer rechazo (401) la app **cierra sesión sola**, borra el token y te deja el botón CONECTAR para volver a entrar (SEC-002) |

**Ejemplo fácil:** revocas el acceso de la app desde anilist.co en el móvil. Antes: al volver al PC, todo parecía normal pero nada se sincronizaba. Ahora: al primer intento la app se "desconecta" y te invita a reconectar; sabes exactamente qué pasa.

---

## 2. Descargar episodios

**Escenario:** descargas un episodio desde la ficha del anime (fuente animeav1/yt-dlp).

**a) Servidor que no anuncia el tamaño o que miente (SEC-003)**
- **Antes:** si el servidor no soportaba descargas por rangos, la descarga secuencial **no tenía límite**: un servidor trampa podía llenarte el disco. Los topes de 35 GB solo existían en el modo segmentado.
- **Después:** el modo secuencial tiene el mismo tope de 35 GB. Si el servidor declara un tamaño absurdo o el archivo crece sin fin, la descarga **se corta, se borra el archivo parcial** y ves un error claro ("supera el límite de seguridad…") en lugar de un disco lleno.

**b) Enlaces que redirigen (SEC-003)**
- **Antes:** si el enlace de descarga hacía un redirect de `https://…` a `http://…`, el cliente lo seguía a ciegas (tu tráfico de video podía viajar sin cifrar).
- **Después:** cada salto de redirección se valida: solo se permite https, sin credenciales embebidas, y máx. 5 saltos. Si el servidor intenta degradar a http o mandarte a un host raro, la descarga se **bloquea con error** en lugar de seguir.

---

## 3. Portadas de la galería

**Escenario:** añades un anime desde el buscador o importas una biblioteca con un JSON.

- **Antes (SEC-004):** la app descargaba la portada de **cualquier** URL http/https que llegara en los datos, y **guardaba los bytes en `Covers\` aunque no fueran una imagen**. Un JSON manipulado podía hacer que la app sondeara servicios internos de tu red (SSRF) y dejar basura de hasta 10 MB en disco.
- **Después:** las portadas solo se descargan de la CDN oficial de AniList (`s4.anilist.co` y subdominios de `anilist.co`) por **https**, y antes de guardar se comprueba la **firma real del archivo** (JPEG/PNG/GIF/WebP). Una URL rara se ignora con un aviso en el log; tu carpeta de portadas solo contiene imágenes reales.

---

## 4. Reproducción y pantallas (sin cambios visibles, pero más robusto)

**Escenario:** Galería → ficha de un anime → Reproducir → F11 (pantalla completa) → volver a la Galería.

| | Antes | Después |
|---|---|---|
| Cambio de pantalla (ARC-002/004) | La ventana principal "cocinaba" cada vista a mano con `new Vista()` y una caché interna; las vistas registradas en el contenedor DI estaban muertas | WPF elige la vista automáticamente con plantillas (DataTemplate) según el tipo de pantalla: el mismo flujo, sin fábrica manual ni registros fantasma |
| Pantalla completa (ARC-004) | El reproductor hablaba directamente con "la ventana concreta MainWindow" (cast): si mañana cambiaba la ventana, se rompía | El reproductor usa un contrato mínimo (`IVentanaPrincipal`): F11 sigue igual, pero el reproductor ya no depende de la clase de la ventana |
| Navegación interna (ARC-002) | `MainViewModel` recibía el contenedor completo de dependencias y pedía "dame el ViewModel de X" por todas partes | Hay un único `NavigationService` que reparte pantallas; si algo falla al resolver, se registra en el log en vez de fallar en silencio |
| Excepciones al navegar/buscar (ARC-011) | 4 puntos "async void": una excepción no capturada al abrir el Detalle, el Reproductor o al escribir en el buscador **podía tumbar la app** | Esas tareas ahora son `async Task` con captura: un error se registra en `app.log` y **la app sigue viva** |
| Saltos OP/ED (ARC-005) | Había **dos cachés** del mapeo AniList→MAL ID (una en AniSkipService y otra en el coordinador) | Una sola caché compartida: mismo comportamiento al saltar intros, menos RAM duplicada |

**Ejemplo:** escribes "one" en el buscador, borras rápido y pulsas Enter varias veces. Antes, cualquier excepción de cancelación mal gestionada podía cerrar la app; ahora el peor caso es una línea en el log.

---

## 5. Tu biblioteca y la base de datos (protección a futuro)

**Escenario:** actualizas la app a una versión que añade una columna nueva a la base de datos.

- **Antes (ARC-006):** el esquema se creaba con `CreateTableAsync` y `user_version = 1` fijo. `CreateTable` **no altera** tablas existentes: no había mecanismo para migrar tu biblioteca si el esquema cambiaba.
- **Después:** el esquema evoluciona con **migraciones versionadas**: al arrancar, la app lee la versión de tu base, aplica en orden las migraciones pendientes y registra la versión. Hoy hay una (v1, esquema base); cuando llegue una v2, tu `biblioteca.db` migrará sola **sin tocar tus datos**.

---

## 6. Código nativo (parsing de nombres y miniaturas)

**Escenario:** tu carpeta tiene archivos con caracteres especiales (emojis, surrogates rotos, nombres raros).

- **Antes (SEC-006):** al pasar el nombre del archivo al núcleo Rust (FFI), la conversión UTF-8 **reemplazaba silenciosamente** caracteres inválidos por `?`: el parser podía devolver nombres corruptos sin avisar.
- **Después:** la codificación es **estricta**: si un nombre no es convertible, se registra el error y la operación degrada con elegancia (nunca un nombre silenciosamente corrupto).
- **Además (ARC-008):** las llamadas nativas se generan en compilación (`LibraryImport`) en vez de en runtime (`DllImport`) → errores de firma se detectan al compilar, no al ejecutar. Y si el `ffmpeg.exe` embebido falta (miniaturas), ahora **se avisa en el log** en lugar de depender del ffmpeg del sistema sin decir nada (SEC-07).

---

## 7. Logs y privacidad

**Escenario:** envías `app.log` a soporte o a un foro para reportar un bug.

- **Antes (SEC-012):** el log contenía rutas completas: `C:\Users\TuNombreDeUsuario\AppData\Local\AnimeLocalTrackerData\Backups\...`
- **Después:** esas rutas se guardan como `C:\Users\<perfil>\...\<datos>\Backups\...`. Compartir un log ya no revela tu nombre de usuario de Windows ni la estructura exacta de tu disco.

---

## 8. Sincronización con AniList

- **Antes (FUN-001):** veías 5 episodios con la app sin conexión y, mientras tanto, otros 7 en la web/móvil (total 12). Al reconectar, la app escribía `progress=5` en AniList → **regresión permanente**.
- **Después:** la app **lee primero el progreso remoto** y empuja el mayor de los dos (12). Nunca degrada lo que ya avanzaste en otro sitio.
- **Antes (FUN-002):** si AniList respondía HTTP 200 pero con `"errors"` dentro (token inválido), la app marcaba los episodios como "sincronizados" **sin haberse sincronizado nunca**.
- **Después:** se detecta el error en el cuerpo de la respuesta, se registra y los episodios **quedan pendientes** para el siguiente ciclo.

---

## 9. Menú contextual de la ficha del anime

**Escenario:** en Detalle, clic derecho sobre un episodio descargado → "Reproducir episodio" / "Eliminar episodio del disco".

- **Antes:** los comandos se enlazaban buscando la lista desde dentro del menú contextual; como el `ContextMenu` vive en un árbol visual separado, **el enlace se perdía** y las opciones podían no ejecutar nada.
- **Después:** el menú toma el contexto desde el elemento sobre el que se abrió (`PlacementTarget`) → **Reproducir y Eliminar funcionan** desde el clic derecho.

---

## Checklist de verificación manual sugerida

1. Revocar la app en anilist.co → en ≤5 min la app debe pasar a "desconectado" y pedir reconectar (SEC-002).
2. Marcar episodios en la web con la app cerrada, luego abrirla → el progreso **no** retrocede (FUN-001).
3. Clic derecho en un episodio de Detalle → Reproducir/Eliminar funcionan.
4. Navegación completa Galería→Ficha→Reproductor con F11 y volver — todo igual que siempre (ARC-002/004).
5. Abrir `app.log` → ya no aparecen rutas con tu nombre de usuario (SEC-012).

*Documento informativo; no sustituye a los informes de auditoría (AUDITORIA_BLOQUE1…4) que contienen el detalle técnico con `file:line`.*
