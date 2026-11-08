//! Media files through FFmpeg: reading what is in them, decoding pictures and sound, making filmstrips, proxies,
//! waveforms and reversed copies, mixing the timeline's sound, and exporting.

pub mod artifacts;
mod audio;
pub mod beats;
pub mod encode;
pub mod export;
pub mod mixer;
pub mod probe;
pub mod reverse;
mod sources;
mod video;

use std::path::Path;
use std::sync::Once;

pub use audio::AudioSource;
pub use ffmpeg_next;
pub use sources::MediaSources;
pub use video::{DecodeMode, VideoSource};

#[derive(Debug, thiserror::Error)]
pub enum MediaError {
    #[error("couldn't open {file}: {error}")]
    Open { file: String, error: ffmpeg_next::Error },
    #[error("the file has no {0} stream")]
    NoStream(&'static str),
    #[error(transparent)]
    Ffmpeg(#[from] ffmpeg_next::Error),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error("{0}")]
    Other(String),
}

impl MediaError {
    pub(crate) fn open(path: &Path, error: ffmpeg_next::Error) -> MediaError {
        let file = path.file_name().map_or_else(|| path.display().to_string(), |n| n.to_string_lossy().into_owned());
        MediaError::Open { file, error }
    }
}

/// Starts FFmpeg once, and quiets its console chatter.
pub fn init() {
    static ONCE: Once = Once::new();
    ONCE.call_once(|| {
        let _ = ffmpeg_next::init();
        ffmpeg_next::util::log::set_level(ffmpeg_next::util::log::Level::Error);
    });
}
