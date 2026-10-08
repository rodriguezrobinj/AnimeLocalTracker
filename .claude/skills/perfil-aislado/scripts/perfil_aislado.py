"""Perfil aislado de AnimeLocalTracker: probar la app sin tocar los datos reales del usuario.

    python perfil_aislado.py crear   [--con-rutas-reales]      -> imprime la carpeta del perfil (bajo %TEMP%)
    python perfil_aislado.py iniciar PERFIL [--con-red] [--exe RUTA]
    python perfil_aislado.py cerrar  PERFIL                    -> cierra SOLO el proceso que lanzó 'iniciar' (por PID)
    python perfil_aislado.py borrar  PERFIL                    -> borra el perfil (solo si es de este script y no hay app en marcha)

El perfil redirige USERPROFILE/LOCALAPPDATA/APPDATA a una carpeta temporal: AppDataPaths (BD, ajustes, logs, token) cae ahí.
Solo lee la carpeta real de datos, y la BD en modo solo lectura (VACUUM INTO). No copia anilist_token.txt: queda sin sesión.
Solo usa la biblioteca estándar. Códigos de salida: 0 bien, 1 error de uso, 2 aislamiento no confirmado, 3 hay una app en marcha.
"""
import argparse
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import time
from datetime import datetime
from pathlib import Path

RAIZ = Path(__file__).resolve().parents[4]
EXE_DEBUG = RAIZ / "AnimeLocalTracker" / "bin" / "Debug" / "net8.0-windows10.0.26100.0" / "AnimeLocalTracker.exe"
TEMP = Path(os.environ.get("TEMP") or tempfile.gettempdir())
PREFIJO = "perfil-aislado-"
CARPETA_DATOS = "AnimeLocalTrackerData"


def datos(perfil):
    return perfil / "AppData" / "Local" / CARPETA_DATOS


def datos_reales():
    return Path(os.environ["LOCALAPPDATA"]) / CARPETA_DATOS


def comprobar_perfil(texto):
    perfil = Path(texto).resolve()
    if not perfil.name.startswith(PREFIJO) or perfil.parent != TEMP.resolve():
        sys.exit(f"'{perfil}' no es un perfil creado por este script (debe ser %TEMP%\\{PREFIJO}*).")
    return perfil


def pids_app():
    """PIDs de AnimeLocalTracker.exe en marcha (de cualquiera: pueden ser la app real del usuario)."""
    salida = subprocess.run(["tasklist", "/FI", "IMAGENAME eq AnimeLocalTracker.exe", "/FO", "CSV", "/NH"],
                            capture_output=True, text=True).stdout
    return [int(f.split('","')[1]) for f in salida.splitlines() if f.startswith('"AnimeLocalTracker.exe"')]


def pid_vivo(pid):
    salida = subprocess.run(["tasklist", "/FI", f"PID eq {pid}", "/FI", "IMAGENAME eq AnimeLocalTracker.exe", "/FO", "CSV", "/NH"],
                            capture_output=True, text=True).stdout
    return "AnimeLocalTracker.exe" in salida


def sql_ruta(ruta):
    return str(ruta).replace("'", "''")


# ───────────────────────────── crear ─────────────────────────────

def reescribir_rutas(con, base_real, base_nueva, datos_real, datos_nuevo):
    """Cambia las rutas de la copia para que NADA apunte a la biblioteca ni a los datos reales. Devuelve un resumen."""
    resumen = []
    # (tabla, columna, prefijo real, prefijo nuevo, valor de "sin ruta": las de texto no nulo usan '', la miniatura admite NULL)
    for tabla, col, real, nuevo, vacio in (("AnimeItem", "RutaCarpeta", base_real, base_nueva, "''"),
                                           ("RegistroEpisodio", "RutaArchivo", base_real, base_nueva, "''"),
                                           ("RegistroEpisodio", "RutaMiniatura", datos_real, datos_nuevo, "NULL")):
        n = len(str(real))
        movidas = con.execute(
            f"UPDATE {tabla} SET {col} = '{sql_ruta(nuevo)}' || substr({col}, {n + 1}) "
            f"WHERE {col} IS NOT NULL AND {col} <> '' AND substr({col}, 1, {n}) = '{sql_ruta(real)}' COLLATE NOCASE").rowcount
        fuera = con.execute(
            f"UPDATE {tabla} SET {col} = {vacio} WHERE {col} IS NOT NULL AND {col} <> '' "
            f"AND substr({col}, 1, {len(str(nuevo))}) <> '{sql_ruta(nuevo)}' COLLATE NOCASE").rowcount
        resumen.append(f"{tabla}.{col}: {movidas} reescritas, {fuera} fuera de la base (vaciadas)")
    # Cualquier otra columna de texto con rutas (p. ej. DescargaHistorial): mismo cambio de prefijo, sin lista de columnas conocidas.
    otras = 0
    for tabla, col in columnas_de_texto(con):
        otras += con.execute(
            f'UPDATE "{tabla}" SET "{col}" = replace(replace("{col}", ?, ?), ?, ?) '
            f'WHERE instr(lower("{col}"), lower(?)) > 0 OR instr(lower("{col}"), lower(?)) > 0',
            (str(base_real), str(base_nueva), str(datos_real), str(datos_nuevo), str(base_real), str(datos_real))).rowcount
    resumen.append(f"resto de columnas de texto: {otras} valores reescritos")
    con.execute("UPDATE PreferenciaEmision SET AutoDescargar = 0, Avisar = 0")
    return resumen


def columnas_de_texto(con):
    for (tabla,) in con.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").fetchall():
        for _, col, tipo, *_ in con.execute(f"PRAGMA table_info('{tabla}')").fetchall():
            if tipo.upper() in ("", "TEXT") or "CHAR" in tipo.upper():
                yield tabla, col


def valores_con_ruta_real(db, prefijos):
    """Cuenta valores de texto de toda la BD que aún contienen alguno de los prefijos reales (tiene que dar 0)."""
    con = sqlite3.connect(db)
    total = sum(con.execute(f'SELECT COUNT(*) FROM "{tabla}" WHERE instr(lower("{col}"), lower(?)) > 0', (p,)).fetchone()[0]
                for tabla, col in columnas_de_texto(con) for p in prefijos)
    con.close()
    return total


def crear(args):
    perfil = TEMP / f"{PREFIJO}{datetime.now():%Y%m%d-%H%M%S}"
    nuevo = datos(perfil)
    nuevo.mkdir(parents=True)
    (perfil / "AppData" / "Roaming").mkdir()
    base_nueva = perfil / "Anime"
    base_nueva.mkdir()
    real = datos_reales()

    ajustes = json.loads((real / "settings.json").read_text(encoding="utf-8-sig")) if (real / "settings.json").exists() else {}
    base_real = ajustes.get("RutaBaseAnimes") or str(Path.home() / "Videos" / "Anime")

    # Silencio y sin ruido: volumen 0 desde el arranque, sin notificaciones, sin buscar actualizaciones, cierra de verdad.
    for clave, valor in (("VolumenReproductor", 0), ("VolumenMusica", 0), ("BuscarActualizacionesAlIniciar", False),
                         ("NotificarNuevosEpisodios", False), ("MinimizarABandejaAlCerrar", False),
                         ("PluginsHabilitados", False), ("RegistroDetallado", True)):
        ajustes[clave] = valor
    if not args.con_rutas_reales:
        ajustes["RutaBaseAnimes"] = str(base_nueva)
    (nuevo / "settings.json").write_text(json.dumps(ajustes, ensure_ascii=False, indent=2), encoding="utf-8")

    informe = []
    origen = real / "biblioteca.db"
    if origen.exists():
        # mode=ro: solo se toca biblioteca.db-shm; VACUUM INTO escribe únicamente en la copia.
        con = sqlite3.connect(origen.as_uri() + "?mode=ro", uri=True)
        con.execute(f"VACUUM INTO '{sql_ruta(nuevo / 'biblioteca.db')}'")
        con.close()
        if not args.con_rutas_reales:
            con = sqlite3.connect(nuevo / "biblioteca.db")
            informe += reescribir_rutas(con, base_real, base_nueva, real, nuevo)
            con.commit()
            con.close()
            restantes = valores_con_ruta_real(nuevo / "biblioteca.db", [base_real, str(real), str(Path(os.environ["APPDATA"]) / "AnimeLocalTracker")])
            informe.append(f"valores de la BD que aún apuntan a la biblioteca o a los datos reales: {restantes}"
                           + ("" if restantes == 0 else "  <-- REVISAR antes de borrar o descargar nada"))
    else:
        informe.append("no hay biblioteca.db real: el perfil arranca con una base vacía")

    print(f"PERFIL={perfil}")
    print(f"datos: {nuevo}")
    print(f"biblioteca de prueba: {base_nueva if not args.con_rutas_reales else base_real + '  (REAL: no borrar ni descargar)'}")
    for linea in informe:
        print("  " + linea)
    print("sin sesión de AniList (no se copió el token); volumen 0.")


# ───────────────────────────── iniciar / cerrar ─────────────────────────────

def sesiones(carpeta_datos):
    d = carpeta_datos / "Logs" / "sesiones"
    return sorted(d.glob("*.log")) if d.exists() else []


def parar(pid, espera=10):
    """Cierre educado por PID (WM_CLOSE: corre ProcessExit y se mata el daemon); si no sale, forzado con su árbol."""
    subprocess.run(["taskkill", "/PID", str(pid)], capture_output=True)
    for _ in range(espera * 2):
        if not pid_vivo(pid):
            return "cerrada"
        time.sleep(0.5)
    subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"], capture_output=True)
    return "forzada"


def iniciar(args):
    perfil = comprobar_perfil(args.perfil)
    exe = Path(args.exe) if args.exe else EXE_DEBUG
    if not exe.exists():
        sys.exit(f"No existe {exe}. Compila antes (skill repo-build-test).")
    previas = pids_app()
    if previas:
        print(f"Hay AnimeLocalTracker en marcha (PID {previas}): puede ser tu app real. La instancia única (mutex global) haría que "
              "una segunda se cierre sola, así que NO se lanza nada. No la cierres tú: pide a la persona que la cierre.")
        sys.exit(3)
    reales_antes = sesiones(datos_reales())
    propios_antes = sesiones(datos(perfil))  # un perfil reutilizado ya trae registros de ejecuciones anteriores

    env = os.environ.copy()
    env.update(USERPROFILE=str(perfil), LOCALAPPDATA=str(perfil / "AppData" / "Local"), APPDATA=str(perfil / "AppData" / "Roaming"),
               ANIMELOCALTRACKER_LOG_DETALLADO="1")
    if not args.con_red:
        env["ANIMELOCALTRACKER_SIN_RED"] = "1"
    proceso = subprocess.Popen([str(exe)], env=env, cwd=str(exe.parent))
    (perfil / "pid.txt").write_text(str(proceso.pid))

    limite = time.time() + 60
    while time.time() < limite and sesiones(datos(perfil)) == propios_antes:
        time.sleep(0.5)
    if sesiones(datos(perfil)) == propios_antes or sesiones(datos_reales()) != reales_antes:
        print("AISLAMIENTO NO CONFIRMADO (no hay registro de sesión en el perfil, o apareció uno en la carpeta real). Cierro la instancia.")
        parar(proceso.pid)
        sys.exit(2)
    print(f"PID={proceso.pid}")
    print(f"aislamiento confirmado; registro: {sesiones(datos(perfil))[-1]}")
    print("sin red" if not args.con_red else "CON RED: las descargas y consultas son reales")
    print("Espera ~25 s antes de hacer clics. Cierra con: perfil_aislado.py cerrar PERFIL")


def cerrar(args):
    perfil = comprobar_perfil(args.perfil)
    archivo = perfil / "pid.txt"
    if not archivo.exists():
        sys.exit("Este perfil no tiene pid.txt: no hay nada que cerrar desde aquí.")
    pid = int(archivo.read_text())
    if not pid_vivo(pid):
        print(f"PID {pid} ya no está en marcha.")
    else:
        print(f"PID {pid}: {parar(pid)}")
    archivo.unlink()


def borrar(args):
    perfil = comprobar_perfil(args.perfil)
    if (perfil / "pid.txt").exists():
        sys.exit("El perfil tiene pid.txt: cierra primero la app con 'cerrar'.")
    shutil.rmtree(perfil)
    print(f"borrado {perfil}")


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # la consola de Windows usa cp1252 y rompería los acentos
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="accion", required=True)
    c = sub.add_parser("crear")
    c.add_argument("--con-rutas-reales", action="store_true", help="no reescribe las rutas (la prueba tocaría tus videos reales)")
    i = sub.add_parser("iniciar")
    i.add_argument("perfil")
    i.add_argument("--con-red", action="store_true")
    i.add_argument("--exe")
    for nombre in ("cerrar", "borrar"):
        sub.add_parser(nombre).add_argument("perfil")
    args = p.parse_args()
    {"crear": crear, "iniciar": iniciar, "cerrar": cerrar, "borrar": borrar}[args.accion](args)


if __name__ == "__main__":
    main()
