# Shaders de Anime4K

De [Anime4K](https://github.com/bloc97/Anime4K) v4.0.1 (bloc97, licencia MIT; cada archivo lleva su aviso). Son los que carga el
escalado inteligente del motor nuevo (`MotorVideo.SombreadoresAnime4K`): recortar brillos, restaurar líneas y ampliar, en dos
niveles (S para gráficas integradas, M para dedicadas).

## La única modificación

`Anime4K_Clamp_Highlights.glsl` y `Anime4K_Restore_CNN_*.glsl` llevan en cada pasada la misma condición que los de ampliación
ya traían de origen:

```
//!WHEN OUTPUT.w MAIN.w / 1.200 > OUTPUT.h MAIN.h / 1.200 > *
```

Así todo el juego actúa solo cuando el video se agranda al menos un 20 % en pantalla. Sin ella, «restaurar» corría siempre:
medido en la app con una Intel HD 620, a 1080p perdía 8 fotogramas por segundo (749 en 90 s) para una mejora que apenas se ve
cuando el video no se amplía.

Al actualizar los shaders hay que volver a añadir esa línea después de cada `//!DESC` de esos tres archivos (lo comprueba la
prueba `LosShadersQueNoAmplian_SoloActuanSiElVideoSeAgranda`).
