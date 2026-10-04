---
Proyecto: AnimeLocalTracker
Fase: 1 - Seguridad
Fecha de auditoría: 2026-09-14
Hora de inicio: 23:32:00
Duración de la fase: 18:00
---

# Fase 1 — Seguridad

## Metodología y desviaciones documentadas respecto al plan original

- **OWASP Dependency-Check completo: NO ejecutado.** Requiere descargar/alojar la base NVD
  (varios GB en el primer run, históricamente 20-40 min incluso con buena conexión) y no está
  preinstalado en este entorno. Sustituido por `dotnet list package --vulnerable
  --include-transitive` (misma fuente de datos que usa el propio CI del proyecto,
  `.github/workflows/ci.yml`) contra `nuget.org` en vivo. Documentado como decisión de alcance,
  no como fallo — ambas herramientas consultan bases de vulnerabilidades públicas, la diferencia
  es la exhaustividad de CVEs de terceros no-.NET, que aquí se cubre con `cargo audit`/`pip-audit`.
- **ILSpy/dotPeek**: no se instaló un descompilador dedicado. Al tener acceso completo al código
  fuente (mismo repo), la búsqueda de credenciales/tokens embebidos se hizo por grep exhaustivo
  sobre el código fuente en vez de sobre el IL compilado — cubre el mismo objetivo (detectar
  secretos hardcodeados) con mayor precisión, ya que el fuente es superset de lo que terminaría en
  el binario. Limitación reconocida: no detecta secretos que solo se inyecten en tiempo de build
  vía variables de entorno de CI no versionadas (fuera del alcance de una auditoría de código).
- Herramientas usadas: `dotnet list package --vulnerable` (nativo del SDK 8), `cargo-audit 0.22.2`
  (ya instalado localmente, misma versión pineada que usa el CI), `pip-audit 2.10.1` y `bandit`
  (instalados vía `pip install` en esta sesión — únicos instalados en esta fase).

## 1. Escaneo de dependencias (SCA)

| Componente | Herramienta | Resultado |
|---|---|---|
| `AnimeLocalTracker.csproj` (NuGet) | `dotnet list package --vulnerable --include-transitive` | **0 vulnerabilidades conocidas** |
| `AnimeLocalTracker.Tests.csproj` (NuGet) | ídem | **0 vulnerabilidades conocidas** |
| `native/animetracker_core` (Cargo, 19 crates) | `cargo audit` (1246 advisories cargados) | **0 vulnerabilidades conocidas**, exit code 0 |
| `tools/python` (6 deps directas: anitopy, rapidfuzz, yt-dlp, pydantic, opencv-python-headless, numpy) | `pip-audit 2.10.1` | **"No known vulnerabilities found"** |
| `tools/python` (análisis estático, 1161 líneas) | `bandit` | 43 hallazgos, **todos Low/B101 (`assert` en tests)** — 0 Medium/High, sin secretos embebidos |

**Severidad: Informativo/Positivo.** Las tres cadenas de dependencias (NuGet, Cargo, PyPI) están
limpias a fecha de esta corrida (2026-09-14) y el CI ya las audita en cada push/PR de forma
bloqueante (confirmado leyendo `ci.yml`, Fase 0 §4), lo cual es una práctica sólida poco común en
software de consumo de un solo desarrollador.

## 2. Firma digital del instalador/ejecutable — HALLAZGO (Severidad: Media)

**El instalador y el ejecutable distribuidos en `Releases/` NO están firmados digitalmente
(Authenticode).**

Evidencia:
```
Get-AuthenticodeSignature Releases\AnimeLocalTracker-win-Setup.exe
  → Status: NotSigned
Get-AuthenticodeSignature AnimeLocalTracker.exe (extraído de AnimeLocalTracker-win-Portable.zip)
  → Status: NotSigned
```

**Impacto:**
- Windows SmartScreen mostrará advertencia de "Editor desconocido" en la primera ejecución del
  instalador — fricción de onboarding no documentada, contradice la promesa del README de
  "Únete a la Revolución en 30 Segundos" (ver Fase 6).
- La integridad de las actualizaciones **sí** está parcialmente protegida a nivel de Velopack: el
  archivo `Releases/RELEASES` contiene hashes SHA1 por paquete (ej. `1A910626B9A69FB2D575DDDF87
  EA88FB71A6F7BE AnimeLocalTracker-1.0.3-full.nupkg`), y `UpdateService.cs:51` fija
  `AllowVersionDowngrade = false` (comentario `SEC-011` en el propio código reconoce
  explícitamente el riesgo de downgrade/paquetes sin firma). Pero esta protección depende de que
  el `RELEASES` manifest y los paquetes viajen juntos desde el mismo origen confiable (GitHub
  Releases sobre HTTPS) — **no hay una raíz de confianza independiente (certificado Authenticode)
  que permita al usuario o al SO verificar que el binario proviene realmente del autor**, solo la
  cadena de confianza de GitHub + HTTPS. Si la cuenta de GitHub o el pipeline de release se
  vieran comprometidos, un atacante podría publicar una release maliciosa que Velopack aplicaría
  sin fricción adicional (el propio hash SHA1 en `RELEASES` lo genera el mismo pipeline
  comprometido).

**Remediación sugerida:** firmar `Setup.exe` y el ejecutable principal con un certificado
Authenticode (EV o estándar; EV evita el "smartscreen reputation cooldown" inicial). Coste
recurrente (~100-400 USD/año) a evaluar contra el hecho de que el proyecto es MIT/gratuito de un
solo mantenedor — alternativa de menor coste: firma estándar (no EV) + acumular reputación en
SmartScreen con el tiempo.

## 3. Almacenamiento del token OAuth de AniList — Verificado correcto

`AuthService.cs` usa **DPAPI** (`System.Security.Cryptography.ProtectedData`) con
`DataProtectionScope.CurrentUser` tanto para cifrar (`Protect`, línea 218) como descifrar
(`Unprotect`, línea 37) el token antes de escribirlo a disco. Si el descifrado falla (token
corrupto o copiado a otra máquina/usuario, ya que DPAPI en `CurrentUser` scope está atado al
perfil de Windows), el código captura la excepción, loguea una advertencia **sin exponer el valor
del token** (verificado: ningún `AppLogger.*` interpola la variable del token, solo mensajes de
evento) y fuerza un nuevo login. Esto es la práctica recomendada en Windows para credenciales
locales de apps de escritorio — coincide con la afirmación implícita de "privacidad" del README.

No se encontró ningún `client_secret` embebido: el flujo usa `response_type=token`
(AniList OAuth2 **Implicit Grant**), que no requiere secreto de cliente porque el token se
devuelve directamente en el fragmento de la URL de redirección. Nota de diseño (no vulnerabilidad
de la app): el Implicit Grant está desaconsejado por las guías modernas de OAuth 2.1 en favor de
Authorization Code + PKCE, pero esta es una limitación del soporte de OAuth de **AniList como
proveedor**, no una decisión insegura de AnimeLocalTracker — el redirect es a un listener HTTP
local (`127.0.0.1:5050`) bajo control exclusivo del propio proceso de la app, lo que mitiga el
principal riesgo del Implicit Grant (fuga del token vía Referer de un tercero).

### 3.1 Validación del listener OAuth local — Verificado correcto, con evidencia de hardening previo

`AuthService.EsOrigenLocal()` valida el header `Origin`/`Referer` del callback OAuth parseando la
URI completa (`Uri.TryCreate` + comparación exacta de `Scheme`+`Host`+`Port`), **no** una
comparación de prefijo de cadena. El propio comentario en el código (líneas 280-284) documenta
explícitamente por qué: una comparación de prefijo aceptaría hosts evasivos como
`127.0.0.1:5050.evil.com`. Esto indica que el proyecto ya pasó por una revisión de seguridad
previa en este punto exacto — buena señal de madurez, no un hallazgo nuevo.

## 4. Comunicación con APIs externas (AniList, AniSkip) — Verificado correcto

Todas las llamadas de red salientes usan HTTPS explícito:
- `https://anilist.co/api/v2/oauth/authorize` (AuthService.cs:72)
- `https://graphql.anilist.co` (AniSkipService.cs:125, AniListTrackingService.cs:54)
- `https://api.aniskip.com/v2/skip-times/...` (AniSkipService.cs:57)

No se encontró ningún `ServerCertificateCustomValidationCallback`,
`DangerousAcceptAnyServerCertificateValidator` ni desactivación de revocación de certificados —
la validación TLS estándar de .NET no está siendo bypaseada en ningún punto del código.

El único `http://` (no-TLS) en el código es `http://127.0.0.1:5050/` — el listener HTTP local
para el callback de OAuth, que es tráfico loopback (nunca sale de la máquina) y por tanto no
representa una exposición de red real.

**Manejo de caída/cambio de contrato de las APIs de terceros**: `AniListTrackingService.cs:79`
maneja explícitamente el caso HTTP 401 (token rechazado) cerrando la sesión local y forzando
reautenticación — no se probaron activamente otros escenarios de fallo (5xx, timeout, JSON con
esquema cambiado) por no generar tráfico artificial contra AniList/AniSkip real (regla explícita
del alcance). Recomendación para Fase 3/5: cubrir estos casos con mocks en tests de integración,
si no existen ya (a verificar contra la suite de 327 tests en Fase 2).

## 5. Otros controles revisados

| Control | Resultado |
|---|---|
| `BinaryFormatter` (deserialización insegura) | No encontrado en el código C# |
| Elevación UAC (`requestedExecutionLevel`) | No hay `app.manifest` propio — WPF usa el comportamiento por defecto (`asInvoker`, sin elevación). Consistente con el claim del README de "sin molestos permisos de UAC" |
| Secretos/API keys hardcodeados (C#) | Grep exhaustivo (`api_key`, `secret`, `password`, `token = "..."`) — sin coincidencias |
| Secretos/API keys hardcodeados (Python) | `bandit` — 0 hallazgos Medium/High |
| `subprocess`/`Process.Start` con `shell=True` o concatenación de strings | No encontrado — todas las llamadas Python usan listas de argumentos (`subprocess.run([...])`), reduciendo el riesgo de inyección de comandos vía nombres de archivo maliciosos |
| Telemetría oculta no documentada | No se encontró ningún SDK de APM/analytics (Application Insights, Sentry, etc.) referenciado en `.csproj` ni en el código. El propio string de la UI (`LocalizationService.cs:747`, panel "Acerca de") declara explícitamente: *"No telemetry or analytics. Logs and history are stored in plain text..."* — consistente con el código real (ver también Fase 6) |

## 6. Componentes Rust y Python — auditados con el mismo rigor (confirmado en Fase 0 que son producción)

- **Rust (`native/animetracker_core`)**: sin vulnerabilidades de dependencias (`cargo audit`
  limpio). No se auditó en esta fase el manejo de `panic!`/`unwrap()` en el límite FFI expuesto
  vía `LibraryImport` — un panic no capturado ahí puede tumbar el proceso completo de la app WPF;
  este análisis de **estabilidad** (no de vulnerabilidad de seguridad clásica) se documenta en la
  Fase 2 (arquitectura/interoperabilidad).
- **Python (`tools/python`, empaquetado como `AnimeTrackerTools.exe` vía PyInstaller)**: sin
  vulnerabilidades de dependencias (`pip-audit` limpio), sin hallazgos de seguridad relevantes en
  análisis estático (`bandit`). Al ejecutarse como proceso hijo separado (no in-process), un
  fallo o crash del daemon Python está más aislado del proceso WPF que el núcleo Rust in-process
  — ver Fase 2 para el análisis completo de resiliencia ante fallo de cada componente.

## 7. Resumen de severidad

| # | Hallazgo | Severidad | Estado |
|---|---|---|---|
| 1 | Instalador/ejecutable sin firma Authenticode | **Media** | Abierto — remediación sugerida arriba |
| 2 | Dependencias NuGet/Cargo/PyPI | Ninguna | Limpio a la fecha |
| 3 | Almacenamiento de token OAuth | Ninguna | Correcto (DPAPI) |
| 4 | Validación de origen del listener OAuth local | Ninguna | Correcto, ya endurecido previamente |
| 5 | Comunicación con APIs externas | Ninguna | Correcto (HTTPS, sin bypass de TLS) |
| 6 | Telemetría oculta | Ninguna | Claim del README verificado contra el código |
| 7 | Secretos hardcodeados | Ninguna | No encontrados en C#/Rust/Python |
