"""Auditoría de duplicación en XAML: bloques repetidos (Style, ControlTemplate, Storyboard, Border con efecto…),
literales de color que ya tienen pincel en la paleta, y patrones repetidos."""
import hashlib
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

raiz = Path(r'C:\Users\HP\RiderProjects\AnimeLocalTracker\AnimeLocalTracker')
archivos = [p for p in raiz.rglob('*.xaml') if 'obj' not in p.parts and 'bin' not in p.parts]
textos = {p: p.read_text(encoding='utf-8-sig') for p in archivos}
rel = lambda p: str(p.relative_to(raiz)).replace('\\', '/')

# ── 1. Paleta de App.xaml: color -> clave ──
app = textos[raiz / 'App.xaml']
paleta = {}
for m in re.finditer(r'<SolidColorBrush\s+x:Key="([^"]+)"\s+Color="(#[0-9A-Fa-f]{6,8})"', app):
    paleta.setdefault(m.group(2).upper(), []).append(m.group(1))
for m in re.finditer(r'<Color\s+x:Key="([^"]+)">(#[0-9A-Fa-f]{6,8})</Color>', app):
    paleta.setdefault(m.group(2).upper(), []).append(m.group(1))
print(f'PALETA App.xaml: {sum(len(v) for v in paleta.values())} pinceles/colores con clave, {len(paleta)} colores distintos')

literales = Counter()
por_archivo = defaultdict(Counter)
for p, t in textos.items():
    if p.name == 'App.xaml':
        continue
    for m in re.finditer(r'"(#[0-9A-Fa-f]{6,8})"', t):
        c = m.group(1).upper()
        literales[c] += 1
        por_archivo[rel(p)][c] += 1
con_pincel = sum(n for c, n in literales.items() if c in paleta)
print(f'LITERALES fuera de App.xaml: {sum(literales.values())} usos, {len(literales)} colores; '
      f'{con_pincel} usos ({con_pincel * 100 // max(1, sum(literales.values()))} %) son de un color que YA tiene pincel en la paleta')
print('  top con pincel existente:', ', '.join(f'{c}×{n}→{paleta[c][0]}' for c, n in literales.most_common(40) if c in paleta)[:900])
print('  top SIN pincel:', ', '.join(f'{c}×{n}' for c, n in literales.most_common(60) if c not in paleta)[:600])

# ── 2. Bloques repetidos ──
def bloques(texto, etiqueta):
    """Bloques <etiqueta ...>...</etiqueta> de primer nivel de esa etiqueta (sin anidar la misma)."""
    for m in re.finditer(rf'<{etiqueta}(?=[\s>])[^>]*?(?<!/)>.*?</{etiqueta}>', texto, re.S):
        yield m.group(0), texto.count('\n', 0, m.start()) + 1

def normalizar(b):
    b = re.sub(r'<!--.*?-->', '', b, flags=re.S)
    b = re.sub(r'\s+', ' ', b)
    b = re.sub(r'x:(Key|Name)="[^"]*"', '', b)
    return b.strip()

for etiqueta, minimo in (('Style', 6), ('ControlTemplate', 6), ('Storyboard', 2), ('DataTemplate', 8), ('Border.Effect', 1), ('materialDesign:PackIcon.Style', 4), ('ProgressBar', 3)):
    grupos = defaultdict(list)
    total = 0
    for p, t in textos.items():
        for b, linea in bloques(t, etiqueta):
            if b.count('\n') + 1 < minimo:
                continue
            total += 1
            grupos[hashlib.md5(normalizar(b).encode()).hexdigest()].append((rel(p), linea, b.count('\n') + 1))
    repetidos = sorted((g for g in grupos.values() if len(g) > 1), key=lambda g: -len(g) * g[0][2])
    sobran = sum((len(g) - 1) * g[0][2] for g in repetidos)
    print(f'\n<{etiqueta}> de ≥{minimo} líneas: {total} bloques; {len(repetidos)} grupos IDÉNTICOS repetidos; líneas sobrantes ≈ {sobran}')
    for g in repetidos[:7]:
        donde = Counter(a for a, _, _ in g)
        print(f'   ×{len(g)} de {g[0][2]} líneas: ' + ', '.join(f'{a}({n})' for a, n in donde.most_common(4)) + f'  [p.ej. {g[0][0]}:{g[0][1]}]')

# ── 3. Estilos casi iguales: misma firma de Setters ignorando valores ──
firmas = defaultdict(list)
for p, t in textos.items():
    for b, linea in bloques(t, 'Style'):
        if b.count('\n') + 1 < 8:
            continue
        tipo = re.search(r'TargetType="([^"]+)"', b)
        props = tuple(sorted(set(re.findall(r'<Setter Property="([^"]+)"', b))))
        if len(props) >= 4:
            firmas[(tipo.group(1) if tipo else '?', props)].append((rel(p), linea, b.count('\n') + 1))
casi = sorted((g for g in firmas.items() if len(g[1]) > 2), key=lambda g: -sum(x[2] for x in g[1]))
print(f'\nESTILOS con la misma lista de propiedades (≥3 copias, valores quizá distintos): {len(casi)} familias')
for (tipo, props), g in casi[:8]:
    donde = Counter(a for a, _, _ in g)
    print(f'   {tipo} ×{len(g)} ({sum(x[2] for x in g)} líneas): ' + ', '.join(f'{a}({n})' for a, n in donde.most_common(5)))

# ── 4. Patrones sueltos ──
todo = '\n'.join(textos.values())
def cuenta(patron): return len(re.findall(patron, todo, re.S))
print('\nPATRONES:')
print('  botón solo-icono con ToolTip y AutomationProperties.Name con el MISMO binding:',
      cuenta(r'ToolTip="(\{Binding \[[^\]]+\][^"]*)"\s+AutomationProperties\.Name="\1"'))
print('  spinner MaterialDesignCircularProgressBar:', cuenta(r'MaterialDesignCircularProgressBar'))
print('  DownloadCircleOutline (botón de descarga):', cuenta(r'DownloadCircleOutline'), 'en', sum('DownloadCircleOutline' in t for t in textos.values()), 'archivos')
print('  AppEmptyTitle (estado vacío):', cuenta(r'AppEmptyTitle'), 'en', sum('AppEmptyTitle' in t for t in textos.values()), 'archivos')
print('  CornerRadius literales:', dict(Counter(re.findall(r'CornerRadius="([^"{]+)"', todo)).most_common(8)))
print('  FontSize literales:', dict(Counter(re.findall(r'FontSize="([^"{]+)"', todo)).most_common(10)))
print('  Duration de animaciones:', dict(Counter(re.findall(r'Duration="([^"]+)"', todo)).most_common(10)))
print('  EasingFunction inline:', cuenta(r'<(Cubic|Quadratic|Sine|Quartic|Back|Circle|Exponential)Ease'), '| RepeatBehavior="Forever":', cuenta(r'RepeatBehavior="Forever"'))
print('  Trigger IsMouseOver:', cuenta(r'Property="IsMouseOver"'), '| Cursor="Hand" literal:', cuenta(r'Cursor="Hand"'))
print('  RelativeSource AncestorType=UserControl:', cuenta(r'RelativeSource=\{RelativeSource AncestorType=(\{x:Type )?UserControl'))
print('  conversores declarados por vista:', dict(Counter(re.findall(r'<(?:conv|converters|local|c):(\w+Converter)\s+x:Key', todo)).most_common(8)))
print('  BooleanToVisibilityConverter declarado en', sum('<BooleanToVisibilityConverter' in t for t in textos.values()), 'archivos')
