# "Parecidos en tu biblioteca" (recomendaciones locales): diseño y plan

Fecha: 2026-10-17. Cuarta función de `investigacion-integraciones-y-clips.md`.

## Objetivo

En la ficha de un anime, una sección "Parecidos en tu biblioteca" con hasta 5 animes **pendientes** de la biblioteca del usuario que se le parecen, con el porqué (temas compartidos) y si tienen episodios sin ver en el disco. Todo se calcula en local; a AniList solo se le piden datos públicos del anime (sus tags), nunca datos del usuario.

Ejemplo: en Steins;Gate → **Erased** ("Time Manipulation · Thriller · Misterio", "4 en disco") y **Re:Zero** ("Time Manipulation · Psychological · Drama"). Un clic abre su ficha.

## Decisiones tomadas

- Ubicación: solo la ficha, columna izquierda bajo los botones. No en la Galería ni en "¿Qué veo hoy?".
- Candidatos: solo pendientes = ni COMPLETED ni DROPPED en `EstadoUsuario`, y sin todos los episodios vistos (`TotalEpisodios > 0 && vistos >= TotalEpisodios` lo excluye). Nunca el propio anime.
- Enfoque: tags de AniList con su porcentaje + géneros + TF-IDF de la sinopsis, similitud coseno en C#. Descartados: solo géneros + sinopsis (demasiado general y ruidoso) y embeddings ONNX (~150 MB).
- Tags en inglés tal como los da AniList (como los títulos: no hay traducción oficial de sus ~400 tags); géneros traducidos con `TraducirGenero` al pintar.
- Se calcula en segundo plano después de abrir la ficha (no retrasa su apertura); si no hay ninguno por encima del umbral, la sección no aparece.

## Datos

- La consulta por lotes de `ObtenerAnimesPorIdsLoteAsync` (la de la actualización al arrancar) pide además `tags { name rank isMediaSpoiler isGeneralSpoiler }` → `AniListMedia.Tags` (`List<AniListTag>?`).
- `AnimeItem.Etiquetas` (string, JSON `[{"n":"Time Manipulation","r":95}]`: un tag con ":" o "|" no rompe nada); solo `rank >= 50`, sin spoilers, hasta 15, de mayor a menor rank. Migración **v24** (`CreateTableAsync<AnimeItem>`, como v21): las filas existentes quedan NULL hasta la próxima actualización.
- `GaleriaViewModel.AplicarDatosDeAniList` copia las etiquetas si la respuesta trae el campo y cambian (y marca el anime como modificado para el guardado por lotes que ya existe).
- La consulta automática del arranque solo pedía los animes con datos incompletos (en la prueba en vivo, 23 de 258 recibieron tags): `NecesitaDatosDeAniList` incluye también los que tienen `Etiquetas == null`. La primera vez se piden todos (≈ 6 consultas de 50); con `"[]"` guardado no se repiten.
- `DatabaseService` conserva las etiquetas guardadas cuando se guarda una copia del anime sin ellas (lecturas "ligeras", JSON anterior a v24), como ya hacía con "Conservar los videos".
- Exportación/importación JSON: el campo va con el resto del modelo (no requiere cambios).

## Cálculo (`Services/Recomendaciones/SimilitudAnimes.cs`, puro)

- Tres vectores por anime, cada uno normalizado (L2) y luego combinados con peso: tags (`rank/100`) × 1,0; géneros (1 por género) × 0,5; sinopsis TF-IDF × 0,35. La sinopsis se limpia de HTML (`SinopsisLimpia`), se pasa a minúsculas, palabras de ≥ 4 letras sin palabras vacías del inglés; IDF sobre la biblioteca.
- Similitud coseno del vector combinado. Umbral 0,15; hasta 5, de mayor a menor.
- Temas compartidos (hasta 3): primero tags comunes ordenados por el menor de los dos ranks, después géneros comunes (traducidos).
- `EtiquetasAnime.Serializar(IEnumerable<AniListTag>)` / `EtiquetasAnime.Leer(string?)`.

## Servicio (`Services/Recomendaciones/ParecidosService.cs`)

- `Task<IReadOnlyList<Parecido>> BuscarAsync(int aniListId, CancellationToken ct)`; `record Parecido(AnimeItem Anime, double Puntuacion, IReadOnlyList<string> Temas, int EnDisco)`.
- Lee `ObtenerTodosLosAnimesAsync`, `ObtenerEpisodiosVistosPorAnimeAsync` y `ObtenerTodosLosRegistrosAsync` (EnDisco = episodios con archivo y sin ver) en `Task.Run`; nunca lanza (lista vacía y registro).
- Sin caché: con ~260 animes son milisegundos, en segundo plano.

## Ficha

- `DetalleViewModel`: `ObservableCollection<Parecido> Parecidos`, `bool HayParecidos`; `_ = CargarParecidosAsync(ctFicha)` junto a las demás cargas de fondo; se vacía al abrir otra ficha; comando `AbrirParecido(Parecido)` → `NavegarMensaje_Detalle(p.Anime)`.
- `DetalleView.xaml`, columna izquierda bajo la barra de acciones: título `Det_ParecidosTitulo` y hasta 5 filas (miniportada 44×62 con placeholder debajo, título en 2 líneas, temas en `AppText.Caption`, distintivo `Det_ParecidosEnDiscoFormato` si EnDisco > 0). Fila pulsable con `Cursor="Hand"`, `ToolTip` y `AutomationProperties.Name`. Colores de la paleta.

## Pruebas

- `EtiquetasAnime`: filtra spoilers y < 50, ordena, máx. 15, ida y vuelta, NULL/vacío.
- `SimilitudAnimes`: Steins;Gate ↔ Erased (tag compartido) por encima de un slice of life; solo pendientes; nunca el propio; umbral (nada parecido → vacío); temas compartidos en orden; géneros traducidos; sin tags (base vieja) cae a géneros + sinopsis.
- Lectura de `tags` de la respuesta JSON de AniList (deserialización).
- `AplicarDatosDeAniList` copia las etiquetas (si es accesible; si no, a través del flujo de la Galería con mocks).
- Migración v24: base vieja sin la columna → la agrega, conserva filas; base nueva en la última versión.
- `ParecidosService` con base mockeada: EnDisco y vistos.
- `DetalleViewModel`: tras abrir, `Parecidos` con los resultados; `AbrirParecido` envía el mensaje.
- En vivo (perfil aislado **con red**, para que la actualización traiga los tags): `Migración aplicada: v24` en el registro, filas con etiquetas en la copia, y la sección en una ficha.

## Fuera de alcance

Galería, "¿Qué veo hoy?", recomendaciones de fuera de la biblioteca, traducir tags, caché del índice.

---

# Plan de implementación

Ejecución en esta sesión, sin subagentes ni commits salvo orden. Cada tarea: prueba que falla → código → verde.

## Restricciones globales
- Sin dependencias nuevas. Migración solo por la lista `Migraciones` (v24). Lecturas por `_lectura`.
- Nada de disco ni cálculo en el hilo de interfaz; la ficha no espera por los parecidos.
- Textos `Det_*` en ES y EN; colores de la paleta.

## Focos de revisión
1. Base vieja sin tags (NULL): el cálculo no falla y usa géneros + sinopsis (tarea 2).
2. Abrir otra ficha mientras se calcula: no se pintan parecidos de la anterior (cancelación, tarea 4).
3. Sinopsis vacía o con HTML (tarea 2).
4. Biblioteca de un solo anime o sin pendientes → sección oculta (tarea 2/4).
5. Tags con ":" o "|" en el nombre (serialización, tarea 1).

## Tarea 1: datos (tags, migración v24, volcado)
## Tarea 2: cálculo puro
## Tarea 3: servicio
## Tarea 4: ficha (ViewModel, vista, textos)
## Tarea 5: cierre (suite, verificación en vivo con red sobre copia de la base real)
