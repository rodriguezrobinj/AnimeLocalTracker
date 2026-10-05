using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Core;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.ViewModels;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Xunit;

namespace AnimeLocalTracker.Tests.ViewModels;

/// <summary>
/// Cajón de episodios (marca del que se está viendo), aviso cuando el siguiente/anterior descargado no es el consecutivo,
/// y el ecualizador del reproductor.
/// </summary>
public class ReproductorCajonYEcualizadorTests
{
    private readonly Mock<ISettingsService> _ajustes = new();
    private readonly Mock<IDatabaseService> _db = new();
    private readonly Mock<IDialogService> _dialogos = new();
    private AppSettings _config = new();

    public ReproductorCajonYEcualizadorTests()
    {
        _ajustes.Setup(s => s.ObtenerConfiguracion()).Returns(() => _config);
        _ajustes.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>())).Returns(Task.CompletedTask);
    }

    private ReproductorViewModel CrearSut(bool conAjustes = true, bool conDialogos = false) => new(
        _db.Object, Mock.Of<IAnimeTrackingService>(), Mock.Of<IAuthService>(),
        settingsService: conAjustes ? _ajustes.Object : null,
        playbackStateService: Mock.Of<IPlaybackStateService>(),
        dialogService: conDialogos ? _dialogos.Object : null);

    private static List<EpisodioItem> Lista(params int[] numeros) =>
        numeros.Select(n => new EpisodioItem { NumeroEpisodio = n, RutaCompleta = $"C:\\Anime\\Ep{n:00}.mkv" }).ToList();

    // ── Episodio en reproducción ──

    [Fact]
    public void ElCajonMarcaSoloElEpisodioQueSeEstaViendo_YLaMarcaSigueAlCambiar()
    {
        var lista = Lista(1, 2, 3);
        using (var sut = CrearSut())
        {
            sut.CargarVideo(lista[0].RutaCompleta, 101, "Frieren", 1, lista);
            lista.Select(e => e.EsReproduciendose).Should().Equal(true, false, false);

            sut.SiguienteEpisodio();
            lista.Select(e => e.EsReproduciendose).Should().Equal(false, true, false);
        }

        lista.Should().OnlyContain(e => !e.EsReproduciendose, "los episodios son los de la ficha: al cerrar el reproductor no deben quedar marcados");
    }

    // ── Saltos no consecutivos ──

    [Fact]
    public void EpisodiosIntermedios_SonLosQueQuedanEntreMedias_EnCualquierSentido()
    {
        EpisodeNavigator.EpisodiosIntermedios(3, 4).Should().BeEmpty();
        EpisodeNavigator.EpisodiosIntermedios(3, 7).Should().Equal(4, 5, 6);
        EpisodeNavigator.EpisodiosIntermedios(7, 3).Should().Equal(4, 5, 6);
    }

    [Fact]
    public void TextoEpisodiosSaltados_NombraElHueco()
    {
        ReproductorViewModel.TextoEpisodiosSaltados(3, 4).Should().BeEmpty();
        ReproductorViewModel.TextoEpisodiosSaltados(3, 5).Should().Contain("4");
        ReproductorViewModel.TextoEpisodiosSaltados(3, 7).Should().Contain("4").And.Contain("6").And.Contain("3");
    }

    [Fact]
    public void SiFaltanEpisodiosDescargados_ElSiguienteLoAvisa_YElAnteriorConsecutivoNo()
    {
        var lista = Lista(1, 2, 3, 6, 7);
        using var sut = CrearSut();

        sut.CargarVideo(lista[2].RutaCompleta, 101, "Frieren", 3, lista);

        sut.AvisoSaltoSiguiente.Should().Contain("4").And.Contain("5");
        sut.EpisodioSiguienteTooltip.Should().Contain("6").And.Contain("⚠");
        sut.AvisoSaltoAnterior.Should().BeEmpty();
        sut.EpisodioAnteriorTooltip.Should().NotContain("⚠");
    }

    [Fact]
    public void PasarConLaTeclaSiguienteSobreUnHueco_AvisaConUnToast()
    {
        var lista = Lista(3, 6);
        var avisos = new List<MostrarDialogoRequestMessage>();
        var receptor = new object();
        WeakReferenceMessenger.Default.Register<MostrarDialogoRequestMessage>(receptor, (_, m) =>
        {
            lock (avisos) avisos.Add(m);
        });
        try
        {
            using var sut = CrearSut();
            sut.CargarVideo(lista[0].RutaCompleta, 101, "Frieren", 3, lista);
            sut.SiguienteEpisodio();

            sut.Episodio.Should().Be(6);
            lock (avisos)
                avisos.Should().Contain(m => m.Mensaje.Contains('4') && m.Mensaje.Contains('5') && m.Titulo.Contains('6'));
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(receptor);
        }
    }

    // ── Volumen ──

    [Fact]
    public void ElVolumenGuardado_SeUsaAlAbrirOtroVideo()
    {
        _config = new AppSettings { VolumenReproductor = 35 };

        using var sut = CrearSut();

        sut.Volumen.Should().Be(35);
    }

    [Fact]
    public async Task CambiarElVolumen_LoGuarda()
    {
        using var sut = CrearSut();

        sut.Volumen = 40;
        await Task.Delay(900);

        _config.VolumenReproductor.Should().Be(40);
    }

    // Silenciar pone la barra a cero: eso no es "quiero el volumen a cero en el próximo video".
    [Fact]
    public async Task Silenciar_NoGuardaElCero()
    {
        _config = new AppSettings { VolumenReproductor = 60 };
        using var sut = CrearSut();

        sut.ToggleMute();
        await Task.Delay(900);

        sut.Volumen.Should().Be(0);
        _config.VolumenReproductor.Should().Be(60);
    }

    // ── Sonido por anime o por capítulo ──

    private void HayAudioGuardado(int anime, int episodio, int volumen, string preset, bool noche) =>
        _db.Setup(d => d.ObtenerAjusteAudioAsync(anime, episodio)).ReturnsAsync(new AjusteAudio
        {
            Clave = AjusteAudio.ClaveDe(anime, episodio), AniListId = anime, NumeroEpisodio = episodio, Volumen = volumen,
            EcualizadorActivo = true, Ganancias = EcualizadorAudio.Presets.Single(p => p.Clave == preset).Ganancias.ToList(), ModoNoche = noche,
        });

    private async Task<ReproductorViewModel> AbrirAsync(int episodio = 3)
    {
        var lista = Lista(3, 4);
        var sut = CrearSut();
        sut.CargarVideo(lista[0].RutaCompleta, 101, "Frieren", episodio, lista);
        await sut.TareaAudio;
        return sut;
    }

    [Fact]
    public async Task Global_NoBuscaNadaPorAnime_YElModoNocheSeGuardaEnLosAjustes()
    {
        _config = new AppSettings { VolumenReproductor = 80 };
        using var sut = await AbrirAsync();

        sut.ModoNocheActivo = true;
        await Task.Delay(900);

        sut.Volumen.Should().Be(80);
        _config.ModoNocheActivo.Should().BeTrue();
        _db.Verify(d => d.ObtenerAjusteAudioAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        _db.Verify(d => d.GuardarAjusteAudioAsync(It.IsAny<AjusteAudio>()), Times.Never);
    }

    [Fact]
    public async Task PorAnime_AbreConLoGuardadoParaEseAnime_YNoLoVuelveAGuardar()
    {
        _config = new AppSettings { AmbitoAjustesAudio = AmbitoAudio.PorAnime, VolumenReproductor = 80 };
        HayAudioGuardado(101, 0, volumen: 30, preset: "Cine", noche: true);

        using var sut = await AbrirAsync();
        await Task.Delay(900);

        sut.Volumen.Should().Be(30);
        sut.EcualizadorActivo.Should().BeTrue();
        sut.PresetEcualizador.Should().Be("Cine");
        sut.PresetsEcualizador.Where(p => p.EsActual).Select(p => p.Clave).Should().Equal("Cine");
        sut.ModoNocheActivo.Should().BeTrue();
        _config.VolumenReproductor.Should().Be(80, "lo del anime no toca lo global");
        _db.Verify(d => d.GuardarAjusteAudioAsync(It.IsAny<AjusteAudio>()), Times.Never);
        _ajustes.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task PorAnime_SinNadaGuardado_EmpiezaConLosAjustesGlobales()
    {
        var voces = EcualizadorAudio.Presets.Single(p => p.Clave == "VocesClaras").Ganancias.ToList();
        _config = new AppSettings { AmbitoAjustesAudio = AmbitoAudio.PorAnime, VolumenReproductor = 45, EcualizadorActivo = true, EcualizadorGanancias = voces };

        using var sut = await AbrirAsync();

        sut.Volumen.Should().Be(45);
        sut.EcualizadorActivo.Should().BeTrue();
        sut.PresetEcualizador.Should().Be("VocesClaras");
    }

    [Fact]
    public async Task PorAnime_UnCambio_SeGuardaSoloEnEseAnime()
    {
        _config = new AppSettings { AmbitoAjustesAudio = AmbitoAudio.PorAnime, VolumenReproductor = 80 };
        using var sut = await AbrirAsync();

        sut.Volumen = 40;
        await Task.Delay(900);

        _db.Verify(d => d.GuardarAjusteAudioAsync(It.Is<AjusteAudio>(a =>
            a.Clave == AjusteAudio.ClaveDe(101, 0) && a.AniListId == 101 && a.NumeroEpisodio == 0 && a.Volumen == 40)), Times.Once);
        _config.VolumenReproductor.Should().Be(80);
        _ajustes.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task PorCapitulo_PrefiereLoDelCapitulo_YAlPasarAOtroSinAjustesTomaLosDelAnime()
    {
        _config = new AppSettings { AmbitoAjustesAudio = AmbitoAudio.PorCapitulo, VolumenReproductor = 80 };
        HayAudioGuardado(101, 0, volumen: 30, preset: "Cine", noche: false);
        HayAudioGuardado(101, 3, volumen: 55, preset: "Musica", noche: true);

        using var sut = await AbrirAsync(episodio: 3);
        sut.Volumen.Should().Be(55);
        sut.PresetEcualizador.Should().Be("Musica");
        sut.ModoNocheActivo.Should().BeTrue();

        sut.SiguienteEpisodio(); // el 4 no tiene ajustes propios
        await sut.TareaAudio;

        sut.Episodio.Should().Be(4);
        sut.Volumen.Should().Be(30);
        sut.PresetEcualizador.Should().Be("Cine");
        sut.ModoNocheActivo.Should().BeFalse();
    }

    [Fact]
    public async Task PorCapitulo_ElModoNoche_SeGuardaEnEseCapitulo()
    {
        _config = new AppSettings { AmbitoAjustesAudio = AmbitoAudio.PorCapitulo };
        using var sut = await AbrirAsync(episodio: 3);

        sut.ModoNocheActivo = true;
        await Task.Delay(900);

        _db.Verify(d => d.GuardarAjusteAudioAsync(It.Is<AjusteAudio>(a => a.AniListId == 101 && a.NumeroEpisodio == 3 && a.ModoNoche)), Times.Once);
        _config.ModoNocheActivo.Should().BeFalse();
    }

    // Con el video silenciado, cargar los ajustes del siguiente capítulo no debe devolverle el sonido.
    [Fact]
    public async Task PorCapitulo_SiEstaSilenciado_SigueSilenciadoAlCambiarDeCapitulo()
    {
        _config = new AppSettings { AmbitoAjustesAudio = AmbitoAudio.PorCapitulo };
        HayAudioGuardado(101, 4, volumen: 70, preset: "Cine", noche: false);
        using var sut = await AbrirAsync(episodio: 3);
        sut.ToggleMute();

        sut.SiguienteEpisodio();
        await sut.TareaAudio;

        sut.IsMuted.Should().BeTrue();
        sut.Volumen.Should().Be(0);
        sut.ToggleMute();
        sut.Volumen.Should().Be(70, "al quitar el silencio vuelve con el volumen de ese capítulo");
    }

    // ── Ecualizador ──

    [Fact]
    public void SinAjustes_ElEcualizadorEmpiezaPlanoYApagado()
    {
        using var sut = CrearSut(conAjustes: false);

        sut.BandasEcualizador.Should().HaveCount(10).And.OnlyContain(b => b.Ganancia == 0);
        sut.EcualizadorActivo.Should().BeFalse();
        sut.PresetEcualizador.Should().Be(EcualizadorAudio.PresetPlano);
    }

    [Fact]
    public async Task CargaLoGuardado_YNoLoVuelveAGuardarSoloPorAbrirElReproductor()
    {
        var voces = EcualizadorAudio.Presets.Single(p => p.Clave == "VocesClaras").Ganancias.ToList();
        _config = new AppSettings { EcualizadorActivo = true, EcualizadorGanancias = voces };

        using var sut = CrearSut();
        await Task.Delay(700);

        sut.EcualizadorActivo.Should().BeTrue();
        sut.BandasEcualizador.Select(b => b.Ganancia).Should().Equal(voces);
        sut.PresetEcualizador.Should().Be("VocesClaras");
        sut.PresetsEcualizador.Where(p => p.EsActual).Select(p => p.Clave).Should().Equal("VocesClaras");
        _ajustes.Verify(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task ElegirUnAjuste_LoPoneEnLasBandas_EnciendeElEcualizador_YSeGuarda()
    {
        AppSettings? guardado = null;
        _ajustes.Setup(s => s.GuardarConfiguracionAsync(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(c => guardado = c).Returns(Task.CompletedTask);
        var cine = EcualizadorAudio.Presets.Single(p => p.Clave == "Cine").Ganancias;
        using var sut = CrearSut();

        sut.AplicarPresetEcualizadorCommand.Execute("Cine");
        await Task.Delay(900);

        sut.BandasEcualizador.Select(b => b.Ganancia).Should().Equal(cine);
        sut.EcualizadorActivo.Should().BeTrue();
        sut.PresetsEcualizador.Single(p => p.EsActual).Clave.Should().Be("Cine");
        guardado.Should().NotBeNull();
        guardado!.EcualizadorActivo.Should().BeTrue();
        guardado.EcualizadorGanancias.Should().Equal(cine);
    }

    [Fact]
    public void MoverUnaBanda_LoConvierteEnPersonalizado_YEnciendeElEcualizador()
    {
        using var sut = CrearSut();
        sut.AplicarPresetEcualizadorCommand.Execute("Musica");
        sut.EcualizadorActivo = false;

        sut.BandasEcualizador[3].Ganancia = 5;

        sut.PresetEcualizador.Should().Be(EcualizadorAudio.PresetPersonalizado);
        sut.PresetsEcualizador.Should().OnlyContain(p => !p.EsActual);
        sut.EcualizadorActivo.Should().BeTrue();
    }

    [Fact]
    public void Restablecer_DejaTodoPlano()
    {
        using var sut = CrearSut();
        sut.AplicarPresetEcualizadorCommand.Execute("GravesPotentes");

        sut.RestablecerEcualizadorCommand.Execute(null);

        sut.BandasEcualizador.Should().OnlyContain(b => b.Ganancia == 0);
        sut.PresetEcualizador.Should().Be(EcualizadorAudio.PresetPlano);
    }

    // ── Favorito del episodio (botón de arriba) ──

    [Fact]
    public void ConLaListaDeLaFicha_ElBotonMuestraSiElEpisodioEsFavorito()
    {
        var lista = Lista(1, 2);
        lista[1].Favorito = true;
        using var sut = CrearSut();

        sut.CargarVideo(lista[0].RutaCompleta, 101, "Frieren", 1, lista);
        sut.EpisodioEsFavorito.Should().BeFalse();

        sut.SiguienteEpisodio();
        sut.EpisodioEsFavorito.Should().BeTrue();
    }

    [Fact]
    public async Task MarcarFavorito_ActualizaLaFilaDeLaFichaYGuardaSoloLaMarca()
    {
        var lista = Lista(1, 2);
        using var sut = CrearSut();
        sut.CargarVideo(lista[0].RutaCompleta, 101, "Frieren", 1, lista);

        await sut.AlternarFavoritoEpisodioCommand.ExecuteAsync(null);

        sut.EpisodioEsFavorito.Should().BeTrue();
        lista[0].Favorito.Should().BeTrue("la ficha y el cajón usan la misma fila");
        _db.Verify(d => d.GuardarFavoritoEpisodioAsync(101, 1, true, lista[0].RutaCompleta), Times.Once);
        _db.Verify(d => d.GuardarRegistroEpisodioAsync(It.IsAny<RegistroEpisodio>()), Times.Never, "guardar el registro entero ponía a cero el progreso");
    }

    [Fact]
    public async Task SinListaDeLaFicha_ElFavoritoSeLeeDeLaBaseDeDatos()
    {
        _db.Setup(d => d.ObtenerRegistrosPorAnimeAsync(101)).ReturnsAsync(new List<RegistroEpisodio>
        {
            new() { AniListId = 101, NumeroEpisodio = 4, FavoritoLocal = false },
            new() { AniListId = 101, NumeroEpisodio = 5, FavoritoLocal = true },
        });
        using var sut = CrearSut();

        await sut.CargarVideoAsync(@"C:\Anime\Ep05.mkv", 101, "Frieren", 5); // abierto desde Historial: sin lista

        sut.EpisodioEsFavorito.Should().BeTrue();
    }

    // ── Avisos: los del reproductor se marcan como suyos ──

    [Fact]
    public void LosAvisosDelReproductor_SeMandanComoDelReproductor()
    {
        var lista = Lista(3, 6);
        using var sut = CrearSut(conDialogos: true);
        sut.CargarVideo(lista[0].RutaCompleta, 101, "Frieren", 3, lista);

        sut.SiguienteEpisodio(); // se salta el 4 y el 5

        _dialogos.Verify(d => d.MostrarToastReproductor(It.IsAny<string>(), It.Is<string>(m => m.Contains('4')), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _dialogos.Verify(d => d.MostrarToast(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
