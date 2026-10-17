# Mapa de calor de visionado — plan de implementación

> Para quien lo ejecute: seguir las tareas en orden con `superpowers:executing-plans`. Los pasos usan casillas
> (`- [ ]`). **Sin commits**: en este repo no se hace commit ni push sin orden del usuario (`CLAUDE.md`); cuando la pida,
> se usa el skill `commit-es`.

**Objetivo:** una tarjeta "Constancia" en Estadísticas con la cuadrícula de las últimas 53 semanas, coloreada según los
episodios vistos cada día, y el resto de Estadísticas contando por día local.

**Arquitectura:** un calculador estático y puro (`CalculadorMapaCalor`) convierte la lista de días locales en 371 celdas
ya ordenadas para un `UniformGrid` de 7×53, más las etiquetas de mes y día. `EstadisticasViewModel` calcula una sola
lista de días locales, la usa para el mapa y para las cifras que ya existían, y publica el resultado. La vista lo pinta
con tres `ItemsControl` dentro de un `Viewbox`.

**Tecnología:** .NET 8, WPF, CommunityToolkit.Mvvm, xUnit + FluentAssertions + Moq.

**Diseño:** `docs/investigacion-mapa-calor-visionado.md` (aprobado el 2026-10-16). Leerlo antes de empezar.

## Restricciones globales

- Sin colores nuevos: solo `Brush.*` de `App.xaml` (`Brush.Success`, `Brush.SurfaceChip`, `Brush.Surface1`,
  `Brush.Border`, `Brush.TextPrimary`, `Brush.TextSecondary`, `Brush.TextTertiary`) y `Transparent`.
- Todo texto visible sale de `LocalizationService` (claves en `Es` y en `En`, mismos marcadores); fechas y nombres de
  mes/día con `LocalizationService.Cultura`, nunca `CurrentCulture` (skill `localizacion`).
- Fechas de la base = UTC con `Kind = Unspecified`: se pasan a local con `MotorLogros.ALocal` (`persistence.md` #4).
- Cuenta un episodio si `VistoLocal` y `UltimaReproduccion != null`.
- Niveles: 0 → 0 · 1 → 1 · 2–3 → 2 · 4–6 → 3 · 7+ → 4; fuera de rango → −1.
- Rango: 53 columnas; la última es la semana de hoy; los días posteriores a hoy no se pintan ni tienen tooltip.
- Las pruebas no tocan `AppDataPaths` ni cambian `LocalizationService.Instance.Idioma` (otras pruebas lo usan en
  paralelo): el calculador recibe la cultura por parámetro y las pruebas comprueban fechas y números, no los textos
  fijos de un idioma.
- Compilar y probar con el skill `repo-build-test` (app sola primero; pruebas con `-p:BuildProjectReferences=false`).

## Puntos de revisión (casos que el diseño no nombra)

1. **Celda futura con tooltip vacío:** al pasar por un día posterior a hoy no debe salir un recuadro de tooltip en
   blanco. Prueba en la tarea 3 (`ToolTipService.IsEnabled` en falso para nivel −1).
2. **Hoy es el primer día de la semana:** la última columna tiene un solo día en rango y el resto futuro; hoy sigue en
   la última columna. Prueba en la tarea 1 (lunes en `es-ES`, domingo en `en-US`).
3. **Reproducciones anteriores al inicio del rango:** no cuentan en el mapa ni en sus totales. Prueba en la tarea 1.
4. **Reproducción con fecha futura** (reloj cambiado, viaje entre zonas horarias): no cuenta en el mapa. Prueba en la
   tarea 1.
5. **Día 1 de mes en el futuro dentro de la semana actual** (hoy 28 de octubre, la semana incluye el 1 de noviembre):
   no se etiqueta "nov" en la última columna. Prueba en la tarea 1.

---

### Tarea 1: `CalculadorMapaCalor` y sus textos

**Archivos:**
- Crear: `AnimeLocalTracker/Services/CalculadorMapaCalor.cs`
- Modificar: `AnimeLocalTracker/Services/LocalizationService.cs` (bloque `Stats_` de `Es`, junto a
  `["Stats_PorAnioSub"]`, hoy línea 230; y el mismo bloque de `En`, hoy línea 1624)
- Crear prueba: `AnimeLocalTracker.Tests/Services/CalculadorMapaCalorTests.cs`

**Interfaces:**
- Produce:
  - `public sealed record DiaCalor(DateTime Fecha, int Episodios, int Nivel, string Texto);`
  - `public sealed record MapaCalor(IReadOnlyList<DiaCalor> Celdas, IReadOnlyList<string> Meses, IReadOnlyList<string> Dias, int TotalEpisodios, int DiasActivos);`
  - `public static class CalculadorMapaCalor` con `public const int Semanas = 53;`,
    `public static int NivelDe(int episodios)`, `public static DateTime InicioDelRango(DateTime hoy, DayOfWeek primerDia)`
    y `public static MapaCalor Calcular(IEnumerable<DateTime> diasLocales, DateTime hoy, CultureInfo cultura)`.
  - Claves: `Stats_Constancia`, `Stats_ConstanciaSubFormato`, `Stats_ConstanciaVacia`, `Stats_Menos`, `Stats_Mas`,
    `Stats_MapaDiaUnoFormato`, `Stats_MapaDiaVariosFormato`, `Stats_MapaDiaNingunoFormato`.

- [ ] **Paso 1: escribir las pruebas (fallan: no existe el calculador)**

`AnimeLocalTracker.Tests/Services/CalculadorMapaCalorTests.cs`:

```csharp
using System;
using System.Globalization;
using System.Linq;
using AnimeLocalTracker.Services;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services;

/// <summary>La cuadrícula de constancia de Estadísticas: 53 semanas que terminan hoy, fila a fila, con niveles fijos.</summary>
public class CalculadorMapaCalorTests
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    // Miércoles 14 de octubre de 2026.
    private static readonly DateTime Hoy = new(2026, 10, 14);

    private static DiaCalor Celda(MapaCalor mapa, DateTime fecha) => mapa.Celdas.Single(c => c.Fecha == fecha);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(30, 4)]
    public void NivelDe_UsaLosUmbralesFijos(int episodios, int nivel) =>
        CalculadorMapaCalor.NivelDe(episodios).Should().Be(nivel);

    [Fact]
    public void Celdas_SonSieteFilasDeCincuentaYTresSemanas_FilaAFila()
    {
        var mapa = CalculadorMapaCalor.Calcular([], Hoy, Es);
        var inicio = CalculadorMapaCalor.InicioDelRango(Hoy, DayOfWeek.Monday);

        mapa.Celdas.Should().HaveCount(7 * CalculadorMapaCalor.Semanas);
        mapa.Meses.Should().HaveCount(CalculadorMapaCalor.Semanas);
        mapa.Dias.Should().HaveCount(7);
        for (int fila = 0; fila < 7; fila++)
            for (int columna = 0; columna < CalculadorMapaCalor.Semanas; columna++)
                mapa.Celdas[fila * CalculadorMapaCalor.Semanas + columna].Fecha.Should().Be(inicio.AddDays(columna * 7 + fila));
    }

    [Theory]
    [InlineData("es-ES", 2026, 10, 12, DayOfWeek.Monday)] // hoy es lunes: primer día de la semana en español
    [InlineData("en-US", 2026, 10, 11, DayOfWeek.Sunday)] // hoy es domingo: primer día de la semana en inglés
    [InlineData("es-ES", 2026, 10, 14, DayOfWeek.Monday)]
    [InlineData("en-US", 2026, 10, 14, DayOfWeek.Sunday)]
    public void Hoy_EstaEnLaUltimaColumna_EnLaFilaDeSuDia_YLoQueSigueNoSePinta(string cultura, int a, int m, int d, DayOfWeek primerDia)
    {
        var hoy = new DateTime(a, m, d);
        var mapa = CalculadorMapaCalor.Calcular([], hoy, CultureInfo.GetCultureInfo(cultura));

        int indice = mapa.Celdas.ToList().FindIndex(c => c.Fecha == hoy);
        (indice % CalculadorMapaCalor.Semanas).Should().Be(CalculadorMapaCalor.Semanas - 1, "hoy va en la última semana");
        (indice / CalculadorMapaCalor.Semanas).Should().Be(((int)hoy.DayOfWeek - (int)primerDia + 7) % 7);
        mapa.Celdas.Where(c => c.Fecha > hoy).Should().OnlyContain(c => c.Nivel == -1 && c.Texto == "");
        mapa.Celdas.Where(c => c.Fecha <= hoy).Should().OnlyContain(c => c.Nivel >= 0 && c.Texto != "");
        mapa.Celdas[0].Fecha.DayOfWeek.Should().Be(primerDia);
    }

    [Fact]
    public void CuentaLosEpisodiosDeCadaDia_YSoloLosDelRango()
    {
        var inicio = CalculadorMapaCalor.InicioDelRango(Hoy, DayOfWeek.Monday);
        DateTime[] dias =
        [
            Hoy, Hoy, Hoy,                 // 3 hoy
            inicio,                        // 1 el primer día del rango
            inicio.AddDays(-1),            // fuera: anterior al rango
            Hoy.AddDays(1),                // fuera: fecha futura (reloj cambiado)
            Hoy.AddHours(23).AddMinutes(30) // se toma solo la fecha: cuenta hoy
        ];

        var mapa = CalculadorMapaCalor.Calcular(dias, Hoy, Es);

        Celda(mapa, Hoy).Episodios.Should().Be(4);
        Celda(mapa, Hoy).Nivel.Should().Be(3);
        Celda(mapa, inicio).Episodios.Should().Be(1);
        mapa.TotalEpisodios.Should().Be(5);
        mapa.DiasActivos.Should().Be(2);
    }

    [Fact]
    public void Texto_LlevaElNumeroYLaFechaLargaDeLaCultura()
    {
        var mapa = CalculadorMapaCalor.Calcular([Hoy, Hoy, Hoy], Hoy, En);

        Celda(mapa, Hoy).Texto.Should().Contain("3").And.Contain(Hoy.ToString("D", En));
        Celda(mapa, Hoy.AddDays(-1)).Texto.Should().Contain(Hoy.AddDays(-1).ToString("D", En));
    }

    [Fact]
    public void Meses_SeEtiquetanEnLaSemanaDondeEmpiezan_YNoSiElDiaUnoEsFuturo()
    {
        // Miércoles 28 de octubre de 2026: su semana (lunes 26 – domingo 1) contiene el 1 de noviembre, que aún no llegó.
        var hoy = new DateTime(2026, 10, 28);
        var mapa = CalculadorMapaCalor.Calcular([], hoy, Es);
        var inicio = CalculadorMapaCalor.InicioDelRango(hoy, DayOfWeek.Monday);

        int columnaOctubre = (int)((new DateTime(2026, 10, 1) - inicio).TotalDays / 7);
        mapa.Meses[columnaOctubre].Should().Be(Es.DateTimeFormat.GetAbbreviatedMonthName(10).TrimEnd('.'));
        mapa.Meses[CalculadorMapaCalor.Semanas - 1].Should().BeEmpty("el 1 de noviembre es futuro");
        mapa.Meses.Count(m => m != "").Should().BeInRange(11, 12);
    }

    [Theory]
    [InlineData("es-ES", 0, 2, 4)] // lun, mié, vie
    [InlineData("en-US", 1, 3, 5)] // Mon, Wed, Fri
    public void Dias_SeEtiquetanLunesMiercolesYViernes(string nombreCultura, int a, int b, int c)
    {
        var cultura = CultureInfo.GetCultureInfo(nombreCultura);
        var mapa = CalculadorMapaCalor.Calcular([], Hoy, cultura);

        mapa.Dias.Select((t, i) => (t, i)).Where(x => x.t != "").Select(x => x.i).Should().Equal(a, b, c);
        mapa.Dias[a].Should().Be(cultura.DateTimeFormat.GetAbbreviatedDayName(DayOfWeek.Monday).TrimEnd('.'));
    }
}
```

- [ ] **Paso 2: comprobar que fallan**

Run: `dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false`
Expected: error CS0246/CS0103 por `CalculadorMapaCalor`, `MapaCalor` y `DiaCalor` inexistentes.

- [ ] **Paso 3: escribir el calculador**

`AnimeLocalTracker/Services/CalculadorMapaCalor.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AnimeLocalTracker.Services;

/// <summary>Un día de la cuadrícula de constancia. <paramref name="Nivel"/> −1 = fuera del rango (futuro): no se pinta.</summary>
public sealed record DiaCalor(DateTime Fecha, int Episodios, int Nivel, string Texto);

/// <summary>
/// La cuadrícula ya lista para pintar: <see cref="Celdas"/> fila a fila (todas las semanas del primer día de la semana,
/// luego las del segundo…), que es el orden en que las coloca un UniformGrid de 7 filas.
/// </summary>
public sealed record MapaCalor(IReadOnlyList<DiaCalor> Celdas, IReadOnlyList<string> Meses, IReadOnlyList<string> Dias,
    int TotalEpisodios, int DiasActivos);

/// <summary>
/// Mapa de calor de las últimas 53 semanas (docs/investigacion-mapa-calor-visionado.md). Cálculo puro: recibe los días
/// LOCALES de cada episodio visto (uno por episodio) y la cultura, que da el primer día de la semana y los nombres.
/// </summary>
public static class CalculadorMapaCalor
{
    public const int Semanas = 53;

    /// <summary>Niveles fijos y no relativos al máximo: "7 o más" es siempre una maratón de unas 3 h.</summary>
    public static int NivelDe(int episodios) => episodios switch
    {
        <= 0 => 0,
        1 => 1,
        <= 3 => 2,
        <= 6 => 3,
        _ => 4
    };

    /// <summary>Primer día de la primera columna: la semana de hoy es la última de las <see cref="Semanas"/>.</summary>
    public static DateTime InicioDelRango(DateTime hoy, DayOfWeek primerDia)
    {
        var inicioSemanaHoy = hoy.Date.AddDays(-(((int)hoy.DayOfWeek - (int)primerDia + 7) % 7));
        return inicioSemanaHoy.AddDays(-7 * (Semanas - 1));
    }

    public static MapaCalor Calcular(IEnumerable<DateTime> diasLocales, DateTime hoy, CultureInfo cultura)
    {
        hoy = hoy.Date;
        var formato = cultura.DateTimeFormat;
        var inicio = InicioDelRango(hoy, formato.FirstDayOfWeek);

        // Lo anterior al rango y lo posterior a hoy (reloj cambiado, viaje entre zonas) no entra.
        var porDia = diasLocales
            .Select(d => d.Date)
            .Where(d => d >= inicio && d <= hoy)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        var celdas = new DiaCalor[7 * Semanas];
        for (int fila = 0; fila < 7; fila++)
        {
            for (int columna = 0; columna < Semanas; columna++)
            {
                var fecha = inicio.AddDays(columna * 7 + fila);
                int episodios = porDia.GetValueOrDefault(fecha);
                celdas[fila * Semanas + columna] = fecha > hoy
                    ? new DiaCalor(fecha, 0, -1, string.Empty)
                    : new DiaCalor(fecha, episodios, NivelDe(episodios), TextoDe(episodios, fecha, cultura));
            }
        }

        // Cada mes se etiqueta en la semana que contiene su día 1, si ese día ya llegó.
        var meses = new string[Semanas];
        for (int columna = 0; columna < Semanas; columna++)
        {
            var semana = inicio.AddDays(columna * 7);
            var diaUno = Enumerable.Range(0, 7).Select(i => semana.AddDays(i)).FirstOrDefault(d => d.Day == 1 && d <= hoy);
            meses[columna] = diaUno == default ? string.Empty : formato.GetAbbreviatedMonthName(diaUno.Month).TrimEnd('.');
        }

        // Como GitHub: solo lunes, miércoles y viernes llevan nombre, empiece la semana en lunes o en domingo.
        var dias = new string[7];
        for (int fila = 0; fila < 7; fila++)
        {
            var dia = (DayOfWeek)(((int)formato.FirstDayOfWeek + fila) % 7);
            dias[fila] = dia is DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday
                ? formato.GetAbbreviatedDayName(dia).TrimEnd('.')
                : string.Empty;
        }

        return new MapaCalor(celdas, meses, dias, porDia.Values.Sum(), porDia.Count);
    }

    private static string TextoDe(int episodios, DateTime fecha, CultureInfo cultura)
    {
        string dia = fecha.ToString("D", cultura);
        return episodios switch
        {
            0 => string.Format(LocalizationService.T("Stats_MapaDiaNingunoFormato"), dia),
            1 => string.Format(LocalizationService.T("Stats_MapaDiaUnoFormato"), dia),
            _ => string.Format(LocalizationService.T("Stats_MapaDiaVariosFormato"), episodios, dia)
        };
    }
}
```

- [ ] **Paso 4: añadir los textos en los dos idiomas**

En `LocalizationService.cs`, mitad `Es`, después de `["Stats_PorAnioSub"] = "Evolución de tu consumo anual",`:

```csharp
        ["Stats_Constancia"] = "Constancia",
        ["Stats_ConstanciaSubFormato"] = "{0} episodios en los últimos 12 meses · {1} días con actividad",
        ["Stats_ConstanciaVacia"] = "Aún no hay reproducciones en los últimos 12 meses",
        ["Stats_Menos"] = "Menos",
        ["Stats_Mas"] = "Más",
        ["Stats_MapaDiaUnoFormato"] = "1 episodio · {0}",
        ["Stats_MapaDiaVariosFormato"] = "{0} episodios · {1}",
        ["Stats_MapaDiaNingunoFormato"] = "Sin episodios · {0}",
```

Mitad `En`, después de `["Stats_PorAnioSub"] = "Evolution of your yearly consumption",`:

```csharp
        ["Stats_Constancia"] = "Consistency",
        ["Stats_ConstanciaSubFormato"] = "{0} episodes in the last 12 months · {1} active days",
        ["Stats_ConstanciaVacia"] = "No playback in the last 12 months yet",
        ["Stats_Menos"] = "Less",
        ["Stats_Mas"] = "More",
        ["Stats_MapaDiaUnoFormato"] = "1 episode · {0}",
        ["Stats_MapaDiaVariosFormato"] = "{0} episodes · {1}",
        ["Stats_MapaDiaNingunoFormato"] = "No episodes · {0}",
```

- [ ] **Paso 5: compilar y comprobar que pasan**

Run: `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false` (0 errores, 0 advertencias), y
`dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~CalculadorMapaCalorTests|FullyQualifiedName~LocalizationServiceTests"`
Expected: todo en verde (incluida la paridad de claves y marcadores de `LocalizationServiceTests`).

- [ ] **Paso 6: sin commit** (regla del repo).

---

### Tarea 2: día local en Estadísticas y propiedades del mapa

**Archivos:**
- Modificar: `AnimeLocalTracker/ViewModels/EstadisticasViewModel.cs` (propiedades de "ACTIVIDAD RECIENTE", hoy
  líneas 79-81; `CalcularEstadisticas`, hoy líneas 271-449)
- Prueba: `AnimeLocalTracker.Tests/ViewModels/EstadisticasViewModelTests.cs`

**Interfaces:**
- Consume: `CalculadorMapaCalor.Calcular(IEnumerable<DateTime>, DateTime, CultureInfo)` → `MapaCalor` (tarea 1);
  `MotorLogros.ALocal(DateTime)` (ya existe, `internal`, `AnimeLocalTracker.Services.Logros`).
- Produce para la vista: `MapaCalor? MapaCalor`, `string MapaCalorResumen`, `bool HayActividadEnMapa`.

- [ ] **Paso 1: escribir la prueba de regresión (falla en máquinas con zona horaria distinta de UTC)**

Añadir al final de `EstadisticasViewModelTests` (antes de la llave de cierre):

```csharp
    [Fact]
    public async Task UnaReproduccionCuentaEnSuDiaLocal_EnElMapaLaActividadYLaRacha()
    {
        // La base guarda UTC con Kind=Unspecified. Se elige una hora local de hoy cuya fecha UTC es otra (de noche si la
        // zona va por detrás de UTC, de madrugada si va por delante): agrupar por la fecha UTC la mandaría a otro día.
        var desfase = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        var horaLocal = DateTime.Today.AddHours(desfase < TimeSpan.Zero ? 23.5 : 0.5);
        var guardada = DateTime.SpecifyKind(horaLocal.ToUniversalTime(), DateTimeKind.Unspecified);
        var animes = new List<AnimeItem> { new() { AniListId = 1, Titulo = "Frieren", TotalEpisodios = 28 } };
        var registros = new List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true, UltimaReproduccion = guardada }
        };
        var (vm, _) = CrearVm(animes, registros);

        await vm.CargarEstadisticasAsync();

        vm.MapaCalor!.Celdas.Single(c => c.Fecha == DateTime.Today).Episodios.Should().Be(1);
        vm.MapaCalor.TotalEpisodios.Should().Be(1);
        vm.HayActividadEnMapa.Should().BeTrue();
        vm.ActividadSemana.Last().Valor.Should().Be(1, "la última barra es hoy");
        vm.RachaActual.Should().Be(string.Format(LocalizationService.T("Stats_DiasFormato"), 1));
    }

    [Fact]
    public async Task SinReproduccionesEnElAnio_ElMapaQuedaVacioConSuAviso()
    {
        var registros = new List<RegistroEpisodio> { new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true } }; // marcado a mano: sin fecha
        var (vm, _) = CrearVm(new List<AnimeItem> { new() { AniListId = 1, Titulo = "Frieren", TotalEpisodios = 28 } }, registros);

        await vm.CargarEstadisticasAsync();

        vm.MapaCalor!.TotalEpisodios.Should().Be(0);
        vm.HayActividadEnMapa.Should().BeFalse();
        vm.MapaCalorResumen.Should().Be(LocalizationService.T("Stats_ConstanciaVacia"));
    }
```

- [ ] **Paso 2: comprobar que fallan**

Run: `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false` y después `dotnet build AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false`
Expected: error de compilación por `MapaCalor`/`HayActividadEnMapa`/`MapaCalorResumen` inexistentes.

- [ ] **Paso 3: propiedades nuevas**

En `EstadisticasViewModel`, debajo de `[ObservableProperty] private string _promedioDiarioTexto = "0";`:

```csharp

    // === CONSTANCIA: mapa de calor de las últimas 53 semanas ===
    [ObservableProperty] private MapaCalor? _mapaCalor;
    [ObservableProperty] private string _mapaCalorResumen = "";
    [ObservableProperty] private bool _hayActividadEnMapa;
```

- [ ] **Paso 4: una sola lista de días locales en `CalcularEstadisticas`**

Sustituir

```csharp
        var vistos = registros.Where(r => r.VistoLocal).ToList();
        var porDia = vistos.ToLookup(r => r.UltimaReproduccion?.Date);
```

por

```csharp
        var vistos = registros.Where(r => r.VistoLocal).ToList();
        // Día LOCAL de cada episodio visto con fecha (persistence.md #4: la base guarda UTC). Antes se agrupaba por la fecha
        // UTC: lo visto a las 21:00 en Colombia contaba para el día siguiente. Lo usan el mapa, la actividad, las rachas y
        // los años, que así cuadran entre sí.
        var diasVistos = vistos
            .Where(r => r.UltimaReproduccion.HasValue)
            .Select(r => MotorLogros.ALocal(r.UltimaReproduccion!.Value).Date)
            .ToList();
        var porDia = diasVistos.ToLookup(d => d);
```

En el bloque "ACTIVIDAD: últimos 7 días", la etiqueta pasa a la cultura de la app y el filtro de la semana a la nueva
clave (ya no es `DateTime?`):

```csharp
            actividad.Add((dia.ToString("ddd d", LocalizationService.Cultura), n));
```

```csharp
        int semana = porDia.Where(g => g.Key >= DateTime.Today.AddDays(-6)).Sum(g => g.Count());
```

Justo después de `PromedioDiarioTexto = $"{semana / 7.0:F1}";`:

```csharp

        // === CONSTANCIA ===
        var mapa = CalculadorMapaCalor.Calcular(diasVistos, DateTime.Today, LocalizationService.Cultura);
        MapaCalor = mapa;
        HayActividadEnMapa = mapa.TotalEpisodios > 0;
        MapaCalorResumen = HayActividadEnMapa
            ? string.Format(LocalizationService.T("Stats_ConstanciaSubFormato"), mapa.TotalEpisodios, mapa.DiasActivos)
            : LocalizationService.T("Stats_ConstanciaVacia");
```

"POR AÑO": sustituir

```csharp
        var porAnio = vistos
            .Where(r => r.UltimaReproduccion.HasValue)
            .GroupBy(r => r.UltimaReproduccion!.Value.Year)
```

por

```csharp
        var porAnio = diasVistos
            .GroupBy(d => d.Year)
```

"Mejor año": sustituir

```csharp
        var mejorAnio = vistos
            .Where(r => r.UltimaReproduccion.HasValue)
            .GroupBy(r => r.UltimaReproduccion!.Value.Year)
```

por

```csharp
        var mejorAnio = diasVistos
            .GroupBy(d => d.Year)
```

"Horas por mes": sustituir `.Select(r => r.UltimaReproduccion!.Value)` por
`.Select(r => MotorLogros.ALocal(r.UltimaReproduccion!.Value))` (este recorre `registros`, no solo los vistos: se deja
así).

"Rachas": sustituir

```csharp
        var diasConActividad = vistos
            .Where(r => r.UltimaReproduccion.HasValue)
            .Select(r => r.UltimaReproduccion!.Value.Date)
            .Distinct()
```

por

```csharp
        var diasConActividad = diasVistos
            .Distinct()
```

- [ ] **Paso 5: compilar y comprobar que pasan**

Run: `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false` y después
`dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~EstadisticasViewModelTests|FullyQualifiedName~CalculadorMapaCalorTests"`
Expected: todo en verde, incluidas las pruebas que ya existían.

- [ ] **Paso 6: sin commit** (regla del repo).

---

### Tarea 3: tarjeta "Constancia" en la vista

**Archivos:**
- Modificar: `AnimeLocalTracker/Views/EstadisticasView.xaml` (`UserControl.Resources`; nueva fila entre el cierre de
  la "FILA 2: INSIGHTS DEL ANALISTA" y el comentario `<!-- ══ FILA 3: DONUTS (ESTADO | GÉNEROS) ══ -->`)
- Prueba: `AnimeLocalTracker.Tests/Views/EstadisticasViewTests.cs`

**Interfaces:**
- Consume: `MapaCalor`, `MapaCalorResumen` y `HayActividadEnMapa` (tarea 2); `DiaCalor.Nivel` y `DiaCalor.Texto`
  (tarea 1).
- Produce: `ItemsControl x:Name="CeldasMapaCalor"` (lo busca la prueba).

- [ ] **Paso 1: escribir la prueba de la vista (falla: no existe `CeldasMapaCalor`)**

Añadir a `EstadisticasViewTests` (y `using System.Linq; using System.Windows; using System.Windows.Controls;
using System.Windows.Media; using AnimeLocalTracker.Models; using AnimeLocalTracker.Services; using
AnimeLocalTracker.ViewModels; using FluentAssertions; using Moq;` arriba):

```csharp
    [Fact]
    public async System.Threading.Tasks.Task MapaDeCalor_PintaCadaNivel_YLosDiasFuturosNoTienenTooltip()
    {
        var db = new Mock<IDatabaseService>();
        db.Setup(d => d.ObtenerTodosLosAnimesAsync()).ReturnsAsync(new System.Collections.Generic.List<AnimeItem>());
        db.Setup(d => d.ObtenerTodosLosRegistrosAsync()).ReturnsAsync(new System.Collections.Generic.List<RegistroEpisodio>
        {
            new() { AniListId = 1, NumeroEpisodio = 1, VistoLocal = true, UltimaReproduccion = System.DateTime.UtcNow }
        });
        var vm = new EstadisticasViewModel(db.Object, Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(), Mock.Of<IDialogService>());
        await vm.CargarEstadisticasAsync();

        _host.Ejecutar(() =>
        {
            var vista = new EstadisticasView { DataContext = vm };
            vista.Measure(new Size(1200, 4000));
            vista.Arrange(new Rect(0, 0, 1200, 4000));
            vista.UpdateLayout();

            var celdas = (ItemsControl)vista.FindName("CeldasMapaCalor");
            Border BordeDe(int i) => (Border)VisualTreeHelper.GetChild(celdas.ItemContainerGenerator.ContainerFromIndex(i), 0);
            var mapa = vm.MapaCalor!;
            int hoy = mapa.Celdas.ToList().FindIndex(c => c.Fecha == System.DateTime.Today);
            int vacio = mapa.Celdas.ToList().FindIndex(c => c.Nivel == 0);
            int futuro = mapa.Celdas.ToList().FindIndex(c => c.Nivel == -1);

            BordeDe(hoy).Opacity.Should().BeApproximately(0.30, 0.001, "1 episodio = nivel 1");
            BordeDe(hoy).ToolTip.Should().Be(mapa.Celdas[hoy].Texto);
            BordeDe(vacio).Background.Should().BeSameAs(Application.Current.Resources["Brush.SurfaceChip"]);
            if (futuro >= 0) ToolTipService.GetIsEnabled(BordeDe(futuro)).Should().BeFalse("un día futuro no debe mostrar un tooltip vacío");
        });
    }
```

(Si hoy es el último día de la semana no hay celdas futuras: por eso el `if`.)

- [ ] **Paso 2: comprobar que falla**

Run: `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false` y después `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~EstadisticasViewTests"`
Expected: FAIL (`FindName` devuelve null → `InvalidCastException`/`NullReferenceException`).

- [ ] **Paso 3: estilo de celda**

En `EstadisticasView.xaml`, dentro de `<UserControl.Resources>`, después de `GradAmbar`:

```xml
        <!-- Celda del mapa de constancia. El nivel llega por Tag (Nivel de DiaCalor en la cuadrícula, número fijo en la
             leyenda); el DataTrigger compara también un Tag numérico con el texto del Value. -->
        <Style x:Key="CeldaMapa" TargetType="Border">
            <Setter Property="Width" Value="11"/>
            <Setter Property="Height" Value="11"/>
            <Setter Property="Margin" Value="1.5"/>
            <Setter Property="CornerRadius" Value="2"/>
            <Setter Property="Background" Value="{StaticResource Brush.Success}"/>
            <Setter Property="ToolTipService.InitialShowDelay" Value="150"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource Self}}" Value="-1">
                    <Setter Property="Background" Value="Transparent"/>
                    <Setter Property="ToolTipService.IsEnabled" Value="False"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource Self}}" Value="0">
                    <Setter Property="Background" Value="{StaticResource Brush.SurfaceChip}"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource Self}}" Value="1">
                    <Setter Property="Opacity" Value="0.30"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource Self}}" Value="2">
                    <Setter Property="Opacity" Value="0.55"/>
                </DataTrigger>
                <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource Self}}" Value="3">
                    <Setter Property="Opacity" Value="0.80"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
```

- [ ] **Paso 4: la tarjeta**

Entre el `</Grid>` que cierra la "FILA 2: INSIGHTS DEL ANALISTA" y `<!-- ══ FILA 3: DONUTS (ESTADO | GÉNEROS) ══ -->`:

```xml
                <!-- ══ FILA 2B: CONSTANCIA (mapa de calor de las últimas 53 semanas) ══ -->
                <Border Background="{StaticResource Brush.Surface1}" BorderBrush="{StaticResource Brush.Border}" BorderThickness="1"
                        CornerRadius="14" Padding="22,18" Margin="0,0,0,16">
                    <StackPanel>
                        <Grid Margin="0,0,0,12">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <StackPanel>
                                <TextBlock Text="{loc:T Stats_Constancia}" FontSize="14" FontWeight="Bold" Foreground="{StaticResource Brush.TextPrimary}" Margin="0,0,0,4"/>
                                <TextBlock Text="{Binding MapaCalorResumen}" FontSize="11" Foreground="{StaticResource Brush.TextSecondary}"/>
                            </StackPanel>
                            <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Top">
                                <TextBlock Text="{loc:T Stats_Menos}" FontSize="10" Foreground="{StaticResource Brush.TextTertiary}" VerticalAlignment="Center" Margin="0,0,4,0"/>
                                <Border Style="{StaticResource CeldaMapa}" Tag="0"/>
                                <Border Style="{StaticResource CeldaMapa}" Tag="1"/>
                                <Border Style="{StaticResource CeldaMapa}" Tag="2"/>
                                <Border Style="{StaticResource CeldaMapa}" Tag="3"/>
                                <Border Style="{StaticResource CeldaMapa}" Tag="4"/>
                                <TextBlock Text="{loc:T Stats_Mas}" FontSize="10" Foreground="{StaticResource Brush.TextTertiary}" VerticalAlignment="Center" Margin="4,0,0,0"/>
                            </StackPanel>
                        </Grid>

                        <!-- Todo escala junto para ocupar el ancho de la tarjeta (y encogerse con la ventana mínima). No es una lista
                             desplazable: 371 celdas fijas, sin virtualización (ui-wpf-vistas.md #2 no aplica). -->
                        <Viewbox Stretch="Uniform" StretchDirection="Both">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/>
                                </Grid.ColumnDefinitions>
                                <Grid.RowDefinitions>
                                    <RowDefinition Height="14"/><RowDefinition Height="Auto"/>
                                </Grid.RowDefinitions>

                                <ItemsControl Grid.Row="1" ItemsSource="{Binding MapaCalor.Dias}" Margin="0,0,6,0" Focusable="False">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate><UniformGrid Rows="7" Columns="1"/></ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <TextBlock Text="{Binding}" FontSize="8" Height="14" Foreground="{StaticResource Brush.TextTertiary}"/>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>

                                <!-- Un Canvas por columna: el nombre del mes se sale de su columna de 14 px sin recortarse. -->
                                <ItemsControl Grid.Column="1" ItemsSource="{Binding MapaCalor.Meses}" Focusable="False">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate><UniformGrid Rows="1" Columns="53"/></ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <Canvas Height="14">
                                                <TextBlock Text="{Binding}" FontSize="8" Foreground="{StaticResource Brush.TextTertiary}"/>
                                            </Canvas>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>

                                <ItemsControl x:Name="CeldasMapaCalor" Grid.Row="1" Grid.Column="1" ItemsSource="{Binding MapaCalor.Celdas}" Focusable="False">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate><UniformGrid Rows="7" Columns="53"/></ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <Border Style="{StaticResource CeldaMapa}" Tag="{Binding Nivel}"
                                                    ToolTip="{Binding Texto}" AutomationProperties.Name="{Binding Texto}"/>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </Grid>
                        </Viewbox>
                    </StackPanel>
                </Border>
```

- [ ] **Paso 5: compilar y comprobar que pasan**

Run: `dotnet build AnimeLocalTracker/AnimeLocalTracker.csproj -c Debug -nodeReuse:false` (0 advertencias; el hook `aviso_reglas.py` no debe avisar de colores literales) y
`dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false --filter "FullyQualifiedName~EstadisticasViewTests"`.
Expected: PASS las dos pruebas de la clase.

- [ ] **Paso 6: buscar texto sin traducir**

Run: `python .claude/skills/localizacion/scripts/buscar_literales.py AnimeLocalTracker/Views/EstadisticasView.xaml AnimeLocalTracker/Services/CalculadorMapaCalor.cs AnimeLocalTracker/ViewModels/EstadisticasViewModel.cs`
Expected: ningún candidato nuevo en las líneas añadidas.

- [ ] **Paso 7: sin commit** (regla del repo).

---

### Tarea 4: verificación final

- [ ] **Paso 1: suite completa, una vez** (`repo-build-test`): app y luego
  `dotnet test AnimeLocalTracker.Tests/AnimeLocalTracker.Tests.csproj -c Debug --no-restore -p:BuildProjectReferences=false`.
  El total debe ser el de antes más las pruebas nuevas, sin fallos.
- [ ] **Paso 2: en la app real con `perfil-aislado`** (copia de la biblioteca, que tiene historial) y
  `wpf-visual-verification`: abrir Estadísticas y capturar la tarjeta con la ventana normal y al mínimo (800 px).
  Comprobar:
  - que hoy está en la última columna;
  - que las etiquetas de mes no se pisan ni se cortan;
  - que el tooltip de un día con actividad dice su número y fecha;
  - que en inglés (cambiar el idioma en Configuración y volver a la pestaña) la semana empieza en domingo y los
    nombres salen en inglés.
- [ ] **Paso 3: rendimiento:** en el registro de la sesión aislada, la línea `[Perf] Navegación … → Estadísticas` de
  la primera visita no debe crecer de forma apreciable frente a `main` (371 celdas, del orden de milisegundos). Si
  crece más de unos 30 ms, anotarlo y avisar antes de seguir.
- [ ] **Paso 4:** cerrar la instancia lanzada por su PID e informar al usuario: qué se ve, qué se verificó y qué no.
  Sin commit hasta que lo pida.
