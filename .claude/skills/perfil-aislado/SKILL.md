---
name: perfil-aislado
description: Probar AnimeLocalTracker en vivo sin tocar los datos reales del usuario (biblioteca, ajustes, token y cuenta de AniList, videos). Crea un perfil temporal con una copia de la biblioteca, lanza la app con él y la cierra por PID. Usar SIEMPRE antes de lanzar AnimeLocalTracker.exe para verificar un cambio de UI, reproductor, descargas, borrados o migraciones de base de datos; nunca lanzar el exe sin perfil.
---

# Perfil aislado — probar la app sin tocar los datos reales

La app instalada del usuario es la real: su BD, `settings.json`, sesión de AniList y videos. Ya hubo incidentes (token borrado por una prueba, ajustes reales cambiados por un clic a ciegas, un episodio marcado COMPLETED en AniList). Este skill automatiza la forma segura: `AppDataPaths` se redirige a una carpeta bajo `%TEMP%` con una **copia** de la biblioteca, sin token (sin sesión de AniList, así que nada se sincroniza) y con el volumen a 0.

Para pruebas unitarias no hace falta: usan carpetas temporales inyectables (ver `persistence.md`). Esto es para ver la app en marcha. Para capturas y clics, ver el skill `wpf-visual-verification`.

## Flujo

Todo con `python .claude/skills/perfil-aislado/scripts/perfil_aislado.py <acción>`:

1. Compila (skill `repo-build-test`): el script lanza el exe de Debug.
2. `crear` → imprime `PERFIL=<carpeta bajo %TEMP%>`. Copia la BD en solo lectura (`VACUUM INTO`), reescribe **todas** las rutas de la copia (carpeta de anime, archivos, miniaturas, historial de descargas…) a una biblioteca de prueba dentro del perfil y comprueba que quedan **0** valores apuntando a lo real; si no da 0, no borres ni descargues nada hasta entenderlo. También apaga el volumen, las notificaciones, las actualizaciones al iniciar, los plugins y la bandeja al cerrar, y desactiva `AutoDescargar`/`Avisar`.
3. `iniciar PERFIL` → lanza la app con `USERPROFILE`/`LOCALAPPDATA`/`APPDATA` apuntando al perfil, **sin red** (`ANIMELOCALTRACKER_SIN_RED=1`) y con registro detallado. Confirma el aislamiento viendo aparecer el registro de sesión en el perfil (y ninguno nuevo en la carpeta real); si no lo confirma, cierra la app y sale con código 2. Añade `--con-red` solo si la prueba lo necesita (descargas, búsquedas).
4. Espera ~25 s tras lanzar: los clics antes no navegan. Para probar una opción de Configuración, escribe el valor en el `settings.json` del perfil **antes** de `iniciar`.
5. `cerrar PERFIL` → cierra solo el PID que lanzó `iniciar` (cierre educado y, si no sale, forzado con su árbol de procesos, daemon Python incluido). Si lo pides en los primeros segundos puede salir "forzada": es normal.
6. `borrar PERFIL` → elimina el perfil (solo carpetas `%TEMP%\perfil-aislado-*` creadas por el script).

`--con-rutas-reales` en `crear` deja las rutas como están: solo para casos en que el propio objetivo es leer la biblioteca real, y entonces **no se borra ni se descarga nada**.

## Reglas que siguen valiendo dentro del perfil

- **Si ya hay una AnimeLocalTracker en marcha, `iniciar` no lanza nada (código 3):** la instancia única (mutex global) haría que la segunda se cierre sola. Esa instancia puede ser la app real con un episodio en marcha: no la cierres (el hook lo bloquea); pide al usuario que la cierre.
- **Clics:** el script de clics debe abortar **toda** la secuencia si la app no está en primer plano (no solo saltarse el tecleo). Antes de pulsar "reproducir", confirma con una captura que se abrió la ficha correcta. Un clic fijo en la barra de tareas puede minimizar la app ya enfocada.
- **Nunca "Guardar Preferencias"**, ni siquiera en el perfil: llama a `IStartupService.Sincronizar` y escribe en el **registro real de Windows** (arranque con Windows) con la ruta del exe de Debug. El guardado se cubre con una prueba unitaria.
- No pulses "Escuchar" de la música (suena en los altavoces del usuario). Si algo suena, `M` silencia.
- Al cambiar de pestaña con un video en marcha, la app pasa el reproductor a un **mini reproductor** (ventana aparte, sin controles visibles para UIA); se libera al cerrarlo con ✕. Sal del reproductor con `Esc` antes de ponerte con otra cosa: si queda abierto sigue avanzando el progreso.
- **Descargas y borrados** (`crear` normal, rutas reescritas): las descargas son reales (TransferIt/Mega, ~140–220 MB por episodio, gastan la cuota de Mega de la IP del usuario). Haz las mínimas, avísalo en el informe y borra los videos al acabar. "Eliminar tras ver": usa un video de ~30 s con `-g 25` y `UmbralMarcadoVisto` 50 en el `settings.json` del perfil (uno de 12 s no marcó "visto" de forma fiable).
- Nunca lances el exe con `--borrar-datos`: borra los datos reales sin posibilidad de redirigirlos.

## Comprobar que no se tocó nada real

Antes de `iniciar` y después de `cerrar`, lista la carpeta real y compara; debe dar 0 diferencias:

```bash
R="$LOCALAPPDATA/AnimeLocalTrackerData"; find "$R" -type f -printf '%P|%s|%T@\n' | sort > "$TEMP/real-antes.txt"
# ... iniciar, probar, cerrar ...
find "$R" -type f -printf '%P|%s|%T@\n' | sort > "$TEMP/real-despues.txt"; diff "$TEMP/real-antes.txt" "$TEMP/real-despues.txt" && echo "0 diferencias"
```

`crear` sí toca `biblioteca.db-shm` de la carpeta real (lectura en modo `ro` de una BD en WAL): haz la foto "antes" después de `crear`.

## Códigos de salida

`0` bien · `1` uso incorrecto o perfil que no es de este script · `2` aislamiento no confirmado (la app se cierra sola) · `3` hay una AnimeLocalTracker en marcha (no se lanza nada).
