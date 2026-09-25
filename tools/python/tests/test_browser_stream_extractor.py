import sys
import os
from unittest.mock import MagicMock, patch

sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..')))

from resolvers.browser_stream_extractor import BrowserStreamExtractor, es_dominio_byse


def test_es_dominio_byse_reconoce_el_host_exacto_y_subdominios():
    assert es_dominio_byse("byselapuix.com") is True
    assert es_dominio_byse("www.byselapuix.com") is True
    assert es_dominio_byse("BYSELAPUIX.COM") is True


def test_es_dominio_byse_rechaza_otros_hosts():
    assert es_dominio_byse("mp4upload.com") is False
    assert es_dominio_byse("evil-byselapuix.com.attacker.net") is False
    assert es_dominio_byse("") is False
    assert es_dominio_byse(None) is False


def test_extract_byse_en_entorno_congelado_falla_rapido_sin_tocar_playwright():
    # El desafío anti-bot de Byse no se resuelve dentro del .exe de PyInstaller
    # (confirmado contra el sitio real: 0/6, 0/6, 0/3) — en ese entorno debe
    # fallar YA, sin gastar ~20s lanzando un navegador condenado a fallar.
    with patch("resolvers.browser_stream_extractor.BrowserStreamExtractor._es_entorno_congelado", return_value=True), \
         patch("playwright.sync_api.sync_playwright") as mock_sync_playwright:
        resultado = BrowserStreamExtractor.extract_byse("https://byselapuix.com/e/xyz")

    assert resultado["success"] is False
    assert "empaquetada" in resultado["error"]
    mock_sync_playwright.assert_not_called()


def _mock_playwright_con_requests(urls_a_disparar):
    """Simula sync_playwright().chromium.launch(...).new_context().new_page() y
    dispara los request handlers registrados con las URLs dadas, como si el
    navegador real las hubiera pedido durante page.goto/wait_for_timeout."""
    page = MagicMock()
    handlers = []
    page.on.side_effect = lambda event, handler: handlers.append(handler)

    def goto_side_effect(*args, **kwargs):
        req = MagicMock()
        for u in urls_a_disparar:
            req.url = u
            for h in handlers:
                h(req)

    page.goto.side_effect = goto_side_effect

    context = MagicMock()
    context.new_page.return_value = page
    browser = MagicMock()
    browser.new_context.return_value = context
    chromium = MagicMock()
    chromium.launch.return_value = browser

    pw_instance = MagicMock()
    pw_instance.chromium = chromium
    pw_cm = MagicMock()
    pw_cm.__enter__.return_value = pw_instance
    pw_cm.__exit__.return_value = False

    return pw_cm, browser


def test_extract_byse_devuelve_el_master_m3u8_capturado():
    urls = [
        "https://cdn.example.com/hls/index-v1-a1.m3u8?t=abc",
        "https://cdn.example.com/hls/master.m3u8?t=abc",
    ]
    pw_cm, browser = _mock_playwright_con_requests(urls)

    with patch("playwright.sync_api.sync_playwright", return_value=pw_cm):
        resultado = BrowserStreamExtractor.extract_byse("https://byselapuix.com/e/xyz")

    assert resultado["success"] is True
    assert resultado["direct_url"] == "https://cdn.example.com/hls/master.m3u8?t=abc"
    assert resultado["directUrl"] == resultado["direct_url"]
    browser.close.assert_called_once()


def test_extract_byse_sin_master_usa_el_primer_m3u8_capturado():
    urls = ["https://cdn.example.com/hls/index-v1-a1.m3u8?t=abc"]
    pw_cm, _ = _mock_playwright_con_requests(urls)

    with patch("playwright.sync_api.sync_playwright", return_value=pw_cm):
        resultado = BrowserStreamExtractor.extract_byse("https://byselapuix.com/e/xyz")

    assert resultado["success"] is True
    assert resultado["direct_url"] == "https://cdn.example.com/hls/index-v1-a1.m3u8?t=abc"


def _reloj_creciente(paso_ms=4000):
    """Simula el paso del tiempo sin dormir de verdad: cada llamada a time.monotonic()
    avanza `paso_ms` milisegundos, para poder agotar el presupuesto de sondeo del
    extractor en el test sin esperar los ~16s reales."""
    estado = {"t": 0.0}

    def _tick():
        valor = estado["t"]
        estado["t"] += paso_ms / 1000.0
        return valor

    return _tick


def test_extract_byse_sin_ninguna_peticion_de_video_devuelve_error():
    pw_cm, _ = _mock_playwright_con_requests([])

    with patch("playwright.sync_api.sync_playwright", return_value=pw_cm), \
         patch("resolvers.browser_stream_extractor.time.monotonic", side_effect=_reloj_creciente()):
        resultado = BrowserStreamExtractor.extract_byse("https://byselapuix.com/e/xyz")

    assert resultado["success"] is False
    assert "m3u8" in resultado["error"]


def test_extract_byse_hace_un_solo_clic_nunca_lo_reintenta():
    # Regresión: se probó reintentar el clic cada pocos segundos si no había
    # resultado y la tasa de éxito empeoró de forma medible contra el sitio real
    # (1/5 en vez de 5/5) — el reproductor es play/pausa y un segundo clic
    # probablemente lo pausaba justo cuando la red iba a pedir el video. Un solo
    # clic, siempre.
    handlers = []
    page = MagicMock()
    page.on.side_effect = lambda event, handler: handlers.append(handler)
    page.goto.side_effect = lambda *a, **k: None  # nada durante la carga inicial

    def click_side_effect(x, y):
        # El video "carga" bastante después del clic (simula el buffering real).
        pass

    page.mouse.click.side_effect = click_side_effect

    context = MagicMock()
    context.new_page.return_value = page
    browser = MagicMock()
    browser.new_context.return_value = context
    chromium = MagicMock()
    chromium.launch.return_value = browser
    pw_instance = MagicMock()
    pw_instance.chromium = chromium
    pw_cm = MagicMock()
    pw_cm.__enter__.return_value = pw_instance
    pw_cm.__exit__.return_value = False

    with patch("playwright.sync_api.sync_playwright", return_value=pw_cm), \
         patch("resolvers.browser_stream_extractor.time.monotonic", side_effect=_reloj_creciente(4000)):
        BrowserStreamExtractor.extract_byse("https://byselapuix.com/e/xyz")

    assert page.mouse.click.call_count == 1


def test_extract_byse_corta_el_sondeo_apenas_llega_el_video_sin_esperar_el_resto():
    # Sondeo activo: si el video llega en la primera vuelta del sondeo, no debe
    # seguir esperando el presupuesto completo (page.wait_for_timeout no se llama
    # más veces de las necesarias tras detectarlo).
    handlers = []
    page = MagicMock()
    page.on.side_effect = lambda event, handler: handlers.append(handler)
    page.goto.side_effect = lambda *a, **k: None

    def click_side_effect(x, y):
        req = MagicMock()
        req.url = "https://cdn.example.com/hls/master.m3u8"
        for h in handlers:
            h(req)

    page.mouse.click.side_effect = click_side_effect

    context = MagicMock()
    context.new_page.return_value = page
    browser = MagicMock()
    browser.new_context.return_value = context
    chromium = MagicMock()
    chromium.launch.return_value = browser
    pw_instance = MagicMock()
    pw_instance.chromium = chromium
    pw_cm = MagicMock()
    pw_cm.__enter__.return_value = pw_instance
    pw_cm.__exit__.return_value = False

    with patch("playwright.sync_api.sync_playwright", return_value=pw_cm), \
         patch("resolvers.browser_stream_extractor.time.monotonic", side_effect=_reloj_creciente(4000)):
        resultado = BrowserStreamExtractor.extract_byse("https://byselapuix.com/e/xyz")

    assert resultado["success"] is True
    # El click ya dispara la petición antes de entrar al bucle de sondeo: no debió
    # llamar a wait_for_timeout dentro del bucle (la primera comprobación del
    # while ya encuentra `encontrados` no vacío).
    assert page.wait_for_timeout.call_args_list.count(((BrowserStreamExtractor._POLL_INTERVAL_MS,), {})) == 0


def test_extract_byse_sin_edge_ni_chrome_devuelve_error_claro():
    pw_instance = MagicMock()
    pw_instance.chromium.launch.side_effect = Exception("Executable doesn't exist")
    pw_cm = MagicMock()
    pw_cm.__enter__.return_value = pw_instance
    pw_cm.__exit__.return_value = False

    with patch("playwright.sync_api.sync_playwright", return_value=pw_cm):
        resultado = BrowserStreamExtractor.extract_byse("https://byselapuix.com/e/xyz")

    assert resultado["success"] is False
    assert "Edge" in resultado["error"]
    assert "Chrome" in resultado["error"]
    # Se intentaron los dos canales, no solo el primero.
    assert pw_instance.chromium.launch.call_count == 2


def test_extract_byse_sin_edge_cae_a_chrome_como_segundo_intento():
    urls = ["https://cdn.example.com/hls/master.m3u8?t=abc"]
    pw_cm, browser_chrome = _mock_playwright_con_requests(urls)
    pw_instance = pw_cm.__enter__.return_value

    # El primer intento (Edge) falla; el segundo (Chrome) sí lanza el navegador mockeado.
    pw_instance.chromium.launch.side_effect = [Exception("msedge no encontrado"), browser_chrome]

    with patch("playwright.sync_api.sync_playwright", return_value=pw_cm):
        resultado = BrowserStreamExtractor.extract_byse("https://byselapuix.com/e/xyz")

    assert resultado["success"] is True
    assert resultado["direct_url"] == "https://cdn.example.com/hls/master.m3u8?t=abc"
    assert pw_instance.chromium.launch.call_count == 2
    primer_canal = pw_instance.chromium.launch.call_args_list[0].kwargs["channel"]
    segundo_canal = pw_instance.chromium.launch.call_args_list[1].kwargs["channel"]
    assert (primer_canal, segundo_canal) == ("msedge", "chrome")


def test_extract_byse_usa_referer_de_headers_personalizados_si_se_pasa():
    pw_cm, browser = _mock_playwright_con_requests(["https://cdn.example.com/hls/master.m3u8"])

    with patch("playwright.sync_api.sync_playwright", return_value=pw_cm):
        resultado = BrowserStreamExtractor.extract_byse(
            "https://byselapuix.com/e/xyz", {"Referer": "https://otro-sitio.com/"}
        )

    assert resultado["http_headers"]["Referer"] == "https://otro-sitio.com/"
    browser.new_context.assert_called_once()
    _, kwargs = browser.new_context.call_args
    assert kwargs["extra_http_headers"]["Referer"] == "https://otro-sitio.com/"
