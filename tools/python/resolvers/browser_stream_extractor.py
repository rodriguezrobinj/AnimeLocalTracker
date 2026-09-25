import sys
import time
from typing import Any, Dict, List, Optional

# Dominios que solo renderizan su reproductor con JavaScript en el cliente (SPA sin
# nada server-side que scrapear) y para los que yt-dlp no tiene extractor propio.
# Se resuelven capturando la petición de red real del manifiesto HLS en vez de
# parsear HTML — investigado y confirmado contra el sitio real: Voe y UPNShare
# quedaron descartados (Voe se ahoga en ~150 peticiones de redes de publicidad sin
# llegar a pedir el video; UPNShare no disparó ninguna petición de video ni con
# clic simulado), solo Byse resultó viable de forma consistente.
BYSE_HOST_SUFFIXES = ("byselapuix.com",)


def es_dominio_byse(host: str) -> bool:
    host = (host or "").lower()
    return any(host == h or host.endswith("." + h) for h in BYSE_HOST_SUFFIXES)


class BrowserStreamExtractor:
    """
    Extrae streams de sitios cuyo embed es una SPA vacía (nada que scrapear con
    una petición HTTP normal): usa un navegador Chromium YA instalado en el sistema
    (Playwright con channel="msedge"/"chrome") en vez de empaquetar un Chromium
    propio — evita ~300-700MB extra en el instalador. Edge primero (viene de fábrica
    en Windows), Chrome como segundo intento si Edge no está. Firefox/Brave/Opera no
    aplican: Playwright no los reconoce como "canal" de navegador del sistema (a
    Firefox ni lo controla — usa su propia build parcheada, no la instalada).
    Si no hay ninguno de los dos, falla con un error claro y el proveedor sigue con
    el resto de servidores.

    Timing — medido contra el sitio real, no adivinado (dos regresiones ya
    descartadas, ver abajo):
    - _PRE_CLICK_SETTLE_MS **no se puede acortar**: el reproductor necesita ~8s
      tras cargar la página antes de que un clic le haga algo (probablemente
      termina de montar su JS/overlay recién ahí). Bajarlo a 1.5s dio 0/6 y 0/5
      reales contra el sitio, sin importar el resto de los ajustes.
    - UN SOLO clic, nunca reintentado: reintentar el clic cada pocos segundos si
      no había resultado también empeoró la tasa de éxito (1/5) — el reproductor
      es tipo play/pausa, así que un segundo clic probablemente lo pausaba justo
      cuando la red iba a pedir el video.
    - El sondeo activo solo se aplica DESPUÉS del clic, para cortar apenas llega
      la petición del m3u8 en vez de esperar siempre el presupuesto completo —
      eso sí es una mejora real (más rápido cuando funciona) sin tocar los
      tiempos que ya se demostró que hacían falta.

    SOLO DESARROLLO — deshabilitado en el .exe empaquetado (ver `_es_entorno_congelado`):
    Byse usa un desafío anti-bot (proof-of-work + "attestation": /api/videos/access/
    challenge → /attest → /embed/captcha → /captcha/verify → /embed/playback, recién
    ahí autoriza el video real) que se resuelve bien corriendo `python cli.py` pero
    falla 100% de las veces (0/6, 0/6, 0/3 en pruebas reales) dentro del binario
    compilado con PyInstaller — misma URL, mismos tiempos, mismo código. La causa
    exacta no se investigó a fondo (parent process/env vars/args de lanzamiento del
    navegador son las sospechas, sin confirmar). Hasta identificarla, intentarlo en
    el .exe solo desperdicia ~20s por episodio en un intento condenado a fallar —
    mejor fallar rápido y dejar que el proveedor caiga a MP4Upload de inmediato.
    """

    _NAVIGATION_TIMEOUT_MS = 25000
    _PRE_CLICK_SETTLE_MS = 8000
    _POLL_INTERVAL_MS = 500
    _MAX_POST_CLICK_WAIT_MS = 10000
    _DEFAULT_REFERER = "https://animeav1.com/"
    _CANALES_NAVEGADOR = ("msedge", "chrome")

    @staticmethod
    def _es_entorno_congelado() -> bool:
        """True dentro del .exe compilado con PyInstaller (sys.frozen), False
        corriendo `python cli.py` desde el código fuente."""
        return bool(getattr(sys, "frozen", False))

    @staticmethod
    def extract_byse(url: str, custom_headers: Optional[Dict[str, str]] = None) -> Dict[str, Any]:
        if BrowserStreamExtractor._es_entorno_congelado():
            return {
                "success": False,
                "error": (
                    "Byse deshabilitado en la app empaquetada: el desafío anti-bot del "
                    "sitio no se resuelve dentro del .exe compilado (sí funciona en "
                    "desarrollo). Ver docstring de BrowserStreamExtractor."
                ),
            }

        try:
            from playwright.sync_api import sync_playwright
        except ImportError:
            return {"success": False, "error": "Playwright no está instalado en el daemon."}

        referer = (custom_headers or {}).get("Referer") or BrowserStreamExtractor._DEFAULT_REFERER
        encontrados: List[str] = []

        try:
            with sync_playwright() as p:
                browser = None
                errores_lanzamiento = []
                for canal in BrowserStreamExtractor._CANALES_NAVEGADOR:
                    try:
                        browser = p.chromium.launch(channel=canal, headless=True)
                        break
                    except Exception as ex:
                        errores_lanzamiento.append(f"{canal}: {ex}")

                if browser is None:
                    detalle = " | ".join(errores_lanzamiento)
                    return {"success": False, "error": f"No se encontró Edge ni Chrome en el sistema ({detalle})."}

                try:
                    context = browser.new_context(
                        user_agent=(
                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                        ),
                        extra_http_headers={"Referer": referer},
                    )
                    page = context.new_page()
                    page.on(
                        "request",
                        lambda req: encontrados.append(req.url) if ".m3u8" in req.url else None,
                    )
                    page.goto(url, wait_until="domcontentloaded", timeout=BrowserStreamExtractor._NAVIGATION_TIMEOUT_MS)
                    page.wait_for_timeout(BrowserStreamExtractor._PRE_CLICK_SETTLE_MS)

                    try:
                        # El centro del viewport por defecto (1280x720) cae sobre el
                        # reproductor. UN SOLO clic — ver docstring de la clase.
                        page.mouse.click(640, 360)
                    except Exception:
                        pass

                    # Sondeo activo: corta apenas el listener de arriba capture la
                    # petición del m3u8, en vez de esperar siempre el presupuesto
                    # completo (la mayoría de los casos exitosos resuelven en los
                    # primeros segundos tras el clic).
                    inicio = time.monotonic()
                    while not encontrados:
                        transcurrido_ms = (time.monotonic() - inicio) * 1000
                        if transcurrido_ms >= BrowserStreamExtractor._MAX_POST_CLICK_WAIT_MS:
                            break
                        page.wait_for_timeout(BrowserStreamExtractor._POLL_INTERVAL_MS)
                finally:
                    browser.close()
        except Exception as ex:
            return {"success": False, "error": f"Error resolviendo con el navegador: {ex}"}

        # Preferir el manifiesto "master" (lista las variantes de calidad); si el
        # sitio no publica uno con ese nombre, cualquier .m3u8 capturado sirve.
        directo = next((u for u in encontrados if "master" in u.lower()), None) or (
            encontrados[0] if encontrados else ""
        )
        if not directo.startswith("https://"):
            return {"success": False, "error": "No se detectó ninguna petición de video (m3u8) en la página."}

        return {
            "success": True,
            "title": "",
            "duration": 0,
            "direct_url": directo,
            "directUrl": directo,
            "thumbnail": "",
            "http_headers": {"Referer": referer},
            "httpHeaders": {"Referer": referer},
            "formats": [],
            "subtitles": [],
        }
