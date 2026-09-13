# NOTA DE CAMBIOS — Remediación del Bloque 4 explicada con ejemplos de uso

> Explicación orientada a usuario/equipo de los cambios de la remediación del Bloque 4
> (auditoría de UX/Accesibilidad, Legal/Privacidad y Marketing). Cada sección describe el
> escenario dentro de la aplicación y el comportamiento "antes → después". Complementa a
> `AUDITORIA_BLOQUE4_UX_LEGAL_MARKETING.md` (hallazgos técnicos con `file:line`).

---

## 1. "Borrar todos mis datos" — por fin existe (PRI-001)

**Escenario:** quieres empezar de cero o regalar/vender tu PC.

- **Antes:** no había forma de purgar los datos: ni siquiera **desinstalar** la app
  borraba tu biblioteca, historial, token de AniList, portadas y backups (viven a
  propósito fuera del directorio de la aplicación).
- **Después:** en Configuración → Cuenta hay un botón **"Borrar todos mis datos"** con
  **doble confirmación**:
  1. Avisa exactamente qué se borra (biblioteca, historial, sesión de AniList,
     portadas, miniaturas, backups y logs) y que la lista en la nube **NO** se toca.
  2. Pide una última confirmación y avisa de que la app se cerrará.
  Al aceptar: cierra sesión de AniList, vacía la base de datos en una sola transacción,
  borra las carpetas de datos y cierra la app. El siguiente arranque empieza limpio.

---

## 2. Consentimiento antes de conectar AniList (PRI-002)

**Escenario:** pulsas "CONECTAR" en la Galería por primera vez.

- **Antes:** el navegador se abría directamente con el login de AniList; el usuario solo
  descubría qué se sincronizaba después (o en Configuración).
- **Después:** antes de abrir el navegador aparece un diálogo que explica con claridad:
  *"Se LEERÁ tu perfil y tu lista; se ESCRIBIRÁ tu progreso, estado y puntuación cuando
  los marques. Tus archivos nunca se suben y no hay telemetría."* Si no aceptas, no se
  abre nada.

---

## 3. Sección de privacidad en "Acerca de" (PRI-003/004)

- **Antes:** no existía ninguna política/descripción de privacidad dentro de la app.
- **Después:** "Acerca de" incluye una tarjeta **"Privacidad y tus datos"** que resume:
  local-first (dónde viven los datos), el alcance exacto de la sincronización con
  AniList, qué recibe AniSkip (solo identificadores públicos), ausencia de telemetría,
  retención acotada (log 5 MB, 5 backups) y las opciones de exportar/borrar desde
  Configuración.

---

## 4. El README ya es preciso sobre la nube (PRI-005)

- **Antes:** "La aplicación nunca sube, rastrea ni comparte tus archivos locales" podía
  leerse como "nada sale de tu equipo".
- **Después:** se añadió el matiz: al **conectar AniList** sí se sincroniza tu
  progreso/estado/puntuación (es la función del producto); lo que nunca sale son tus
  archivos de video, la biblioteca local y el historial.

---

## 5. Contraste accesible en la interfaz (UX-002/003)

**Escenario:** usas la app en una habitación iluminada o con baja visión.

| Elemento | Antes (ratio) | Después (ratio) | Cumple AA |
|---|---|---|---|
| Texto secundario/terciario (KPIs, calendario, títulos "visto") `#6B7280` | 3.7:1 | `#94A3B8` ≈ **7:1** | ✅ |
| Texto blanco en botones primarios (fondo `#3B82F6`) | 3.7:1 | fondo `#2563EB` ≈ **5.1:1** | ✅ |
| Chips de estado (verde/gris) con texto blanco | 2.7:1 | texto oscuro `#0B1220` ≈ **6:1** | ✅ |
| Badge "N Nuevos" (fondo `#F43F5E`) | 3.7:1 | fondo `#E11D48` ≈ **4.7:1** | ✅ |

---

## 6. Nombres accesibles para lectores de pantalla (UX-001)

**Escenario:** usas un lector de pantalla (Narrator/JAWS) o un dispositivo braille.

- **Antes:** la app tenía **cero** `AutomationProperties.Name`: los botones solo-icono
  (cerrar, pantalla completa, pausar descarga, favorito, menú del episodio…) se
  anunciaban como "botón" sin decir su función.
- **Después:** todos los botones solo-icono identificados en la auditoría tienen nombre
  accesible: ventana (pantalla completa/minimizar/maximizar/cerrar), galería
  (selección múltiple/sincronizar), descargas (pausar-reanudar/cancelar), reproductor
  (volver) y detalle (favorito/episodio descargado).

---

## 7. Diálogos operables por teclado (UX-005)

**Escenario:** se abre una confirmación ("¿Eliminar este anime?", "¿Restaurar backup?"…).

- **Antes:** el foco quedaba en la página subyacente: el teclado podía activar controles
  que estaban *debajo* del diálogo, y **Esc no lo cerraba** (de hecho, con el reproductor
  abierto, Esc podía cerrar el reproductor aunque hubiera un diálogo encima).
- **Después:** al abrirse un diálogo, el **foco salta al botón Aceptar** (Enter
  confirma), y **Esc cancela** el diálogo abierto.

---

## 8. Licencia y changelog (MKT-001/003)

- **Antes:** el README y el badge enlazaban a `LICENSE`… que **no existía** (enlace 404)
  y no había changelog.
- **Después:** existe el archivo **`LICENSE`** (MIT) y un **`CHANGELOG.md`** con todas
  las correcciones de esta remediación agrupadas por área, listo para nutrir las notas
  de release curadas.

---

## Checklist de verificación manual sugerida

1. Configuración → Cuenta → "Borrar todos mis datos" → cancela en la 2ª confirmación:
   no debe borrarse nada; repite aceptando → la app cierra y el siguiente arranque está
   vacío (PRI-001).
2. Galería → "CONECTAR" → debe aparecer el aviso de consentimiento antes del navegador
   (PRI-002).
3. "Acerca de" → existe la tarjeta "Privacidad y tus datos" (PRI-003).
4. Abre un diálogo de confirmación → el foco está en Aceptar y **Esc** lo cierra
   (UX-005).
5. Con un lector de pantalla, recorre la barra superior de la ventana → cada botón
   anuncia su función (UX-001).

*Documento informativo; no sustituye a los informes de auditoría (AUDITORIA_BLOQUE1…4)
que contienen el detalle técnico con `file:line`.*
