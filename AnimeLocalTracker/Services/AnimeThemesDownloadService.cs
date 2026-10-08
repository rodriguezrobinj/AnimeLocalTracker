using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AnimeLocalTracker.Messages;
using AnimeLocalTracker.Models;
using CommunityToolkit.Mvvm.Messaging;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Descarga el .ogg (audio solo, sin video) de un tema de AnimeThemes.moe y lo convierte a .mp3
/// con el FFmpeg embebido de la app (mismo binario que usa VideoIntegrityService/PythonEpisodeEnricher;
/// trae el codificador libmp3lame incluido). El .ogg nunca queda en disco tras el intento: solo se
/// guarda el .mp3 final. Además puede dejar el mp3 en una caché temporal de "vistas previas" para escucharlo
/// antes de guardarlo (guardar = mover el archivo, sin volver a bajarlo).
/// </summary>
public class AnimeThemesDownloadService : IAnimeThemesDownloadService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _carpetaMusica;
    private readonly string _carpetaVistasPrevias;
    private readonly string? _carpetaReferencias;
    private readonly string? _carpetaPortadas;
    private readonly IDatabaseService? _baseDatos;

    /// <summary>Cada cuánto, como mucho, se avisa del avance a la pestaña Descargas (una canción manda cientos de trozos).</summary>
    private static readonly TimeSpan IntervaloAvisos = TimeSpan.FromMilliseconds(250);

    /// <summary>La parte de bajar el .ogg pesa lo mismo que ~90 % del trabajo; el resto es la conversión.</summary>
    private const double PesoDeLaDescarga = 0.9;

    /// <summary>Un corte de red a mitad de canción se reintenta una vez antes de darla por fallida.</summary>
    private const int MaximoIntentosDescarga = 2;

    /// <summary>Espera antes de reintentar una descarga cortada. Ajustable solo en pruebas.</summary>
    internal TimeSpan EsperaEntreIntentos { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Descargas en marcha por ruta final. La descarga sigue aunque el usuario salga de la ficha: al volver, la fila nueva se
    /// "engancha" a la que ya está en curso (ve su avance) en vez de lanzar otra que chocaría con el mismo archivo temporal.
    /// </summary>
    private readonly Dictionary<string, TrabajoEnCurso> _enCurso = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="carpetaMusica">Solo para pruebas: carpeta de descargas (por defecto la de datos del usuario).</param>
    /// <param name="carpetaVistasPrevias">Solo para pruebas: carpeta de la caché temporal de vistas previas.</param>
    /// <param name="baseDatos">Para apuntar cada descarga (completada o fallida) en el historial de la pestaña Descargas y
    /// mostrarla con el título del anime de la biblioteca. Sin ella las descargas siguen funcionando igual.</param>
    /// <param name="carpetaPortadas">Solo para pruebas: de dónde sale la portada que se incrusta en el mp3 ({id}.jpg). Misma
    /// regla que <paramref name="carpetaReferencias"/>.</param>
    /// <param name="carpetaReferencias">Solo para pruebas: caché de .ogg del salto de openings/endings. Si las pruebas pasan su
    /// propia carpeta de música y no esta, no se mira ninguna (nunca se lee la carpeta de datos real desde una prueba).</param>
    public AnimeThemesDownloadService(IHttpClientFactory httpClientFactory, string? carpetaMusica = null, string? carpetaVistasPrevias = null,
        string? carpetaReferencias = null, string? carpetaPortadas = null, IDatabaseService? baseDatos = null)
    {
        _baseDatos = baseDatos;
        _httpClientFactory = httpClientFactory;
        _carpetaMusica = carpetaMusica ?? AppDataPaths.MusicDir;
        _carpetaVistasPrevias = carpetaVistasPrevias ?? AppDataPaths.MusicPreviewsDir;
        _carpetaReferencias = carpetaReferencias ?? (carpetaMusica == null ? AppDataPaths.SkipReferencesDir : null);
        _carpetaPortadas = carpetaPortadas ?? (carpetaMusica == null ? AppDataPaths.CoversDir : null);
    }

    /// <summary>
    /// El .ogg original de este tema si ya lo bajó el salto de openings/endings (<see cref="ReferenciasAudioService"/>, misma
    /// convención de nombres). Se reconoce por tipo+slug+versión: el rango del nombre puede estar desfasado.
    /// </summary>
    internal string? BuscarOggEnReferencias(int aniListId, AnimeThemeInfo tema)
    {
        if (_carpetaReferencias == null) return null;
        string carpeta = Path.Combine(_carpetaReferencias, aniListId.ToString());
        if (!Directory.Exists(carpeta)) return null;

        try
        {
            string clave = tema.ClaveEstable();
            foreach (var archivo in Directory.GetFiles(carpeta, "*.ogg"))
            {
                if (TryParseNombreArchivo(archivo, out var local) && local?.ClaveEstable() == clave && new FileInfo(archivo).Length > 0)
                    return archivo;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo mirar la caché de referencias de {aniListId}: {ex.Message}");
        }
        return null;
    }

    private string CarpetaAnime(int aniListId) => Path.Combine(_carpetaMusica, aniListId.ToString());
    private string CarpetaVistaPrevia(int aniListId) => Path.Combine(_carpetaVistasPrevias, aniListId.ToString());

    public string CarpetaDescargas(int aniListId) => CarpetaAnime(aniListId);

    /// <summary>Dónde se guarda un tema nuevo: nombre legible ("OP1 - We Are!.mp3").</summary>
    private string RutaLegible(int aniListId, AnimeThemeInfo tema) => Path.Combine(CarpetaAnime(aniListId), tema.NombreArchivoLegible());

    /// <summary>El archivo que ya hay para este tema, con el nombre de ahora o con uno anterior (técnico, u otro título). Null si no hay.</summary>
    internal string? BuscarArchivoLocal(int aniListId, AnimeThemeInfo tema)
    {
        string legible = RutaLegible(aniListId, tema);
        if (File.Exists(legible)) return legible;

        string carpeta = CarpetaAnime(aniListId);
        string tecnico = Path.Combine(carpeta, tema.NombreArchivoLocal());
        if (File.Exists(tecnico)) return tecnico;
        if (!Directory.Exists(carpeta)) return null;

        try
        {
            string clave = tema.ClaveEstable();
            foreach (var archivo in Directory.GetFiles(carpeta, "*.mp3"))
            {
                if (TryParseNombreCualquiera(archivo, out var local) && local!.ClaveEstable() == clave) return archivo;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo buscar '{tema.Slug}' en la carpeta de {aniListId}: {ex.Message}");
        }
        return null;
    }

    public string ObtenerRutaLocalEsperada(int aniListId, AnimeThemeInfo tema) =>
        BuscarArchivoLocal(aniListId, tema) ?? RutaLegible(aniListId, tema);

    public bool EstaDescargado(int aniListId, AnimeThemeInfo tema) => BuscarArchivoLocal(aniListId, tema) != null;

    public bool EstaDescargando(int aniListId, AnimeThemeInfo tema)
    {
        lock (_enCurso) return _enCurso.ContainsKey(RutaLegible(aniListId, tema));
    }

    public int ReconciliarDescargasLocales(int aniListId, IReadOnlyList<AnimeThemeInfo> catalogo)
    {
        string carpeta = CarpetaAnime(aniListId);
        if (catalogo == null || catalogo.Count == 0 || !Directory.Exists(carpeta)) return 0;

        var nombresActuales = new HashSet<string>(catalogo.Select(t => t.NombreArchivoLegible()), StringComparer.OrdinalIgnoreCase);
        // Si una misma clave apareciera dos veces en el catálogo no se adivina a cuál corresponde el archivo.
        var porClave = catalogo.GroupBy(t => t.ClaveEstable())
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());

        int renombrados = 0;
        try
        {
            foreach (var archivo in Directory.GetFiles(carpeta, "*.mp3"))
            {
                if (nombresActuales.Contains(Path.GetFileName(archivo))) continue;
                if (!TryParseNombreCualquiera(archivo, out var local) || local == null) continue;
                if (!porClave.TryGetValue(local.ClaveEstable(), out var tema)) continue;

                string destino = RutaLegible(aniListId, tema);
                if (File.Exists(destino)) continue; // ya hay uno con el nombre de ahora: no se pisa

                try
                {
                    File.Move(archivo, destino);
                    renombrados++;
                    AppLogger.Info("AnimeThemesDownloadService", $"'{Path.GetFileName(archivo)}' de AniListId {aniListId} pasa a '{Path.GetFileName(destino)}'.");
                }
                catch (Exception ex)
                {
                    // Suele ser que otro proceso lo tiene abierto (p. ej. el análisis de saltos): se intentará la próxima vez.
                    AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo renombrar '{archivo}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"Error reconciliando las descargas de AniListId {aniListId}: {ex.Message}");
        }
        return renombrados;
    }

    public Task<string?> DescargarYConvertirAsync(int aniListId, AnimeThemeInfo tema, CancellationToken ct = default) =>
        DescargarYConvertirAsync(aniListId, tema, null, ct);

    public Task<string?> DescargarYConvertirAsync(int aniListId, AnimeThemeInfo tema, IProgress<double>? progreso, CancellationToken ct) =>
        DescargarConvertirAsync(aniListId, tema, RutaLegible(aniListId, tema), progreso, visibleEnDescargas: true, ct);

    public string? ObtenerRutaVistaPrevia(int aniListId, AnimeThemeInfo tema)
    {
        string ruta = RutaVistaPrevia(aniListId, tema);
        return File.Exists(ruta) ? ruta : null;
    }

    public async Task<string?> PrepararVistaPreviaAsync(int aniListId, AnimeThemeInfo tema, IProgress<double>? progreso, CancellationToken ct)
    {
        string? existente = ObtenerRutaVistaPrevia(aniListId, tema);
        if (existente != null)
        {
            progreso?.Report(1.0);
            return existente;
        }

        return await DescargarConvertirAsync(aniListId, tema, RutaVistaPrevia(aniListId, tema), progreso, visibleEnDescargas: false, ct);
    }

    public bool GuardarVistaPrevia(int aniListId, AnimeThemeInfo tema)
    {
        string origen = RutaVistaPrevia(aniListId, tema);
        string destino = RutaLegible(aniListId, tema);

        try
        {
            if (!File.Exists(origen)) return false;

            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
            if (BuscarArchivoLocal(aniListId, tema) != null)
            {
                // Ya estaba guardado (p. ej. desde otro sitio): la copia temporal sobra.
                File.Delete(origen);
                return true;
            }

            File.Move(origen, destino);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo guardar la vista previa de '{tema.Slug}': {ex.Message}");
            return false;
        }
    }

    public void LimpiarVistasPrevias()
    {
        try
        {
            if (Directory.Exists(_carpetaVistasPrevias)) Directory.Delete(_carpetaVistasPrevias, recursive: true);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo vaciar la caché de vistas previas: {ex.Message}");
        }
    }

    private string RutaVistaPrevia(int aniListId, AnimeThemeInfo tema) =>
        Path.Combine(CarpetaVistaPrevia(aniListId), tema.NombreArchivoLocal());

    /// <summary>
    /// Núcleo común: baja el .ogg del tema, lo convierte y deja el mp3 en <paramref name="rutaFinal"/>. Si ya hay un trabajo en
    /// marcha hacia esa misma ruta, se une a él (recibe su avance y su resultado) en vez de empezar otro.
    /// </summary>
    /// <param name="visibleEnDescargas">true para "Descargar mp3"/"Descargar todos": sale en la pestaña Descargas y en su
    /// historial. Las vistas previas (escuchar antes de guardar) no: son temporales.</param>
    private async Task<string?> DescargarConvertirAsync(int aniListId, AnimeThemeInfo tema, string rutaFinal, IProgress<double>? progreso,
        bool visibleEnDescargas, CancellationToken ct)
    {
        TrabajoEnCurso? existente;
        TrabajoEnCurso? nuevo = null;
        lock (_enCurso)
        {
            if (!_enCurso.TryGetValue(rutaFinal, out existente))
            {
                nuevo = new TrabajoEnCurso();
                if (visibleEnDescargas) nuevo.Visible = new DescargaVisible(aniListId, tema, Path.GetDirectoryName(rutaFinal)!);
                _enCurso[rutaFinal] = nuevo;
            }
        }

        if (existente != null)
        {
            existente.AgregarObservador(progreso);
            try { return await existente.Resultado.WaitAsync(ct); }
            catch (OperationCanceledException) { return null; }
        }

        var trabajo = nuevo!;
        trabajo.AgregarObservador(progreso);
        string? resultado = null;
        var visible = trabajo.Visible;
        using var enlazado = visible != null ? CancellationTokenSource.CreateLinkedTokenSource(ct, visible.Cancelacion.Token) : null;
        try
        {
            if (visible != null)
            {
                visible.AnimeTitulo = await ResolverTituloAnimeAsync(aniListId, tema);
                visible.Avisar(forzar: true);
            }
            resultado = await DescargarConvertirSinCompartirAsync(aniListId, tema, rutaFinal, trabajo, enlazado?.Token ?? ct);
            return resultado;
        }
        finally
        {
            lock (_enCurso) _enCurso.Remove(rutaFinal);
            trabajo.Terminar(resultado);
            if (visible != null) await TerminarDescargaVisibleAsync(visible, resultado, cancelada: visible.CanceladaPorUsuario || ct.IsCancellationRequested);
        }
    }

    /// <summary>Título del anime como aparece en la biblioteca (igual que las descargas de episodios); si no, el de AnimeThemes.</summary>
    private async Task<string> ResolverTituloAnimeAsync(int aniListId, AnimeThemeInfo tema)
    {
        try
        {
            if (_baseDatos != null && await _baseDatos.ObtenerAnimePorIdAsync(aniListId) is { Titulo: { Length: > 0 } titulo }) return titulo;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo leer el título de {aniListId}: {ex.Message}");
        }
        return string.IsNullOrWhiteSpace(tema.NombreAnime) ? $"AniList {aniListId}" : tema.NombreAnime;
    }

    /// <summary>Aviso final a la pestaña Descargas y fila en el historial (salvo si el usuario la canceló).</summary>
    private async Task TerminarDescargaVisibleAsync(DescargaVisible visible, string? ruta, bool cancelada)
    {
        visible.Terminar(ruta, cancelada);
        if (cancelada || _baseDatos == null) return;

        try
        {
            long tamano = 0;
            try { if (ruta != null && File.Exists(ruta)) tamano = new FileInfo(ruta).Length; } catch { /* solo informativo */ }

            await _baseDatos.GuardarDescargaHistorialAsync(new DescargaHistorial
            {
                AniListId = visible.AniListId,
                AnimeTitulo = visible.AnimeTitulo,
                NumeroEpisodio = 0,
                CarpetaDestino = visible.Carpeta,
                RutaArchivo = ruta ?? string.Empty,
                TamanoBytes = tamano,
                FechaUtc = DateTime.UtcNow,
                Completada = ruta != null,
                Error = ruta != null ? null : visible.Error ?? LocalizationService.T("Desc_MusicaErrorDescarga"),
                Tipo = DescargaHistorial.TipoMusica,
                TemaClave = visible.TemaClave,
                TemaTitulo = visible.TemaTitulo
            });
            WeakReferenceMessenger.Default.Send(new DescargaHistorialActualizadoMensaje());
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo apuntar '{visible.TemaTitulo}' en el historial de descargas: {ex.Message}");
        }
    }

    public IReadOnlyList<DescargaItem> ObtenerDescargasMusicaActivas()
    {
        lock (_enCurso)
        {
            return _enCurso.Values.Select(t => t.Visible).Where(v => v != null).Select(v => v!.ComoDescargaItem()).ToList();
        }
    }

    public bool CancelarDescargaMusica(int aniListId, string temaClave)
    {
        DescargaVisible? visible;
        lock (_enCurso)
        {
            visible = _enCurso.Values.Select(t => t.Visible)
                .FirstOrDefault(v => v != null && v.AniListId == aniListId && v.TemaClave == temaClave);
        }
        if (visible == null) return false;

        visible.Cancelar();
        lock (_canceladasPorUsuario) _canceladasPorUsuario.Add(visible.Id);
        return true;
    }

    public void CancelarTodasMusica()
    {
        List<DescargaVisible> visibles;
        lock (_enCurso) visibles = _enCurso.Values.Select(t => t.Visible).OfType<DescargaVisible>().ToList();
        foreach (var v in visibles)
        {
            v.Cancelar();
            lock (_canceladasPorUsuario) _canceladasPorUsuario.Add(v.Id);
        }
    }

    /// <summary>Descargas que el usuario canceló desde la pestaña Descargas (anime|clave): la ficha no debe decir "no se pudo".</summary>
    private readonly HashSet<string> _canceladasPorUsuario = new(StringComparer.Ordinal);

    public bool FueCanceladaPorUsuario(int aniListId, AnimeThemeInfo tema)
    {
        lock (_canceladasPorUsuario) return _canceladasPorUsuario.Remove(DescargaVisible.IdDe(aniListId, tema.ClaveEstable()));
    }

    private async Task<string?> DescargarConvertirSinCompartirAsync(int aniListId, AnimeThemeInfo tema, string rutaFinal, TrabajoEnCurso trabajo, CancellationToken ct)
    {
        string carpeta = Path.GetDirectoryName(rutaFinal)!;
        string rutaTemporalOgg = Path.Combine(carpeta, Path.GetFileNameWithoutExtension(rutaFinal) + ".ogg.tmp");

        try
        {
            Directory.CreateDirectory(carpeta);
            trabajo.Informar(0);

            // Si el salto de openings ya bajó este tema, solo falta convertirlo: nada de red.
            string? oggLocal = BuscarOggEnReferencias(aniListId, tema);
            if (oggLocal != null)
            {
                if (trabajo.Visible != null) trabajo.Visible.Convirtiendo = true;
                trabajo.Informar(PesoDeLaDescarga);
                AppLogger.Debug("AnimeThemesDownloadService", $"'{tema.Slug}' v{tema.Version} de {aniListId}: se convierte el .ogg de la caché de referencias, sin red.");
                if (await ConvertirAMp3Async(oggLocal, rutaFinal, tema, aniListId, ct))
                {
                    trabajo.Informar(1.0);
                    return rutaFinal;
                }
                AppLogger.Debug("AnimeThemesDownloadService", $"El .ogg de la caché de referencias de '{tema.Slug}' no se pudo convertir; se baja de nuevo.");
                if (trabajo.Visible != null) trabajo.Visible.Convirtiendo = false;
            }

            for (int intento = 1; ; intento++)
            {
                try
                {
                    await BajarOggAsync(tema, rutaTemporalOgg, trabajo, ct);
                    break;
                }
                catch (Exception ex) when (intento < MaximoIntentosDescarga && EsFalloDeRedPasajero(ex) && !ct.IsCancellationRequested)
                {
                    AppLogger.Debug("AnimeThemesDownloadService", $"Descarga de '{tema.Slug}' interrumpida ({ex.Message}); reintentando.");
                    await Task.Delay(EsperaEntreIntentos, ct);
                }
            }

            if (trabajo.Visible != null) trabajo.Visible.Convirtiendo = true;
            trabajo.Informar(PesoDeLaDescarga);
            bool convertido = await ConvertirAMp3Async(rutaTemporalOgg, rutaFinal, tema, aniListId, ct);
            if (convertido) trabajo.Informar(1.0);
            else if (trabajo.Visible != null) trabajo.Visible.Error = LocalizationService.T("Desc_MusicaErrorConvertir");
            return convertido ? rutaFinal : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"Error descargando/convirtiendo '{tema.Slug}': {ex.Message}");
            if (trabajo.Visible != null) trabajo.Visible.Error = ex.Message;
            return null;
        }
        finally
        {
            try { if (File.Exists(rutaTemporalOgg)) File.Delete(rutaTemporalOgg); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Baja el .ogg completo. Si el servidor corta antes de mandar todo lo anunciado, lanza <see cref="IOException"/>: una
    /// canción recortada no debe darse por buena (convertida sin error, quedaría como "descargada" pero cortada a la mitad).
    /// </summary>
    private async Task BajarOggAsync(AnimeThemeInfo tema, string rutaTemporalOgg, TrabajoEnCurso trabajo, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("Downloader");
        using var respuesta = await http.GetAsync(tema.AudioUrlOgg, HttpCompletionOption.ResponseHeadersRead, ct);
        respuesta.EnsureSuccessStatusCode();
        long? total = respuesta.Content.Headers.ContentLength;

        await using var origen = await respuesta.Content.ReadAsStreamAsync(ct);
        await using var destino = File.Create(rutaTemporalOgg);

        var buffer = new byte[81920];
        long leidos = 0;
        int n;
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        while ((n = await origen.ReadAsync(buffer, ct)) > 0)
        {
            await destino.WriteAsync(buffer.AsMemory(0, n), ct);
            leidos += n;
            double bps = reloj.Elapsed.TotalSeconds > 0.2 ? leidos / reloj.Elapsed.TotalSeconds : 0;
            if (total is > 0) trabajo.Informar(Math.Min(PesoDeLaDescarga, leidos / (double)total.Value * PesoDeLaDescarga), bps);
        }

        if (total is > 0 && leidos != total.Value)
            throw new IOException($"Descarga incompleta: llegaron {leidos} de {total.Value} bytes.");
        if (leidos == 0)
            throw new IOException("El servidor no mandó ningún dato.");
    }

    /// <summary>Cortes de red y errores del servidor (5xx) se reintentan; un 404/403 no va a cambiar al reintentar.</summary>
    internal static bool EsFalloDeRedPasajero(Exception ex) => ex switch
    {
        IOException => true,
        HttpRequestException h => h.StatusCode == null || (int)h.StatusCode >= 500,
        _ => false
    };

    /// <summary>
    /// Convierte a mp3 con sus etiquetas (título, artista, anime como álbum y la portada), para que se vea bien también fuera
    /// de la app (móvil, reproductor de Windows). Si la portada da problemas se convierte sin ella: nunca se pierde la canción
    /// por la imagen.
    /// </summary>
    private async Task<bool> ConvertirAMp3Async(string rutaOgg, string rutaMp3Destino, AnimeThemeInfo tema, int aniListId, CancellationToken ct)
    {
        string? portada = RutaPortada(aniListId);
        if (portada != null)
        {
            if (await EjecutarFfmpegAsync(ArgumentosConversion(rutaOgg, rutaMp3Destino + ".part", tema, portada), rutaMp3Destino, ct)) return true;
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo incrustar la portada en '{tema.Slug}'; se convierte sin ella.");
        }
        return await EjecutarFfmpegAsync(ArgumentosConversion(rutaOgg, rutaMp3Destino + ".part", tema, null), rutaMp3Destino, ct);
    }

    private string? RutaPortada(int aniListId)
    {
        if (_carpetaPortadas == null) return null;
        string ruta = Path.Combine(_carpetaPortadas, aniListId + ".jpg");
        try { return File.Exists(ruta) && new FileInfo(ruta).Length > 0 ? ruta : null; }
        catch { return null; }
    }

    /// <summary>
    /// Argumentos de ffmpeg. -hide_banner -loglevel error: sin esto ffmpeg vuelca bastante texto a stderr que, al no leerse,
    /// llena el buffer del pipe redirigido y CUELGA el proceso (comprobado en vivo: quedó un ffmpeg.exe zombi). -q:a 2 = VBR
    /// ~190 kbps. -f mp3: la extensión ".part" no le dice a ffmpeg qué formato escribir. ID3v2.3: la versión que mejor lee el
    /// Explorador de Windows. La portada se recodifica a JPEG (la caché de portadas puede traer otro formato con nombre .jpg).
    /// </summary>
    /// <param name="copiarAudio">true = el origen ya es el mp3 y solo se le ponen etiquetas: el audio se copia tal cual (sin
    /// volver a codificar ni perder calidad, una fracción de segundo por canción).</param>
    internal static List<string> ArgumentosConversion(string rutaOgg, string rutaSalida, AnimeThemeInfo tema, string? rutaPortada, bool copiarAudio = false)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        // Lo descargado se lee como lo que debe ser, un .ogg: sin decírselo, ffmpeg adivina el formato por el contenido y
        // un servidor comprometido podría mandar una lista de reproducción que le hiciera abrir otras direcciones o archivos.
        if (!copiarAudio) args.AddRange(["-f", "ogg"]);
        args.AddRange(["-i", rutaOgg]);
        if (rutaPortada != null) args.AddRange(["-i", rutaPortada]);

        args.AddRange(["-map", "0:a:0"]);
        if (rutaPortada != null)
        {
            args.AddRange(["-map", "1:v:0", "-c:v", "mjpeg", "-q:v", "3", "-disposition:v:0", "attached_pic",
                "-metadata:s:v", "title=Album cover", "-metadata:s:v", "comment=Cover (front)"]);
        }

        args.AddRange(copiarAudio
            ? ["-codec:a", "copy", "-map_metadata", "-1"]
            : ["-codec:a", "libmp3lame", "-q:a", "2"]);
        args.AddRange(["-id3v2_version", "3", "-write_id3v1", "1"]);

        void Etiqueta(string nombre, string? valor)
        {
            if (!string.IsNullOrWhiteSpace(valor)) args.AddRange(["-metadata", $"{nombre}={valor.Trim()}"]);
        }
        Etiqueta("title", string.IsNullOrWhiteSpace(tema.TituloCancion) ? tema.Slug : tema.TituloCancion);
        Etiqueta("artist", tema.Artistas);
        Etiqueta("album", tema.NombreAnime);
        Etiqueta("genre", "Anime");
        string rango = string.IsNullOrWhiteSpace(tema.RangoEpisodios) ? "" : $" · ep {tema.RangoEpisodios}";
        string notas = string.IsNullOrWhiteSpace(tema.Notas) ? "" : $" · {tema.Notas}";
        Etiqueta("comment", $"{tema.Slug} v{tema.Version}{rango}{notas} · AnimeThemes.moe");

        args.AddRange(["-f", "mp3", rutaSalida]);
        return args;
    }

    /// <summary>
    /// Ejecuta ffmpeg escribiendo en "{destino}.part" y lo mueve al destino solo si terminó bien: un fallo o una cancelación
    /// nunca dejan un mp3 a medias con el nombre final (que la ficha contaría como descargado).
    /// </summary>
    private static async Task<bool> EjecutarFfmpegAsync(List<string> argumentos, string rutaMp3Destino, CancellationToken ct)
    {
        string rutaParcial = rutaMp3Destino + ".part";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in argumentos) psi.ArgumentList.Add(arg);

            using var proceso = Process.Start(psi);
            if (proceso == null) return false;

            // Defensa adicional: aunque -loglevel error ya debería dejar stderr casi vacío, drenar
            // ambos streams en paralelo evita el mismo deadlock si algún día vuelve a haber salida
            // inesperada (advertencias, etc.) — nunca dejar un pipe redirigido sin lector.
            var salidaEstandar = proceso.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var salidaError = proceso.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await proceso.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Cancelado (se salió de la ficha durante la vista previa): ffmpeg no se detiene solo al soltar el Process.
                try { proceso.Kill(entireProcessTree: true); } catch { /* ya había terminado */ }
                try { await proceso.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch { /* best-effort */ }
                throw;
            }
            await Task.WhenAll(salidaEstandar, salidaError);

            if (proceso.ExitCode != 0 || !File.Exists(rutaParcial) || new FileInfo(rutaParcial).Length == 0)
            {
                AppLogger.Debug("AnimeThemesDownloadService", $"ffmpeg no pudo convertir (código {proceso.ExitCode}): {salidaError.Result.Trim()}");
                return false;
            }

            File.Move(rutaParcial, rutaMp3Destino, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"Error convirtiendo a mp3: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(rutaParcial)) File.Delete(rutaParcial); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Lo que la pestaña Descargas sabe de una descarga de música en marcha. Manda <see cref="DescargaMusicaProgresoMensaje"/>
    /// (como mucho cada <see cref="IntervaloAvisos"/>, salvo el primero y el último).
    /// </summary>
    private sealed class DescargaVisible
    {
        private readonly object _candado = new();
        private DateTime _ultimoAviso = DateTime.MinValue;

        public int AniListId { get; }
        public string TemaClave { get; }
        public string TemaTitulo { get; }
        public string Carpeta { get; }
        public string Id => IdDe(AniListId, TemaClave);
        public string AnimeTitulo { get; set; }
        public double Progreso { get; private set; }
        public double Bps { get; private set; }
        public bool Convirtiendo { get; set; }
        public string? Error { get; set; }
        public bool CanceladaPorUsuario { get; private set; }
        public CancellationTokenSource Cancelacion { get; } = new();

        public DescargaVisible(int aniListId, AnimeThemeInfo tema, string carpeta)
        {
            AniListId = aniListId;
            TemaClave = tema.ClaveEstable();
            string prefijo = tema.Version > 1 ? $"{tema.Slug} v{tema.Version}" : tema.Slug;
            TemaTitulo = string.IsNullOrWhiteSpace(tema.TituloCancion) ? prefijo : $"{prefijo} · {tema.TituloCancion}";
            Carpeta = carpeta;
            AnimeTitulo = string.IsNullOrWhiteSpace(tema.NombreAnime) ? $"AniList {aniListId}" : tema.NombreAnime;
        }

        public static string IdDe(int aniListId, string temaClave) => $"{aniListId}|{temaClave}";

        public void Actualizar(double avance, double bps)
        {
            Progreso = Math.Clamp(avance, 0, 1) * 100;
            if (bps > 0) Bps = bps;
            Avisar(forzar: avance >= 1);
        }

        public void Avisar(bool forzar)
        {
            lock (_candado)
            {
                var ahora = DateTime.UtcNow;
                if (!forzar && ahora - _ultimoAviso < IntervaloAvisos) return;
                _ultimoAviso = ahora;
            }
            Enviar(terminada: false, completada: false, cancelada: false, ruta: null);
        }

        public void Cancelar()
        {
            CanceladaPorUsuario = true;
            try { Cancelacion.Cancel(); } catch (ObjectDisposedException) { /* ya terminó */ }
        }

        public void Terminar(string? ruta, bool cancelada)
        {
            if (ruta != null) Progreso = 100;
            Enviar(terminada: true, completada: ruta != null, cancelada: cancelada, ruta: ruta);
        }

        private void Enviar(bool terminada, bool completada, bool cancelada, string? ruta)
        {
            try
            {
                WeakReferenceMessenger.Default.Send(new DescargaMusicaProgresoMensaje(
                    AniListId, AnimeTitulo, TemaClave, TemaTitulo, Progreso, Bps, Convirtiendo && !terminada,
                    terminada, completada, cancelada, ruta, completada || cancelada ? null : Error));
            }
            catch (Exception ex)
            {
                AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo avisar del avance de '{TemaTitulo}': {ex.Message}");
            }
        }

        public DescargaItem ComoDescargaItem() => new()
        {
            EsMusica = true,
            AniListId = AniListId,
            AnimeTitulo = AnimeTitulo,
            TemaClave = TemaClave,
            TemaTitulo = TemaTitulo,
            Fuente = "AnimeThemes",
            Progreso = Progreso,
            Convirtiendo = Convirtiendo,
            VelocidadBps = Bps,
            IsDownloading = true
        };
    }

    /// <summary>Una descarga en marcha compartida por todos los que la piden: reparte el avance y el resultado.</summary>
    private sealed class TrabajoEnCurso
    {
        private readonly object _candado = new();
        private readonly List<IProgress<double>> _observadores = [];
        private readonly TaskCompletionSource<string?> _resultado = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double _ultimoAvance;

        public Task<string?> Resultado => _resultado.Task;

        /// <summary>Solo las descargas de verdad (no las vistas previas): lo que ve la pestaña Descargas.</summary>
        public DescargaVisible? Visible { get; set; }

        /// <summary>Añade a alguien que quiere ver el avance y le cuenta enseguida por dónde va.</summary>
        public void AgregarObservador(IProgress<double>? progreso)
        {
            if (progreso == null) return;
            double actual;
            lock (_candado)
            {
                _observadores.Add(progreso);
                actual = _ultimoAvance;
            }
            progreso.Report(actual);
        }

        public void Informar(double avance, double bytesPorSegundo = 0)
        {
            IProgress<double>[] observadores;
            lock (_candado)
            {
                _ultimoAvance = avance;
                observadores = [.. _observadores];
            }
            foreach (var o in observadores) o.Report(avance);
            Visible?.Actualizar(avance, bytesPorSegundo);
        }

        public void Terminar(string? ruta) => _resultado.TrySetResult(ruta);
    }

    /// <summary>Archivos que se están etiquetando ahora (la ficha y "Organizar mi música" pueden coincidir en el mismo).</summary>
    private readonly HashSet<string> _etiquetando = new(StringComparer.OrdinalIgnoreCase);

    public async Task<int> EtiquetarDescargasLocalesAsync(int aniListId, IReadOnlyList<AnimeThemeInfo> catalogo, CancellationToken ct = default)
    {
        if (catalogo == null || catalogo.Count == 0) return 0;

        int etiquetados = 0;
        foreach (var tema in catalogo)
        {
            ct.ThrowIfCancellationRequested();
            string? ruta = BuscarArchivoLocal(aniListId, tema);
            if (ruta == null || TieneTituloId3(ruta)) continue;

            lock (_etiquetando)
            {
                if (!_etiquetando.Add(ruta)) continue;
            }
            try
            {
                if (await EtiquetarArchivoAsync(ruta, tema, aniListId, ct)) etiquetados++;
            }
            finally
            {
                lock (_etiquetando) _etiquetando.Remove(ruta);
            }
        }
        return etiquetados;
    }

    /// <summary>Le pone etiquetas y portada a un mp3 que ya existe, sustituyéndolo solo si ffmpeg terminó bien.</summary>
    private async Task<bool> EtiquetarArchivoAsync(string ruta, AnimeThemeInfo tema, int aniListId, CancellationToken ct)
    {
        string? portada = RutaPortada(aniListId);
        if (portada != null && await EjecutarFfmpegAsync(ArgumentosConversion(ruta, ruta + ".part", tema, portada, copiarAudio: true), ruta, ct))
            return true;

        // Sin portada (o si la portada falló). Si el archivo está en uso (sonando, o lo lee el análisis de saltos) no se
        // puede sustituir: se queda como estaba y se intentará la próxima vez.
        return await EjecutarFfmpegAsync(ArgumentosConversion(ruta, ruta + ".part", tema, null, copiarAudio: true), ruta, ct);
    }

    public IReadOnlyList<int> AnimesConDescargas()
    {
        var ids = new List<int>();
        try
        {
            if (!Directory.Exists(_carpetaMusica)) return ids;
            foreach (var carpeta in Directory.GetDirectories(_carpetaMusica))
            {
                if (int.TryParse(Path.GetFileName(carpeta), out int id) && id > 0 && Directory.EnumerateFiles(carpeta, "*.mp3").Any())
                    ids.Add(id);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo recorrer la carpeta de música: {ex.Message}");
        }
        return ids;
    }

    public int ContarPendientesDeOrganizar(int aniListId)
    {
        string carpeta = CarpetaAnime(aniListId);
        if (!Directory.Exists(carpeta)) return 0;

        int pendientes = 0;
        try
        {
            foreach (var archivo in Directory.GetFiles(carpeta, "*.mp3"))
            {
                if (TryParseNombreArchivo(archivo, out _)) pendientes++;                       // nombre técnico antiguo
                else if (TryParseNombreLegible(archivo, out _) && !TieneTituloId3(archivo)) pendientes++; // sin etiquetas
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo revisar la carpeta de {aniListId}: {ex.Message}");
        }
        return pendientes;
    }

    /// <summary>
    /// ¿El mp3 ya tiene título en su etiqueta ID3v2? Se lee solo la cabecera y los encabezados de cada marco (sin cargar la
    /// portada ni lanzar procesos): es lo que distingue un mp3 ya etiquetado por la app de uno de antes.
    /// </summary>
    internal static bool TieneTituloId3(string ruta)
    {
        try
        {
            using var fs = new FileStream(ruta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> cabecera = stackalloc byte[10];
            if (fs.Read(cabecera) < 10 || cabecera[0] != 'I' || cabecera[1] != 'D' || cabecera[2] != '3') return false;

            int version = cabecera[3];
            if (version is < 3 or > 4 || (cabecera[5] & 0x40) != 0) return false; // v2.2 o cabecera extendida: se reetiqueta
            long fin = 10 + Syncsafe(cabecera[6..10]);

            Span<byte> marco = stackalloc byte[10];
            while (fs.Position + 10 <= fin)
            {
                if (fs.Read(marco) < 10 || marco[0] == 0) break; // relleno: no hay más marcos
                long tamano = version == 4
                    ? Syncsafe(marco[4..8])
                    : (long)marco[4] << 24 | (long)marco[5] << 16 | (long)marco[6] << 8 | marco[7];
                if (marco[0] == 'T' && marco[1] == 'I' && marco[2] == 'T' && marco[3] == '2') return tamano > 1;
                if (tamano <= 0) break;
                fs.Seek(tamano, SeekOrigin.Current);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo leer la etiqueta de '{Path.GetFileName(ruta)}': {ex.Message}");
        }
        return false;
    }

    private static long Syncsafe(ReadOnlySpan<byte> b) => (long)(b[0] & 0x7F) << 21 | (long)(b[1] & 0x7F) << 14 | (long)(b[2] & 0x7F) << 7 | (long)(b[3] & 0x7F);

    public void Eliminar(int aniListId, AnimeThemeInfo tema)
    {
        try
        {
            string? ruta = BuscarArchivoLocal(aniListId, tema);
            if (ruta != null) File.Delete(ruta);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"No se pudo borrar '{tema.Slug}': {ex.Message}");
        }
    }

    public List<TemaLocalDisponible> ListarDescargasLocales(int aniListId)
    {
        var resultado = new List<TemaLocalDisponible>();
        string carpeta = CarpetaAnime(aniListId);
        if (!Directory.Exists(carpeta)) return resultado;

        try
        {
            foreach (var archivo in Directory.GetFiles(carpeta, "*.mp3"))
            {
                if (TryParseNombreCualquiera(archivo, out var tema) && tema != null)
                {
                    resultado.Add(tema);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("AnimeThemesDownloadService", $"Error listando descargas locales de AniListId {aniListId}: {ex.Message}");
        }
        return resultado;
    }

    private static readonly System.Text.RegularExpressions.Regex PatronSlug =
        new(@"^(OP|ED|IN)[0-9]*(-[A-Za-z0-9]+)*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex PatronVersion =
        new(@"^v([0-9]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Acepta el nombre técnico antiguo y el legible de ahora.</summary>
    internal static bool TryParseNombreCualquiera(string rutaArchivo, out TemaLocalDisponible? resultado) =>
        TryParseNombreArchivo(rutaArchivo, out resultado) || TryParseNombreLegible(rutaArchivo, out resultado);

    /// <summary>
    /// Reconoce "OP1 - We Are!.mp3" / "ED1-TV v2 - Título.mp3" / "OP1.mp3": slug y versión van antes del primer " - ". El
    /// tipo sale del slug (OP/ED/IN). Sin rango: el nombre no lo lleva (lo aporta el catálogo). Un mp3 cualquiera que el
    /// usuario haya dejado en la carpeta ("notas.mp3") no encaja y se ignora.
    /// </summary>
    internal static bool TryParseNombreLegible(string rutaArchivo, out TemaLocalDisponible? resultado)
    {
        resultado = null;
        string nombre = Path.GetFileNameWithoutExtension(rutaArchivo);
        int separador = nombre.IndexOf(" - ", StringComparison.Ordinal);
        string cabeza = separador >= 0 ? nombre[..separador] : nombre;

        var partes = cabeza.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length is 0 or > 2 || !PatronSlug.IsMatch(partes[0])) return false;

        int version = 1;
        if (partes.Length == 2)
        {
            var m = PatronVersion.Match(partes[1]);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out version) || version < 1) return false;
        }

        string slug = partes[0];
        string tipo = new string(slug.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        resultado = new TemaLocalDisponible(tipo, slug, version, null, rutaArchivo);
        return true;
    }

    /// <summary>Reconstruye tipo/slug/versión/rango a partir de un nombre generado por
    /// <see cref="AnimeThemeInfo.NombreArchivoLocal"/> (formato: Tipo_Slug_vN_epRango.mp3).</summary>
    internal static bool TryParseNombreArchivo(string rutaArchivo, out TemaLocalDisponible? resultado)
    {
        resultado = null;
        string nombre = Path.GetFileNameWithoutExtension(rutaArchivo);
        var partes = nombre.Split('_', 4);
        if (partes.Length < 4) return false;

        string tipo = partes[0];
        string slug = partes[1];

        if (!partes[2].StartsWith('v') || !int.TryParse(partes[2][1..], out int version)) return false;
        if (!partes[3].StartsWith("ep", StringComparison.Ordinal)) return false;

        string epParte = partes[3]["ep".Length..];
        string? rango = epParte == "todos" ? null : epParte.Replace('_', ',');

        resultado = new TemaLocalDisponible(tipo, slug, version, rango, rutaArchivo);
        return true;
    }
}
