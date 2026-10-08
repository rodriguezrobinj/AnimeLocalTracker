---
name: localizacion
description: Añadir o cambiar texto visible en AnimeLocalTracker (español/inglés) sin dejar nada sin traducir: claves en LocalizationService, {loc:T} en XAML, LocalizationService.T() en C#, marcadores {0}, fechas con Cultura, cambio de idioma en caliente y géneros. Usar al escribir cualquier etiqueta, tooltip, diálogo, mensaje de estado o formato de fecha/número que vea el usuario, o al revisar texto sin traducir.
---

# Localización ES/EN — AnimeLocalTracker

Todo texto que ve el usuario sale de dos diccionarios, `Es` y `En`, en `AnimeLocalTracker/Services/LocalizationService.cs` (hoy 1257 claves en cada uno, paridad exacta). Reglas de fondo en `wpf-mvvm.md` #1; esto es el procedimiento.

## 1. Añadir un texto

1. **Clave** `Prefijo_Nombre`. El prefijo es la pantalla: `Cfg` Configuración, `Det` Ficha/Detalle, `Gal` Galería, `Act` Actualizaciones, `Hist` Historial, `Cal` Calendario, `Add` Agregar anime, `Desc` Descargas, `Player` Reproductor, `Stats` Estadísticas, `Logro`, `Mini` minijuegos, `Dlg` diálogos genéricos, `Nav`, `Tray`… (mira cómo nombra la pantalla en la que trabajas). Sufijos que ya se usan: `Titulo` y `Msj` (diálogos), `Sub` (texto secundario), `Tooltip`, `Desc`, y **`Formato`** si la cadena lleva `{0}` y va a `string.Format`.
2. **Las dos mitades del archivo**: añade la clave en `Es` (empieza cerca de la línea 34) y en `En` (cerca de la 1385), en el mismo bloque temático. El inglés no puede ser copia del español, salvo nombres propios.
3. **Marcadores**: los mismos `{n}` en los dos idiomas. Lo que cambia entre idiomas es el formato de dentro, no el índice (`{0:dd/MM/yyyy}` ↔ `{0:MM/dd/yyyy}`).
4. **Uso**:
   - XAML: `{loc:T Clave}` (`xmlns:loc="clr-namespace:AnimeLocalTracker.Services"`). No escribas a mano el `{Binding [Clave], Source=…}`; solo dentro de un `MultiBinding`, con `Mode=OneWay`.
   - C#: `LocalizationService.T("Clave")`; con datos, `string.Format(LocalizationService.T("Clave_Formato"), x)`. Nunca un literal en español ni `$"Episodio {n}"` (para eso existe `Act_EpisodioFormato`).
   - Botones solo-icono: el mismo texto en `ToolTip` y `AutomationProperties.Name`.
5. **Fechas y números** con `LocalizationService.Cultura` (es-ES / en-US), nunca `CurrentCulture`. El nombre de un archivo (`Episodio 05.mp4`) no se traduce.

## 2. Cambio de idioma en caliente

- Un ViewModel que muestra texto construido en C# se suscribe con `WeakReferenceMessenger` a `IdiomaCambiadoMensaje` (`IRecipient<IdiomaCambiadoMensaje>`) y lo vuelve a pedir. **Nunca** `LocalizationService.Instance.PropertyChanged += …` (fuga entre pruebas y carrera real).
- Un `ComboBox` con lista de strings no se re-traduce solo: la lista va como propiedad (no `const`) que llama a `T()` en cada acceso, y se reconstruye al recibir el mensaje; si la selección debe sobrevivir, guárdala por índice o por código, no por texto.
- Propiedades calculadas de modelos (`TemaAnimeItem`, estadísticas…) se recalculan igual.

## 3. Datos mostrados vs. guardados

La traducción es solo de presentación. Los géneros de AniList se guardan, filtran y envían en inglés; `LocalizationService.TraducirGenero()` / `GeneroTraducidoConverter` se usan **solo al pintar**. Lo mismo vale para cualquier valor que se compare o persista (código de temporada, estado).

## 4. Comprobar

- **Prueba**: `LocalizationServiceTests.TodasLasClaves_ExistenEnLosDosIdiomas_ConLosMismosMarcadores` recorre los dos diccionarios y falla si falta una clave en cualquiera o si los marcadores `{n}` difieren. Compila y corre `--filter "FullyQualifiedName~LocalizationServiceTests"`.
- **Texto suelto**: `python .claude/skills/localizacion/scripts/buscar_literales.py <archivos que tocaste>` lista literales en español en C# y atributos XAML con texto fijo. Es una heurística de **candidatos**: marca también valores por defecto que luego se sobrescriben con `T()`, descripciones internas (migraciones) y nombres propios. Úsalo sobre tus archivos, no esperes cero en todo el proyecto. Si vas a dar por traducida una pantalla, ejecútalo sobre su vista y su ViewModel.
- **Vista rápida**: cambiar el idioma en Configuración con la app en un perfil aislado (skill `perfil-aislado`) y recorrer la pantalla; lo que siga en español es lo que falta.
