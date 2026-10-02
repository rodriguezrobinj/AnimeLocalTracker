# Regla: Comunicación y Explicaciones Prácticas

**Idioma:** todo mensaje al usuario va en español, incluidos los avisos intermedios mientras se trabaja.

Al explicar cambios, diagnósticos, soluciones o nuevas funciones al usuario:
1. **Ejemplos prácticos dentro de la aplicación:**
   - Ilustra el impacto con una situación real de uso cotidiano en AnimeLocalTracker cuando el cambio sea visible para el usuario o afecte a su experiencia. En avisos de estado cortos ("compilando", "tests OK") no hace falta ejemplo.
   - Ejemplos de referencia:
     - *"Si estás viendo un capítulo de Shingeki no Kyojin y salta la intro con AniSkip..."*
     - *"Al navegar por la galería principal, notarás que las portadas cargan de inmediato sin parpadeos..."*
     - *"Si se va la conexión de internet mientras ves un episodio, tu progreso no se pierde y al volver la red se sincroniza solo con AniList..."*
2. **Claridad sin tecnicismos excesivos:**
   - Evita jerga abstracta innecesaria (como "despachador de sincronización de locks reentrantes").
   - Traduce los conceptos técnicos a su efecto tangible: fluidez del video, estabilidad de la ventana, ahorro de clics o seguridad de los datos.
3. **Enfoque en el beneficio de uso:**
   - Explica brevemente qué problema soluciona en la experiencia del usuario y cómo se visualiza o interactúa con ello en la interfaz.
4. **Brevedad sin perder claridad:**
   - Ser conciso es bueno; comprimir el estilo hasta quitar el ejemplo o el motivo no lo es. Por eso el modo `caveman` está apagado en este repo (ver `skills-orquestacion.md`).
5. **Informar con honestidad:**
   - Si algo no se pudo verificar (por ejemplo, no se probó en la app real), decirlo explícitamente. No declarar "listo" sin la evidencia que exige `repo-build-test`.
