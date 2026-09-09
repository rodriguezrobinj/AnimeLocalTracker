# Regla: Arquitectura Políglota (Rust FFI y Daemon Python)

1. **Interoperabilidad con Rust (`animetracker_core.dll`):**
   - Declarar llamadas FFI exclusivamente mediante el atributo moderno `[LibraryImport]` de .NET 8 (generación de código en compilación).
   - Utilizar tipos de datos blittable para evitar sobrecarga en la recolección de basura y fugas de memoria.
2. **Ciclo de Vida del Daemon Python (`AnimeTrackerTools.exe`):**
   - Todo proceso externo de Python debe controlarse mediante `PythonBridgeService`.
   - Es obligatorio registrar la terminación forzosa del proceso en el evento `AppDomain.CurrentDomain.ProcessExit` para asegurar que no queden procesos huérfanos consumiendo memoria o bloqueando puertos tras cerrar la aplicación.
   - El protocolo de comunicación debe contemplar reintentos con retraso progresivo (backoff) ante pérdidas temporales de conexión.
