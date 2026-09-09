---
name: repo-build-test
description: Compila AnimeLocalTracker en doble pasada obligatoria con TreatWarningsAsErrors y ejecuta la suite de pruebas unitarias.
---

# Skill: Build y Verificación de Pruebas

Procedimiento mandatorio para verificar la salud y compilación del proyecto AnimeLocalTracker tras cualquier cambio.

## Verificación Completa del Repositorio

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -RunTests
```

## Verificación Final Adicional (obligatoria tras tocar código C#/XAML)

El doble pase de `build.ps1` **puede reportar "OK" con errores ocultos** (proyecto temporal `*_wpftmp`, BAMLs bloqueados) y dejar el exe sin regenerar. Confirmar SIEMPRE con builds directos no incrementales:

```powershell
dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug --no-incremental
dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-incremental
dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-build
```

**Exe de prueba:** `AnimeLocalTracker\bin\Debug\net8.0-windows\AnimeLocalTracker.exe`.
Si tras un build "OK" el exe no existe/está viejo: buscar procesos colgados (`Get-Process | ? { $_.Name -match 'dotnet|MSBuild|VBCS|testhost' }`) → matar, borrar `obj` de la app y reconstruir (errores MC1000/BG1002/MC3072 espurios por BAML bloqueado/parcial).

## Criterios de Éxito Innegociables

1. **0 Warnings:** `TreatWarningsAsErrors` activo (analizadores `CA*`/`CS*` incluidos). Recordar que los runners de GitHub traen SDK 10 pero `global.json` fija SDK 8: la divergencia de analizadores entre SDK puede ocultar/crear avisos (ej. CA2263).
2. **Suite de Tests al 100%:** objetivo actual **327 tests** (xUnit + FluentAssertions + Moq + pytest de Python en CI). Si el total no sube al añadir tests, recompilar con `--no-incremental` (binarios stale).
3. **Manejo de Errores:** si el build o los tests fallan, corregir quirúrgicamente la causa antes de notificar; no "aprobar" con el doble pase en verde si la verificación no-incremental falla.

## Verificación en CI (cuando aplica)

```powershell
gh run list --limit 1          # run del último push
gh run view <id> --log-failed  # log del paso fallido
```
