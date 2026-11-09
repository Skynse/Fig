//! The preview's picture: composited on its own thread by the same renderer exports use, and handed to the window's
//! video surface. While playing it follows the clock frame by frame; while stopped it draws what was asked for last
//! (older requests are dropped, so a fast scrub never queues work behind the pointer).

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use fig_media::{DecodeMode, MediaSources};
use fig_render::layers::LayerRasterizer;
use fig_timeline::{ClipBody, FrameShape, Timeline, time};
use rune::native_surface::{ByteFrame, ColorInfo, Format};

use super::{Clock, Snapshot};

/// How sharp the preview is drawn: the picture's short side in pixels.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Quality {
    P270,
    P360,
    P540,
    P720,
    P1080,
}

impl Quality {
    pub const ALL: [Quality; 5] = [Quality::P270, Quality::P360, Quality::P540, Quality::P720, Quality::P1080];

    pub fn short_side(self) -> u32 {
        match self {
            Quality::P270 => 270,
            Quality::P360 => 360,
            Quality::P540 => 540,
            Quality::P720 => 720,
            Quality::P1080 => 1080,
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Quality::P270 => "270p",
            Quality::P360 => "360p",
            Quality::P540 => "540p",
            Quality::P720 => "720p",
            Quality::P1080 => "1080p",
        }
    }

    fn from_index(i: u32) -> Quality {
        Quality::ALL[(i as usize).min(Quality::ALL.len() - 1)]
    }
}

/// The picture size of a timeline: its shape, or the first video's own size for Auto.
pub fn frame_size(timeline: &Timeline, media: &[fig_timeline::MediaAsset]) -> (u32, u32) {
    let footage = timeline
        .clips()
        .find_map(|c| match &c.body {
            ClipBody::Video(v) => media.iter().find(|m| m.id == v.source_id && m.width > 0 && m.height > 0),
            _ => None,
        })
        .map_or((0, 0), |m| (m.width, m.height));
    let (w, h) = timeline.shape.size(footage.0, footage.1);
    (w.max(2) as u32, h.max(2) as u32)
}

#[derive(Default)]
struct Request {
    /// A still frame wanted at this time (none while playing or once drawn).
    show: Option<(f64, bool)>,
    /// Draw again: the timeline or the quality changed.
    redraw: bool,
    reset: bool,
    generation: u64,
}

struct Shared {
    request: Mutex<Request>,
    wake: Condvar,
    quality: AtomicU32,
}

pub struct Preview {
    shared: Arc<Shared>,
}

impl Preview {
    pub fn start(surface: rune::NativeSurface, snapshot: Arc<Mutex<Arc<Snapshot>>>, clock: Arc<Clock>) -> Preview {
        let shared = Arc::new(Shared {
            request: Mutex::new(Request::default()),
            wake: Condvar::new(),
            quality: AtomicU32::new(2),
        });
        let worker = shared.clone();
        std::thread::Builder::new()
            .name("fig-preview".into())
            .spawn(move || Worker::new(surface).run(&worker, &snapshot, &clock))
            .expect("preview thread");
        Preview { shared }
    }

    pub fn show(&self, sec: f64, scrubbing: bool) {
        let mut r = self.shared.request.lock().unwrap();
        r.show = Some((sec, scrubbing));
        r.generation += 1;
        drop(r);
        self.shared.wake.notify_all();
    }

    pub fn redraw(&self) {
        let mut r = self.shared.request.lock().unwrap();
        r.redraw = true;
        r.generation += 1;
        drop(r);
        self.shared.wake.notify_all();
    }

    pub fn reset(&self) {
        self.shared.request.lock().unwrap().reset = true;
        self.redraw();
    }

    pub fn set_quality(&self, q: Quality) {
        let i = Quality::ALL.iter().position(|x| *x == q).unwrap_or(2) as u32;
        self.shared.quality.store(i, Ordering::Relaxed);
        self.redraw();
    }

    pub fn quality(&self) -> Quality {
        Quality::from_index(self.shared.quality.load(Ordering::Relaxed))
    }
}

struct Worker {
    surface: rune::NativeSurface,
    sources: Option<MediaSources>,
    layers: Arc<LayerRasterizer>,
    frame_id: u64,
    /// What's on screen: (timeline time, snapshot revision, quality, exact).
    shown: Option<(f64, u64, u32, bool)>,
}

impl Worker {
    fn new(surface: rune::NativeSurface) -> Worker {
        Worker { surface, sources: None, layers: Arc::new(LayerRasterizer::new()), frame_id: 0, shown: None }
    }

    fn run(mut self, shared: &Shared, snapshot: &Mutex<Arc<Snapshot>>, clock: &Clock) {
        let mut still: Option<(f64, bool)> = None;
        let mut last_request = Instant::now();
        loop {
            let snap = snapshot.lock().unwrap().clone();
            let quality = shared.quality.load(Ordering::Relaxed);
            let rate = snap.timeline.rate;
            if clock.is_playing() {
                {
                    let mut r = shared.request.lock().unwrap();
                    r.show = None;
                    r.redraw = false;
                    if std::mem::take(&mut r.reset) {
                        self.sources = None;
                    }
                }
                let t = time::snap_to_frame(clock.now().max(0.0), rate);
                if self.shown != Some((t, snap.revision, quality, true)) {
                    self.draw(&snap, t, quality, DecodeMode::Playback, true);
                }
                // sleep to the next frame boundary (or until something changes)
                let frame = 1.0 / rate.fps().max(1.0);
                let ahead = (t + frame - clock.now()) / clock.rate().abs().max(1.0);
                let wait = Duration::from_secs_f64(ahead.clamp(0.002, frame));
                let r = shared.request.lock().unwrap();
                let _ = shared.wake.wait_timeout(r, wait).unwrap();
                continue;
            }
            // stopped: take the newest request, or polish the last quick frame once the pointer rests
            let mut r = shared.request.lock().unwrap();
            if std::mem::take(&mut r.reset) {
                self.sources = None;
            }
            let redraw = std::mem::take(&mut r.redraw);
            let wanted = r.show.take();
            if wanted.is_none() && !redraw {
                let polish = still.is_some_and(|(_, quick)| quick) && last_request.elapsed() > Duration::from_millis(120);
                if !polish {
                    let timeout = if still.is_some_and(|(_, quick)| quick) { 60 } else { 500 };
                    let _ = shared.wake.wait_timeout(r, Duration::from_millis(timeout)).unwrap();
                    continue;
                }
                drop(r);
                let (t, _) = still.unwrap();
                still = Some((t, false));
                self.draw(&snap, t, quality, DecodeMode::Playback, true);
                continue;
            }
            drop(r);
            if let Some(w) = wanted {
                still = Some(w);
                last_request = Instant::now();
            } else if still.is_none() {
                still = Some((clock.now(), false));
            }
            let (t, quick) = still.unwrap();
            let t = time::snap_to_frame(t.max(0.0), rate);
            let mode = if quick { DecodeMode::Scrub } else { DecodeMode::Playback };
            if self.shown != Some((t, snap.revision, quality, !quick)) || redraw {
                let exact = self.draw(&snap, t, quality, mode, !quick);
                if exact {
                    still = Some((t, false));
                }
            }
        }
    }

    /// Composites the frame at `t` and pushes it. Returns whether it is exact (no stand-in keyframe was shown).
    fn draw(&mut self, snap: &Snapshot, t: f64, quality: u32, mode: DecodeMode, want_exact: bool) -> bool {
        let (fw, fh) = frame_size(&snap.timeline, &snap.media);
        let short = Quality::from_index(quality).short_side() as f64;
        let scale = (short / fw.min(fh) as f64).min(1.0);
        let w = ((fw as f64 * scale).round() as usize).max(2) & !1;
        let h = ((fh as f64 * scale).round() as usize).max(2) & !1;
        let sources = self.sources.get_or_insert_with(|| {
            let mut s = MediaSources::new(snap.media.clone(), self.layers.clone());
            s.use_proxies = true;
            s
        });
        sources.media = snap.media.clone();
        sources.mode = mode;
        sources.retain_clips(|id| snap.timeline.find_clip(id).is_some());
        let scene = fig_render::build_scene(&snap.timeline, &snap.media, t, w, h, sources);
        let frame = fig_render::render(&scene);
        self.frame_id += 1;
        let _ = self.surface.push_bytes(
            ByteFrame {
                width: frame.width as u32,
                height: frame.height as u32,
                format: Format::Bgra,
                color: ColorInfo::default(),
                planes: [frame.pixels, Vec::new()],
            },
            self.frame_id,
        );
        let exact = want_exact || mode == DecodeMode::Playback;
        self.shown = Some((t, snap.revision, quality, exact));
        exact
    }
}

#[allow(dead_code)]
fn _shape_is_used(_: FrameShape) {}
