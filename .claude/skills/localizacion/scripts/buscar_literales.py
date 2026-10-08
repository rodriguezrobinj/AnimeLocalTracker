"""Busca texto visible sin traducir en AnimeLocalTracker: literales en español en C# y atributos de texto con literal en XAML.

    python buscar_literales.py [archivo_o_carpeta ...]     (por defecto, todo el proyecto principal)

Es una heurística para REVISAR candidatos, no un veredicto: ignora comentarios, LocalizationService.cs, registros (AppLogger),
excepciones y nombres de archivo/ruta. Código de salida 0 siempre.
"""
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
RAIZ = Path(__file__).resolve().parents[4] / "AnimeLocalTracker"
IGNORAR_RUTA = re.compile(r"[\\/](obj|bin|Migrations)[\\/]|LocalizationService\.cs$")

# C#: literal de texto con tilde/ñ/¿/¡ (el español casi siempre las lleva) o con una palabra de UI muy común.
LITERAL_CS = re.compile(r'(?<!\$@)(?<!@)"((?:[^"\\\n]|\\.)*(?:[áéíóúñÁÉÍÓÚÑ¿¡]|\b(?:Episodio|Cancelar|Aceptar|Guardar|Error)\b)(?:[^"\\\n]|\\.)*)"')
# No son texto de UI: registros, excepciones, atributos y demás.
LINEA_IGNORADA_CS = re.compile(
    r"AppLogger\.|Debug\.|Trace\.|throw new|\[Obsolete|\[Description|Exception\(|nameof\(|//|\bUsing\b|_logger\.|Console\.|\[Conditional|\[Fact|\[Theory|\[InlineData"
)
# XAML: atributos de texto con literal (no {Binding}, no {loc:T}, no {StaticResource}...).
ATRIBUTO_XAML = re.compile(r'\b(Text|Content|ToolTip|Header|Title|Watermark|PlaceholderText|AutomationProperties\.Name|HintAssist\.Hint)\s*=\s*"([^"{][^"]*)"')


def candidatos_cs(ruta):
    for n, linea in enumerate(ruta.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        if LINEA_IGNORADA_CS.search(linea):
            continue
        for m in LITERAL_CS.finditer(linea):
            texto = m.group(1)
            if re.search(r"[\\/]|\.\w{2,4}$|^\W*$", texto):  # rutas, extensiones, solo símbolos
                continue
            yield n, texto.strip()


def candidatos_xaml(ruta):
    for n, linea in enumerate(ruta.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        for m in ATRIBUTO_XAML.finditer(linea):
            texto = m.group(2).strip()
            if re.search(r"[A-Za-zÁÉÍÓÚáéíóúñ]{3}", texto) and not texto.startswith(("pack:", "/", "#")):
                yield n, f"{m.group(1)}=\"{texto}\""


def main():
    destinos = [Path(a) for a in sys.argv[1:]] or [RAIZ]
    total = 0
    for destino in destinos:
        archivos = [destino] if destino.is_file() else sorted([*destino.rglob("*.cs"), *destino.rglob("*.xaml")])
        for ruta in archivos:
            if IGNORAR_RUTA.search(str(ruta)):
                continue
            gen = candidatos_cs(ruta) if ruta.suffix == ".cs" else candidatos_xaml(ruta)
            for n, texto in gen:
                total += 1
                print(f"{ruta.relative_to(RAIZ.parent) if RAIZ in ruta.parents else ruta}:{n}: {texto[:110]}")
    print(f"\n{total} candidato(s)")


if __name__ == "__main__":
    main()
