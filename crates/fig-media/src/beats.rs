//! Beat detection: the musical beats of a file's sound, or its kick-drum attacks, as times. The expensive half (decode
//! and spectra) runs once; choosing markers from it for a sensitivity is cheap, so a slider can move them live.

use std::path::Path;

use rustfft::FftPlanner;
use rustfft::num_complex::Complex;

use crate::{AudioSource, MediaError};

/// One analysed stretch of sound, ready to answer any sensitivity.
#[derive(Clone, Debug, Default)]
pub struct BeatAnalysis {
    onsets: Vec<f64>,
    kicks: Vec<f64>,
    tracked: Vec<f64>,
    frames_per_second: f64,
    offset: f64,
}

impl BeatAnalysis {
    /// Beat (or kick) times in source seconds for a sensitivity from 0 (strong hits only) to 1 (more hits).
    pub fn select(&self, sensitivity: f64, kicks_only: bool) -> Vec<f64> {
        if kicks_only {
            pick_attacks(&self.kicks, self.frames_per_second, self.offset, sensitivity)
        } else {
            filter_beat_strengths(&self.tracked, &self.onsets, self.frames_per_second, self.offset, sensitivity)
        }
    }
}

fn hz_to_mel(hz: f64) -> f64 {
    2595.0 * (1.0 + hz / 700.0).log10()
}

fn mel_to_hz(mel: f64) -> f64 {
    700.0 * (10f64.powf(mel / 2595.0) - 1.0)
}

fn mel_filters(sample_rate: f64, window: usize, bands: usize) -> Vec<Vec<f64>> {
    let bins = window / 2 + 1;
    let max_mel = hz_to_mel(sample_rate / 2.0);
    let hz: Vec<f64> = (0..bands + 2).map(|i| mel_to_hz(max_mel * i as f64 / (bands + 1) as f64)).collect();
    (0..bands)
        .map(|b| {
            let (l, c, r) = (hz[b], hz[b + 1], hz[b + 2]);
            (0..bins)
                .map(|bin| {
                    let f = bin as f64 * sample_rate / window as f64;
                    // the two slopes meet at one at the centre
                    ((f - l) / (c - l)).min((r - f) / (r - c)).max(0.0)
                })
                .collect()
        })
        .collect()
}

/// Decodes and analyses a stretch of a file's sound once (all of it after `start_sec` when no duration).
pub fn analyze(path: &Path, start_sec: f64, duration_sec: Option<f64>) -> Result<BeatAnalysis, MediaError> {
    const HOP: usize = 256;
    const RATE: u32 = 22_050;
    const WINDOW: usize = 1024;
    const BANDS: usize = 40;
    let duration = match duration_sec {
        Some(d) => d,
        None => crate::probe::probe(path)?.duration_sec - start_sec,
    };
    if !start_sec.is_finite() || start_sec < 0.0 || !duration.is_finite() || duration <= 0.0 {
        return Ok(BeatAnalysis::default());
    }
    let stereo = AudioSource::open(path, RATE)?.read(start_sec, duration);
    let mono: Vec<f32> = stereo.chunks_exact(2).map(|p| (p[0] + p[1]) * 0.5).collect();
    let filters = mel_filters(RATE as f64, WINDOW, BANDS);
    let fft = FftPlanner::<f32>::new().plan_fft_forward(WINDOW);
    let hann: Vec<f32> = (0..WINDOW)
        .map(|i| (0.5 - 0.5 * (2.0 * std::f64::consts::PI * i as f64 / WINDOW as f64).cos()) as f32)
        .collect();
    let mut spectrum = vec![Complex::new(0f32, 0f32); WINDOW];
    let mut previous_bands = vec![0f64; BANDS];
    let mut previous_mags = vec![0f32; WINDOW / 2 + 1];
    let (mut onsets, mut kicks) = (Vec::new(), Vec::new());
    let mut start = 0;
    while start + WINDOW <= mono.len() {
        for i in 0..WINDOW {
            spectrum[i] = Complex::new(mono[start + i] * hann[i], 0.0);
        }
        fft.process(&mut spectrum);
        // scaled by 1/n, as the first version's FFT was
        let mags: Vec<f32> = spectrum[..WINDOW / 2 + 1].iter().map(|c| c.norm() / WINDOW as f32).collect();
        // positive changes in the bass favour kick-like attacks (low transients, not an instrument classifier)
        let mut kick = 0.0;
        for (bin, &m) in mags.iter().enumerate() {
            let hz = bin as f64 * RATE as f64 / WINDOW as f64;
            if (35.0..=180.0).contains(&hz) {
                kick += (m - previous_mags[bin]).max(0.0) as f64;
            }
            previous_mags[bin] = m;
        }
        kicks.push(if start == 0 { 0.0 } else { kick });
        let mut onset = 0.0;
        for (b, filter) in filters.iter().enumerate() {
            let magnitude: f64 = mags.iter().zip(filter).map(|(m, w)| *m as f64 * w).sum();
            // floored, so silent bands never take log(0)
            let log = 20.0 * magnitude.max(1e-8).log10();
            onset += (log - previous_bands[b]).max(0.0);
            previous_bands[b] = log;
        }
        onsets.push(if start == 0 { 0.0 } else { onset });
        start += HOP;
    }
    let frames_per_second = RATE as f64 / HOP as f64;
    let offset = start_sec + WINDOW as f64 / (2.0 * RATE as f64);
    let tracked = track_beats(&onsets, frames_per_second, offset);
    Ok(BeatAnalysis { onsets, kicks, tracked, frames_per_second, offset })
}

fn max(v: &[f64]) -> f64 {
    v.iter().copied().fold(f64::MIN, f64::max)
}

fn filter_beat_strengths(beats: &[f64], onsets: &[f64], fps: f64, offset: f64, sensitivity: f64) -> Vec<f64> {
    if onsets.is_empty() {
        return Vec::new();
    }
    let s = if sensitivity.is_finite() { sensitivity.clamp(0.0, 1.0) } else { 0.5 };
    let threshold = max(onsets) * 0.6 * (1.0 - s);
    beats
        .iter()
        .copied()
        .filter(|&t| {
            let frame = (((t - offset) * fps).round_ties_even() as i64).clamp(0, onsets.len() as i64 - 1) as usize;
            let lo = frame.saturating_sub(2);
            let hi = (frame + 2).min(onsets.len() - 1);
            onsets[lo..=hi].iter().copied().fold(0.0, f64::max) >= threshold
        })
        .collect()
}

fn pick_attacks(strengths: &[f64], fps: f64, offset: f64, sensitivity: f64) -> Vec<f64> {
    if strengths.len() < 3 || max(strengths) <= 1e-8 {
        return Vec::new();
    }
    let s = if sensitivity.is_finite() { sensitivity.clamp(0.0, 1.0) } else { 0.5 };
    let threshold = max(strengths) * (0.6 - 0.55 * s);
    let radius = ((fps * 0.25).round_ties_even() as usize).max(1);
    let mut candidates = Vec::new();
    for i in 1..strengths.len() - 1 {
        if strengths[i] < threshold || strengths[i] <= strengths[i - 1] || strengths[i] < strengths[i + 1] {
            continue;
        }
        let mut around: Vec<f64> = strengths[i.saturating_sub(radius)..=(i + radius).min(strengths.len() - 1)].to_vec();
        around.sort_by(f64::total_cmp);
        if strengths[i] > around[around.len() / 2] * 1.5 {
            candidates.push(i);
        }
    }
    // strongest first, so one attack never gets several markers
    let spacing = ((fps * 0.12).round_ties_even() as usize).max(1);
    candidates.sort_by(|a, b| strengths[*b].total_cmp(&strengths[*a]).then(a.cmp(b)));
    let mut selected: Vec<usize> = Vec::new();
    for f in candidates {
        if selected.iter().all(|o| o.abs_diff(f) >= spacing) {
            selected.push(f);
        }
    }
    selected.sort_unstable();
    selected.into_iter().map(|i| offset + i as f64 / fps).collect()
}

/// The beat times in an onset-strength curve: its tempo by autocorrelation (leaning towards 120 BPM), then the
/// best-placed beats by dynamic programming (Ellis), rewarding attacks and penalizing uneven spacing.
pub fn track_beats(onsets: &[f64], fps: f64, time_offset: f64) -> Vec<f64> {
    if onsets.len() < 2 || max(onsets) <= 1e-8 {
        return Vec::new();
    }
    let n = onsets.len() as f64;
    let mean = onsets.iter().sum::<f64>() / n;
    let deviation = (onsets.iter().map(|v| (v - mean).powi(2)).sum::<f64>() / n).sqrt();
    if deviation <= 1e-8 {
        return Vec::new();
    }
    let strength: Vec<f64> = onsets.iter().map(|v| (v - mean) / deviation).collect();
    let min_lag = (fps * 60.0 / 180.0).ceil() as usize;
    let max_lag = ((fps * 60.0 / 60.0).floor() as usize).min(strength.len() / 2);
    let (mut best_score, mut best_lag) = (0.0, 0usize);
    for lag in min_lag..=max_lag {
        let mut score: f64 = (lag..strength.len()).map(|i| strength[i] * strength[i - lag]).sum();
        score /= (strength.len() - lag) as f64;
        // a broad preference for 120 BPM settles half/double-tempo doubt
        let octaves = (60.0 * fps / lag as f64 / 120.0).log2();
        score *= (-0.5 * octaves * octaves).exp();
        if score > best_score {
            best_score = score;
            best_lag = lag;
        }
    }
    if best_lag == 0 {
        return Vec::new();
    }
    let mut scores = vec![0.0f64; strength.len()];
    let mut previous = vec![-1i64; strength.len()];
    let (first, last) = ((best_lag / 2).max(1), best_lag * 2);
    for i in 0..strength.len() {
        let mut best_prev = 0.0;
        let mut interval = first;
        while interval <= last && interval <= i {
            let p = i - interval;
            let error = (interval as f64 / best_lag as f64).ln();
            let candidate = scores[p] - 100.0 * error * error;
            if candidate > best_prev {
                best_prev = candidate;
                previous[i] = p as i64;
            }
            interval += 1;
        }
        scores[i] = strength[i] + best_prev;
    }
    let mut end: i64 = -1;
    for i in 0..scores.len() {
        if strength[i] > 0.0 && (end < 0 || scores[i] >= scores[end as usize]) {
            end = i as i64;
        }
    }
    let mut beats = Vec::new();
    let mut i = end;
    while i >= 0 {
        beats.push(time_offset + i as f64 / fps);
        i = previous[i as usize];
    }
    beats.reverse();
    beats
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_steady_pulse_is_tracked_at_its_tempo() {
        // 86 frames a second, a hit every 43 frames: 120 BPM
        let fps = 86.0;
        let onsets: Vec<f64> = (0..860).map(|i| if i % 43 == 5 { 10.0 } else { 0.1 }).collect();
        let beats = track_beats(&onsets, fps, 0.0);
        assert!(beats.len() >= 18, "{}", beats.len());
        let gaps: Vec<f64> = beats.windows(2).map(|w| w[1] - w[0]).collect();
        let mean = gaps.iter().sum::<f64>() / gaps.len() as f64;
        assert!((mean - 0.5).abs() < 0.02, "{mean}");
    }
}
