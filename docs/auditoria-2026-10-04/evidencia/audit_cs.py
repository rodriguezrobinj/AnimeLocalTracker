"""Auditoría de duplicación en C#: clones por ventana deslizante de líneas normalizadas y conteo de patrones."""
import hashlib
import re
from collections import Counter, defaultdict
from pathlib import Path

raiz = Path(r'C:\Users\HP\RiderProjects\AnimeLocalTracker\AnimeLocalTracker')
archivos = [p for p in raiz.rglob('*.cs') if 'obj' not in p.parts and 'bin' not in p.parts and p.name != 'LocalizationService.cs']
rel = lambda p: str(p.relative_to(raiz)).replace('\\', '/')
textos = {p: p.read_text(encoding='utf-8-sig') for p in archivos}

VENTANA = 6

def significativas(texto):
    en_bloque = False
    for i, linea in enumerate(texto.splitlines(), 1):
        s = linea.strip()
        if en_bloque:
            if '*/' in s: en_bloque = False
            continue
        if s.startswith('/*'):
            en_bloque = '*/' not in s
            continue
        if not s or s.startswith('//') or s in ('{', '}', '};', ');', '{ }') or s.startswith(('using ', 'namespace ', '[', '#')):
            continue
        s = re.sub(r'//.*$', '', s).strip()
        s = re.sub(r'\$?@?"(?:[^"\\]|\\.)*"', '"S"', s)
        s = re.sub(r'\b\d+(\.\d+)?\b', 'N', s)
        s = re.sub(r'\s+', ' ', s)
        if len(s) > 3:
            yield i, s

lineas = {p: list(significativas(t)) for p, t in textos.items()}
indice = defaultdict(list)
for p, ls in lineas.items():
    for k in range(len(ls) - VENTANA + 1):
        clave = hashlib.md5('\n'.join(s for _, s in ls[k:k + VENTANA]).encode()).hexdigest()
        indice[clave].append((p, k))

# Pares de posiciones clonadas -> se funden en tramos contiguos por par de archivos
pares = defaultdict(set)
for ocurrencias in indice.values():
    if len(ocurrencias) < 2 or len(ocurrencias) > 12:
        continue
    for a in range(len(ocurrencias)):
        for b in range(a + 1, len(ocurrencias)):
            (pa, ka), (pb, kb) = sorted([ocurrencias[a], ocurrencias[b]], key=lambda x: (rel(x[0]), x[1]))
            if pa == pb and abs(ka - kb) < VENTANA:
                continue
            pares[(pa, pb)].add((ka, kb))

tramos = []
for (pa, pb), puntos in pares.items():
    restantes = set(puntos)
    for ka, kb in sorted(puntos):
        if (ka, kb) not in restantes:
            continue
        largo = 0
        while (ka + largo, kb + largo) in restantes:
            restantes.discard((ka + largo, kb + largo))
            largo += 1
        n = largo + VENTANA - 1
        tramos.append((n, rel(pa), lineas[pa][ka][0], rel(pb), lineas[pb][kb][0], lineas[pa][ka][1][:70]))

tramos.sort(reverse=True)
total = sum(t[0] for t in tramos)
print(f'CLONES (≥{VENTANA} líneas significativas iguales, ignorando textos y números): {len(tramos)} tramos, ≈{total} líneas clonadas')
entre = [t for t in tramos if t[1] != t[3]]
dentro = [t for t in tramos if t[1] == t[3]]
print(f'  entre archivos distintos: {len(entre)} tramos ≈{sum(t[0] for t in entre)} líneas | dentro del mismo archivo: {len(dentro)} ≈{sum(t[0] for t in dentro)}')
print('\nTOP entre archivos:')
for n, a, la, b, lb, muestra in entre[:28]:
    print(f'  {n:3d} líneas  {a}:{la}  ≈  {b}:{lb}   | {muestra}')
print('\nTOP dentro del mismo archivo:')
for n, a, la, b, lb, muestra in dentro[:14]:
    print(f'  {n:3d} líneas  {a}:{la} ≈ :{lb}   | {muestra}')

por_par = Counter()
for n, a, la, b, lb, _ in entre:
    por_par[(a, b)] += n
print('\nPARES de archivos con más líneas clonadas:')
for (a, b), n in por_par.most_common(14):
    print(f'  {n:4d}  {a}  ↔  {b}')

# ── Patrones ──
todo = '\n'.join(textos.values())
def cuenta(patron, flags=0): return len(re.findall(patron, todo, flags))
def en(patron): return sum(1 for t in textos.values() if re.search(patron, t))
print('\nPATRONES:')
filas = [
    ('Dispatcher.CheckAccess() … else InvokeAsync/BeginInvoke', r'Dispatcher\.CheckAccess\(\)'),
    ('Application.Current.Dispatcher / ?.Dispatcher', r'Application\.Current\??\.Dispatcher'),
    ('new ProcessStartInfo', r'new ProcessStartInfo'),
    ('ReadToEndAsync (drenar salida de procesos)', r'ReadToEndAsync'),
    ('new HttpClient(', r'new HttpClient\('),
    ('new JsonSerializerOptions', r'new (System\.Text\.Json\.)?JsonSerializerOptions'),
    ('catch vacío: catch { } / catch (…) { }', r'catch\s*(\([^)]*\))?\s*\{\s*(/\*[^*]*\*/)?\s*\}'),
    ('catch (Exception ex) + AppLogger', r'catch \(Exception ex\)\s*\{\s*AppLogger\.'),
    ('ObtenerConfiguracion() (leer ajustes)', r'ObtenerConfiguracion\(\)'),
    ('GuardarConfiguracionAsync (guardar ajustes)', r'GuardarConfiguracionAsync\('),
    ('WeakReferenceMessenger.Default.Register', r'WeakReferenceMessenger\.Default\.Register'),
    ('WeakReferenceMessenger.Default.Send', r'WeakReferenceMessenger\.Default\.Send'),
    ('[ObservableProperty]', r'\[ObservableProperty\]'),
    ('propiedad manual con SetProperty(ref', r'SetProperty\(ref '),
    ('[RelayCommand]', r'\[RelayCommand'),
    ('async void', r'async void '),
    ('[DllImport', r'\[DllImport'),
    ('[LibraryImport', r'\[LibraryImport'),
    ('LocalizationService.T(', r'LocalizationService\.T\('),
    ('MostrarDialogoAsync(', r'MostrarDialogoAsync\('),
    ('Toast / MostrarToast', r'MostrarToast\w*\('),
    ('.ToLocalTime()', r'\.ToLocalTime\(\)'),
    ('SHA256.HashData', r'SHA256\.HashData'),
    ('Path.GetTempPath()', r'Path\.GetTempPath\(\)'),
    ('Task.Run(', r'Task\.Run\('),
    ('CancellationTokenSource nuevo', r'new CancellationTokenSource'),
    ('?.Cancel(); ?.Dispose(); (reinicio de CTS)', r'\?\.Cancel\(\);\s*\w+\?\.Dispose\(\);'),
    ('EstaCargando = true', r'EstaCargando = true'),
    ('IsBusy/Cargando try…finally', r'finally\s*\{\s*\w*(Cargando|IsBusy|Ocupado)\w* = false'),
    ('1024 (formato de tamaños a mano)', r'1024(\.0)?\s*[*/]\s*1024|/ 1024'),
    ('interfaces declaradas', r'\binterface I[A-Z]\w+'),
]
for nombre, patron in filas:
    print(f'  {cuenta(patron, re.S):5d} en {en(patron):3d} archivos  {nombre}')

dll = Counter(re.findall(r'\[(?:DllImport|LibraryImport)\("([^"]+)"[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:public|private|internal)[^;(]*?\b(\w+)\(', todo, re.S))
rep = {k: v for k, v in dll.items() if v > 1}
print(f'  P/Invoke declarados más de una vez: {len(rep)} funciones →', ', '.join(f'{f}×{n}' for (_, f), n in sorted(rep.items(), key=lambda x: -x[1])[:14]))
