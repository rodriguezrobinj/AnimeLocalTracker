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
