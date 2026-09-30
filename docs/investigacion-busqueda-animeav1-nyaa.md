# Investigación: precisión de la búsqueda de episodios en AnimeAv1 y Nyaa (2026-09-29)

Objetivo: que al pedir un episodio la app encuentre **el anime correcto, la temporada correcta y el episodio correcto**, con el menor número de peticiones posible.

Método: lectura del código, revisión de `app.log` real, consultas en vivo a animeav1.com y nyaa.si, y un arnés temporal que pasó 12 animes reales de la biblioteca por el código de verdad (antes y después), contando peticiones, tiempo y si la página aceptada era la correcta (MAL ID). Los casos incluyen temporadas 2/3, partes, arcos con nombre, One Piece (1100+ episodios), títulos con guiones y un anime que el sitio no tiene.

## Resultado medido (mismos 12 animes, sitios reales)

| | Antes | Después |
|---|---|---|
| AnimeAv1: encontrados correctamente | 10/12 | **11/12** (el 12.º no está en el sitio) |
| AnimeAv1 sin MAL ID (AniList caído): página equivocada | 1 (Re:Zero S2 parte 1 por parte 2) | **0** |
| AnimeAv1: peticiones por episodio encontrado | 7–37 | **6–20** |
| AnimeAv1: tiempo típico | 1,0–3,0 s | **0,9–2,0 s** |
| AnimeAv1: anime que no está (Isekai no Yu) | 53 peticiones, 4,4 s | **27 peticiones, 2,5 s** |
| Nyaa: temporada o anime equivocado | **4/12** | **0/12** |
| Nyaa: episodio suelto encontrado (en vez de pack) | 5/12 | **11/12** |
| Nyaa: tiempo | 0,4–0,7 s (5 s One Piece) | 1,8–2,7 s (4–5 consultas en paralelo) |

## Hallazgos — Nyaa (los más graves)

1. **Sin verificación de identidad.** Se aceptaba el primer release con el número de episodio pedido, de cualquier anime o temporada. Reproducido en vivo:
   - *Iruma-kun 2* ep 5 → pack de la **temporada 4**.
   - *Shingeki no Kyojin Season 3* ep 2 → **The Final Season Part 3** ep 2.
   - *Mushoku Tensei II* ep 13 → **Mushoku Tensei III** ep 13.
   - *One Piece* → packs de **otros animes** (*Mistress Kanan*, *Sayonara Lara*) por buscar el sinónimo "OP".
2. **Tope de 75 resultados del RSS**: episodios antiguos (One Piece 1100, temporadas pasadas) no aparecían; se caía a packs.
3. **Packs sin comprobar el rango**: un pack `(01-13)` se elegía para el episodio 99; los packs de varias temporadas (`S1+S2+S3`) también.
4. **Dentro de un pack, si ningún archivo se reconocía como el episodio, se descargaba el más grande** (otro episodio guardado con el número pedido).
5. Términos con guion inicial (`Re:ZERO -Starting Life…`): Nyaa interpreta `-palabra` como **excluir**, 0 resultados.
6. `- 12.5` (recopilatorio) contaba como episodio 12; `01 ~ 12` se leía como episodio 1.
7. Doblajes solo en inglés elegidos antes que releases con audio japonés.

## Hallazgos — AnimeAv1

1. **Los ~47 géneros del catálogo** (`accion`, `isekai`, `ona`…) se extraían como animes candidatos y se visitaban (404 seguros que gastaban el presupuesto de 40 páginas).
2. **Términos basura** por partir en guiones pegados: `Kusunoki-tei` → `tei`, `Kaitaku-ki` → `ki` (visto en `app.log`).
3. **Títulos en japonés/tailandés/ruso**: el buscador del sitio no los entiende y devuelve su listado por defecto (comprobado en vivo): solo ruido.
4. **rapidfuzz**: distingue mayúsculas (`BLACK TORCH` vs `Black Torch` = 27 %) y, al rechazar, respondía `success:false`, que la app leía como "daemon no disponible" y decidía con otro criterio más permisivo. Además no distingue temporadas (*Mushoku Tensei II* vs *III* = 98,9 %).
5. **Episodio equivocado en series recién estrenadas**: si el sitio solo tenía el episodio 1, pedir el 2 descargaba el 1 guardado como "Episodio 02" (atajo pensado para películas).
6. **Partes que el sitio junta**: Re:Zero 2nd Season Part 2 no existe como página propia; está dentro de "2nd Season" (25 episodios). Antes "no existía" (o, sin MAL ID, se descargaba el ep 3 de la parte 1).
7. **Orquestador**: 3 "no encontrado" seguidos (episodios aún no publicados) dejaban a AnimeAV1 —el único proveedor— en pausa 5 minutos; las descargas que sí existían fallaban al instante.
8. Un fallo pasajero (503, corte) en la página correcta daba el episodio por inexistente (sin reintento).
9. El MAL ID se pedía siempre a AniList aunque la biblioteca ya lo tiene (221/224 animes).

## Qué se cambió

- **`Core/FirmaTitulo`** (nuevo): separa nombre de serie y temporada/parte (`2nd Season`, `S2`, `III`, `Part 2`, `Final Season`, `第3期`, número suelto final con doble lectura). Lo usan Nyaa y AnimeAv1.
- **Nyaa**: `AnalizarRelease` (nombre + alternativos, temporada, episodio, rango, varias temporadas, solo doblaje); cada release se verifica contra todos los títulos, temporada incluida. Consultas `nombre + episodio` y `nombre` (y el título con temporada si la lleva), en paralelo, solo títulos latinos sin siglas cortas. El selector manual muestra también los no confirmados con el aviso **"NOMBRE DISTINTO"**; la descarga automática nunca los usa.
- **Torrent**: en un pack solo se descarga el archivo reconocido como el episodio (o el único video del torrent).
- **AnimeAv1**: resultados del catálogo con su título (sin géneros); candidatos ordenados por parecido con temporada; búsqueda en dos tandas (términos fiables primero); tope de 6 páginas sin parecido; veredicto MAL → desfase por precuela → nombres con control de temporada; reintento único ante fallos pasajeros; episodio-1-de-película solo para el episodio 1.
- **Precuelas** (`Services/PrecuelasAnime`): de las relaciones de AniList ya guardadas + biblioteca, sin red.
- **App.xaml.cs**: MAL ID local primero; rapidfuzz en minúsculas y con umbral 0 (siempre devuelve su puntuación).
- **Orquestador**: "no encontrado" ya no degrada; si todos están en pausa, se prueban igual.

Tests: 66 nuevos (casos reales de ambos sitios); suite 2193/2193.

## Segunda ronda (2026-09-30): películas, partes y numeración continua

Prueba real del usuario: *Re:Zero 2nd Season Part 2* ep 1 acabó en Nyaa. Causa (en `app.log`): la página correcta nunca se revisó; el presupuesto de 40 páginas se gastó en *Re:Monster*, *Tokyo Ghoul:re*, *Yowamushi Pedal*… porque el orden priorizaba "misma temporada" aunque el parecido fuera del 33 % (los títulos chino/coreano "Re:…" inflaban a cualquier anime que empezara por "Re"), y 8 slugs deducidos daban 404.

- **Orden de candidatos**: puntuación = máx(misma parte, 0,9 × misma serie otra parte), comparando solo con títulos latinos. Re:Zero S2P2 ep 1 → página de 2nd Season, **episodio 14**, en 14 peticiones.
- **Slugs deducidos**: uno por título (dos como mucho), al estilo del sitio (`rezero-…`); se eliminaron las variaciones -2/-ii/-2nd-season (404 seguros, alguna deformada).
- **Películas**: "Película 14 / Movie 2 / Film / Gekijouban / 劇場版" es un marcador, no parte del nombre. Solo se tolera que el SITIO diga "Película 14" y nosotros nada (nombre de 4+ palabras); al revés no ("One Piece Film 15" no es el pack "One Piece (01-900)"; "Iruma-kun Movie" no es la serie). Las relations (DBZ → sus películas) vuelven a adelantarse aunque ya estuvieran en la lista.
- **Número de episodio**: si la lista del sitio no empieza en 1, el episodio N es el N-ésimo (películas numeradas por franquicia: *Kami to Kami* = 14; partes con numeración 13–24).
- **Nyaa, numeración continua**: con las precuelas (MAL, episodios y títulos, de la biblioteca), un release de una parte anterior vale si su número es exactamente N + episodios de las partes intermedias: `S02E14 [Episode-39]` y `Re Zero … - 39` = ep 1 de S2 Part 2. Se añaden consultas con esos números.
- **Nyaa, alternativos mal etiquetados**: un nombre entre paréntesis que dice otra temporada que la del release se ignora (VARYG: `… S03E01 (Re:Zero … 2nd Season Part 2)`).
- **Nyaa, películas**: para el episodio 1 se aceptan releases del anime sin número ni rango ("Dragon Ball Z - Battle of Gods (Directors Cut)").

Medido en vivo: 12 películas de la biblioteca (One Piece 04/09/Strong World/Z/Gold/Red, DBZ 01/05/12/14, Broly, Super Hero) → AnimeAv1 **12/12**; Nyaa encuentra la película correcta cuando existe con nombre reconocible (8/12) y ya no packs de la serie. Regresión de temporadas: 11/11. Suite 2214/2214.

## Pendiente / no hecho

- Nyaa solo busca en la categoría "English-translated" (1_2).
- El catálogo de AnimeAv1 solo se lee en su primera página (20 resultados por término).
- La consulta `match-title` del daemon Python sigue distinguiendo mayúsculas (no la usa la búsqueda de episodios).
- Nyaa no reconoce la numeración continua si la parte anterior no está en la biblioteca (sin su total de episodios no se puede calcular).
- No verificado en la app real: el aviso "NOMBRE DISTINTO" del selector (solo aparece con releases no confirmados) ni una descarga de extremo a extremo (escribiría en la biblioteca del usuario).
