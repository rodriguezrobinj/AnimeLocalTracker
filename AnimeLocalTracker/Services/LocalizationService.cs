using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AnimeLocalTracker.Services;

/// <summary>
/// Localización ES/EN con cambio en caliente: las vistas enlazan con
/// {Binding [Clave], Source={x:Static loc:LocalizationService.Instance}} y al
/// cambiar Idioma se eleva PropertyChanged("Item[]") → todos los textos se
/// refrescan al instante sin reiniciar. Las cadenas generadas en código usan T().
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private string _idioma = "es";
    public string Idioma
    {
        get => _idioma;
        set
        {
            // LOC-03: normalizar — cualquier valor que no sea "en" se trata como "es"
            value = value == "en" ? "en" : "es";
            if (_idioma != value)
            {
                _idioma = value;
                OnPropertyChanged("Item[]");
            }
        }
    }

    private static readonly Dictionary<string, string> Es = new()
    {
        ["Nav_Galeria"] = "Galería",
        ["Nav_Agregar"] = "Agregar Anime",
        ["Nav_Calendario"] = "Calendario",
        ["Nav_Descargas"] = "Descargas",
        ["Nav_Configuracion"] = "Configuración",
        ["Nav_AcercaDe"] = "Acerca de",

        ["Cfg_Almacenamiento"] = "Almacenamiento",
        ["Cfg_AlmacenamientoSub"] = "Carpeta donde se guardan y organizan tus animes",
        ["Cfg_RutaHint"] = "Ruta de Almacenamiento",
        ["Cfg_CambiarCarpeta"] = "Cambiar Carpeta",
        ["Cfg_Explorador"] = "Explorador",
        ["Cfg_EspacioDisco"] = "Espacio en Disco",
        ["Cfg_TotalAnimes"] = "animes registrados",
        ["Cfg_LimpiarCache"] = "Limpiar caché de imágenes",
        ["Cfg_LimpiarCacheTip"] = "Elimina miniaturas y portadas de animes que ya no existen en tu biblioteca",
        ["Cfg_ExportarBackup"] = "Exportar copia de seguridad de la base de datos",
        ["Cfg_BackupOk"] = "Copia de seguridad creada correctamente.",
        ["Cfg_BackupError"] = "No se pudo crear la copia de seguridad.",
        ["Cfg_ExportarBiblioteca"] = "Exportar biblioteca (JSON)",
        ["Cfg_BibliotecaExportada"] = "Biblioteca exportada: {0} animes y su historial.",
        ["Cfg_ImportarBiblioteca"] = "Importar biblioteca (JSON)",
        ["Cfg_ImportarConfirmacion"] = "Se fusionará la biblioteca del archivo con la actual (los animes existentes se actualizarán). ¿Continuar?",
        ["Cfg_BibliotecaImportada"] = "Biblioteca importada: {0} animes fusionados.",
        ["Cfg_BtnBackup"] = "Exportar copia de seguridad",
        ["Cfg_BtnExportarJson"] = "Exportar biblioteca (JSON)",
        ["Cfg_BtnImportarJson"] = "Importar biblioteca (JSON)",

        ["Cfg_Reproduccion"] = "Reproducción y Descargas",
        ["Cfg_ReproduccionSub"] = "Ajustes de comportamiento del reproductor y concurrencia",
        ["Cfg_AutoPlay"] = "Auto-Play del siguiente episodio",
        ["Cfg_AutoPlaySub"] = "Muestra cuenta regresiva de 5s y reproduce automáticamente el siguiente capítulo",
        ["Cfg_AutoSkip"] = "Saltar Intro / Outro automáticamente (AniSkip)",
        ["Cfg_AutoSkipSub"] = "Omite openings y endings de forma automática sin necesidad de hacer clic",
        ["Cfg_Subtitulos"] = "Subtítulos activados por defecto",
        ["Cfg_SubtitulosSub"] = "Carga pistas de subtítulos automáticamente al iniciar un video",
        ["Cfg_DescargasSimultaneas"] = "Descargas simultáneas máximas",
        ["Cfg_DescargasSimultaneasSub"] = "Cantidad de episodios descargándose en paralelo (1 a 5)",
        ["Cfg_IntervaloSync"] = "Intervalo de sincronización con AniList",
        ["Cfg_IntervaloSyncSub"] = "Frecuencia en segundo plano para sincronizar el historial de visualización",
        ["Cfg_Velocidad"] = "Velocidad de reproducción por defecto",
        ["Cfg_VelocidadSub"] = "Se aplica al abrir cada episodio (ajustable en el reproductor)",
        ["Cfg_Atajos"] = "Atajos de teclado del reproductor",
        ["Cfg_AtajosSub"] = "Personaliza las teclas rápidas (se aplican al abrir el reproductor)",
        ["Atajo_PlayPausa"] = "Reproducir / Pausar",
        ["Atajo_PantallaCompleta"] = "Pantalla completa",
        ["Atajo_Silenciar"] = "Silenciar",
        ["Atajo_SubirVolumen"] = "Subir volumen",
        ["Atajo_BajarVolumen"] = "Bajar volumen",
        ["Atajo_Adelantar10"] = "Adelantar 10 s",
        ["Atajo_Retroceder10"] = "Retroceder 10 s",
        ["Atajo_SaltarIntro"] = "Saltar intro/outro",
        ["Atajo_SiguienteEpisodio"] = "Siguiente episodio",
        ["Atajo_AnteriorEpisodio"] = "Episodio anterior",
        ["Atajo_Cerrar"] = "Cerrar reproductor",
        ["Atajo_CapturarFrame"] = "Capturar frame (screenshot)",
        ["Cfg_UmbralVisto"] = "Umbral para marcar como visto",
        ["Cfg_UmbralVistoSub"] = "Porcentaje reproducido del episodio a partir del cual se marca automáticamente como visto",
        ["Cfg_NotificarEpisodios"] = "Notificar episodios nuevos",
        ["Cfg_NotificarEpisodiosSub"] = "Avisa cuando aparecen archivos de episodios nuevos en tu biblioteca",
        ["Cfg_Idioma"] = "Idioma / Language",
        ["Cfg_IdiomaSub"] = "El cambio se aplica al instante",
        ["Cfg_Guardar"] = "Guardar Preferencias",

        ["Cfg_Cuenta"] = "Cuenta AniList y Sincronización",
        ["Cfg_CuentaSub"] = "Estado de enlace para sincronizar progreso con tu perfil en la nube",
        ["Cfg_SyncSub"] = "Los episodios marcados como vistos se sincronizan automáticamente con tu perfil online de AniList",
        ["Cfg_CerrarSesion"] = "Cerrar Sesión",

        ["Notif_NuevosEpisodios"] = "Episodios nuevos",
        ["Notif_ResumenNuevos"] = "nuevo(s) episodio(s) detectado(s) en tu biblioteca:",
        ["Notif_SinTitulo"] = "Anime",

        // === ESTADÍSTICAS (LOC-01) ===
        ["Stats_Titulo"] = "Panel de Estadísticas",
        ["Stats_Subtitulo"] = "Análisis de tu historial de visualización",
        ["Stats_EpisodiosVistos"] = "EPISODIOS VISTOS",
        ["Stats_HorasReproducidas"] = "HORAS REPRODUCIDAS",
        ["Stats_AnimesBiblioteca"] = "ANIMES EN BIBLIOTECA",
        ["Stats_BibliotecaCompletada"] = "BIBLIOTECA COMPLETADA",
        ["Stats_AMedioVer"] = "A medio ver",
        ["Stats_EpisodiosFavoritos"] = "Episodios favoritos",
        ["Stats_EpisodiosDescargados"] = "Episodios descargados",
        ["Stats_DuracionPromedio"] = "Duración promedio / ep.",
        ["Stats_GeneroFavorito"] = "GÉNERO FAVORITO",
        ["Stats_AnimeMasVisto"] = "ANIME MÁS VISTO",
        ["Stats_AnioMasActivo"] = "AÑO MÁS ACTIVO",
        ["Stats_Ritmo"] = "RITMO",
        ["Stats_PorMes"] = " / mes",
        ["Stats_RachaActual"] = "Racha actual: ",
        ["Stats_DistribucionLista"] = "Distribución de tu lista",
        ["Stats_PorEstado"] = "Por estado de seguimiento",
        ["Stats_DonutCentroAnimes"] = "animes",
        ["Stats_AnalisisGenero"] = "Análisis de género",
        ["Stats_GeneroSub"] = "Animes con episodios vistos, por género",
        ["Stats_Actividad7Dias"] = "Actividad de los últimos 7 días",
        ["Stats_PromedioDiarioSub"] = " episodios/día · racha máxima: ",
        ["Stats_TopAnimes"] = "Top 5 animes más vistos",
        ["Stats_TopAnimesSub"] = "Por número de episodios reproducidos",
        ["Stats_PorAnio"] = "Episodios vistos por año",
        ["Stats_PorAnioSub"] = "Evolución de tu consumo anual",

        // === VENTANA PRINCIPAL (LOC-01) ===
        ["Nav_Estadisticas"] = "Estadísticas",
        ["Nav_ComprobarUpdates"] = "Comprobar actualizaciones en GitHub",
        ["Nav_PantallaCompleta"] = "Pantalla Completa (F11)",
        ["Dlg_Cancelar"] = "CANCELAR",
        ["Dlg_Aceptar"] = "ACEPTAR",

        // === CONFIGURACIÓN (LOC-02) ===
        ["Cfg_CambiarCarpetaTip"] = "Elegir una nueva carpeta de almacenamiento",
        ["Cfg_ExploradorTip"] = "Abrir en el Explorador de Windows",
        ["Cfg_ColeccionTotal"] = "Colección Total",
        ["Tecla_Espacio"] = "Espacio",

        // === RESTAURAR BACKUP (BAK-03) ===
        ["Cfg_BtnRestaurar"] = "Restaurar copia",
        ["Cfg_RestaurarBackup"] = "Restaurar copia de seguridad",
        ["Cfg_RestaurarConfirmacion"] = "Se reemplazará la biblioteca actual por la copia seleccionada. ¿Continuar?",
        ["Cfg_RestaurarOk"] = "Biblioteca restaurada correctamente.",
        ["Cfg_RestaurarError"] = "No se pudo restaurar la copia (archivo inválido o corrupto).",

        // === TÍTULO DE CONFIGURACIÓN (LOC-07) ===
        ["Cfg_Titulo"] = "Configuración y Preferencias",
        ["Cfg_TituloSub"] = "Personaliza el almacenamiento de tus animes, reproducción multimedia y sincronización con AniList"
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["Nav_Galeria"] = "Library",
        ["Nav_Agregar"] = "Add Anime",
        ["Nav_Calendario"] = "Calendar",
        ["Nav_Descargas"] = "Downloads",
        ["Nav_Configuracion"] = "Settings",
        ["Nav_AcercaDe"] = "About",

        ["Cfg_Almacenamiento"] = "Storage",
        ["Cfg_AlmacenamientoSub"] = "Folder where your anime are stored and organized",
        ["Cfg_RutaHint"] = "Storage Path",
        ["Cfg_CambiarCarpeta"] = "Change Folder",
        ["Cfg_Explorador"] = "Explorer",
        ["Cfg_EspacioDisco"] = "Disk Space",
        ["Cfg_TotalAnimes"] = "registered anime",
        ["Cfg_LimpiarCache"] = "Clean image cache",
        ["Cfg_LimpiarCacheTip"] = "Removes thumbnails and covers of anime that no longer exist in your library",
        ["Cfg_ExportarBackup"] = "Export database backup",
        ["Cfg_BackupOk"] = "Backup created successfully.",
        ["Cfg_BackupError"] = "Could not create the backup.",
        ["Cfg_ExportarBiblioteca"] = "Export library (JSON)",
        ["Cfg_BibliotecaExportada"] = "Library exported: {0} anime and their history.",
        ["Cfg_ImportarBiblioteca"] = "Import library (JSON)",
        ["Cfg_ImportarConfirmacion"] = "The file's library will be merged with the current one (existing anime will be updated). Continue?",
        ["Cfg_BibliotecaImportada"] = "Library imported: {0} anime merged.",
        ["Cfg_BtnBackup"] = "Export backup",
        ["Cfg_BtnExportarJson"] = "Export library (JSON)",
        ["Cfg_BtnImportarJson"] = "Import library (JSON)",

        ["Cfg_Reproduccion"] = "Playback & Downloads",
        ["Cfg_ReproduccionSub"] = "Player behavior and concurrency settings",
        ["Cfg_AutoPlay"] = "Auto-play next episode",
        ["Cfg_AutoPlaySub"] = "Shows a 5s countdown and automatically plays the next chapter",
        ["Cfg_AutoSkip"] = "Auto-skip Intro / Outro (AniSkip)",
        ["Cfg_AutoSkipSub"] = "Skips openings and endings automatically without clicking",
        ["Cfg_Subtitulos"] = "Subtitles enabled by default",
        ["Cfg_SubtitulosSub"] = "Loads subtitle tracks automatically when starting a video",
        ["Cfg_DescargasSimultaneas"] = "Max simultaneous downloads",
        ["Cfg_DescargasSimultaneasSub"] = "Number of episodes downloading in parallel (1 to 5)",
        ["Cfg_IntervaloSync"] = "AniList sync interval",
        ["Cfg_IntervaloSyncSub"] = "Background frequency to sync your watch history",
        ["Cfg_Velocidad"] = "Default playback speed",
        ["Cfg_VelocidadSub"] = "Applied when opening each episode (adjustable in the player)",
        ["Cfg_Atajos"] = "Player keyboard shortcuts",
        ["Cfg_AtajosSub"] = "Customize hotkeys (applied when opening the player)",
        ["Atajo_PlayPausa"] = "Play / Pause",
        ["Atajo_PantallaCompleta"] = "Fullscreen",
        ["Atajo_Silenciar"] = "Mute",
        ["Atajo_SubirVolumen"] = "Volume up",
        ["Atajo_BajarVolumen"] = "Volume down",
        ["Atajo_Adelantar10"] = "Forward 10s",
        ["Atajo_Retroceder10"] = "Back 10s",
        ["Atajo_SaltarIntro"] = "Skip intro/outro",
        ["Atajo_SiguienteEpisodio"] = "Next episode",
        ["Atajo_AnteriorEpisodio"] = "Previous episode",
        ["Atajo_Cerrar"] = "Close player",
        ["Atajo_CapturarFrame"] = "Capture frame (screenshot)",
        ["Cfg_UmbralVisto"] = "Watched threshold",
        ["Cfg_UmbralVistoSub"] = "Percentage of the episode played from which it is automatically marked as watched",
        ["Cfg_NotificarEpisodios"] = "Notify new episodes",
        ["Cfg_NotificarEpisodiosSub"] = "Alerts when new episode files appear in your library",
        ["Cfg_Idioma"] = "Idioma / Language",
        ["Cfg_IdiomaSub"] = "The change applies instantly",
        ["Cfg_Guardar"] = "Save Preferences",

        ["Cfg_Cuenta"] = "AniList Account & Sync",
        ["Cfg_CuentaSub"] = "Link status to sync progress with your cloud profile",
        ["Cfg_SyncSub"] = "Episodes marked as watched sync automatically with your AniList online profile",
        ["Cfg_CerrarSesion"] = "Sign Out",

        ["Notif_NuevosEpisodios"] = "New episodes",
        ["Notif_ResumenNuevos"] = "new episode(s) detected in your library:",
        ["Notif_SinTitulo"] = "Anime",

        // === STATISTICS (LOC-01) ===
        ["Stats_Titulo"] = "Statistics Dashboard",
        ["Stats_Subtitulo"] = "Analysis of your watch history",
        ["Stats_EpisodiosVistos"] = "EPISODES WATCHED",
        ["Stats_HorasReproducidas"] = "HOURS PLAYED",
        ["Stats_AnimesBiblioteca"] = "ANIME IN LIBRARY",
        ["Stats_BibliotecaCompletada"] = "LIBRARY COMPLETED",
        ["Stats_AMedioVer"] = "In progress",
        ["Stats_EpisodiosFavoritos"] = "Favorite episodes",
        ["Stats_EpisodiosDescargados"] = "Downloaded episodes",
        ["Stats_DuracionPromedio"] = "Avg. duration / ep.",
        ["Stats_GeneroFavorito"] = "FAVORITE GENRE",
        ["Stats_AnimeMasVisto"] = "MOST WATCHED ANIME",
        ["Stats_AnioMasActivo"] = "MOST ACTIVE YEAR",
        ["Stats_Ritmo"] = "PACE",
        ["Stats_PorMes"] = " / month",
        ["Stats_RachaActual"] = "Current streak: ",
        ["Stats_DistribucionLista"] = "Your list distribution",
        ["Stats_PorEstado"] = "By tracking status",
        ["Stats_DonutCentroAnimes"] = "anime",
        ["Stats_AnalisisGenero"] = "Genre analysis",
        ["Stats_GeneroSub"] = "Anime with watched episodes, by genre",
        ["Stats_Actividad7Dias"] = "Activity of the last 7 days",
        ["Stats_PromedioDiarioSub"] = " episodes/day · max streak: ",
        ["Stats_TopAnimes"] = "Top 5 most watched anime",
        ["Stats_TopAnimesSub"] = "By number of episodes played",
        ["Stats_PorAnio"] = "Episodes watched per year",
        ["Stats_PorAnioSub"] = "Evolution of your yearly consumption",

        // === MAIN WINDOW (LOC-01) ===
        ["Nav_Estadisticas"] = "Statistics",
        ["Nav_ComprobarUpdates"] = "Check for updates on GitHub",
        ["Nav_PantallaCompleta"] = "Fullscreen (F11)",
        ["Dlg_Cancelar"] = "CANCEL",
        ["Dlg_Aceptar"] = "OK",

        // === SETTINGS (LOC-02) ===
        ["Cfg_CambiarCarpetaTip"] = "Choose a new storage folder",
        ["Cfg_ExploradorTip"] = "Open in Windows Explorer",
        ["Cfg_ColeccionTotal"] = "Total Collection",
        ["Tecla_Espacio"] = "Space",

        // === RESTORE BACKUP (BAK-03) ===
        ["Cfg_BtnRestaurar"] = "Restore backup",
        ["Cfg_RestaurarBackup"] = "Restore backup",
        ["Cfg_RestaurarConfirmacion"] = "The current library will be replaced by the selected backup. Continue?",
        ["Cfg_RestaurarOk"] = "Library restored successfully.",
        ["Cfg_RestaurarError"] = "Could not restore the backup (invalid or corrupt file).",

        // === SETTINGS TITLE (LOC-07) ===
        ["Cfg_Titulo"] = "Settings & Preferences",
        ["Cfg_TituloSub"] = "Customize your anime storage, playback and AniList sync"
    };

    public string this[string key] =>
        _idioma == "en" && En.TryGetValue(key, out var en) ? en
        : Es.TryGetValue(key, out var es) ? es
        : key;

    /// <summary>Acceso desde código (C#): LocalizationService.T("Clave").</summary>
    public static string T(string key) => Instance[key];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? nombre = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nombre));
}
