"""Complejidad por método y por clase: longitud, ramas, anidamiento, banderas booleanas, dependencias del constructor."""
import re
from collections import Counter, defaultdict
from pathlib import Path

raiz = Path(r'C:\Users\HP\RiderProjects\AnimeLocalTracker\AnimeLocalTracker')
archivos = [p for p in raiz.rglob('*.cs') if 'obj' not in p.parts and 'bin' not in p.parts and p.name != 'LocalizationService.cs']
rel = lambda p: str(p.relative_to(raiz)).replace('\\', '/')

FIRMA = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|partial|unsafe|new|extern)\s+)+[\w<>\[\],.?\s()]+?\s+(\w+)\s*(?:<[^>]+>)?\s*\(([^;{]*)$')
RAMAS = re.compile(r'\b(if|else if|case|for|foreach|while|catch|when)\b|&&|\|\||\?\?|\?(?=[^?.:\n]*:)')

metodos = []
clases = []
for p in archivos:
    lineas = p.read_text(encoding='utf-8-sig').splitlines()
    texto = '\n'.join(lineas)
    campos = len(re.findall(r'^\s*(?:private|protected|internal)\s+(?:static\s+)?(?:readonly\s+)?[\w<>\[\],.?]+\s+_\w+\s*(=|;)', texto, re.M))
    banderas = len(re.findall(r'^\s*(?:\[ObservableProperty\]\s*)?(?:private|protected|internal)\s+(?:static\s+)?(?:volatile\s+)?bool\s+_\w+', texto, re.M))
    ctor = max((len([a for a in m.group(1).split(',') if a.strip()]) for m in re.finditer(r'public\s+' + re.escape(p.stem.split('.')[0]) + r'\s*\(([^)]*)\)', texto, re.S)), default=0)
    clases.append((len(lineas), rel(p), campos, banderas, ctor,
                   len(re.findall(r'\[ObservableProperty\]', texto)), len(re.findall(r'\[RelayCommand', texto)),
                   len(re.findall(r'IRecipient<', texto))))
    i = 0
    while i < len(lineas):
        m = FIRMA.match(lineas[i])
        if m and m.group(1) not in ('if', 'while', 'for', 'foreach', 'switch', 'using', 'lock', 'catch', 'return', 'new'):
            # buscar la llave de apertura
            j = i
            while j < len(lineas) and '{' not in lineas[j] and '=>' not in lineas[j] and ';' not in lineas[j]:
                j += 1
            if j < len(lineas) and '{' in lineas[j] and '=>' not in lineas[j].split('{')[0]:
                prof = 0; k = j; maxprof = 0
                while k < len(lineas):
                    s = re.sub(r'"(?:[^"\\]|\\.)*"|//.*$', '', lineas[k])
                    prof += s.count('{') - s.count('}')
                    maxprof = max(maxprof, prof)
                    if prof <= 0:
                        break
                    k += 1
                cuerpo = '\n'.join(lineas[j:k + 1])
                largo = k - i + 1
                if largo >= 8:
                    metodos.append((len(RAMAS.findall(cuerpo)), largo, maxprof - 1, rel(p), i + 1, m.group(1)))
                i = k
        i += 1

print(f'MÉTODOS analizados (≥8 líneas): {len(metodos)}')
largos = Counter()
for r, l, a, *_ in metodos:
    largos['≥200 líneas' if l >= 200 else '100–199' if l >= 100 else '60–99' if l >= 60 else '<60'] += 1
print('  por longitud:', dict(largos))
print('\nTOP 28 por ramas (ramas / líneas / anidamiento):')
for r, l, a, f, n, nombre in sorted(metodos, reverse=True)[:28]:
    print(f'  {r:4d} / {l:4d} / {a}   {f}:{n}  {nombre}')
print('\nTOP 12 por anidamiento (≥6 niveles):')
for r, l, a, f, n, nombre in sorted((m for m in metodos if m[2] >= 6), key=lambda m: (-m[2], -m[0]))[:12]:
    print(f'  niveles={a} ramas={r} líneas={l}   {f}:{n}  {nombre}')

print('\nCLASES (líneas / campos _ / banderas bool / parámetros del constructor / [ObservableProperty] / [RelayCommand] / IRecipient):')
for c in sorted(clases, reverse=True)[:22]:
    print(f'  {c[0]:5d}  campos={c[2]:3d} bool={c[3]:3d} ctor={c[4]:2d} props={c[5]:3d} cmds={c[6]:3d} recibe={c[7]:2d}  {c[1]}')
por_archivo = defaultdict(lambda: [0, 0])
for r, l, a, f, n, nombre in metodos:
    por_archivo[f][0] += r; por_archivo[f][1] += 1
print('\nARCHIVOS con más ramas en total:')
for f, (r, n) in sorted(por_archivo.items(), key=lambda x: -x[1][0])[:12]:
    print(f'  {r:5d} ramas en {n:3d} métodos  {f}')
