using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.ViewModels;

/// <summary>Etapas del panel "Qué veo hoy" (el panel solo está visible fuera de <see cref="Oculto"/>).</summary>
public enum FaseQueVer
{
    Oculto,
    /// <summary>Escaneando carpetas de la biblioteca en busca de episodios pendientes.</summary>
    Buscando,
    /// <summary>La tira de portadas gira hasta detenerse en el anime elegido.</summary>
    Girando,
    /// <summary>Anime elegido: el usuario decide entre verlo, pedir otro o cerrar.</summary>
    Resultado,
    SinCarpeta,
    SinPendientes,
    Error
}

/// <summary>
/// "Qué veo hoy": elige al azar un anime con episodios locales pendientes y propone su SIGUIENTE
/// episodio no visto (orden cronológico, para no romper la trama). En vez de saltar directo al
/// reproductor, muestra una ruleta de portadas y deja al usuario aceptar, pedir otro o cerrar.
/// </summary>
public partial class GaleriaViewModel : IDisposable
{
    // Con 8 candidatos hay variedad de sobra para la ruleta y para varios "Otro": no hace falta
    // escanear TODA la biblioteca en cada clic (antes se recorrían las carpetas de los ~200 animes).
    private const int CandidatosObjetivoQueVer = 8;
    private const int EscaneosParalelosQueVer = 4;
    private const int LargoTiraQueVer = 40;
    private const int IndiceGanadorTiraQueVer = 33;
    private const int PortadasDistintasTiraQueVer = 18;

    private sealed record CandidatoQueVer(AnimeItem Anime, EpisodioItem Siguiente, List<EpisodioItem> Todos);

    private readonly List<CandidatoQueVer> _candidatosQueVer = new();
    private readonly HashSet<int> _yaMostradosQueVer = new();
    private Queue<AnimeItem> _colaEscaneoQueVer = new();
    private Queue<AnimeItem> _colaFallbackQueVer = new();
    private CandidatoQueVer? _elegidoQueVer;
    private CancellationTokenSource? _ctsQueVer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueVerHoyAbierto))]
    [NotifyPropertyChangedFor(nameof(EstaBuscandoQueVer))]
    [NotifyPropertyChangedFor(nameof(SePuedeAyudarAverQueVer))]
    [NotifyPropertyChangedFor(nameof(EsFaseBuscandoQueVer))]
    [NotifyPropertyChangedFor(nameof(EsFaseGirandoQueVer))]
    [NotifyPropertyChangedFor(nameof(EsFaseResultadoQueVer))]
    [NotifyPropertyChangedFor(nameof(MostrarTiraQueVer))]
    [NotifyPropertyChangedFor(nameof(EsFaseVaciaQueVer))]
    [NotifyCanExecuteChangedFor(nameof(ElegirQueVerHoyCommand))]
    [NotifyCanExecuteChangedFor(nameof(OtroQueVerHoyCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerElegidoQueVerCommand))]
    private FaseQueVer _faseActualQueVer = FaseQueVer.Oculto;

    public bool QueVerHoyAbierto => FaseActualQueVer != FaseQueVer.Oculto;
    /// <summary>Mientras hay panel abierto y trabajando (escaneo o ruleta) el botón queda ocupado.</summary>
    public bool EstaBuscandoQueVer => FaseActualQueVer is FaseQueVer.Buscando or FaseQueVer.Girando;
    public bool SePuedeAyudarAverQueVer => FaseActualQueVer == FaseQueVer.Oculto && !BibliotecaVacia;
    public bool EsFaseBuscandoQueVer => FaseActualQueVer == FaseQueVer.Buscando;
    public bool EsFaseGirandoQueVer => FaseActualQueVer == FaseQueVer.Girando;
    public bool EsFaseResultadoQueVer => FaseActualQueVer == FaseQueVer.Resultado;
    public bool MostrarTiraQueVer => FaseActualQueVer is FaseQueVer.Girando or FaseQueVer.Resultado;
    public bool EsFaseVaciaQueVer => FaseActualQueVer is FaseQueVer.SinCarpeta or FaseQueVer.SinPendientes or FaseQueVer.Error;

    /// <summary>Portadas de la ruleta; el ganador está en <see cref="RuletaIndiceGanadorQueVer"/>.</summary>
    [ObservableProperty]
    private ObservableCollection<AnimeItem> _ruletaPortadasQueVer = new();

    [ObservableProperty]
    private int _ruletaIndiceGanadorQueVer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueVerProgresoTexto))]
    private AnimeItem? _queVerElegido;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueVerEpisodioTexto))]
    private int _queVerEpisodioNumero;

    [ObservableProperty]
    private string _queVerMensaje = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OtroQueVerHoyCommand))]
    private bool _hayOtraOpcionQueVer;

    public string QueVerEpisodioTexto => string.Format(LocalizationService.T("Act_EpisodioFormato"), QueVerEpisodioNumero);
    public string QueVerProgresoTexto => QueVerElegido?.ProgresoEpisodiosTexto ?? string.Empty;

    [RelayCommand(CanExecute = nameof(SePuedeAyudarAverQueVer))]
    private async Task ElegirQueVerHoyAsync()
    {
        ReiniciarEstadoQueVer();
        var cts = NuevoCtsQueVer();
        FaseActualQueVer = FaseQueVer.Buscando;

        try
        {
            var conCarpeta = BibliotecaLocales
                .Where(a => !string.IsNullOrWhiteSpace(a.RutaCarpeta))
                .ToList();

            if (conCarpeta.Count == 0)
            {
                MostrarEstadoVacioQueVer(FaseQueVer.SinCarpeta, "Gal_QueVeoHoySinCarpetaMsj");
                return;
            }

            PrepararColasQueVer(conCarpeta);
            await EscanearMasCandidatosAsync(CandidatosObjetivoQueVer, cts.Token);

            if (_candidatosQueVer.Count == 0)
            {
                MostrarEstadoVacioQueVer(FaseQueVer.SinPendientes, "Gal_QueVeoHoySinEpisodiosMsj");
                return;
            }

            await ElegirYGirarAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // El usuario cerró el panel mientras se buscaba: CerrarQueVerHoy ya dejó todo limpio.
        }
        catch (Exception ex)
        {
            AppLogger.Error("GaleriaViewModel", "Error en Qué veo hoy", ex);
            MostrarEstadoVacioQueVer(FaseQueVer.Error, "Gal_QueVeoHoyErrorMsj");
        }
    }

    private bool PuedeElegirOtroQueVer() => FaseActualQueVer == FaseQueVer.Resultado && HayOtraOpcionQueVer;

    /// <summary>"Otro": vuelve a girar la ruleta sin repetir lo ya mostrado (mientras haya opciones).</summary>
    [RelayCommand(CanExecute = nameof(PuedeElegirOtroQueVer))]
    private async Task OtroQueVerHoyAsync()
    {
        var cts = NuevoCtsQueVer();
        try
        {
            if (!HayCandidatoSinMostrarQueVer())
            {
                // Ya se mostraron todos los candidatos escaneados: se busca en el resto de la biblioteca.
                FaseActualQueVer = FaseQueVer.Buscando;
                await EscanearMasCandidatosAsync(CandidatosObjetivoQueVer, cts.Token);
            }

            if (!await ElegirYGirarAsync(cts.Token))
            {
                FaseActualQueVer = FaseQueVer.Resultado;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("GaleriaViewModel", "Error al elegir otro en Qué veo hoy", ex);
            MostrarEstadoVacioQueVer(FaseQueVer.Error, "Gal_QueVeoHoyErrorMsj");
        }
    }

    /// <summary>La vista lo invoca al terminar la animación de la ruleta.</summary>
    [RelayCommand]
    private void TerminarGiroQueVer()
    {
        if (FaseActualQueVer == FaseQueVer.Girando)
        {
            FaseActualQueVer = FaseQueVer.Resultado;
        }
    }

    [RelayCommand(CanExecute = nameof(EsFaseResultadoQueVer))]
    private void VerElegidoQueVer()
    {
        var candidato = _elegidoQueVer;
        if (candidato == null) return;

        // Se arma el mensaje antes de cerrar: cerrar limpia el estado del panel.
        var mensaje = new NavegarMensaje_Reproductor(
            candidato.Siguiente.RutaCompleta,
            candidato.Anime.AniListId,
            candidato.Anime.Titulo,
            candidato.Siguiente.NumeroEpisodio,
            EpisodiosDisponibles: candidato.Todos,
            RutaPortada: candidato.Anime.PortadaVisible);

        CerrarQueVerHoy();
        WeakReferenceMessenger.Default.Send(mensaje);
    }

    [RelayCommand]
    private void CerrarQueVerHoy()
    {
        _ctsQueVer?.Cancel();
        _ctsQueVer = null;
        ReiniciarEstadoQueVer();
        FaseActualQueVer = FaseQueVer.Oculto;
    }

    // ── Selección ──

    public void Dispose()
    {
        _ctsQueVer?.Cancel();
        _ctsQueVer?.Dispose();
        _ctsQueVer = null;
        GC.SuppressFinalize(this);
    }

    private CancellationTokenSource NuevoCtsQueVer()
    {
        _ctsQueVer?.Cancel();
        _ctsQueVer = new CancellationTokenSource();
        return _ctsQueVer;
    }

    private void ReiniciarEstadoQueVer()
    {
        _candidatosQueVer.Clear();
        _yaMostradosQueVer.Clear();
        _colaEscaneoQueVer = new Queue<AnimeItem>();
        _colaFallbackQueVer = new Queue<AnimeItem>();
        _elegidoQueVer = null;
        QueVerElegido = null;
        QueVerEpisodioNumero = 0;
        QueVerMensaje = string.Empty;
        HayOtraOpcionQueVer = false;
        RuletaPortadasQueVer = new ObservableCollection<AnimeItem>();
    }

    private void MostrarEstadoVacioQueVer(FaseQueVer fase, string claveMensaje)
    {
        QueVerMensaje = LocalizationService.T(claveMensaje);
        FaseActualQueVer = fase;
    }

    /// <summary>
    /// Prioriza lo que el usuario está viendo (CURRENT). Los COMPLETED y DROPPED nunca se sugieren:
    /// un anime completado en AniList cuyos episodios se vieron fuera de la app no tiene registros
    /// locales y aparecería recomendado desde el episodio 1.
    /// </summary>
    private void PrepararColasQueVer(List<AnimeItem> conCarpeta)
    {
        _colaEscaneoQueVer = new Queue<AnimeItem>(Barajar(conCarpeta.Where(a => a.EstadoUsuario == "CURRENT")));
        _colaFallbackQueVer = new Queue<AnimeItem>(Barajar(conCarpeta.Where(a =>
            a.EstadoUsuario != "CURRENT" && a.EstadoUsuario != "COMPLETED" && a.EstadoUsuario != "DROPPED")));
    }

    private static List<AnimeItem> Barajar(IEnumerable<AnimeItem> animes)
        => animes.OrderBy(_ => Random.Shared.Next()).ToList();

    private bool HayCandidatoSinMostrarQueVer()
        => _candidatosQueVer.Any(c => !_yaMostradosQueVer.Contains(c.Anime.AniListId));

    /// <summary>
    /// Escanea carpetas (en lotes paralelos) hasta reunir <paramref name="objetivo"/> candidatos nuevos
    /// o agotar la cola. Solo pasa a los animes que no están "en curso" cuando ya no queda nada
    /// sin mostrar entre los prioritarios.
    /// </summary>
    private async Task EscanearMasCandidatosAsync(int objetivo, CancellationToken ct)
    {
        int encontrados = 0;

        while (encontrados < objetivo)
        {
            if (_colaEscaneoQueVer.Count == 0)
            {
                if (_colaFallbackQueVer.Count == 0 || HayCandidatoSinMostrarQueVer()) break;
                _colaEscaneoQueVer = _colaFallbackQueVer;
                _colaFallbackQueVer = new Queue<AnimeItem>();
            }

            var lote = new List<AnimeItem>(EscaneosParalelosQueVer);
            while (lote.Count < EscaneosParalelosQueVer && _colaEscaneoQueVer.Count > 0)
            {
                lote.Add(_colaEscaneoQueVer.Dequeue());
            }

            var resultados = await Task.WhenAll(lote.Select(a => EscanearCandidatoAsync(a, ct)));

            // Si el panel se cerró mientras se escaneaba, no se contamina el estado de una búsqueda nueva.
            ct.ThrowIfCancellationRequested();

            foreach (var candidato in resultados.OfType<CandidatoQueVer>())
            {
                _candidatosQueVer.Add(candidato);
                encontrados++;
            }
        }
    }

    private async Task<CandidatoQueVer?> EscanearCandidatoAsync(AnimeItem anime, CancellationToken ct)
    {
        return await Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var episodios = (await _fileScannerService.EscanearEpisodiosAsync(anime.RutaCarpeta!))
                    .Where(e => !string.IsNullOrWhiteSpace(e.RutaCompleta))
                    .Where(e => e.NumeroEpisodio > 0) // FUN-004: sin número no es candidato
                    .OrderBy(e => e.NumeroEpisodio)
                    .ToList();

                if (episodios.Count == 0) return null;

                var registros = await _databaseService.ObtenerRegistrosPorAnimeAsync(anime.AniListId);
                var vistos = new HashSet<int>(registros.Where(r => r.VistoLocal).Select(r => r.NumeroEpisodio));

                var siguiente = episodios.FirstOrDefault(ep => !vistos.Contains(ep.NumeroEpisodio));
                return siguiente == null ? null : new CandidatoQueVer(anime, siguiente, episodios);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("GaleriaViewModel", $"Qué veo hoy: error escaneando {anime.Titulo}: {ex.Message}");
                return null;
            }
        }, ct);
    }

    /// <summary>Elige un candidato (sin repetir lo mostrado), prepara la ruleta y la pone a girar.</summary>
    private async Task<bool> ElegirYGirarAsync(CancellationToken ct)
    {
        var candidato = TomarCandidatoQueVer();
        if (candidato == null) return false;

        await PrepararTiraAsync(candidato.Anime, ct);
        ct.ThrowIfCancellationRequested();

        _elegidoQueVer = candidato;
        _yaMostradosQueVer.Add(candidato.Anime.AniListId);
        QueVerElegido = candidato.Anime;
        QueVerEpisodioNumero = candidato.Siguiente.NumeroEpisodio;
        HayOtraOpcionQueVer = _candidatosQueVer.Any(c => c.Anime.AniListId != candidato.Anime.AniListId)
                              || _colaEscaneoQueVer.Count > 0
                              || _colaFallbackQueVer.Count > 0;

        FaseActualQueVer = FaseQueVer.Girando;
        return true;
    }

    private CandidatoQueVer? TomarCandidatoQueVer()
    {
        var actualId = _elegidoQueVer?.Anime.AniListId;
        var disponibles = _candidatosQueVer.Where(c => !_yaMostradosQueVer.Contains(c.Anime.AniListId)).ToList();

        if (disponibles.Count == 0)
        {
            // Se mostró todo lo escaneado: se reinicia el ciclo, pero nunca se repite el actual.
            _yaMostradosQueVer.Clear();
            disponibles = _candidatosQueVer.Where(c => c.Anime.AniListId != actualId).ToList();
            if (disponibles.Count == 0)
            {
                // Único candidato (primer sorteo): se elige ese.
                disponibles = _candidatosQueVer.ToList();
            }
        }

        return disponibles.Count == 0 ? null : disponibles[Random.Shared.Next(disponibles.Count)];
    }

    // ── Ruleta ──

    /// <summary>
    /// Arma la tira de portadas (el ganador en un índice fijo cerca del final, para que la
    /// animación recorra bastante camino) y precarga las portadas que aún no están en memoria.
    /// </summary>
    private async Task PrepararTiraAsync(AnimeItem ganador, CancellationToken ct)
    {
        var relleno = Barajar(BibliotecaLocales.Where(a =>
                a.AniListId != ganador.AniListId && !string.IsNullOrWhiteSpace(a.UrlPortada)))
            .Take(PortadasDistintasTiraQueVer)
            .ToList();

        var tira = new List<AnimeItem>(LargoTiraQueVer);
        int siguienteRelleno = 0;
        for (int i = 0; i < LargoTiraQueVer; i++)
        {
            if (i == IndiceGanadorTiraQueVer || relleno.Count == 0)
            {
                tira.Add(ganador);
            }
            else
            {
                tira.Add(relleno[siguienteRelleno++ % relleno.Count]);
            }
        }

        await PrecargarPortadasAsync(tira.Distinct().ToList(), ct);

        RuletaIndiceGanadorQueVer = IndiceGanadorTiraQueVer;
        RuletaPortadasQueVer = new ObservableCollection<AnimeItem>(tira);
    }

    /// <summary>
    /// Sin esto, las portadas fuera de pantalla de la galería (virtualizada) saldrían vacías al pasar
    /// por la ruleta. Se espera como máximo un par de segundos: mejor una tarjeta sin imagen que
    /// una ruleta que tarda en empezar.
    /// </summary>
    private async Task PrecargarPortadasAsync(List<AnimeItem> animes, CancellationToken ct)
    {
        var faltantes = animes
            .Where(a => !string.IsNullOrWhiteSpace(a.UrlPortada) && _imageCacheService.ObtenerPortadaEnMemoria(a.AniListId) == null)
            .ToList();
        if (faltantes.Count == 0) return;

        var cargas = Task.WhenAll(faltantes.Select(CargarPortadaAsync));

        var terminada = await Task.WhenAny(cargas, Task.Delay(TimeSpan.FromSeconds(2.5), ct));
        ct.ThrowIfCancellationRequested();

        if (terminada == cargas)
        {
            NotificarPortadas(cargas.Result.OfType<AnimeItem>().ToList());
        }
        else
        {
            // La ruleta arranca sin esperar, pero las portadas rezagadas se enchufan cuando lleguen.
            _ = NotificarPortadasTardiasAsync(cargas);
        }
    }

    private async Task<AnimeItem?> CargarPortadaAsync(AnimeItem anime)
    {
        try
        {
            var img = await _imageCacheService.ObtenerPortadaAsync(anime.AniListId, anime.UrlPortada);
            return img != null ? anime : null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("GaleriaViewModel", $"Qué veo hoy: portada no disponible para {anime.Titulo}: {ex.Message}");
            return null;
        }
    }

    private static async Task NotificarPortadasTardiasAsync(Task<AnimeItem?[]> cargas)
    {
        try
        {
            var animes = (await cargas.ConfigureAwait(false)).OfType<AnimeItem>().ToList();
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                await dispatcher.InvokeAsync(() => NotificarPortadas(animes));
            }
            else
            {
                NotificarPortadas(animes);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("GaleriaViewModel", $"Qué veo hoy: error notificando portadas tardías: {ex.Message}");
        }
    }

    private static void NotificarPortadas(List<AnimeItem> animes)
    {
        foreach (var anime in animes)
        {
            anime.NotificarPortadaActualizada();
        }
    }
}
