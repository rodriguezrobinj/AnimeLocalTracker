# Regla: Persistencia de Datos y SQLite

1. **Ubicación de Datos de Usuario:**
   - La base de datos, configuraciones, portadas cacheadas y logs deben ubicarse exclusivamente en `AppDataPaths` (`%LocalAppData%\AnimeLocalTrackerData`).
   - Jamás escribir datos dinámicos o temporales dentro del directorio de instalación del programa.
2. **Copias de Seguridad Confiables (Snapshots Atómicos):**
   - Dado que SQLite opera con Write-Ahead Logging (modo WAL), los respaldos de la base de datos abierta deben realizarse únicamente con el comando SQL `VACUUM INTO 'ruta_destino'`.
   - Prohibido utilizar `File.Copy` sobre la base de datos en ejecución para evitar corrupciones de datos.
3. **Sincronización Local-First Segura:**
   - Las importaciones locales de copias de seguridad (JSON) deben marcarse automáticamente con `SincronizadoEnNube = true` para no enviar mutaciones accidentales a la API remota de AniList.
4. **Fechas y Zonas Horarias:**
   - Guardar SIEMPRE fechas en UTC (`DateTime.UtcNow`).
   - **sqlite-net devuelve `DateTime` con `Kind = Unspecified`**: al mostrar/agrupar, tratarlas como UTC (`fecha.Kind == DateTimeKind.Local ? fecha : fecha.ToLocalTime()`). No comparar `Kind == Utc` para decidir (falla tras round-trip a BD).
5. **Historial = Visionado Real:**
   - Solo la reproducción real registra `UltimaReproduccion`; el marcado manual de "visto" no debe fabricar la fecha (NULL no se rellena en la capa de datos).
   - Al borrar un archivo del disco, conservar el registro (limpiar solo `RutaArchivo`/miniatura/metadatos técnicos): el historial es un registro permanente.
   - Los feeds de episodios (historial/actualizaciones) consultan `UltimaReproduccion IS NOT NULL OR ProgresoSegundos > 0` ordenado desc con `LIMIT`.
6. **Migraciones de Esquema:**
   - El esquema evoluciona con la lista `Migraciones` de `DatabaseService` (versión + acción), nunca con `CreateTableAsync` suelto.
   - Los atributos `[Indexed]` del modelo solo aplican a bases NUEVAS: para bases existentes hace falta una migración explícita (`CREATE INDEX IF NOT EXISTS`).
