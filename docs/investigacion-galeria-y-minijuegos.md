# Investigación: Galería y Minijuegos (2026-10-02)

Estado real de la pestaña Galería (biblioteca, filtros, "Qué veo hoy") y de su sección Minijuegos (menú y los tres juegos),
antes de tocar nada. Solo se investigó: no hay cambios de código en este informe.

## Cómo se investigó

- **Código leído completo:** `GaleriaViewModel` (3 archivos), `GaleriaView.xaml` y su code-behind, `SelectorSeccionGaleria`,
  `MinijuegosViewModel`, `MinijuegoViewModelBase`, los tres ViewModels de juego, sus tres vistas, `MinijuegoOpcionesView`,
  `MinijuegoResumenView`, `MinijuegosEstilos`, `MinijuegoFocoHelper`, la lógica pura (`AdivinaAnimeJuego`,
  `AdivinaOpEdJuego`, `AdivinaPersonajeJuego`), `MinijuegosRecordsService`, `ClipPlayer`, `PersonajesService`,
  `ImageCacheService`, `PrecargaBiblioteca`, `AnfitrionVistas`, los convertidores de portada y las partes de
  `DatabaseService`, `NavigationService` y `SeguimientoEditorViewModel` que intervienen.
- **Registros de sesión reales** (líneas `[Perf]` del 1 y 2 de octubre, unas 25 aperturas) y `errores.log`.
- **No se abrió la app ni se corrieron las pruebas.** Cada hallazgo dice cómo se sabe: *medido en el registro* o *leído en
  el código*. Lo que necesita comprobarse en la app real está marcado como *por comprobar* y reunido al final.

## En números

| | |
|---|---|
| Galería, ViewModel | 1.700 líneas en 3 archivos, 10 dependencias en el constructor |
| Galería, vista | 1.169 líneas de XAML: cabecera, filtros, tarjeta, menú contextual, panel "Qué veo hoy" y la sección Minijuegos, todo en un archivo |
| Minijuegos | 4 ViewModels (1.300 líneas), 6 vistas (830 líneas de XAML), 3 clases de lógica pura (610 líneas) |
| Colores | Galería: 46 colores escritos a mano. Minijuegos: 2 (el resto ya sale de `Brush.*`) |
| Pruebas | Minijuegos: unas 230 en 14 archivos. Galería: 38 en 4 archivos (filtros y acciones casi sin cubrir) |
| Tu biblioteca | 227 animes, 3.409 registros de episodios |
| `errores.log` | Ningún error de Galería ni de Minijuegos |

---

## 1. Errores confirmados

### 1.1 "Actualizar biblioteca" puede dejar un anime con 0 episodios — *código*

`GaleriaViewModel.cs:1082`: si AniList no tiene programado el próximo episodio y tampoco da el total (pasa con series largas
en pausa, como One Piece entre arcos), el total se pone a **0** (`Episodes ?? 0`). La misma regla está escrita otras tres
veces en la app y las otras tres conservan el total que ya tenías (`DetalleViewModel.cs:373`,
`MediaEnrichmentService.cs:41`) o lo tratan aparte (`AnimeLibraryService.cs:104`).

Ejemplo: pulsas el botón de sincronizar un día en que One Piece no tiene fecha de próximo capítulo. La tarjeta pasa a
"1180 / 0", la barra de progreso se vacía y el orden "Mayor progreso" lo manda al final.

**Arreglo propuesto:** una sola función con la regla (la de `MediaEnrichmentService`) usada desde los cuatro sitios.

### 1.2 Mover animes con la selección múltiple no llega a AniList y se deshace solo — *código*

`CategorizarSeleccionadosAsync` (`GaleriaViewModel.cs:1174`) cambia el estado solo en la base de datos local. El editor de
seguimiento de la Ficha, en cambio, guarda un `SeguimientoLocal` marcado como pendiente y lo envía a AniList
(`SeguimientoEditorViewModel.cs:179-204`). Como la Galería no deja nada pendiente, la siguiente vez que pulsas
"Actualizar biblioteca" con la cuenta conectada, AniList manda (`GaleriaViewModel.cs:1109-1113`) y tu cambio desaparece.

Ejemplo: seleccionas cinco animes, los mueves a "Completados", sincronizas, y vuelven a "Planeando".

**Arreglo propuesto:** que la selección múltiple use el mismo camino que la Ficha (guardar pendiente y enviar).

### 1.3 La selección múltiple cambia animes que ya no ves — *código*

La acción se aplica a todo lo que tenga la marca de seleccionado en la biblioteca entera, no a lo que está en pantalla. Si
seleccionas tres animes y después escribes en el buscador o cambias de filtro, los que quedaron ocultos siguen
seleccionados y también se mueven. La barra tampoco dice cuántos hay seleccionados, así que no hay forma de notarlo.

**Arreglo propuesto:** contador en la barra ("3 seleccionados") y aplicar solo a los visibles, o vaciar la selección al
cambiar de filtro.

### 1.4 Eliminar un anime "con sus archivos" borra la carpeta entera, sin comprobar nada — *código*

`GaleriaViewModel.cs:981` (y lo mismo en `DetalleViewModel.cs:301`): `Directory.Delete(carpeta, recursive: true)`.

- No comprueba que la carpeta sea solo de ese anime. Si dos temporadas comparten carpeta, o si la ruta asociada es una
  carpeta general (`D:\Anime`), se borra todo lo que haya dentro.
- Se ejecuta en el hilo de la interfaz: con una carpeta grande la ventana se congela hasta que termina.
- Si falla (un video abierto en el reproductor), solo queda una línea de depuración en el registro. El anime ya salió de
  la biblioteca y los archivos siguen en el disco sin que nadie te lo diga.
- La segunda pregunta ("¿borrar también los archivos?") sale siempre, aunque el anime no tenga carpeta.

**Arreglo propuesto:** negarse (con aviso) si otra entrada de la biblioteca usa esa carpeta o una subcarpeta suya, o si es
la raíz de una unidad; borrar fuera del hilo de la interfaz; avisar con un toast si no se pudo; no preguntar cuando no hay
carpeta.

### 1.5 Si "Actualizar biblioteca" falla a medias, el botón queda muerto — *código*

`ActualizarBibliotecaAsync` no tiene `try/finally`. Un fallo al guardar en la base de datos deja `EstaActualizando` en
verdadero: el panel flotante se queda en pantalla y el botón no vuelve a responder hasta reiniciar la app.

Además, la barra de progreso es de adorno: el recorrido de los 227 animes no cede el control a la interfaz en ningún
momento, así que la barra salta de vacía a llena de golpe. Después se guardan las próximas emisiones una por una (hasta
227 escrituras sueltas) y se esperan 2 segundos fijos.

### 1.6 Los filtros no se enteran de lo que cambia fuera de la Galería — *código*

La lista solo se vuelve a filtrar y ordenar cuando tocas un filtro. No lo hace cuando:

- ves un episodio (cambia `EpisodiosVistos`): con "Pendientes" activo u orden por progreso, el anime se queda donde estaba;
- cambias el estado en la Ficha: con el filtro "Viendo" activo, un anime que acabas de completar sigue ahí al volver.

### 1.7 "Adivina el OP/ED" descarga a tu carpeta de música y lo anota en Descargas — *código*

El juego llama a `DescargarYConvertirAsync` con la variante que usa `visibleEnDescargas: true` y la ruta "legible"
(`AnimeThemesDownloadService.cs:181-185`). Cada partida nueva puede dejar hasta 10 mp3 (unos 2 MB cada uno) en tu
biblioteca de música, con su entrada en el historial de la pestaña Descargas, de canciones que tú no pediste.

**Arreglo propuesto:** que el juego descargue a la caché temporal de vistas previas (ya existe para la Ficha) y sin
aparecer en Descargas. Es decisión tuya: ver "Decisiones que son tuyas".

### 1.8 Sin sonido, el juego de OP/ED no avisa — *código*

`AdivinaOpEdViewModel.Reproducir` pone "sonando" en verdadero en cuanto pide el clip. Si el archivo falla al abrirse
(`MediaFailed`), `ClipPlayer` solo escribe una línea de depuración: el icono del altavoz se queda encendido, no se oye nada
y no hay mensaje. El volumen está fijo al 85 % en el código, sin control en pantalla. Y la música de fondo que el juego
calla al empezar no se reanuda al salir.

### 1.9 Sin internet real, "Adivina el personaje" puede tardar 40 s y fallar teniendo datos guardados — *código, por comprobar*

Sin conexión el juego pide los personajes de toda la biblioteca (`AdivinaPersonajeViewModel.cs:226-229`). `PersonajesService`
entrega primero lo guardado, pero después intenta igualmente consultar a AniList cada grupo de 5 animes sin copia vigente, y
cuando un grupo falla **sigue con el siguiente** (`PersonajesService.cs:130`). Con el cable desconectado cada intento falla
al instante y no pasa nada. Con wifi conectado pero sin salida a internet, cada intento espera 15 s: se agota el tope de
40 s de la partida, se descarta todo (también lo que ya estaba leído del disco) y sale "no se pudo preparar la partida".

**Arreglo propuesto:** al primer grupo que falle, dejar de consultar; y con `PareceSinConexion` no consultar.

### 1.10 Tras responder con el ratón, Enter no pasa a la siguiente ronda — *código, por comprobar*

El atajo Enter está en la vista del juego. Al hacer clic en una opción el foco se queda en ese botón, y un botón con foco
se queda con la tecla Enter (vuelve a "pulsar" la opción ya respondida, que no hace nada). El ayudante de foco solo
devuelve el foco a la vista al cambiar de ronda o de estado, no al responder. Con las teclas 1-4 sí funciona.

### 1.11 No se puede abandonar una partida — *código*

"Volver al inicio" solo existe en la pantalla de resumen. A mitad de partida, "Volver al menú" la deja en pausa y al
volver a entrar sigues en la misma ronda: la única forma de empezar otra es terminar las 10 rondas o reiniciar la app.

### 1.12 Otros fallos menores — *código*

- **Al abrir la app se ve un instante "biblioteca vacía"** *(por comprobar)*: `BibliotecaVacia` es verdadero hasta que
  llegan los datos y la Galería no tiene estado de "cargando". La carga espera a propósito a que la ventana esté pintada
  (`GaleriaViewModel.cs:488`), así que el mensaje de biblioteca vacía con su botón "Buscar y añadir" es lo primero que se
  dibuja. Si la base de datos falla, se queda así.
- **"Sin resultados" no ofrece salida:** si el buscador no encuentra nada y el panel de filtros está cerrado, no hay botón
  "Limpiar filtros" a la vista (el único está dentro del panel).
- **"Más recientes" no ordena por lo último que añadiste:** ordena por número de AniList (`GaleriaViewModel.cs:796`). No
  hay fecha de alta guardada.
- **Cambiar de idioma borra el filtro de temporada** sin avisar (el valor seleccionado es el texto traducido).
- **El buscador distingue tildes:** "pokemon" no encuentra "Pokémon".
- **Animes en pausa o abandonados no tienen filtro:** solo hay Todos / Viendo / Completados / Planeando.
- **La etiqueta de la tarjeta muestra si el anime está en emisión, no tu estado**, mientras que los filtros de al lado
  son de tu estado. No se ve en la tarjeta si un anime está en "Viendo" o en "Planeando".
- **Favorito solo por clic derecho:** no hay ningún botón visible para marcarlo.
- **Con ventana más ancha que 1.248 px**, un clic fuera de esa franja no cierra el menú de usuario.
- **`Receive(EpisodioActualizadoMensaje)`** recorre la colección de la biblioteca desde el hilo en que llegue el mensaje
  (la regla del proyecto pide saltar al hilo de la interfaz) y lee todos los registros del anime para contar los vistos.
- **Minijuegos:** el contador "Ronda 3 de 10" baja a mitad de partida cuando un anime no da para ronda; las opciones
  siguen resaltándose al pasar el ratón después de responder; el botón principal crece 2 px al recibir el foco del
  teclado; el mensaje de error de OP/ED no se traduce al cambiar de idioma.
- **Comentarios desfasados:** `MinijuegoFocoHelper` dice que la vista se recrea en cada visita (ya no), y la restauración
  del desplazamiento en `GaleriaView_Loaded` ya no hace falta desde que la pestaña se conserva.

---

## 2. Rendimiento

### Lo medido (registro, build Debug, 227 animes)

| Qué | Tiempo |
|---|---|
| Leer biblioteca y registros al abrir | 1.080-1.230 ms |
| Dibujar las primeras tarjetas | 520-770 ms |
| Cargar las 203 portadas restantes | 1.700-2.300 ms (en segundo plano) |
| Volver a la Galería desde otra pestaña | 2-13 ms (desde la Ficha, hasta 123 ms en algún caso) |
| Biblioteca → Minijuegos | 69-105 ms |
| Menú → un juego | 60-100 ms |
| Juego → menú, Minijuegos → Biblioteca | 18-53 ms |

Moverse por la Galería y los Minijuegos **ya es rápido**: no hay cuello de botella que se note en los cambios de sección.
Lo que queda está en el arranque y en trabajo repetido que hoy no se nota pero crece con la biblioteca.

### Dónde se va el tiempo

1. **Lectura inicial (≈1,1 s).** Se leen los 3.409 registros de episodios completos solo para contar cuántos están vistos
   por anime, y los 227 animes con su sinopsis entera (hasta 20 KB cada una) que la Galería no muestra. Una consulta que
   cuente en la base de datos (`COUNT ... GROUP BY`) devuelve 227 filas en vez de 3.409. *No está medido cuánto de ese
   1,1 s es lectura y cuánto es espera por el procesador mientras se construye la ventana: hay que medirlo antes de
   prometer una cifra.*
2. **Los minijuegos releen la biblioteca entera cada vez.** Cada juego llama a `ObtenerTodosLosAnimesAsync` (227 animes
   con sinopsis y 227 comprobaciones de archivo) al entrar, al volver al inicio y al volver a la pestaña con la sección
   abierta, y guarda su propia copia. La Galería ya tiene esos mismos datos cargados. Son tres copias de la biblioteca en
   memoria y una lectura que sobra.
3. **Los récords leen todas las partidas de todos los juegos:** tres lecturas completas de la tabla al abrir el menú y
   otra al terminar cada partida, sin usar el índice que ya existe por juego. Hoy son pocas filas; crece sin límite.
4. **Portadas:** se decodifican las 227 al abrir aunque solo se vean 16. Ocupa unos 60 MB y 2 s de un núcleo. Está en
   segundo plano y no bloquea, pero se podría cargar según se desplaza la lista.
5. **Tarjeta:** cada una lleva una sombra de Material Design que cambia al pasar el ratón, escalado de imagen de alta
   calidad y una animación de tamaño. La portada ya se decodifica al tamaño de la tarjeta, así que el escalado de alta
   calidad aporta poco. *Ya se midió que quitar piezas no cambia el primer dibujado; no está medida la fluidez del
   desplazamiento.* Tres insignias llevan `ElevationAssist.Elevation`, que en un `Border` no hace nada.
6. **"Adivina el personaje":** el convertidor de la imagen pixelada comprueba el archivo y decodifica en el hilo de la
   interfaz en cada pista (la regla del proyecto pide no tocar disco desde un enlace). Son pocos milisegundos. La limpieza
   de imágenes antiguas recorre la carpeta después de cada descarga (10 veces por partida, 4 a la vez).

---

## 3. Interfaz y experiencia de uso

### Galería

- **Cabecera cargada:** título, contador, selector de sección, "Qué veo hoy", selección múltiple, sincronizar y cuenta en
  una fila; debajo, buscador, filtros y cuatro pestañas de estado. En 1.366 px de ancho queda justo *(por comprobar)*.
- **Sin estado de carga** (regla 3 de `ui-wpf-vistas.md`: indicador de carga, estado vacío con acción, sin resultados).
- **Barra de selección múltiple:** solo tres destinos (faltan En pausa y Abandonado), sin contador, sin "seleccionar
  todos", sin favorito ni eliminar en lote. Puede solaparse con el panel de sincronización (los dos flotan abajo al centro).
- **Tarjeta:** sin tu estado, sin acceso visible a favorito, sin el título completo al pasar el ratón cuando se corta.
- **Teclado** *(por comprobar)*: cada tarjeta es un botón dentro de un elemento de lista que también recibe el foco. Con
  las flechas se resalta la tarjeta, pero Enter puede no abrirla sin un Tab extra.
- **Colores:** 46 literales (fondos de tarjeta, menú contextual, barra flotante, menú de usuario). La Ficha ya se pasó a
  `Brush.*` sin cambiar ningún valor; aquí falta lo mismo. El menú contextual está copiado del de la Ficha.

### Minijuegos

- **Preguntan por animes que no has visto.** Con 227 animes y muchos en "Planeando", buena parte de las rondas son de
  series que solo tienes apuntadas. Es la mejora de más efecto: una opción "solo animes que he visto o estoy viendo".
- **OP/ED:** no se ve cuánto queda del clip (una barra o un aro que avance), no hay volumen.
- **Resumen:** solo puntos y aciertos. No lista qué rondas fallaste ni cuál era la respuesta.
- **Menú:** tres tarjetas en una fila fija de tres columnas; en ventana estrecha se aprietan en vez de bajar de línea
  *(por comprobar)*. Los récords son una línea de texto.
- **Mientras se prepara "Adivina el personaje"** (8 s la primera vez) solo hay un indicador giratorio, sin poder cancelar
  salvo saliendo al menú.
- Lo que ya estaba apuntado como pendiente y sigue igual: pista con un fotograma de un episodio local, modo de respuesta
  escrita, récords en la pestaña Estadísticas.

---

## 4. Estructura y organización

### Galería

- **`GaleriaView.xaml` (1.169 líneas) tiene cinco cosas dentro.** Piezas que se pueden sacar sin cambiar nada de lo que se
  ve: la tarjeta de anime, el menú contextual (compartido con la Ficha), el panel "Qué veo hoy" (155 líneas más su
  animación en el code-behind) y la barra de filtros.
- **"Qué veo hoy" ya es un ViewModel aparte en la práctica:** tiene su estado, sus comandos y su cancelación, pero vive
  como archivo parcial de `GaleriaViewModel` y todos sus nombres llevan el sufijo `QueVer` para no chocar. Lo mismo la
  sección Minijuegos. Separarlos baja el constructor de 10 dependencias a 6.
- **Filtros:** siete métodos `On...Changed` idénticos (refrescar y avisar de dos propiedades) y cuatro atributos repetidos
  en cada filtro. Un único método "se cambió un filtro" los sustituye a todos.
- **Texto traducido usado como valor:** "Todos los géneros", la temporada y el criterio de orden se comparan por su texto
  en el idioma actual (origen del fallo de temporada del punto 1.12). El estado del filtro usa palabras en español
  ("Viendo") que luego se traducen a códigos de AniList.
- **Colores en el ViewModel y en el modelo:** `AnimeItem.ColorEstado` y los diálogos reciben colores en hexadecimal.

### Minijuegos

- **Los tres juegos repiten lo mismo.** En los ViewModels: `Responder`, `ResponderNumero`, `Siguiente`,
  `VolverAlInicio`, `PedirPista` y el texto del resultado son casi idénticos y caben en la clase base. En las vistas:
  cargando, sin animes, presentación, ronda y puntos, lista de pistas y "pedir pista" son unas 100 líneas iguales en cada
  una de las tres.
- **El menú tiene las tres tarjetas escritas a mano.** Añadir un cuarto juego hoy toca seis sitios (ViewModel del menú,
  tres en la vista del menú, registro, precalentado).
- **`AdivinaOpEdViewModel`** cambia contadores (`_fallosSeguidos`, `_timeoutsSeguidos`, `_soloLocal`) desde un hilo de
  fondo mientras la interfaz los lee. No se ha visto fallar; conviene que la tarea devuelva el resultado y los contadores
  se toquen en un solo hilo.
- **La base usa una constante de un juego concreto** (`AdivinaAnimeJuego.MinimoAnimes`) para decidir si se puede jugar a
  cualquiera.

---

## 5. Plan por fases

Orden por riesgo para tus datos primero, después lo que se nota al usar, y la reorganización al final (con las pruebas
como red: los minijuegos tienen unas 230, la Galería necesita algunas más antes de moverla).

| Fase | Qué | Puntos |
|---|---|---|
| 1. Datos a salvo | Total de episodios que no se pone a 0; selección múltiple que llega a AniList y solo toca lo visible; borrado de carpeta con comprobaciones, en segundo plano y con aviso; `try/finally` en "Actualizar biblioteca" | 1.1 a 1.5 |
| 2. Fallos de uso | Filtros que se refrescan solos; estado de carga y "limpiar filtros" en sin resultados; buscador sin tildes; abandonar partida; Enter tras clic; aviso si el clip no suena; "Adivina el personaje" sin conexión | 1.6, 1.8 a 1.12 |
| 3. Rendimiento | Medir la lectura inicial y sustituir los 3.409 registros por un recuento; biblioteca compartida para los juegos; récords por juego | sección 2 |
| 4. Interfaz | Tarjeta (tu estado, favorito, título completo); barra de selección múltiple; filtros En pausa y Abandonado; colores a `Brush.*`; en los juegos: "solo lo que he visto", avance y volumen del clip, resumen con las rondas | sección 3 |
| 5. Estructura | Partir `GaleriaView.xaml`; "Qué veo hoy" y Minijuegos fuera de `GaleriaViewModel`; filtros en un solo método; lo común de los juegos a la clase base y a una vista compartida; menú de juegos por lista | sección 4 |

Cada fase termina con `repo-build-test` y, las que cambian lo que se ve, con comprobación en la app real.

### Fase 1 — hecha (2026-10-02), sin probar en la app real

- **Total de episodios (1.1):** una sola regla, `AniListMedia.EpisodiosEmitidos(totalConocido)`, usada por la Galería, la
  Ficha y el alta de animes. Si AniList no da próximo episodio ni total, se conserva el que ya tenías. La copia de
  `MediaEnrichmentService` calculaba el valor y no lo usaba: se quitó.
- **Selección múltiple (1.2 y 1.3):** "Mover a" solo cambia los animes marcados que se ven, la barra dice cuántos son
  ("3 seleccionados") y al salir del modo no queda nada marcado a escondidas. Con cuenta conectada, el estado se envía a
  AniList (`GuardarEstadoSeguimientoAsync`: solo el estado; nota, progreso y fechas no se tocan). Si AniList no responde:
  los animes que tienen copia local de tu seguimiento quedan pendientes y los envía la sincronización; los que no la
  tienen quedan solo en este equipo y un aviso lo dice (no se inventa un seguimiento pendiente, porque al enviarse
  borraría tu nota en AniList).
- **"Actualizar biblioteca" ya no pisa un estado pendiente de enviar** (también protege los cambios hechos sin conexión
  desde la Ficha).
- **Eliminar con archivos (1.4):** `EliminacionAnime`, compartido por Galería y Ficha. Solo ofrece borrar la carpeta si
  existe y es exclusivamente de ese anime (no la raíz de una unidad, no una carpeta personal, no usada por otro anime ni
  con la de otro dentro). Si no lo es, conserva los archivos y avisa. El borrado va fuera del hilo de la interfaz y, si
  falla, sale un aviso. Sin carpeta ya no hay segunda pregunta.
- **Botón de actualizar (1.5):** `try/finally`; si algo falla, aviso y el botón queda libre.
- **De paso:** los siete métodos de filtro repetidos son ahora uno (`RefrescarFiltro`).
- **Pruebas:** 28 nuevas (`GaleriaAccionesTests`, `EliminacionAnimeTests`, `AniListMediaEpisodiosEmitidosTests`), vistas
  fallar antes del cambio. Suite: 2.386, todas en verde salvo `GaleriaPortadasOrdenTests`, que falla a ratos con la suite
  completa por tiempos (ya conocido) y pasa sola.
- **Barra de progreso de "Actualizar biblioteca":** ahora es indeterminada (es una sola consulta a AniList; la barra que
  "avanzaba" por anime saltaba de vacía a llena). Guardar las próximas emisiones una por una sigue pendiente (fase 3).
- **Benchmarks:** `AnimeLocalTracker.Benchmarks` no compilaba desde antes (a su base de datos de mentira le faltaban seis
  métodos de sesiones anteriores); arreglado.

### Fase 2 — hecha (2026-10-02)

- **Filtros al día (1.6):** al marcarse un episodio como visto, la lista se vuelve a filtrar y ordenar (el aviso ya no
  toca la colección fuera del hilo de la interfaz); al volver a la Galería con algún filtro u orden activo, también.
- **Estado de carga:** `EstaCargando` con indicador giratorio; "biblioteca vacía" solo sale cuando de verdad lo está.
- **Sin resultados:** botón "Limpiar filtros" a la vista.
- **Buscador:** no distingue tildes ("pokemon" encuentra "Pokémon").
- **Abandonar partida (1.11):** botón bajo las opciones de los tres juegos; la partida abandonada no se guarda.
- **Enter tras responder con el ratón (1.10):** al responder, el foco vuelve a la vista del juego.
- **Clip que no suena (1.8):** `ClipPlayer` avisa del fallo (`ReproduccionFallida`), el altavoz se apaga y sale un texto
  con qué hacer. El mensaje de error de OP/ED ya se traduce al cambiar de idioma.
- **"Adivina el personaje" sin internet (1.9):** al primer lote que AniList no responde, deja de consultar y sigue con lo
  guardado.
- **Pruebas:** 7 nuevas, vistas fallar antes. Suite: 2.393, todas en verde.
- **Visto en la app real** (instancia de prueba, 1366×768, `settings.json` idéntico y copias de seguridad restauradas):
  contador "2 seleccionados" en la barra y barra oculta al salir del modo; "sin resultados" con su botón; partida de
  "Adivina el anime" con "Abandonar partida", que vuelve a la presentación.
- **No comprobado en la app:** Enter tras clic, el aviso de audio fallido, el indicador de carga al abrir (dura décimas),
  que la lista conserve el desplazamiento al refrescarse sola, y el juego de personajes sin internet.
- **Descargas del juego de OP/ED (1.7, decidido: caché temporal):** los clips nuevos van a la caché de vistas previas
  (`PrepararVistaPreviaAsync`), que se vacía al abrir la app; ya no dejan mp3 en tu carpeta de música ni entradas en la
  pestaña Descargas. Los temas que tú descargaste desde la Ficha se siguen usando primero.
- **Fuera de esta fase:** el volumen del clip y los retoques de las opciones (fase 4); el filtro de temporada que se
  pierde al cambiar de idioma (fase 5, va con "texto traducido usado como valor"); las descargas del juego de OP/ED
  (1.7), que esperan tu decisión.

### Fase 3 — hecha (2026-10-02); la lectura inicial resultó no ser el cuello de botella

- **Medido antes de optimizar** (cronómetro nuevo en `PrecargaBiblioteca`, línea `[Perf] Lectura adelantada`, 4 aperturas):
  leer los 229 animes cuesta 70-100 ms, contar los episodios vistos 7-11 ms y las 24 primeras portadas 240-290 ms. Todo
  termina unos 400 ms después de empezar, mucho antes de que la ventana se muestre (≈1,8 s). **La hipótesis de la sección
  2 era incorrecta:** el "leídos en 1,1 s" que anota la Galería no es base de datos, es la espera a que el hilo de la
  interfaz termine de construir la ventana. Cambiar la lectura no acelera el arranque.
- **Recuento en la base de datos:** `ObtenerEpisodiosVistosPorAnimeAsync` (`COUNT ... GROUP BY`) sustituye a traer los
  3.412 registros de episodios. No se nota en tiempo (ver arriba); sí deja de cargar esas filas en memoria en cada apertura.
- **Biblioteca compartida:** los tres juegos usan la lista que la Galería ya tiene (`MinijuegosViewModel.UsarBiblioteca`);
  solo leen la base de datos si esa lista aún está vacía. Se acaban las relecturas al entrar a un juego, volver al inicio
  o volver a la pestaña.
- **Próximas emisiones:** "Actualizar biblioteca" las guarda en una transacción (`GuardarProximasEmisionesAsync`) en vez
  de una escritura por anime.
- **No hecho, a propósito:** los récords siguen leyendo todas las partidas (son decenas de filas; se revisa si llegan a
  miles). Las portadas se siguen decodificando todas al abrir y la tarjeta no se tocó: no hay medición que diga que el
  desplazamiento vaya mal, y la sesión anterior ya midió que quitar piezas de la tarjeta no cambia el primer dibujado.
- **Qué queda del arranque:** ≈0,75 s entre "ventana construida" y "ventana mostrada" y ≈0,6 s dibujando las primeras
  tarjetas. Es coste de WPF ya estudiado en `investigacion-rendimiento-navegacion-arranque.md`.
- **Pruebas:** 5 nuevas y 4 adaptadas. Suite: 2.398, todas en verde. Benchmarks compila.

### Fase 4, primera parte — hecha (2026-10-02): etiqueta de la tarjeta y órdenes "recientes"

- **Etiqueta de la tarjeta (decidido: tu estado):** dice Viendo / Completado / Planeando / En pausa / Abandonado / Viendo
  de nuevo, con colores de la paleta (`Brush.Accent`, `Success`, `WarningStrong`, `Danger`, gris para Planeando). Se
  actualiza sola al cambiar el estado. *Visto en la app real.*
- **"Más recientes" (decidido: las dos cosas):** la opción pasa a llamarse **"Estreno más reciente"** y ordena de verdad
  por año y temporada de estreno (antes, por el número de AniList). Opción nueva **"Añadidos recientemente"**: usa la
  fecha de alta, que se guarda desde ahora al añadir un anime (`FechaAgregadoUtc`, migración v19). Los animes que ya
  estaban no tienen fecha y quedan al final de ese orden. *El desplegable no se abrió en la app; cubierto por pruebas.*
- **Pruebas:** 12 nuevas. Suite: 2.410; en la corrida completa falló una vez `AgregarAnimeViewModelTests.
  EjecutarBusquedaEnVivo_SiLaApiFalla…` (de tiempos, en un archivo no tocado) y pasó 3 de 3 veces sola.

### Fase 4, segunda parte — hecha (2026-10-02)

- **Filtros de estado:** se añaden "En pausa" y "Abandonados"; "Viendo" incluye también lo que estás viendo de nuevo.
  *Visto en la app: los seis caben en una fila a 1366 px.*
- **Barra de selección múltiple:** "Seleccionar todos" (los que se ven), destinos "En pausa" y "Abandonados", y los
  colores de cada destino iguales a los de la etiqueta de la tarjeta. *Visto en la app: "229 seleccionados".*
- **Favorito a la vista:** el corazón de la tarjeta es ahora un botón; se ve siempre en los favoritos y, en los demás, al
  pasar el ratón. El clic derecho sigue funcionando.
- **Colores de la Galería:** 20 literales que ya existían idénticos en la paleta pasan a `Brush.*` (sin cambiar ningún
  valor), más los de la barra de selección. Quedan 19 sin equivalente exacto (degradados, velos y el menú contextual).
- **Minijuegos, "Solo animes que he visto" (decidido: opción, apagada):** interruptor en el menú de juegos; se recuerda
  entre sesiones (`MinijuegosSoloVistos`). Visto = con algún episodio visto o en cualquier estado que no sea "Planeando".
  *Visto en la app el interruptor; su efecto, solo en pruebas.*
- **Juego de OP/ED:** control de volumen (el mismo ajuste que la música de la Ficha) y barra de avance del clip.
- **Resumen de la partida:** lista "Ronda a ronda" con la respuesta de cada ronda, si se acertó y sus puntos.
- **Pruebas:** 9 nuevas. Suite: 2.419, todas en verde.
- **No comprobado en la app:** el corazón al pasar el ratón, el volumen y la barra de avance del clip, y la lista del
  resumen (hacen falta una partida completa y sonido).
- **Queda de la fase 4:** el título completo al pasar el ratón por una tarjeta cortada, el teclado en las tarjetas, y el
  solape posible entre la barra de selección y el panel de sincronización.

### Fase 5 — hecha (2026-10-02): estructura

- **Juegos:** lo que repetían los tres pasa a `MinijuegoViewModelBase`: responder (y atajos 1-4), siguiente, volver al
  inicio/abandonar, texto del resultado, pistas, puntos posibles, mensaje de error, título/descripción/icono. Cada juego
  solo aporta lo suyo (`IndiceCorrectoRonda`, `RespuestaRonda`, `PuntosSiAcierta`, `AlResponder`, `AvanzarRondaAsync`).
- **Vistas de los juegos:** `MinijuegoInicioView` (cargando / sin animes / presentación con "Jugar") y `MinijuegoRondaView`
  (ronda y puntos, pistas, pedir pista, opciones, resultado, atajos, abandonar; sustituye a `MinijuegoOpcionesView`). Cada
  vista de juego queda en su material propio (portada, audio, imagen pixelada).
- **Menú de minijuegos por lista:** `MinijuegosViewModel.Juegos` y `AbrirJuegoCommand`; una sola plantilla de tarjeta.
  Añadir un juego: su ViewModel, su vista con plantilla, registrarlo y sumarlo a la lista.
- **"Qué veo hoy":** `QueVeoHoyViewModel` (antes, un archivo parcial de `GaleriaViewModel` con sufijos `QueVer`) y
  `QueVeoHoyPanel` con la animación de la ruleta. La Galería le presta su biblioteca.
- **Vista de la Galería:** 1.264 → 469 líneas. Fuera: la barra de filtros (`GaleriaFiltrosView`), la tarjeta y su menú
  contextual (`GaleriaTarjeta.xaml`, recurso `TarjetaAnime`) y el panel de "Qué veo hoy". Se quitó el guardado manual del
  desplazamiento (sobraba desde que la pestaña se conserva).
- **Texto traducido usado como valor:** el filtro de temporada guarda el código de AniList y la vista lo traduce
  (`TemporadaTextoConverter`); cambiar de idioma ya no lo borra. *El mismo problema sigue en el filtro de temporada de
  "Agregar anime", fuera del alcance de esta investigación.*
- **Pruebas:** 1 nueva; las de "Qué veo hoy" apuntan al ViewModel nuevo. Suite: 2.420, todas en verde.
- **Visto en la app real:** Galería (tarjetas, filtros de estado, sin resultados y "Limpiar filtros"), "Qué veo hoy" con su
  ruleta, menú de juegos con los tres juegos, pantalla de inicio de un juego, partida con 4 opciones y "Abandonar
  partida", y el selector de temporada con los nombres traducidos. `settings.json` idéntico y copias de seguridad
  restauradas. *No visto:* una partida completa hasta el resumen, y los juegos de OP/ED y de personajes (necesitan red).
- **Se dejó como estaba:** la sección Minijuegos sigue como archivo parcial de `GaleriaViewModel` (85 líneas, solo decide
  qué sección de la página se ve).

## Decisiones que son tuyas

1. **Descargas del juego de OP/ED (1.7):** ¿siguen yendo a tu carpeta de música y a Descargas, o a una caché temporal que
   no ensucia tu biblioteca? Recomendado: caché temporal.
2. **Selección múltiple (1.2):** ¿envía el cambio a AniList como la Ficha? Recomendado: sí.
3. **Etiqueta de la tarjeta:** ¿tu estado (Viendo, Planeando) en lugar de, o además de, "En emisión / Finalizado"?
4. **"Más recientes":** ¿guardar la fecha en que añades cada anime (migración nueva; los actuales quedarían sin fecha) o
   renombrar la opción?
5. **Minijuegos solo con lo que has visto:** ¿activado por defecto o como opción?

## Lo que no se pudo comprobar

- Nada se probó en la app real: los puntos marcados *por comprobar* (1.9, 1.10, el instante de "biblioteca vacía", el
  teclado en las tarjetas, la cabecera y el menú de juegos en ventana estrecha) salen de leer el código.
- No se corrió la suite de pruebas.
- No está medido cuánto de los 1,1 s de lectura inicial es base de datos, ni la fluidez del desplazamiento de la Galería.
