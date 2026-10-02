# Investigación: fluidez al abrir la app y al moverse entre pestañas (2026-10-01)

**Objetivo:** que abrir la app y pasar de una pestaña a otra no tenga esperas.

**Método:** primero medir, después cambiar. Se añadió a la app un medidor que deja en el registro (líneas `[Perf]`) cuánto
tarda cada paso del arranque y cada cambio de pestaña; se lanzó la app real (build de desarrollo, la que se usa a diario,
con la biblioteca real: 227 animes y 3 409 episodios) y se recorrieron las pestañas de forma automática antes y después de
cada cambio. Equipo: i5-7300U (2 núcleos, 4 hilos), gráfica integrada Intel HD 620, SSD NVMe, pantalla 1366×768,
antivirus de Windows activo.

---

## 1. Resultado en una tabla

| Qué | Antes | Ahora |
|---|---|---|
| Cambiar a una pestaña ya visitada | 105 – 440 ms (siempre, en cada visita) | **3 – 20 ms** |
| Primera visita a Configuración | 400 – 640 ms | **3 – 7 ms** (ya está preparada) |
| Primera visita a Historial, Actualizaciones, Calendario, Estadísticas, Logros, Descargas, Agregar | 105 – 270 ms | **8 – 45 ms** |
| Primera visita a Acerca de | 990 ms | 280 ms (no se prepara por adelantado; las siguientes, 3 ms) |
| Volver a la Galería desde otra pestaña o desde una ficha | 300 – 440 ms | **5 – 35 ms**, y en el mismo punto de desplazamiento |
| Abrir la ficha de un anime (hasta verla pintada) | 330 – 550 ms | **75 – 160 ms** (2.ª ronda, sección 9) |
| Abrir Registros desde Configuración | 260 – 540 ms | **37 – 50 ms** (la primera vez, 177 ms) |
| Galería: pasar de Biblioteca a Minijuegos | 69 – 105 ms | 37 – 68 ms |
| Abrir un minijuego desde su menú | 60 – 100 ms | **11 – 35 ms** |
| Ventana visible al abrir la app (equipo ya en uso) | 2,4 – 2,9 s | **1,8 – 1,9 s** |
| Galería con sus tarjetas y portadas en pantalla | ≈ 3,6 – 4,2 s (ver nota) | **2,4 – 2,6 s** |
| Primer arranque tras compilar (lo más parecido a "en frío") | ventana a los 9,2 s, biblioteca a los 13,6 s | ventana a los 2,1 s, tarjetas a los 2,7 s (ver nota) |

Notas sobre lo que **no** es una comparación limpia:

- "Galería con sus tarjetas" de antes es una suma: la lista llegaba a la pantalla a los 2,5 – 3,1 s (medido) y dibujar las
  tarjetas costaba ≈ 1,1 s más (medido en una versión intermedia, no en la original).
- El arranque "en frío" de antes (9,2 s) fue el primero de la sesión, con el disco sin nada en memoria; el de ahora fue
  justo después de recompilar pero con el equipo ya caliente. La mejora real en frío es grande (se quitaron del camino los
  2,5 s de servicios y la carga de 185 MB de bibliotecas) pero **el número exacto no está medido**: hace falta reiniciar el
  equipo y abrir la app una vez para verlo en el registro.

En la práctica: al pulsar Historial, Calendario o Configuración la pantalla cambia en el mismo fotograma; antes había una
pausa perceptible cada vez, incluso al volver a una pestaña que acababas de ver. Y al volver a la Galería desde la ficha de
un anime, la lista está exactamente donde la dejaste, sin reconstruirse.

---

## 2. Qué causaba las esperas

### 2.1 Cada cambio de pestaña reconstruía la pantalla entera

La ventana principal mostraba la pestaña con un `ContentControl`. Al cambiar de pestaña, WPF tiraba la vista anterior y
construía la nueva desde cero (todos sus botones, listas y estilos), aunque fuera una pestaña que ya habías abierto diez
veces. Por eso la segunda visita tardaba lo mismo que la primera:

| Pestaña | 1.ª visita | 2.ª visita |
|---|---|---|
| Historial | 177 ms | 266 ms |
| Actualizaciones | 162 ms | 196 ms |
| Agregar anime | 129 ms | 279 ms |
| Calendario | 154 ms | 270 ms |
| Estadísticas | 140 ms | 210 ms |
| Logros | 78 ms | 134 ms |
| Descargas | 105 ms | 211 ms |
| Configuración | 641 ms | 399 ms |
| Acerca de | 990 ms | 160 ms |
| Galería (volver) | 437 ms | 297 ms |

Ya estaba anotado como pendiente desde la corrección NAV-01 (que evitó repetir las consultas a la base de datos, pero no la
reconstrucción visual).

### 2.2 El arranque hacía en fila, y en el hilo de la interfaz, cosas que no hacen falta para ver la ventana

Arranque medido, con el equipo ya en uso (2,45 s hasta la ventana):

| Paso | Tiempo |
|---|---|
| Arranque de .NET | 0,22 s |
| Registro de servicios | 0,08 s |
| Estilos de `App.xaml` (Material Design) | 0,25 s |
| Datos antiguos, ajustes e idioma | 0,20 s |
| Base de datos | 0,04 s |
| Sincronización, avisos de emisión, estado de conexión, actualizaciones | 0,26 s |
| Construir la ventana (`MainViewModel` + XAML) | 0,49 s |
| Mostrarla (primera colocación y pintado) | 0,89 s |

En el primer arranque medido (disco frío) los mismos pasos costaron 1,5 s (.NET), 1,0 s (`App.xaml`), 1,0 s (base de
datos), **2,5 s (servicios de fondo)**, 0,9 s (ventana) y 1,8 s (mostrarla).

### 2.3 Al abrir, la app carga 207 MB de bibliotecas; 185 MB no hacen falta para la primera pantalla

- **FFmpeg, 160 MB** (`avcodec` 94 MB, `avfilter` 34 MB, `avformat` 22 MB…). El motor de video se iniciaba en el hilo de la
  interfaz en cuanto la ventana quedaba en reposo, es decir, a 1 s de aparecer y mientras aún cargaban las portadas. Con el
  equipo caliente son 0,29 s de ventana congelada; en frío, Windows tiene que leer esos 160 MB del disco (y el antivirus
  revisarlos) con la ventana ya visible pero sin responder. Es lo que explica que en el arranque en frío la biblioteca
  tardara 4,4 s más en aparecer después de la ventana.
- **Controles multimedia de Windows, 25 MB** (`Microsoft.Windows.SDK.NET.dll`), cargados antes del primer pintado.

### 2.4 Las tarjetas de la Galería se dibujaban compitiendo con la decodificación de las portadas

Dibujar las 16 tarjetas de la primera pantalla costaba **1,1 s**, de los cuales:

- ≈ 0,42 s eran contención: en ese mismo instante 3 hilos decodificaban las 227 portadas, en un procesador de 2 núcleos.
- ≈ 0,10 s era el menú de clic derecho: cada tarjeta creaba el suyo (con sus tres estilos y plantillas) aunque nunca se
  abriera.
- ≈ 0,35 s es coste fijo de la primera vez (preparar las plantillas de Material Design) y ≈ 16 ms por tarjeta. Se probó
  quitar la barra de progreso, el efecto de carga, la casilla de selección y las insignias: ninguna pieza por separado
  cambia el tiempo más allá del ruido de la medición (± 60 ms), así que no se tocó el diseño de la tarjeta.

### 2.5 Accesos al disco en el hilo de la interfaz al entrar en Historial, Actualizaciones y Calendario

`ObtenerAnimesLigerosAsync` comprobaba si existe la portada de cada anime (227 accesos al disco) en el hilo que lo llamaba,
que casi siempre es el de la interfaz. Historial añadía una comprobación más por cada fila (60).

### 2.6 Lo que se revisó y NO es un problema

- **La base de datos**: leer 227 animes y 3 409 episodios tarda milisegundos. Los "7 s leyendo la biblioteca" del primer
  arranque eran espera por el hilo de la interfaz, no la consulta.
- **El mando**: consultar el mando cuesta 0,02 ms. (Aun así se dejó de preguntar 60 veces por segundo cuando no hay ninguno
  conectado.)
- **La optimización guiada por perfil de .NET**: desactivarla no cambia el arranque (1,78 – 1,92 s frente a 1,79 – 1,81 s).

---

## 3. Qué se cambió

### Navegación

1. **Las pestañas se conservan** (`Controls/AnfitrionVistas.cs`, usado en `MainWindow.xaml`). La vista de cada pestaña se
   construye una vez y después solo se muestra u oculta. (En la primera ronda la ficha y el visor de registros seguían
   creándose en cada visita; desde la segunda ronda —sección 9— también se conservan.)
2. **Las pestañas se preparan por adelantado** (`MainWindow.PrecalentarPestanasAsync`): 4 s después de abrir, y solo
   mientras no estás usando el ratón ni el teclado ni hay un episodio abierto, se construyen de una en una sin mostrarlas
   (≈ 1,4 s de trabajo repartido en ≈ 5,5 s). También se construye una ficha de más, vacía, para que la primera que abras
   no pague la preparación de sus plantillas.
3. **Lo que dependía de que la vista se destruyera al salir** ahora reacciona a dejar de verse: el panel "Qué veo hoy" y el
   menú del usuario de la Galería se cierran, el clip de "Adivina el opening" deja de sonar, el menú de opciones de
   Descargas se cierra, y los minijuegos recuperan el foco al volver.

### Arranque

4. **Trabajo en paralelo desde el primer instante** (`App.PrepararDatosYServiciosAsync`): mientras el hilo de la interfaz
   carga los estilos, otro hilo migra datos antiguos, lee los ajustes, abre la base de datos y construye los servicios que
   la ventana va a pedir.
5. **Lectura adelantada de la biblioteca** (`Services/PrecargaBiblioteca.cs`): la lista de animes y las 24 portadas de la
   primera pantalla se leen mientras se construye la ventana. Las tarjetas salen ya con su portada (antes aparecían en gris
   y se rellenaban después) y el resto de portadas espera a que las tarjetas estén dibujadas, usando la mitad de los hilos
   del procesador en vez de todos menos uno.
6. **Primero la ventana, después lo demás** (`App.IniciarTrasPrimerPintado`): sincronización con AniList, avisos de
   emisión, estado de conexión, cola de descargas, comprobación de actualizaciones, avisos de episodios nuevos y motor
   Python arrancan cuando la ventana ya está pintada, y fuera del hilo de la interfaz. La copia de seguridad se hace 8 s
   después de abrir (mientras se hace, las demás consultas a la base de datos esperan).
7. **Motor de video sin congelar la ventana**: las bibliotecas de FFmpeg se cargan en otro hilo y el arranque del motor (que
   sí exige el hilo de la interfaz) se hace 3 s después, en reposo: 0,10 s en vez de 0,29 s. Si abres un episodio antes, el
   reproductor lo inicia por su cuenta, como siempre.
8. **Controles multimedia de Windows e icono de la bandeja** se inicializan después del primer pintado, con su biblioteca
   de 25 MB ya leída en otro hilo.
9. **Una segunda instancia** se detecta antes de empezar a preparar nada (comprobado: se cierra sola, no abre la base de
   datos ni crea un registro de sesión).

### Otros

10. Un solo menú de clic derecho para todas las tarjetas de la Galería (comprobado: muestra "Marcar como favorito" /
    "Eliminar de la biblioteca" del anime sobre el que se pulsa).
11. Las comprobaciones de disco de Historial, Actualizaciones y Calendario pasan a segundo plano.
12. El mando se consulta una vez por segundo cuando no hay ninguno conectado (y 60 veces por segundo en cuanto se conecta).

---

## 4. Lo que cuesta

- **Memoria:** tras recorrer todas las pestañas, la app usa ≈ 395 MB de memoria privada frente a ≈ 342 MB antes
  (≈ +50 MB), porque ahora las vistas siguen vivas. Recién abierta la diferencia es de pocos MB.
- **Una consulta más a AniList al abrir:** preparar el Calendario por adelantado lanza su carga (antes solo al visitarlo).
- **Estado que ahora se conserva:** Configuración se queda en la sección y posición donde la dejaste, y las listas
  conservan su desplazamiento. La animación de entrada de las páginas solo se ve la primera vez que se abre cada pestaña.

---

## 5. Lo que queda por hacer (no se tocó)

| Qué | Por qué no se hizo | Ganancia estimada |
|---|---|---|
| **Usar a diario la versión publicada** en lugar de la de desarrollo | Es una decisión de cómo se usa la app; la instalada es la 1.0.5 del 24-sep (el pipeline de release sigue bloqueado sin firma) | La publicación precompilada (ReadyToRun) midió −16 % al abrir en la auditoría del 25-sep |
| **Imagen de bienvenida** al abrir | Es un cambio visible de diseño | No acelera nada, pero en un arranque en frío se vería algo al segundo y medio en vez de una pantalla vacía |
| **Excluir la carpeta de la app del antivirus** | Es un ajuste de seguridad del equipo, no de la app | Afecta sobre todo al primer arranque tras reiniciar o recompilar |
| **Medir el arranque en frío real** | Requiere reiniciar el equipo | — |
| **Coste fijo de la primera pantalla de tarjetas** (≈ 0,35 s) | Es preparación de plantillas de Material Design; solo baja con la versión precompilada | — |

---

## 9. Segunda ronda (2026-10-02): la ficha y las pestañas internas

Pedido: reutilizar la vista de la ficha y aplicar lo mismo a las pestañas que viven dentro de otras (Minijuegos en la
Galería, Registros en Configuración, la ficha desde la Galería).

| Cambio interno | Antes de esta ronda | Ahora |
|---|---|---|
| Galería → ficha, hasta verla pintada | 264 – 295 ms (330 – 550 ms antes de la primera ronda) | 75 – 160 ms |
| Ficha → Galería | 28 – 34 ms | 6 – 11 ms |
| Configuración → Registros | 259 – 541 ms | 37 – 50 ms (primera vez 177 ms) |
| Registros → Configuración | 42 – 77 ms | 12 – 36 ms |
| Biblioteca → Minijuegos | 69 – 105 ms | 37 – 68 ms |
| Menú de minijuegos → un juego | 60 – 100 ms | 11 – 35 ms |
| Juego → menú, Minijuegos → Biblioteca | 18 – 53 ms | 13 – 52 ms (ya eran rápidos) |

Qué se hizo:

- **Ficha:** hay una sola vista de ficha, que pasa de un anime al siguiente (`IVistaReutilizable`; sustituye a
  `IVistaSinConservar`, que ya no existe). Al cambiar de anime la vista se deja como recién abierta: menús de avisos,
  marcar y herramientas cerrados, caja "Ir al ep." vacía y las dos columnas arriba. Al salir de la ficha se para el contador
  y la música igual que antes (salvo que la música esté puesta para seguir de fondo). Si lo que se oculta es la ventana
  entera (bandeja), la ficha no se toca. La vista se deja construida por adelantado con una ficha vacía.
- **Registros:** la vista se conserva y se prepara por adelantado. Al volver con la sesión actual abierta solo se añaden las
  líneas nuevas arriba de la lista, en vez de releer el archivo y redibujar todas las filas; eso mismo quita el pequeño
  tirón que daba la actualización en vivo cada 3 s. Con otro archivo elegido (o al pulsar Actualizar) se relee entero.
- **Minijuegos:** la sección y cada juego se construyen una vez (`AnfitrionVistas` también dentro de `MinijuegosView`) y se
  preparan por adelantado, un juego por paso. El clip de "Adivina el OP/ED" se sigue cortando al salir.
- **Medidor:** nuevas líneas `[Perf] Galería: Biblioteca → Minijuegos`, `[Perf] Minijuegos: menú → …`.

Comprobado en la app real: cinco fichas seguidas (una repetida) muestran el anime correcto y empiezan arriba; minijuegos
(menú y los tres juegos) y registros abren y vuelven bien; suite completa en verde (2 358 pruebas). **No comprobado en
vivo:** una ficha con la ventana de música o el editor de seguimiento abiertos al cambiar de anime, la música que sigue de
fondo al salir de la ficha, y una partida de minijuego en curso al cambiar de sección.

Memoria tras recorrerlo todo: ≈ 410 – 420 MB privados (≈ 395 MB tras la primera ronda).

---

## 6. Cómo volver a medir

El medidor se queda en la app. En el registro de cada sesión (`%LocalAppData%\AnimeLocalTrackerData\Logs\sesiones`):

- `[Perf] Arranque: …` — cada paso del arranque, en ms desde que Windows creó el proceso.
- `[Perf] Biblioteca: …` — cuándo llegó la lista a la pantalla y cuánto tardaron en dibujarse las tarjetas.
- `[Perf] Portadas: …`, `[Perf] Motor de video iniciado en …`, `[Perf] Pestañas preparadas por adelantado: …`.
- `[Perf] Navegación A → B: colocada a los X ms, interfaz libre a los Y ms` — cada cambio de pestaña. Por debajo de 250 ms
  solo se escribe con "Registro detallado" activado; por encima, siempre.

---

## 7. Verificación

- Compilación sin advertencias ni errores.
- Suite completa en verde, con 12 pruebas nuevas (`AnfitrionVistasTests`, `PrecargaBibliotecaTests`).
- En la app real: recorrido de todas las pestañas con capturas (Galería, Historial, Actualizaciones, Calendario,
  Estadísticas, Logros, Descargas, Configuración), dos fichas abiertas y cerradas, menú de clic derecho de la Galería,
  segunda instancia y cierre (el motor Python no queda huérfano).
- **No comprobado en vivo:** el arranque con `--bandeja` (inicio con Windows), los minijuegos al cambiar de pestaña a mitad
  de partida, navegar con el mini reproductor abierto y el arranque en frío tras reiniciar.

## 8. Efectos en tu equipo durante las pruebas

La app se abrió y cerró unas 30 veces con tu biblioteca real. Se abrieron dos fichas (Arifureta y Black Clover) sin
reproducir nada ni marcar nada. `settings.json` se comparó antes y después de cada tanda: idéntico. Con cada apertura la
app hizo lo que hace siempre (comprobar conexión, consultar el calendario de AniList, rotar la copia de seguridad).

**Copias de seguridad:** la app guarda las 5 últimas y hace una en cada apertura, así que tras tantas aperturas las 5
(`Backups\biblioteca.backup.1-5.db`) son de esta noche (2026-10-02, 00:02 – 00:03) y todas iguales a la biblioteca actual:
las de días anteriores ya no están. Las 5 están completas (827 KB cada una) y la biblioteca no se modificó.
