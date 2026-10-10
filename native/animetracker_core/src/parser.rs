use serde::{Deserialize, Serialize};
use anitomy_pure::{Parser, elements::Category};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ParsedAnimeInfo {
    pub success: bool,
    pub original_filename: String,
    pub anime_title: Option<String>,
    pub episode_number: Option<String>,
    pub release_group: Option<String>,
    pub video_resolution: Option<String>,
    pub season: Option<String>,
    pub file_extension: Option<String>,
    pub checksum: Option<String>,
    pub audio_term: Option<String>,
    pub video_term: Option<String>,
    pub subtitles: Option<String>,
}

impl ParsedAnimeInfo {
    pub fn empty(filename: &str) -> Self {
        Self {
            success: false,
            original_filename: filename.to_string(),
            anime_title: None,
            episode_number: None,
            release_group: None,
            video_resolution: None,
            season: None,
            file_extension: None,
            checksum: None,
            audio_term: None,
            video_term: None,
            subtitles: None,
        }
    }
}

/// Palabras de idioma que muchos sitios en español ponen tras el número ("Anime 12 Latino"). anitomy no las conoce y,
/// al ver letras después del número, lo toma todo por título.
const ETIQUETAS_IDIOMA: [&str; 14] = [
    "latino", "lat", "castellano", "cast", "español", "espanol", "esp", "sub", "subtitulado", "dub", "doblado", "doblaje",
    "audio", "japones",
];

/// Palabras que convierten el número siguiente en otra cosa que un episodio ("Movie 9", "Temporada 2").
const NO_ES_EPISODIO: [&str; 9] = ["movie", "pelicula", "película", "film", "vol", "season", "temporada", "part", "parte"];

/// Regla estrecha para cuando anitomy no encuentra episodio: el nombre termina en etiquetas de idioma y justo antes va un
/// número con título delante ("Anime 12 Latino"). No vale un año, ni un número tras "Movie"/"Temporada" o tras otro número
/// ("Evangelion 1.11").
fn episodio_antes_del_idioma(nombre: &str, extension: Option<&str>) -> Option<String> {
    let sin_extension = extension
        .and_then(|ext| nombre.strip_suffix(ext))
        .and_then(|resto| resto.strip_suffix('.'))
        .unwrap_or(nombre);
    let palabras: Vec<String> = sin_extension
        .split(|c: char| !c.is_alphanumeric())
        .filter(|p| !p.is_empty())
        .map(str::to_lowercase)
        .collect();

    let etiquetas = palabras.iter().rev().take_while(|p| ETIQUETAS_IDIOMA.contains(&p.as_str())).count();
    if etiquetas == 0 || palabras.len() < etiquetas + 2 {
        return None;
    }
    let numero = &palabras[palabras.len() - etiquetas - 1];
    let anterior = &palabras[palabras.len() - etiquetas - 2];
    if numero.len() > 4 || !numero.chars().all(|c| c.is_ascii_digit()) {
        return None;
    }
    let valor: u32 = numero.parse().ok()?;
    let es_episodio = valor > 0
        && !(1900..=2099).contains(&valor)
        && !NO_ES_EPISODIO.contains(&anterior.as_str())
        && !anterior.chars().all(|c| c.is_ascii_digit());
    es_episodio.then(|| numero.clone())
}

pub fn parse_filename(filename: &str) -> ParsedAnimeInfo {
    if filename.trim().is_empty() {
        return ParsedAnimeInfo::empty(filename);
    }

    let elements = match Parser::new(filename).parse() {
        Ok(elems) => elems,
        Err(_) => return ParsedAnimeInfo::empty(filename),
    };

    let anime_title = elements.find(Category::AnimeTitle).map(|el| el.value.to_string());
    let episode_number = elements
        .find(Category::EpisodeNumber)
        .or_else(|| elements.find(Category::EpisodeNumberAlt))
        .map(|el| el.value.to_string());
    let release_group = elements.find(Category::ReleaseGroup).map(|el| el.value.to_string());
    let video_resolution = elements.find(Category::VideoResolution).map(|el| el.value.to_string());
    let season = elements.find(Category::AnimeSeason).map(|el| el.value.to_string());
    let file_extension = elements.find(Category::FileExtension).map(|el| el.value.to_string());
    let checksum = elements.find(Category::FileChecksum).map(|el| el.value.to_string());
    let audio_term = elements.find(Category::AudioTerm).map(|el| el.value.to_string());
    let video_term = elements.find(Category::VideoTerm).map(|el| el.value.to_string());
    let subtitles = elements.find(Category::Subtitles).map(|el| el.value.to_string());

    let episode_number = episode_number.or_else(|| episodio_antes_del_idioma(filename, file_extension.as_deref()));

    let success = anime_title.is_some() || episode_number.is_some();

    ParsedAnimeInfo {
        success,
        original_filename: filename.to_string(),
        anime_title,
        episode_number,
        release_group,
        video_resolution,
        season,
        file_extension,
        checksum,
        audio_term,
        video_term,
        subtitles,
    }
}

#[cfg(test)]
mod pruebas {
    use super::parse_filename;

    fn episodio(nombre: &str) -> Option<String> {
        parse_filename(nombre).episode_number
    }

    #[test]
    fn numero_seguido_de_idioma_es_el_episodio() {
        for (nombre, esperado) in [
            ("Anime 12 Latino.mp4", "12"),
            ("Anime 12 Latino", "12"),
            ("Dragon Ball Z 05 Audio Latino.mkv", "05"),
            ("Naruto 100 Sub Español.mkv", "100"),
            ("Bleach 7 Castellano", "7"),
            ("One Piece 1071 Latino.mp4", "1071"),
        ] {
            assert_eq!(episodio(nombre).as_deref(), Some(esperado), "{nombre}");
        }
    }

    #[test]
    fn numero_seguido_de_idioma_que_no_es_episodio_se_queda_sin_numero() {
        for nombre in [
            "One Piece Movie 9 Latino.mp4",
            "Naruto Temporada 2 Latino",
            "Evangelion 1.11 Latino",
            "Blade Runner 2049 Latino.mkv",
            "12 Latino.mp4",
            "Anime Latino.mp4",
            "Anime 0 Latino",
        ] {
            assert_eq!(episodio(nombre), None, "{nombre}");
        }
    }

    #[test]
    fn lo_que_anitomy_ya_resuelve_no_cambia() {
        assert_eq!(episodio("[SubsPlease] Sousou no Frieren - 01 (1080p) [ABCD1234].mkv").as_deref(), Some("01"));
        assert_eq!(episodio("Solo Leveling 12.mkv").as_deref(), Some("12"));
        assert_eq!(episodio("Movie Name 1080p.mkv"), None);
    }
}
