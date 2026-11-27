//! Captions from speech, with Whisper (whisper.cpp, on the CPU): the speech models and their downloads, listening to
//! the timeline as it plays, and turning what is said into timed words.

use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use fig_media::mixer::{AudioMixer, SAMPLE_RATE as MIX_RATE};
use fig_timeline::{MediaAsset, TimedWord, Timeline};
use sha2::Digest;
use whisper_rs::{FullParams, SamplingStrategy, WhisperContext, WhisperContextParameters};

#[derive(Debug, thiserror::Error)]
pub enum CaptionError {
    #[error("{0}")]
    Download(String),
    #[error("The downloaded {0} model is damaged (its checksum does not match). Try again.")]
    Damaged(&'static str),
    #[error("speech recognition failed: {0}")]
    Whisper(#[from] whisper_rs::WhisperError),
    #[error(transparent)]
    Io(#[from] std::io::Error),
    #[error("cancelled")]
    Cancelled,
}

/// A Whisper model Fig can download: its ggml file on Hugging Face, its size and its SHA-256.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SpeechModel {
    pub id: &'static str,
    pub name: &'static str,
    pub description: &'static str,
    pub file_name: &'static str,
    pub bytes: u64,
    pub sha256: &'static str,
}

impl SpeechModel {
    pub fn url(&self) -> String {
        format!("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{}", self.file_name)
    }

    pub fn size_label(&self) -> String {
        if self.bytes >= 1_000_000_000 {
            format!("{:.1} GB", self.bytes as f64 / 1e9)
        } else {
            format!("{:.0} MB", self.bytes as f64 / 1e6)
        }
    }
}

/// The speech models: quantized builds, a fraction of the full ones' size and as good for captions.
pub const MODELS: [SpeechModel; 4] = [
    SpeechModel {
        id: "tiny",
        name: "Tiny",
        description: "Fastest. Fine for clear speech.",
        file_name: "ggml-tiny-q5_1.bin",
        bytes: 32_152_673,
        sha256: "818710568da3ca15689e31a743197b520007872ff9576237bda97bd1b469c3d7",
    },
    SpeechModel {
        id: "base",
        name: "Base",
        description: "Fast and good. The usual choice.",
        file_name: "ggml-base-q5_1.bin",
        bytes: 59_707_625,
        sha256: "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898",
    },
    SpeechModel {
        id: "small",
        name: "Small",
        description: "Better with accents, noise and music. Slower.",
        file_name: "ggml-small-q5_1.bin",
        bytes: 190_085_487,
        sha256: "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb",
    },
    SpeechModel {
        id: "turbo",
        name: "Large turbo",
        description: "Most accurate. Slow without a strong CPU.",
        file_name: "ggml-large-v3-turbo-q5_0.bin",
        bytes: 574_041_195,
        sha256: "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
    },
];

pub fn default_model() -> &'static SpeechModel {
    &MODELS[1]
}

pub fn find_model(id: &str) -> Option<&'static SpeechModel> {
    MODELS.iter().find(|m| m.id == id)
}

/// The folder speech models are kept in. Models aren't shipped: one is downloaded the first time it is used, checked
/// against its hash, and kept.
pub struct SpeechModels {
    pub directory: PathBuf,
}

impl SpeechModels {
    pub fn new(directory: impl Into<PathBuf>) -> Self {
        SpeechModels { directory: directory.into() }
    }

    pub fn path_of(&self, model: &SpeechModel) -> PathBuf {
        self.directory.join(model.file_name)
    }

    /// The model is on disk, whole (its hash was checked when it was downloaded).
    pub fn is_installed(&self, model: &SpeechModel) -> bool {
        std::fs::metadata(self.path_of(model)).is_ok_and(|m| m.len() == model.bytes)
    }

    /// Downloads a model to a partial file, checks its hash, then moves it into place, so a model on disk is always
    /// whole. `progress` gets 0..1. A failed or cancelled download leaves nothing behind.
    pub fn download(
        &self,
        model: &'static SpeechModel,
        progress: &mut dyn FnMut(f64),
        cancel: &AtomicBool,
    ) -> Result<(), CaptionError> {
        std::fs::create_dir_all(&self.directory)?;
        let target = self.path_of(model);
        let part = target.with_extension("bin.part");
        let result = (|| {
            let response = ureq::get(&model.url()).call().map_err(|e| CaptionError::Download(e.to_string()))?;
            let total = response
                .headers()
                .get("content-length")
                .and_then(|v| v.to_str().ok())
                .and_then(|v| v.parse::<u64>().ok())
                .unwrap_or(model.bytes);
            let mut body = response.into_body().into_reader();
            let mut file = std::fs::File::create(&part)?;
            let mut hash = sha2::Sha256::new();
            let mut buf = vec![0u8; 1 << 16];
            let mut read = 0u64;
            loop {
                if cancel.load(Ordering::Relaxed) {
                    return Err(CaptionError::Cancelled);
                }
                let n = body.read(&mut buf)?;
                if n == 0 {
                    break;
                }
                hash.update(&buf[..n]);
                file.write_all(&buf[..n])?;
                read += n as u64;
                progress((read as f64 / total as f64).min(1.0));
            }
            let sha: String = hash.finalize().iter().map(|b| format!("{b:02x}")).collect();
            if sha != model.sha256 {
                return Err(CaptionError::Damaged(model.name));
            }
            std::fs::rename(&part, &target)?;
            Ok(())
        })();
        let _ = std::fs::remove_file(&part);
        result
    }

    pub fn delete(&self, model: &SpeechModel) {
        let _ = std::fs::remove_file(self.path_of(model));
    }
}

/// What to listen for: the language spoken ("auto" detects it), and whether to write English instead.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TranscribeOptions {
    pub language: String,
    pub translate_to_english: bool,
}

impl Default for TranscribeOptions {
    fn default() -> Self {
        TranscribeOptions { language: "auto".into(), translate_to_english: false }
    }
}

/// Whisper listens at 16 kHz, in mono.
pub const SAMPLE_RATE: u32 = 16_000;

/// The timeline's sound from `start` for `duration`, mixed as it plays (cuts, speed and muted tracks heard as they
/// are) and brought down to 16 kHz mono. `progress` gets 0..1.
pub fn mix_for_speech(
    timeline: &Timeline,
    media: &[MediaAsset],
    start: f64,
    duration: f64,
    progress: &mut dyn FnMut(f64),
    cancel: &AtomicBool,
) -> Result<Vec<f32>, CaptionError> {
    const FACTOR: usize = (MIX_RATE / SAMPLE_RATE) as usize; // 3
    const CHUNK: f64 = 10.0;
    let mut mixer = AudioMixer::new();
    let mut out = Vec::with_capacity((duration * SAMPLE_RATE as f64) as usize + SAMPLE_RATE as usize);
    let mut pos = 0.0;
    while pos < duration - 1e-6 {
        if cancel.load(Ordering::Relaxed) {
            return Err(CaptionError::Cancelled);
        }
        let stereo = mixer.mix(timeline, media, start + pos, CHUNK.min(duration - pos));
        // each sample the average of three frames (both channels): a box filter that keeps speech and cuts most of
        // what would fold back above 8 kHz
        for group in stereo.as_chunks::<{ FACTOR * 2 }>().0 {
            out.push(group.iter().sum::<f32>() / (FACTOR * 2) as f32);
        }
        progress(((pos + CHUNK) / duration).min(1.0));
        pos += CHUNK;
    }
    Ok(out)
}

/// A sound description rather than speech: "[BLANK_AUDIO]", "(music)", "♪♪".
fn is_tag(t: &str) -> bool {
    (t.starts_with('[') && t.ends_with(']'))
        || (t.starts_with('(') && t.ends_with(')'))
        || t.chars().all(|c| matches!(c, '♪' | '♫' | '*' | ' ' | '-'))
}

/// Transcribes 16 kHz mono samples word by word. Times are seconds from the first sample plus `offset_sec`. Each
/// word keeps the space before it (none in languages written without spaces), so joining them gives the sentence
/// back. Non-speech tags ("[Music]", "(laughs)", "♪") are dropped. `progress` gets 0..1.
pub fn transcribe(
    samples: &[f32],
    model_path: &Path,
    options: &TranscribeOptions,
    offset_sec: f64,
    progress: impl FnMut(f64) + 'static,
    cancel: Arc<AtomicBool>,
) -> Result<Vec<TimedWord>, CaptionError> {
    let ctx = WhisperContext::new_with_params(model_path, WhisperContextParameters::default())?;
    let mut state = ctx.create_state()?;
    let mut params = FullParams::new(SamplingStrategy::Greedy { best_of: 1 });
    let language = if options.language.trim().is_empty() { "auto".to_string() } else { options.language.clone() };
    params.set_language(Some(&language));
    let threads = std::thread::available_parallelism().map_or(4, |n| n.get()).saturating_sub(1).clamp(1, 8);
    params.set_n_threads(threads as i32);
    // one segment a word, cut at word boundaries: whisper.cpp's way to time words
    params.set_token_timestamps(true);
    params.set_split_on_word(true);
    params.set_max_len(1);
    params.set_translate(options.translate_to_english);
    params.set_print_progress(false);
    params.set_print_realtime(false);
    params.set_print_special(false);
    params.set_print_timestamps(false);
    let mut progress = progress;
    params.set_progress_callback_safe(move |percent: i32| progress(percent as f64 / 100.0));
    let stop = cancel.clone();
    params.set_abort_callback_safe(move || stop.load(Ordering::Relaxed));
    state.full(params, samples)?;
    if cancel.load(Ordering::Relaxed) {
        return Err(CaptionError::Cancelled);
    }
    let mut words: Vec<TimedWord> = Vec::new();
    // inside "(upbeat music)" or "[applause]", which come a word at a time
    let mut closing: Option<char> = None;
    for segment in state.as_iter() {
        let text = segment.to_str_lossy()?.into_owned();
        let trimmed = text.trim();
        if let Some(close) = closing {
            if trimmed.ends_with(close) {
                closing = None;
            }
            continue;
        }
        if let Some(open @ ('(' | '[')) = trimmed.chars().next() {
            let close = if open == '(' { ')' } else { ']' };
            if !trimmed.ends_with(close) {
                closing = Some(close);
                continue;
            }
        }
        if trimmed.is_empty() || is_tag(trimmed) {
            continue;
        }
        // whisper times are in hundredths of a second
        let start = offset_sec + segment.start_timestamp() as f64 / 100.0;
        let end = (offset_sec + segment.end_timestamp() as f64 / 100.0).max(start);
        // a lone punctuation mark belongs to the word before it
        if let Some(last) = words.last_mut()
            && trimmed.chars().all(|c| c.is_ascii_punctuation() || "…“”‘’«»。、！？".contains(c))
        {
            last.text.push_str(trimmed);
            last.end_sec = last.end_sec.max(end);
            continue;
        }
        words.push(TimedWord::new(text, start, end));
    }
    Ok(words)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sound_tags_are_not_speech() {
        assert!(is_tag("[BLANK_AUDIO]"));
        assert!(is_tag("(music)"));
        assert!(is_tag("♪♪"));
        assert!(!is_tag("hello"));
    }
}
