//! Export jobs: exports run in the background while editing goes on; this lists them with their progress.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

use rune::{El, col, el, row, text};

use crate::app::AppState;
use crate::theme::{Tokens, font, hex};
use crate::widgets::{self, Kind};

pub struct Job {
    pub name: String,
    pub path: PathBuf,
    /// 0..1 as f64 bits.
    pub progress: Arc<AtomicU64>,
    pub cancel: Arc<AtomicBool>,
    pub done: bool,
    pub error: Option<String>,
    pub encoder: Option<&'static str>,
}

impl Job {
    pub fn fraction(&self) -> f64 {
        f64::from_bits(self.progress.load(Ordering::Relaxed))
    }
}

#[derive(Default)]
pub struct Jobs {
    pub list: Vec<Job>,
}

impl Jobs {
    pub fn active(&self) -> usize {
        self.list.iter().filter(|j| !j.done).count()
    }
}

pub fn view(app: &AppState, t: &Tokens) -> El {
    let mut list = col().w_full().gap(10.0);
    if app.jobs.list.is_empty() {
        list = list.child(widgets::note("Nothing is exporting. Use Export in the toolbar to render the timeline.", t));
    }
    for (i, job) in app.jobs.list.iter().enumerate() {
        let f = job.fraction();
        let status = match (&job.error, job.done) {
            (Some(e), _) => format!("Failed: {e}"),
            (None, true) => format!("Done{}", job.encoder.map_or(String::new(), |e| format!(" · {e}"))),
            (None, false) => format!("{:.0}%", f * 100.0),
        };
        let mut r = row()
            .w_full()
            .gap(8.0)
            .center_y()
            .child(col().grow(1.0).min_w(0.0).child(text(job.name.clone()).font(font(12.5, 500.0)).color(t.text_primary).truncate()).child(text(status).font(font(11.0, 400.0)).color(if job.error.is_some() { t.danger } else { t.text_muted })));
        if !job.done {
            r = r.child(widgets::button("Cancel", Kind::Ghost, t).h(26.0).on_press(move |s: &mut AppState| {
                if let Some(j) = s.jobs.list.get(i) {
                    j.cancel.store(true, Ordering::Relaxed);
                }
            }));
        } else if job.error.is_none() {
            let path = job.path.to_string_lossy().into_owned();
            r = r.child(widgets::button("Show", Kind::Ghost, t).h(26.0).on_press(move |s: &mut AppState| {
                if let Err(e) = crate::media::reveal(&path) {
                    crate::toasts::show(s, e);
                }
            }));
        }
        let bar = el().w_full().h(6.0).radius(3.0).fill(hex("#0e0f11")).child(el().w_frac(if job.done { 1.0 } else { f as f32 }).h(6.0).radius(3.0).fill(if job.done && job.error.is_none() { hex("#7fc8a0") } else { hex("#6ea5ff") }));
        list = list.child(col().w_full().gap(6.0).child(r).child(bar));
    }
    super::card(560.0, t)
        .child(super::title_row("Export jobs", t))
        .child(el().w_full().max_h(480.0).scroll(rune::ScrollSpec { stick_bottom: false }).child(list))
        .child(
            row()
                .w_full()
                .center_y()
                .child(widgets::note("You can continue editing while these export.", t).grow(1.0))
                .child(widgets::button("Clear finished", Kind::Ghost, t).on_press(|s: &mut AppState| s.jobs.list.retain(|j| !j.done)))
                .child(widgets::button("Close", Kind::Normal, t).on_press(super::close)),
        )
}
