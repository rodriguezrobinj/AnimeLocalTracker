using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Logros;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.Services.Logros;

public class MotorLogrosTests
{
    private static readonly IReadOnlyDictionary<string, (int Nivel, DateTime? FechaUtc)> SinGuardados =
        new Dictionary<string, (int, DateTime?)>();

    private static AnimeItem Anime(int id, int total = 12, string generos = "", int anio = 0, bool favorito = false) =>
        new() { AniListId = id, Titulo = $"Anime {id}", TotalEpisodios = total, Generos = generos, AnioLanzamiento = anio, EsFavorito = favorito };

    /// <summary>Episodio visto en una fecha LOCAL concreta (Kind=Local: se usa tal cual).</summary>
    private static RegistroEpisodio Ep(int anime, int episodio, DateTime? fechaLocal = null, bool visto = true, double total = 1400) =>
        new() { AniListId = anime, NumeroEpisodio = episodio, VistoLocal = visto, TotalSegundos = total, UltimaReproduccion = fechaLocal };

    private static DateTime Local(int mes, int dia, int hora = 12, int minuto = 0) =>
        new(2026, mes, dia, hora, minuto, 0, DateTimeKind.Local);

    private static Dictionary<string, double> Medir(List<AnimeItem> animes, List<RegistroEpisodio> registros) =>
        MotorLogros.CalcularMetricas(animes, registros);

    // ───────────────────────────── Mediciones ─────────────────────────────

    [Fact]
    public void SinDatos_TodasLasMetricasSonCero()
    {
        var metricas = Medir(new(), new());

        CatalogoLogros.Todos.Should().OnlyContain(l => metricas[l.Id] == 0);
    }

    [Fact]
    public void MaratonDia_CuentaLosEpisodiosDelMismoDiaLocal()
    {
        var registros = Enumerable.Range(1, 6).Select(i => Ep(1, i, Local(9, 1, 10 + (i % 3)))).ToList();
        registros.Add(Ep(1, 7, Local(9, 2)));

        var metricas = Medir(new() { Anime(1) }, registros);

        metricas["maraton_dia"].Should().Be(6);
        var resumen = MotorLogros.Evaluar(metricas, SinGuardados);
        resumen.Logros.Single(l => l.Id == "maraton_dia").NivelActual.Should().Be(2); // umbrales 3, 6 → Plata
    }

    [Fact]
    public void Racha_CuentaDiasConsecutivos()
    {
        var registros = new List<RegistroEpisodio>
        {
            Ep(1, 1, Local(9, 1)), Ep(1, 2, Local(9, 2)), Ep(1, 3, Local(9, 3)),
            Ep(1, 4, Local(9, 5))
        };

        var metricas = Medir(new() { Anime(1) }, registros);

        metricas["racha"].Should().Be(3);
        metricas["dias_activos"].Should().Be(4);
    }

    [Fact]
    public void Horas_UsaElMayorEntreDuracionYProgreso()
    {
        var registros = new List<RegistroEpisodio>
        {
            Ep(1, 1, Local(9, 1), total: 3600),                                              // 1 h
            new() { AniListId = 1, NumeroEpisodio = 2, VistoLocal = true, TotalSegundos = 0, ProgresoSegundos = 1800 } // 0,5 h
        };

        Medir(new() { Anime(1) }, registros)["horas"].Should().BeApproximately(1.5, 1e-9);
    }

    [Fact]
    public void Horas_EstimaLosEpisodiosMarcadosOImportadosSinDuracion()
    {
        // 300 episodios marcados en bloque: sin duración guardada, antes contaban 0 h.
        var registros = Enumerable.Range(1, 300).Select(i => Ep(1, i, fechaLocal: null, total: 0)).ToList();

        Medir(new() { Anime(1, total: 300) }, registros)["horas"].Should().BeApproximately(120, 1e-6); // 300 × 24 min
    }

    [Fact]
    public void FechasUnspecified_SeTratanComoUtcYSeConvierteAHoraLocal()
    {
        // sqlite-net devuelve Kind=Unspecified pero el valor es UTC (persistence.md #4): 03:30 local
        // debe seguir contando como "búho" sin importar el huso horario de la máquina.
        var local = Local(9, 1, 3, 30);
        var unspecifiedUtc = DateTime.SpecifyKind(local.ToUniversalTime(), DateTimeKind.Unspecified);

        var metricas = Medir(new() { Anime(1) }, new() { Ep(1, 1, unspecifiedUtc) });

        metricas["buho"].Should().Be(1);
    }

    [Fact]
    public void Horarios_SeparanBuhoDeMadrugador()
    {
        var registros = new List<RegistroEpisodio>
        {
            Ep(1, 1, Local(9, 1, 2, 0)),   // búho (2–5)
            Ep(1, 2, Local(9, 1, 4, 59)),  // búho
            Ep(1, 3, Local(9, 1, 5, 0)),   // madrugador (5–8)
            Ep(1, 4, Local(9, 1, 7, 59)),  // madrugador
            Ep(1, 5, Local(9, 1, 8, 0)),   // ninguno
        };

        var metricas = Medir(new() { Anime(1) }, registros);

        metricas["buho"].Should().Be(2);
        metricas["madrugador"].Should().Be(2);
    }

    [Fact]
    public void MarcadoManualSinFecha_CuentaEnTotalesPeroNoEnHorarios()
    {
        var registros = new List<RegistroEpisodio> { Ep(1, 1, fechaLocal: null), Ep(1, 2, fechaLocal: null) };

        var metricas = Medir(new() { Anime(1) }, registros);

        metricas["episodios"].Should().Be(2);
        metricas["dias_activos"].Should().Be(0);
        metricas["buho"].Should().Be(0);
    }

    [Fact]
    public void EpisodiosNoVistos_NoCuentan()
    {
        var registros = new List<RegistroEpisodio> { Ep(1, 1, Local(9, 1), visto: false) };

        Medir(new() { Anime(1) }, registros)["episodios"].Should().Be(0);
    }

    [Fact]
    public void SeriesCompletadas_ContaSoloLasTerminadas_YLasLargasAparte()
    {
        var animes = new List<AnimeItem> { Anime(1, total: 12), Anime(2, total: 60), Anime(3, total: 24) };
        var registros = Enumerable.Range(1, 12).Select(i => Ep(1, i, Local(9, 1)))          // serie 1: completa
            .Concat(Enumerable.Range(1, 60).Select(i => Ep(2, i, Local(9, 2))))              // serie 2: completa y larga
            .Concat(Enumerable.Range(1, 10).Select(i => Ep(3, i, Local(9, 3))))              // serie 3: incompleta
            .ToList();

        var metricas = Medir(animes, registros);

        metricas["series_completadas"].Should().Be(2);
        metricas["series_largas"].Should().Be(1);
    }

    [Fact]
    public void SerieSinTotalConocido_NoSeConsideraCompletada()
    {
        var metricas = Medir(new() { Anime(1, total: 0) }, new() { Ep(1, 1, Local(9, 1)) });

        metricas["series_completadas"].Should().Be(0);
    }

    [Fact]
    public void RegistrosDuplicadosDelMismoEpisodio_NoInflanLasSeriesCompletadas()
    {
        var registros = new List<RegistroEpisodio>();
        for (int i = 0; i < 12; i++) registros.Add(Ep(1, 1, Local(9, 1))); // 12 filas del episodio 1

        Medir(new() { Anime(1, total: 12) }, registros)["series_completadas"].Should().Be(0);
    }

    [Fact]
    public void Coleccion_ContabilizaBibliotecaYFavoritos()
    {
        var animes = new List<AnimeItem> { Anime(1, favorito: true), Anime(2), Anime(3, favorito: true) };

        var metricas = Medir(animes, new());

        metricas["biblioteca"].Should().Be(3);
        metricas["favoritos"].Should().Be(2);
    }

    [Fact]
    public void Generos_MideVariedadYEspecialista_SoloConAnimesVistos()
    {
        var animes = new List<AnimeItem>
        {
            Anime(1, generos: "Action, Comedy"),
            Anime(2, generos: "action, Drama"),        // mayúsculas distintas: mismo género
            Anime(3, generos: "Horror")                // sin episodios vistos: no cuenta
        };
        var registros = new List<RegistroEpisodio> { Ep(1, 1, Local(9, 1)), Ep(2, 1, Local(9, 1)) };

        var metricas = Medir(animes, registros);

        metricas["generos_variedad"].Should().Be(3);        // Action, Comedy, Drama
        metricas["genero_especialista"].Should().Be(2);     // Action en 2 animes
    }

    [Fact]
    public void Epocas_MideClasicosYDecadasDistintas()
    {
        var animes = new List<AnimeItem>
        {
            Anime(1, anio: 1995), Anime(2, anio: 1999), Anime(3, anio: 2010), Anime(4, anio: 2024),
            Anime(5, anio: 0),          // sin año: ignorado
            Anime(6, anio: 1985)        // sin episodios vistos: no cuenta
        };
        var registros = new List<RegistroEpisodio>
        {
            Ep(1, 1, Local(9, 1)), Ep(2, 1, Local(9, 1)), Ep(3, 1, Local(9, 1)), Ep(4, 1, Local(9, 1)), Ep(5, 1, Local(9, 1))
        };

        var metricas = Medir(animes, registros);

        metricas["clasicos"].Should().Be(2);   // 1995 y 1999
        metricas["decadas"].Should().Be(3);    // 1990s, 2010s, 2020s
    }

    [Fact]
    public void Secretos_Insomnio_CuentaEpisodiosAntesDeLas6EnUnMismoDia()
    {
        var registros = new List<RegistroEpisodio>
        {
            Ep(1, 1, Local(9, 1, 0, 30)), Ep(1, 2, Local(9, 1, 1, 0)), Ep(1, 3, Local(9, 1, 5, 59)),
            Ep(1, 4, Local(9, 1, 6, 0)),   // ya no es madrugada
            Ep(1, 5, Local(9, 2, 1, 0)),   // otro día
        };

        Medir(new() { Anime(1) }, registros)["insomnio"].Should().Be(3);
    }

    [Fact]
    public void Secretos_DeUnaSentada_CuentaEpisodiosDeLaMismaSerieEnUnDia()
    {
        var registros = Enumerable.Range(1, 12).Select(i => Ep(1, i, Local(9, 1, 8 + (i % 10))))
            .Concat(Enumerable.Range(1, 20).Select(i => Ep(2, i, Local(9, 1, 9))))  // otra serie, más episodios
            .ToList();

        var metricas = Medir(new() { Anime(1), Anime(2, total: 24) }, registros);

        metricas["de_una_sentada"].Should().Be(20); // el máximo entre series
    }

    // ───────────────────────────── Evaluación ─────────────────────────────

    [Fact]
    public void Evaluar_ElUmbralExactoYaDesbloqueaElNivel()
    {
        var metricas = new Dictionary<string, double> { ["horas"] = 100 }; // umbrales 10, 50, 100, 300, 750

        var horas = MotorLogros.Evaluar(metricas, SinGuardados).Logros.Single(l => l.Id == "horas");

        horas.NivelActual.Should().Be(3);
        horas.SiguienteUmbral.Should().Be(300);
        horas.Progreso.Should().BeApproximately(100.0 / 300.0, 1e-9);
        horas.Completo.Should().BeFalse();
    }

    [Fact]
    public void Evaluar_JustoPorDebajoDelUmbral_NoDesbloquea()
    {
        var metricas = new Dictionary<string, double> { ["horas"] = 9.99 };

        var horas = MotorLogros.Evaluar(metricas, SinGuardados).Logros.Single(l => l.Id == "horas");

        horas.NivelActual.Should().Be(0);
        horas.Desbloqueado.Should().BeFalse();
    }

    [Fact]
    public void Evaluar_ValorMaximo_MarcaCompletoYProgresoTotal()
    {
        var metricas = new Dictionary<string, double> { ["horas"] = 5000 };

        var horas = MotorLogros.Evaluar(metricas, SinGuardados).Logros.Single(l => l.Id == "horas");

        horas.Completo.Should().BeTrue();
        horas.NivelActual.Should().Be(5);
        horas.SiguienteUmbral.Should().BeNull();
        horas.Progreso.Should().Be(1.0);
    }

    [Fact]
    public void Evaluar_UnNivelYaGuardadoNoSePierdeAunqueLosDatosBajen()
    {
        // Antes había 750 h (Diamante) y luego borraste series: las métricas actuales son bajas.
        var metricas = new Dictionary<string, double> { ["horas"] = 5 };
        var fecha = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var guardados = new Dictionary<string, (int, DateTime?)> { ["horas"] = (5, fecha) };

        var horas = MotorLogros.Evaluar(metricas, guardados).Logros.Single(l => l.Id == "horas");

        horas.NivelActual.Should().Be(5);
        horas.FechaUltimoNivelUtc.Should().Be(fecha);
    }

    [Fact]
    public void Evaluar_SecretoSinDesbloquear_QuedaOcultoYFueraDeProximos()
    {
        var metricas = new Dictionary<string, double> { ["insomnio"] = 0, ["horas"] = 20 };

        var resumen = MotorLogros.Evaluar(metricas, SinGuardados);

        resumen.Logros.Single(l => l.Id == "insomnio").Oculto.Should().BeTrue();
        resumen.Proximos.Should().NotContain(l => l.Id == "insomnio");
    }

    [Fact]
    public void Evaluar_SecretoDesbloqueado_YaNoEstaOculto()
    {
        var metricas = new Dictionary<string, double> { ["insomnio"] = 3 };

        MotorLogros.Evaluar(metricas, SinGuardados).Logros.Single(l => l.Id == "insomnio").Oculto.Should().BeFalse();
    }

    [Fact]
    public void Proximos_SonLosTresMasCercanosYNoIncluyenCompletos()
    {
        var metricas = new Dictionary<string, double>
        {
            ["horas"] = 5000,          // completo: excluido
            ["episodios"] = 90,        // 90/100 = 0.9
            ["racha"] = 6,             // 6/7 ≈ 0.857
            ["biblioteca"] = 50,       // 50/75 ≈ 0.667
            ["favoritos"] = 4          // 4/5 = 0.8
        };

        var proximos = MotorLogros.Evaluar(metricas, SinGuardados).Proximos;

        proximos.Select(l => l.Id).Should().Equal("episodios", "racha", "favoritos");
    }

    [Fact]
    public void Resumen_SumaNivelesPuntosYReparteContadoresPorDificultad()
    {
        var metricas = new Dictionary<string, double> { ["horas"] = 100, ["racha"] = 7 }; // horas: 3 niveles; racha: 2

        var resumen = MotorLogros.Evaluar(metricas, SinGuardados);

        resumen.NivelesDesbloqueados.Should().Be(5);
        resumen.Puntos.Should().Be(Puntaje.Acumulado(3) + Puntaje.Acumulado(2)); // 6 + 3
        resumen.PorNivel[NivelLogro.Bronce].Should().Be(2);
        resumen.PorNivel[NivelLogro.Plata].Should().Be(2);
        resumen.PorNivel[NivelLogro.Oro].Should().Be(1);
        resumen.PorNivel[NivelLogro.Platino].Should().Be(0);
    }

    // ───────────────────────────── Puntos y rangos ─────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 6)]
    [InlineData(4, 11)]
    [InlineData(5, 19)]
    public void Puntaje_AcumuladoPorNivel(int nivel, int esperado)
    {
        Puntaje.Acumulado(nivel).Should().Be(esperado);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(11, 0)]
    [InlineData(12, 1)]
    [InlineData(40, 2)]
    [InlineData(90, 3)]
    [InlineData(160, 4)]
    [InlineData(239, 4)]
    [InlineData(240, 5)]
    [InlineData(9999, 5)]
    public void Rangos_SegunPuntos(int puntos, int rangoEsperado)
    {
        Rangos.Para(puntos).Indice.Should().Be(rangoEsperado);
    }

    [Fact]
    public void Rango_ProgresoHaciaElSiguiente_YRangoMaximoSiempreCompleto()
    {
        Rangos.Para(26).ProgresoHaciaSiguiente(26).Should().BeApproximately(0.5, 1e-9); // 12..40 → mitad
        Rangos.Para(300).PuntosSiguiente.Should().BeNull();
        Rangos.Para(300).ProgresoHaciaSiguiente(300).Should().Be(1.0);
    }

    // ───────────────────────────── Integridad del catálogo ─────────────────────────────

    [Fact]
    public void Catalogo_IdsUnicos_UmbralesEstrictamenteCrecientes_YMaximoCincoNiveles()
    {
        CatalogoLogros.Todos.Select(l => l.Id).Should().OnlyHaveUniqueItems();

        foreach (var logro in CatalogoLogros.Todos)
        {
            logro.Umbrales.Count.Should().BeInRange(1, 5, logro.Id);
            logro.Umbrales.Should().BeInAscendingOrder(logro.Id);
            logro.Umbrales.Distinct().Should().HaveCount(logro.Umbrales.Count, logro.Id);
        }
    }

    [Fact]
    public void Catalogo_TodaFamiliaTieneUnaMetricaCalculada()
    {
        var metricas = MotorLogros.CalcularMetricas(new List<AnimeItem>(), new List<RegistroEpisodio>());

        CatalogoLogros.Todos.Select(l => l.Id).Should().OnlyContain(id => metricas.ContainsKey(id));
    }

    [Fact]
    public void Catalogo_SuperaLosCincuentaNiveles_YTieneSecretos()
    {
        CatalogoLogros.NivelesTotales.Should().BeGreaterThanOrEqualTo(50);
        CatalogoLogros.Todos.Should().Contain(l => l.Secreto);
    }

    private static readonly string[] ClavesGenerales =
    {
            "Nav_Logros", "Logro_Titulo", "Logro_Subtitulo", "Logro_Cat_Todas", "Logro_Est_Todos", "Logro_Est_EnProgreso",
            "Logro_Est_Desbloqueados", "Logro_Est_Bloqueados", "Logro_Secreto_Titulo", "Logro_Secreto_Desc", "Logro_Maximo",
            "Logro_PuntosFormato", "Logro_PuntosTotalFormato", "Logro_NivelesFormato", "Logro_SiguienteRangoFormato",
            "Logro_RangoMaximo", "Logro_DesbloqueadoElFormato", "Logro_VacioTitulo", "Logro_VacioSubtitulo", "Logro_SinResultados",
            "Logro_Toast_TituloUno", "Logro_Toast_TituloVarios", "Logro_Toast_LineaFormato", "Logro_Toast_ResumenFormato",
            "Stats_LogrosSub", "Stats_LogrosProximos", "Stats_LogrosVerTodos"
    };

    [Fact]
    public void Catalogo_TodosLosTextosExistenEnEspanolEIngles()
    {
        var es = DiccionarioDeTextos("Es");
        var en = DiccionarioDeTextos("En");

        var claves = new List<string>();
        foreach (var l in CatalogoLogros.Todos) { claves.Add(l.ClaveTitulo); claves.Add(l.ClaveDescripcion); }
        for (int n = 0; n <= 5; n++) claves.Add($"Logro_Nivel_{n}");
        for (int r = 0; r <= 5; r++) claves.Add($"Logro_Rango_{r}");
        foreach (var c in Enum.GetValues<CategoriaLogro>()) claves.Add($"Logro_Cat_{c}");
        claves.AddRange(ClavesGenerales);

        es.Keys.Should().Contain(claves, "todas las claves deben existir en español");
        en.Keys.Should().Contain(claves, "todas las claves deben existir en inglés");
    }

    [Fact]
    public void Catalogo_LasDescripcionesUsanElMarcadorDelUmbral()
    {
        var es = DiccionarioDeTextos("Es");
        var en = DiccionarioDeTextos("En");

        foreach (var l in CatalogoLogros.Todos)
        {
            es[l.ClaveDescripcion].Should().Contain("{0}", l.Id);
            en[l.ClaveDescripcion].Should().Contain("{0}", l.Id);
        }
    }

    [Fact]
    public void Catalogo_TodosLosIconosExistenEnMaterialDesign()
    {
        // Un icono inexistente no falla al compilar: en pantalla queda en blanco.
        foreach (var l in CatalogoLogros.Todos)
        {
            Enum.TryParse<MaterialDesignThemes.Wpf.PackIconKind>(l.Icono, out _).Should().BeTrue($"'{l.Icono}' ({l.Id}) debe ser un PackIconKind válido");
        }
        Enum.TryParse<MaterialDesignThemes.Wpf.PackIconKind>("LockQuestion", out _).Should().BeTrue();
    }

    private static Dictionary<string, string> DiccionarioDeTextos(string campo) =>
        (Dictionary<string, string>)typeof(LocalizationService)
            .GetField(campo, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
}
