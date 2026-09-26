using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using FluentAssertions;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>Reproductor de pistas simulado: registra las llamadas y deja disparar los eventos a mano.</summary>
public sealed class FakeAudioTrackPlayer : IAudioTrackPlayer
{
    public List<string> Llamadas { get; } = new();
    public string? RutaAbierta { get; private set; }
    public bool EstaCerrado { get; private set; } = true;
    public bool EstaSonando { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Duración con la que "se abre" cualquier archivo.</summary>
    public TimeSpan DuracionAlAbrir { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Si es true, Abrir dispara Abierto enseguida (como hace MediaPlayer al terminar de cargar).</summary>
    public bool AbrirDisparaEvento { get; set; } = true;

    public TimeSpan Posicion { get; set; }
    public TimeSpan Duracion { get; private set; }
    public double Volumen { get; set; } = 0.8;

    public event EventHandler? Abierto;
    public event EventHandler? Terminado;
    public event EventHandler? Fallo;

    public void Abrir(string ruta)
    {
        Llamadas.Add($"Abrir:{ruta}");
        RutaAbierta = ruta;
        EstaCerrado = false;
        Posicion = TimeSpan.Zero;
        Duracion = TimeSpan.Zero;
        if (AbrirDisparaEvento) DispararAbierto();
    }

    public void DispararAbierto()
    {
        Duracion = DuracionAlAbrir;
        Abierto?.Invoke(this, EventArgs.Empty);
    }

    public void Reproducir() { Llamadas.Add("Reproducir"); EstaSonando = true; }
    public void Pausar() { Llamadas.Add("Pausar"); EstaSonando = false; }

    public void Cerrar()
    {
        Llamadas.Add("Cerrar");
        EstaCerrado = true;
        EstaSonando = false;
        RutaAbierta = null;
        Duracion = TimeSpan.Zero;
    }

    public void DispararTerminado() { EstaSonando = false; Terminado?.Invoke(this, EventArgs.Empty); }
    public void DispararFallo() => Fallo?.Invoke(this, EventArgs.Empty);
    public void Dispose() { Disposed = true; Llamadas.Add("Dispose"); }

    public int VecesQue(string prefijo) => Llamadas.Count(l => l.StartsWith(prefijo, StringComparison.Ordinal));
}

public class ControlReproduccionTemasTests : IDisposable
{
    private readonly FakeAudioTrackPlayer _player = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _player.Dispose();
    }

    private ControlReproduccionTemas CrearSut() => new(_player, usarTemporizador: false);

    private static TemaAnimeItem Tema(string slug = "OP1") => new()
    {
        Info = new AnimeThemeInfo { Slug = slug, Tipo = "OP", TituloCancion = $"Canción {slug}", AudioUrlOgg = "https://a.animethemes.moe/x.ogg" }
    };

    // === Reproducir / pausar / reanudar ===

    [Fact]
    public void Alternar_ConUnTemaNuevo_DeberiaAbrirloReproducirloYMarcarloComoActual()
    {
        var sut = CrearSut();
        var tema = Tema();

        sut.Alternar(tema, @"C:\Music\1\op1.mp3");

        _player.RutaAbierta.Should().Be(@"C:\Music\1\op1.mp3");
        _player.EstaSonando.Should().BeTrue();
        sut.Actual.Should().BeSameAs(tema);
        tema.Reproduciendo.Should().BeTrue();
        tema.EsPistaActual.Should().BeTrue();
        tema.DuracionSegundos.Should().Be(90, "se conoce en cuanto el archivo se abre");
        tema.PosicionSegundos.Should().Be(0);
    }

    [Fact]
    public void Alternar_ElMismoTemaMientrasSuena_DeberiaPausar()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");

        sut.Alternar(tema, "a.mp3");

        _player.EstaSonando.Should().BeFalse();
        tema.Reproduciendo.Should().BeFalse();
        tema.EsPistaActual.Should().BeTrue("en pausa sigue cargada y con su barra visible");
    }

    [Fact]
    public void Alternar_TrasPausar_DeberiaReanudarDondeSeDejoSinVolverAAbrirElArchivo()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");
        _player.Posicion = TimeSpan.FromSeconds(42);
        sut.Alternar(tema, "a.mp3"); // pausa

        sut.Alternar(tema, "a.mp3"); // reanuda

        _player.EstaSonando.Should().BeTrue();
        tema.Reproduciendo.Should().BeTrue();
        _player.VecesQue("Abrir").Should().Be(1, "reabrirlo lo hacía empezar de cero (fallo de antes)");
        _player.Posicion.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public void Alternar_ConOtroTema_DeberiaCortarElAnteriorYEmpezarElNuevo()
    {
        var sut = CrearSut();
        var primero = Tema("OP1");
        var segundo = Tema("ED1");
        sut.Alternar(primero, "a.mp3");
        _player.Posicion = TimeSpan.FromSeconds(30);
        sut.ActualizarPosicion();

        sut.Alternar(segundo, "b.mp3");

        sut.Actual.Should().BeSameAs(segundo);
        primero.Reproduciendo.Should().BeFalse();
        primero.EsPistaActual.Should().BeFalse("solo la pista cargada muestra la barra");
        primero.PosicionSegundos.Should().Be(0);
        segundo.Reproduciendo.Should().BeTrue();
        _player.RutaAbierta.Should().Be("b.mp3");
    }

    // === Fin de pista ===

    [Fact]
    public void AlTerminarLaPista_DeberiaVolverAlPrincipioConLaBarraVisible_YPoderRepetirse()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");
        _player.Posicion = TimeSpan.FromSeconds(90);

        _player.DispararTerminado();

        tema.Reproduciendo.Should().BeFalse();
        tema.EsPistaActual.Should().BeTrue();
        tema.PosicionSegundos.Should().Be(0);
        _player.Posicion.Should().Be(TimeSpan.Zero);

        sut.Alternar(tema, "a.mp3"); // repetir

        _player.EstaSonando.Should().BeTrue();
        tema.Reproduciendo.Should().BeTrue();
        _player.VecesQue("Abrir").Should().Be(1);
    }

    // === Barra: saltar y actualizar ===

    [Fact]
    public void Buscar_DeberiaMoverElReproductorYLaPosicionDelTema()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");

        sut.Buscar(tema, 35.5);

        _player.Posicion.Should().Be(TimeSpan.FromSeconds(35.5));
        tema.PosicionSegundos.Should().Be(35.5);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(500, 90)]
    public void Buscar_FueraDeRango_DeberiaAjustarseALosLimitesDeLaPista(double pedido, double esperado)
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");

        sut.Buscar(tema, pedido);

        tema.PosicionSegundos.Should().Be(esperado);
        _player.Posicion.Should().Be(TimeSpan.FromSeconds(esperado));
    }

    [Fact]
    public void Buscar_ConUnTemaQueNoEsLaPistaActualOUnNumeroInvalido_NoDeberiaHacerNada()
    {
        var sut = CrearSut();
        var actual = Tema("OP1");
        var otro = Tema("ED1");
        sut.Alternar(actual, "a.mp3");
        _player.Posicion = TimeSpan.FromSeconds(10);

        sut.Buscar(otro, 50);
        sut.Buscar(actual, double.NaN);
        sut.Buscar(actual, double.PositiveInfinity);

        _player.Posicion.Should().Be(TimeSpan.FromSeconds(10));
        otro.PosicionSegundos.Should().Be(0);
    }

    [Fact]
    public void ActualizarPosicion_DeberiaCopiarLaPosicionDelReproductorAlTema()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");
        _player.Posicion = TimeSpan.FromSeconds(12.25);

        sut.ActualizarPosicion();

        tema.PosicionSegundos.Should().Be(12.25);
        tema.PosicionTexto.Should().Be("0:12");
        tema.DuracionTexto.Should().Be("1:30");
    }

    [Fact]
    public void ActualizarPosicion_MientrasElUsuarioArrastraLaBarra_NoDeberiaQuitarleElControl()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");
        tema.Arrastrando = true;
        tema.PosicionSegundos = 70; // donde el usuario ha puesto el pulgar
        _player.Posicion = TimeSpan.FromSeconds(5);

        sut.ActualizarPosicion();

        tema.PosicionSegundos.Should().Be(70);
    }

    [Fact]
    public void ActualizarPosicion_DeberiaMarcarQueElCambioVieneDelReproductor_ParaQueLaBarraNoLoTomeComoUnSalto()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");
        _player.Posicion = TimeSpan.FromSeconds(20);
        var sincronizandoAlCambiar = new List<bool>();
        tema.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TemaAnimeItem.PosicionSegundos)) sincronizandoAlCambiar.Add(tema.Sincronizando);
        };

        sut.ActualizarPosicion();
        sut.Buscar(tema, 30);

        sincronizandoAlCambiar.Should().NotBeEmpty().And.OnlyContain(x => x);
        tema.Sincronizando.Should().BeFalse("la marca se quita al terminar");
    }

    [Fact]
    public void ActualizarPosicion_SinPistaActual_NoDeberiaFallar()
    {
        var act = () => CrearSut().ActualizarPosicion();

        act.Should().NotThrow();
    }

    // === Detener / volumen ===

    [Fact]
    public void Detener_DeberiaCortarLiberarElArchivoYLimpiarElEstado()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");

        sut.Detener();

        sut.Actual.Should().BeNull();
        _player.EstaCerrado.Should().BeTrue();
        tema.Reproduciendo.Should().BeFalse();
        tema.EsPistaActual.Should().BeFalse();
        tema.PosicionSegundos.Should().Be(0);
    }

    [Fact]
    public void Detener_SinNadaSonando_NoDeberiaFallar()
    {
        var act = () => CrearSut().Detener();

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.4, 0.4)]
    [InlineData(-1, 0)]
    [InlineData(7, 1)]
    public void Volumen_DeberiaAplicarseAlReproductorDentroDeLosLimites(double pedido, double esperado)
    {
        var sut = CrearSut();

        sut.Volumen = pedido;

        sut.Volumen.Should().Be(esperado);
        _player.Volumen.Should().Be(esperado);
    }

    [Fact]
    public void Volumen_DeberiaAplicarseTambienAlasPistasQueSeAbranDespues()
    {
        var sut = CrearSut();
        sut.Volumen = 0.25;

        sut.Alternar(Tema(), "a.mp3");

        _player.Volumen.Should().Be(0.25);
    }

    // === Mover el archivo sin cortar la escucha (Guardar vista previa) ===

    [Fact]
    public void ConservandoPosicion_ConLaPistaSonando_DeberiaSoltarElArchivoEjecutarLaOperacionYReabrirEnElMismoPunto()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, @"C:\Preview\op1.mp3");
        _player.Posicion = TimeSpan.FromSeconds(42);
        _player.Llamadas.Clear();

        sut.ConservandoPosicion(tema, () =>
        {
            _player.EstaCerrado.Should().BeTrue("Windows no deja mover un archivo abierto por el reproductor");
            return @"C:\Music\op1.mp3";
        });

        _player.Llamadas.Should().ContainInOrder("Cerrar", @"Abrir:C:\Music\op1.mp3", "Reproducir");
        _player.Posicion.Should().Be(TimeSpan.FromSeconds(42));
        tema.PosicionSegundos.Should().Be(42);
        tema.Reproduciendo.Should().BeTrue();
        sut.Actual.Should().BeSameAs(tema);
    }

    [Fact]
    public void ConservandoPosicion_ConLaPistaEnPausa_DeberiaReabrirSinReanudar()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, @"C:\Preview\op1.mp3");
        _player.Posicion = TimeSpan.FromSeconds(15);
        sut.Alternar(tema, "x"); // pausa
        _player.Llamadas.Clear();

        sut.ConservandoPosicion(tema, () => @"C:\Music\op1.mp3");

        _player.Llamadas.Should().NotContain("Reproducir");
        _player.Posicion.Should().Be(TimeSpan.FromSeconds(15));
        tema.Reproduciendo.Should().BeFalse();
    }

    [Fact]
    public void ConservandoPosicion_SiLaOperacionFalla_DeberiaDetenerLaPista()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");

        sut.ConservandoPosicion(tema, () => null);

        sut.Actual.Should().BeNull();
        tema.EsPistaActual.Should().BeFalse();
        _player.EstaCerrado.Should().BeTrue();
    }

    [Fact]
    public void ConservandoPosicion_ConOtroTema_SoloEjecutaLaOperacionSinTocarLaPistaActual()
    {
        var sut = CrearSut();
        var actual = Tema("OP1");
        var otro = Tema("ED1");
        sut.Alternar(actual, "a.mp3");
        _player.Llamadas.Clear();
        bool ejecutada = false;

        sut.ConservandoPosicion(otro, () => { ejecutada = true; return "x"; });

        ejecutada.Should().BeTrue();
        _player.Llamadas.Should().BeEmpty();
        actual.Reproduciendo.Should().BeTrue();
    }

    // === Fallos y liberación ===

    [Fact]
    public void SiElReproductorFalla_DeberiaLimpiarYAvisarDeQueTemaFue()
    {
        var sut = CrearSut();
        var tema = Tema();
        var avisados = new List<TemaAnimeItem>();
        sut.FalloReproduccion += (_, t) => avisados.Add(t);
        sut.Alternar(tema, "a.mp3");

        _player.DispararFallo();

        avisados.Should().ContainSingle().Which.Should().BeSameAs(tema);
        sut.Actual.Should().BeNull();
        tema.Reproduciendo.Should().BeFalse();
    }

    [Fact]
    public void Dispose_DeberiaLiberarElReproductorYDejarDeEscucharSusEventos()
    {
        var sut = CrearSut();
        var tema = Tema();
        sut.Alternar(tema, "a.mp3");

        sut.Dispose();
        var act = () => _player.DispararTerminado();

        _player.Disposed.Should().BeTrue();
        act.Should().NotThrow();
        tema.Reproduciendo.Should().BeTrue("tras liberar, los eventos del reproductor ya no se procesan");
    }
}

public class TemaAnimeItemTests
{
    private static TemaAnimeItem Nuevo() => new() { Info = new AnimeThemeInfo { Slug = "OP1", Tipo = "OP", AudioUrlOgg = "https://a/x.ogg" } };

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(600, "10:00")]
    [InlineData(3725, "1:02:05")]
    [InlineData(-4, "0:00")]
    [InlineData(double.NaN, "0:00")]
    [InlineData(double.PositiveInfinity, "0:00")]
    public void FormatearTiempo_DeberiaMostrarMinutosYSegundos(double segundos, string esperado)
    {
        TemaAnimeItem.FormatearTiempo(segundos).Should().Be(esperado);
    }

    [Fact]
    public void Estados_UnTemaSinNada_SoloOfreceEscucharAntesDeDescargar()
    {
        var t = Nuevo();

        t.PuedePrevisualizar.Should().BeTrue();
        t.PuedeReproducir.Should().BeFalse();
        t.EsSoloVistaPrevia.Should().BeFalse();
    }

    [Fact]
    public void Estados_PreparandoOBajando_NoOfreceEscucharAntesDeDescargar()
    {
        var t = Nuevo();

        t.PreparandoVistaPrevia = true;
        t.PuedePrevisualizar.Should().BeFalse();
        t.PreparandoVistaPrevia = false;

        t.Descargando = true;
        t.PuedePrevisualizar.Should().BeFalse();
    }

    [Fact]
    public void Estados_ConVistaPrevia_SePuedeEscucharPeroNoEstaGuardado()
    {
        var t = Nuevo();

        t.VistaPreviaLista = true;

        t.PuedeReproducir.Should().BeTrue();
        t.EsSoloVistaPrevia.Should().BeTrue();
        t.PuedePrevisualizar.Should().BeFalse();
    }

    [Fact]
    public void Estados_Descargado_SePuedeEscucharYNoEsSoloVistaPrevia()
    {
        var t = Nuevo();
        t.VistaPreviaLista = true;

        t.Descargado = true;
        t.VistaPreviaLista = false;

        t.PuedeReproducir.Should().BeTrue();
        t.EsSoloVistaPrevia.Should().BeFalse();
        t.PuedePrevisualizar.Should().BeFalse();
    }

    [Fact]
    public void Progresos_DeberianExpresarseEnPorcentajeYQuedarEntre0Y100()
    {
        var t = Nuevo();

        t.ProgresoPreparacion = 0.456;
        t.ProgresoDescarga = 1.7;

        t.ProgresoPreparacionPorcentaje.Should().BeApproximately(45.6, 1e-9);
        t.ProgresoDescargaPorcentaje.Should().Be(100);
    }

    [Fact]
    public void Cambios_DeberianNotificarLasPropiedadesDerivadas()
    {
        var t = Nuevo();
        var avisadas = new List<string?>();
        ((INotifyPropertyChanged)t).PropertyChanged += (_, e) => avisadas.Add(e.PropertyName);

        t.VistaPreviaLista = true;
        t.PosicionSegundos = 12;
        t.DuracionSegundos = 90;

        avisadas.Should().Contain([nameof(TemaAnimeItem.PuedeReproducir), nameof(TemaAnimeItem.EsSoloVistaPrevia),
            nameof(TemaAnimeItem.PuedePrevisualizar), nameof(TemaAnimeItem.PosicionTexto), nameof(TemaAnimeItem.DuracionTexto)]);
    }
}
