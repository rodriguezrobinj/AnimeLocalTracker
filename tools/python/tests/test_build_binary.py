import build_binary


def _argumentos():
    return build_binary.argumentos_pyinstaller("python", "cli.py", "out", "build", ".")


def test_playwright_y_sus_dependencias_se_excluyen_del_ejecutable():
    args = _argumentos()

    excluidos = [args[i + 1] for i, a in enumerate(args) if a == "--exclude-module"]
    assert {"playwright", "greenlet", "pyee"} <= set(excluidos)


def test_opencv_no_se_empaqueta_en_el_ejecutable():
    # OpenCV pesaba ~110 MB y solo lo usaba la huella perceptual, que ahora extrae el fotograma con ffmpeg.
    args = _argumentos()

    excluidos = [args[i + 1] for i, a in enumerate(args) if a == "--exclude-module"]
    assert "cv2" in excluidos


def test_ya_no_se_recolecta_el_driver_de_playwright():
    # --collect-all playwright metía ~105 MB (node.exe + driver) que nunca se usan en el .exe (Byse está
    # deshabilitado allí); volver a ponerlo reintroduciría ese peso en cada release.
    args = _argumentos()

    assert "--collect-all" not in args


def test_conserva_las_opciones_de_empaquetado_del_daemon():
    args = _argumentos()

    assert "--onedir" in args
    assert args[args.index("--name") + 1] == "AnimeTrackerTools"
    assert args[-1] == "cli.py"
