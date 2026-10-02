# Regla: Arquitectura Políglota (Rust FFI y Daemon Python)

1. **Interoperabilidad con Rust (`animetracker_core.dll`):**
   - Declarar llamadas FFI exclusivamente mediante el atributo moderno `[LibraryImport]` de .NET 8 (generación de código en compilación). Prohibido `[DllImport]`.
   - Preferir tipos blittable (`int`, `long`, `float`, `byte*`, punteros) en la firma para evitar copias y sobrecarga del recolector de basura. Si hay que pasar cadenas, declarar `StringMarshalling` explícito (UTF-8) y liberar en el lado correcto cualquier memoria que reserve Rust.
2. **Ciclo de Vida del Daemon Python (`AnimeTrackerTools.exe`):**
   - Todo proceso externo de Python debe controlarse mediante `PythonBridgeService`.
   - Es obligatorio registrar la terminación forzosa del proceso en el evento `AppDomain.CurrentDomain.ProcessExit` para asegurar que no queden procesos huérfanos consumiendo memoria o bloqueando puertos tras cerrar la aplicación.
   - El protocolo de comunicación debe contemplar reintentos con retraso progresivo (backoff) y un número máximo de intentos ante pérdidas temporales de conexión.
   - **Una petición cancelada no puede dejar su respuesta en la cola:** el protocolo correlaciona cada respuesta con el id de su petición y, tras una cancelación, descarta o reinicia el daemon. De lo contrario la respuesta tardía se entrega a la petición siguiente (p. ej. los marcadores de OP/ED de un episodio acabarían guardados en otro).
