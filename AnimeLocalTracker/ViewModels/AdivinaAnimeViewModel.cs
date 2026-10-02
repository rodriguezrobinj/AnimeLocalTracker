using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AnimeLocalTracker.Models;
using AnimeLocalTracker.Services;
using AnimeLocalTracker.Services.Minijuegos;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeLocalTracker.ViewModels;

/// <summary>
/// "Adivina el anime": pistas progresivas y portada que se aclara, con animes de tu propia biblioteca (funciona
/// sin conexión). La lógica pura vive en <see cref="AdivinaAnimeJuego"/>.
/// </summary>
public sealed partial class AdivinaAnimeViewModel : MinijuegoViewModelBase
{
    private readonly IDatabaseService _databaseService;

    private Queue<AnimeItem> _respuestasPendientes = new();
    private RondaAdivinaAnime? _ronda;
    private int _pistasReveladas;
    private bool _avanzando;

    /// <summary>Solo para pruebas: posición de la opción correcta de la ronda actual (-1 si no hay ronda).</summary>
    internal int IndiceCorrecto => IndiceCorrectoRonda;

    [ObservableProperty] private string _portadaRonda = string.Empty;
    [ObservableProperty] private double _radioDesenfoque;

    public override string Icono => "HelpCircleOutline";
    protected override string ClaveTitulo => "Mini_AdivinaTitulo";
    protected override string ClaveDescripcion => "Mini_AdivinaDesc";

    protected override int IndiceCorrectoRonda => _ronda?.IndiceCorrecto ?? -1;
    protected override string RespuestaRonda => _ronda?.Respuesta.Titulo ?? string.Empty;
    protected override int PuntosSiAcierta => AdivinaAnimeJuego.Puntos(_pistasReveladas);

    /// <summary>Al responder la portada queda nítida.</summary>
    protected override void AlResponder() => RadioDesenfoque = 0;

    public override bool PuedePedirPista => EsJugando && !HaRespondido && _ronda != null && _pistasReveladas < _ronda.Pistas.Count;
    protected override bool HayMasRondas => _respuestasPendientes.Count > 0;
    public override string ReglasTexto => string.Format(LocalizationService.T("Mini_AdivinaReglasFormato"), AdivinaAnimeJuego.RondasPorPartida, AdivinaAnimeJuego.OpcionesPorRonda);

    public override string AtajosTexto => LocalizationService.T("Mini_Atajos");

    protected override string JuegoId => JuegosMinijuego.AdivinaAnime;

    public AdivinaAnimeViewModel(IDatabaseService databaseService, IMinijuegosRecordsService? records = null) : base(databaseService, records)
    {
        _databaseService = databaseService;
    }

    [RelayCommand]
    private async Task IniciarPartidaAsync()
    {
        if (!PuedeJugar || _avanzando) return;

        var respuestas = AdivinaAnimeJuego.ElegirRespuestas(Biblioteca, AdivinaAnimeJuego.RondasPorPartida, Rng);
        if (respuestas.Count == 0) return;

        _respuestasPendientes = new Queue<AnimeItem>(respuestas);
        ReiniciarPartida();
        TotalRondas = respuestas.Count;
        Estado = EstadoMinijuego.Jugando;

        await AvanzarRondaAsync();
    }

    [RelayCommand]
    private void PedirPista()
    {
        if (!PuedePedirPista || _ronda == null) return;

        _pistasReveladas++;
        RefrescarRonda();
    }

    protected override async Task AvanzarRondaAsync()
    {
        if (_avanzando) return;
        _avanzando = true;
        try
        {
            while (_respuestasPendientes.Count > 0)
            {
                var candidata = _respuestasPendientes.Dequeue();

                DatosExtraAnime? extra = null;
                try
                {
                    extra = await _databaseService.ObtenerDatosExtraAsync(candidata.AniListId);
                }
                catch (Exception ex)
                {
                    // Los datos de AniList son un extra: sin ellos la ronda sigue con las demás pistas.
                    AppLogger.Debug("AdivinaAnimeViewModel", $"Sin datos extra para {candidata.AniListId}: {ex.Message}");
                }

                var ronda = AdivinaAnimeJuego.CrearRonda(candidata, Biblioteca, extra, Rng);
                if (ronda == null)
                {
                    TotalRondas--;
                    continue;
                }

                MostrarRonda(ronda);
                return;
            }

            await TerminarPartidaAsync();
        }
        finally
        {
            _avanzando = false;
        }
    }

    private void MostrarRonda(RondaAdivinaAnime ronda)
    {
        _ronda = ronda;
        _pistasReveladas = 1;

        Opciones = CrearOpciones(ronda.Opciones);
        PortadaRonda = ronda.Respuesta.PortadaVisible;
        RondaNumero++;
        HaRespondido = false;
        UltimoFueAcierto = false;
        ResultadoTexto = string.Empty;

        RefrescarRonda();
        OnPropertyChanged(nameof(TextoSiguiente));
    }

    /// <summary>Reconstruye lo que depende de cuántas pistas hay visibles (lista, desenfoque, puntos posibles).</summary>
    private void RefrescarRonda()
    {
        if (_ronda == null) return;

        Pistas = new ObservableCollection<PistaItem>(_ronda.Pistas.Take(_pistasReveladas).Select(ConvertirPista));
        if (!HaRespondido)
        {
            RadioDesenfoque = AdivinaAnimeJuego.RadioDesenfoque(_pistasReveladas, _ronda.Pistas.Count);
            PuntosPosiblesTexto = string.Format(LocalizationService.T("Mini_PuntosPosiblesFormato"), AdivinaAnimeJuego.Puntos(_pistasReveladas));
        }
        OnPropertyChanged(nameof(PuedePedirPista));
    }

    private static PistaItem ConvertirPista(PistaAnime pista)
    {
        return pista.Tipo switch
        {
            TipoPista.Generos => new PistaItem("TagMultipleOutline", LocalizationService.T("Mini_Pista_Generos"), TextoGeneros(pista.Valor)),
            TipoPista.Estreno => new PistaItem("CalendarOutline", LocalizationService.T("Mini_Pista_Estreno"), TextoEstreno(pista)),
            TipoPista.Episodios => new PistaItem("PlayBoxMultipleOutline", LocalizationService.T("Mini_Pista_Episodios"), TextoEpisodios(pista.Valor)),
            TipoPista.Produccion => new PistaItem("OfficeBuildingOutline", LocalizationService.T("Mini_Pista_Produccion"), TextoProduccion(pista)),
            _ => new PistaItem("TextBoxOutline", LocalizationService.T("Mini_Pista_Sinopsis"), pista.Valor)
        };
    }

    /// <summary>Géneros de AniList (siempre en inglés) traducidos solo para mostrarlos.</summary>
    internal static string TextoGeneros(string generos) =>
        string.Join(", ", generos.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(LocalizationService.TraducirGenero));

    internal static string TextoEstreno(PistaAnime pista)
    {
        string? claveTemporada = pista.Extra?.ToUpperInvariant() switch
        {
            "WINTER" => "Temporada_Invierno",
            "SPRING" => "Temporada_Primavera",
            "SUMMER" => "Temporada_Verano",
            "FALL" => "Temporada_Otonio",
            _ => null
        };
        return claveTemporada == null
            ? pista.Valor
            : string.Format(LocalizationService.T("Mini_EstrenoFormato"), LocalizationService.T(claveTemporada), pista.Valor);
    }

    internal static string TextoEpisodios(string cantidad) =>
        cantidad == "1"
            ? LocalizationService.T("Mini_EpisodioUno")
            : string.Format(LocalizationService.T("Mini_EpisodiosFormato"), cantidad);

    internal static string TextoProduccion(PistaAnime pista)
    {
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(pista.Valor))
            partes.Add(string.Format(LocalizationService.T("Mini_EstudioFormato"), pista.Valor));

        string formato = DetalleViewModel.TraducirValor("Fmt_", pista.Extra ?? string.Empty);
        if (formato.Length > 0) partes.Add(formato);

        string fuente = DetalleViewModel.TraducirValor("Src_", pista.Extra2 ?? string.Empty);
        if (fuente.Length > 0) partes.Add(string.Format(LocalizationService.T("Mini_FuenteFormato"), fuente));

        return string.Join(" · ", partes);
    }

    protected override void RefrescarTextos()
    {
        RefrescarRonda();
        ActualizarResultadoTexto();
        base.RefrescarTextos();
    }
}
