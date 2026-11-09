//! The Home screen: the project browser (`HomeView.axaml`, inventory §3).
//!
//! Reads the real store (`~/.fig/projects`, written by the C# build), shows each project's thumbnail,
//! relative "last edited" text and duration exactly as `ProjectItemViewModel` formats them, and opens
//! the project into the editor. New Project creates a real project; there is no synthetic project in
//! the product.

use chrono::{DateTime, FixedOffset, Local};
use fig_project::ProjectSummary;
use rune::{Align, El, Pixels, col, el, icon, row, text};

use crate::app::AppState;
use crate::icons;
use crate::theme::{font, Tokens, RADIUS_CARD, RADIUS_CONTROL, RADIUS_PANEL};

pub struct HomeProject {
    pub id: String,
    pub name: String,
    pub last_edited: String,
    pub duration: String,
    pub thumbnail: Option<Pixels>,
}

pub struct HomeState {
    pub projects: Vec<HomeProject>,
    /// No projects at all (first run): show the create-first state, not "no search results".
    pub has_none: bool,
}

impl HomeState {
    pub fn new() -> HomeState {
        HomeState { projects: Vec::new(), has_none: true }
    }

    /// The real project list (`ProjectStore.ListProjects`), most recently updated first.
    pub fn from_store(store: &fig_project::ProjectStore) -> HomeState {
        let now = Local::now();
        let summaries = store.list_projects();
        let has_none = summaries.is_empty();
        let projects = summaries
            .into_iter()
            .map(|summary| {
                let thumbnail = summary
                    .thumbnail
                    .as_deref()
                    .map(|thumb| {
                        let path = std::path::Path::new(thumb);
                        if path.is_absolute() {
                            path.to_path_buf()
                        } else {
                            summary.folder.join(path)
                        }
                    })
                    .filter(|path| path.exists())
                    .and_then(|path| crate::services::thumbnail_pixels(&path, 222, 108));
                HomeProject {
                    id: summary.id,
                    name: summary.name,
                    last_edited: format_last_edited(summary.updated_at, now),
                    duration: format_duration(summary.duration_sec),
                    thumbnail,
                }
            })
            .collect();
        HomeState { projects, has_none }
    }

    /// Deterministic cards for the headless `home-projects` snapshot scene.
    pub fn with_samples(count: usize) -> HomeState {
        let names = ["Sweet Apple Pie", "Montage v3", "Sweet Apple Pie", "Montage v3", "City at dusk", "Trailer cut"];
        let durations = ["0:29", "Empty", "Empty", "0:29", "1:12", "Empty"];
        let projects = (0..count)
            .map(|i| HomeProject {
                id: format!("sample-{i}"),
                name: names[i % names.len()].to_string(),
                last_edited: "4 min ago".into(),
                duration: durations[i % durations.len()].into(),
                thumbnail: None,
            })
            .collect();
        HomeState { projects, has_none: false }
    }
}

/// `ProjectItemViewModel.FormatDuration`: "Empty", "m:ss" or "h:mm:ss".
fn format_duration(seconds: f64) -> String {
    if seconds <= 0.0 {
        return "Empty".into();
    }
    let total = seconds.round() as i64;
    let (h, m, s) = (total / 3600, total % 3600 / 60, total % 60);
    if h > 0 { format!("{h}:{m:02}:{s:02}") } else { format!("{m}:{s:02}") }
}

/// `ProjectItemViewModel.FormatLastEdited`.
fn format_last_edited(when: DateTime<FixedOffset>, now: DateTime<Local>) -> String {
    let age = now.signed_duration_since(when.with_timezone(&Local));
    if age.num_minutes() < 1 && age.num_seconds() < 60 {
        return "Just now".into();
    }
    if age.num_hours() < 1 {
        return format!("{} min ago", age.num_minutes().max(1));
    }
    let when_local = when.with_timezone(&Local);
    if when_local.date_naive() == now.date_naive() {
        return format!("Today, {}", when_local.format("%-I:%M %p"));
    }
    if when_local.date_naive() == now.date_naive() - chrono::Duration::days(1) {
        return "Yesterday".into();
    }
    if when_local.format("%Y").to_string() == now.format("%Y").to_string() {
        when_local.format("%b %-d").to_string()
    } else {
        when_local.format("%b %-d, %Y").to_string()
    }
}

pub fn view(home: &HomeState, t: &Tokens) -> El {
    col()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .padding(24.0)
        .gap(18.0)
        .child(header(t))
        .child(section_header(home, t))
        .child(grid(home, t))
        .child(el().grow(1.0))
        .child(status_bar(t))
}

fn header(t: &Tokens) -> El {
    row()
        .w_full()
        .h(44.0)
        .center_y()
        .child(text("Projects").font(font(20.0, 600.0)).color(t.text_primary))
        .child(el().grow(1.0))
        .child(
            crate::widgets::button("Import Project…", t, false).on_press(|s: &mut AppState| {
                s.toasts.show("Import a project by opening its folder");
            }),
        )
        .child(el().w(12.0))
        .child(
            el().h(32.0)
                .padding(14.0)
                .radius(RADIUS_CONTROL)
                .fill(t.accent_soft)
                .center()
                .child(text("+ New Project").font(font(13.0, 600.0)).color(t.text_primary))
                .on_press(|s: &mut AppState| s.new_project()),
        )
}

fn section_header(home: &HomeState, t: &Tokens) -> El {
    let count = home.projects.len();
    row()
        .w_full()
        .center_y()
        .child(
            col()
                .gap(2.0)
                .child(text("Recent projects").font(font(14.0, 600.0)).color(t.text_primary))
                .child(
                    text(if count == 1 { "1 project".to_string() } else { format!("{count} projects") })
                        .font(font(11.0, 400.0))
                        .color(t.text_muted),
                ),
        )
        .child(el().grow(1.0))
        .child(
            el().w(258.0)
                .h(32.0)
                .radius(RADIUS_CONTROL)
                .fill(t.card_bg)
                .padding(10.0)
                .center_y()
                .child(text("Search projects").font(font(11.0, 400.0)).color(t.text_faint)),
        )
        .child(el().w(10.0))
        .child(
            el().w(132.0)
                .h(32.0)
                .radius(RADIUS_CONTROL)
                .fill(t.card_bg)
                .padding(10.0)
                .center_y()
                .child(text("Last edited").font(font(12.0, 400.0)).color(t.text_secondary))
                .child(el().grow(1.0))
                .child(icon(&icons::CHEVRON_LEFT, 11.0).color(t.text_muted)),
        )
        .child(el().w(10.0))
        .child(
            el().w(52.0)
                .h(32.0)
                .radius(RADIUS_CONTROL)
                .fill(t.accent_soft)
                .center()
                .child(text("Grid").font(font(12.0, 500.0)).color(t.text_primary)),
        )
        .child(
            el().w(52.0)
                .h(32.0)
                .radius(RADIUS_CONTROL)
                .fill(t.card_bg)
                .center()
                .child(text("List").font(font(12.0, 400.0)).color(t.text_secondary)),
        )
}

fn grid(home: &HomeState, t: &Tokens) -> El {
    if home.projects.is_empty() {
        let title = if home.has_none { "No projects yet" } else { "No projects match" };
        return col()
            .w_full()
            .grow(1.0)
            .min_h(0.0)
            .center()
            .gap(12.0)
            .child(icon(&icons::FILM, 40.0).color(t.text_faint))
            .child(text(title).font(font(16.0, 600.0)).color(t.text_secondary))
            .child(
                text("Create a project to start cutting clips, or import one.")
                    .font(font(12.0, 400.0))
                    .color(t.text_muted),
            )
            .child(crate::widgets::button("New Project", t, true).on_press(|s: &mut AppState| s.new_project()));
    }
    let mut wrap = row().w_full().min_h(0.0).flex_wrap().gap(22.0);
    for project in &home.projects {
        wrap = wrap.child(card(project, t));
    }
    wrap
}

fn card(project: &HomeProject, t: &Tokens) -> El {
    let project_id = project.id.clone();
    let mut well = col().w(222.0).h(108.0).radius(RADIUS_CARD).fill(t.card_bg);
    well = match &project.thumbnail {
        Some(pixels) => well.clip_rounded(RADIUS_CARD).child(el().w_full().h_full().pixels(pixels)),
        None => well.center().gap(6.0).child(icon(&icons::CLAPPERBOARD, 20.0).color(t.text_muted)).child(
            text("No preview yet").font(font(11.0, 400.0)).color(t.text_faint),
        ),
    };

    col()
        .w(222.0)
        .min_h(190.0)
        .gap(6.0)
        .child(
            el().w(222.0)
                .h(108.0)
                .child(well)
                .child(
                    icon(&icons::LIST, 14.0)
                        .color(t.text_muted)
                        .absolute(198.0, 8.0)
                        .on_press(|s: &mut AppState| s.toasts.show("Project menu")),
                ),
        )
        .child(
            row()
                .w_full()
                .center_y()
                .child(text(project.name.clone()).font(font(14.0, 600.0)).color(t.text_primary).truncate())
                .child(el().grow(1.0))
                .child(icon(&icons::LIST, 12.0).color(t.text_faint)),
        )
        .child(text(format!("Last edited · {}", project.last_edited)).font(font(11.0, 400.0)).color(t.text_muted))
        .child(text(format!("Duration · {}", project.duration)).font(font(11.0, 400.0)).color(t.text_muted))
        .on_press(move |s: &mut AppState| {
            s.open_project(&project_id);
        })
}

fn status_bar(t: &Tokens) -> El {
    row()
        .w_full()
        .h(28.0)
        .center_y()
        .child(hint("Enter", "Open project", t))
        .child(el().w(14.0))
        .child(text("/").font(font(11.0, 400.0)).color(t.text_faint))
        .child(el().w(14.0))
        .child(hint("", "Search", t))
        .child(el().w(14.0))
        .child(hint("Ctrl+N", "New project", t))
        .child(el().grow(1.0))
}

fn hint(key: &str, label: &str, t: &Tokens) -> El {
    row()
        .gap(6.0)
        .center_y()
        .child(text(key).font(font(11.0, 500.0)).color(t.text_secondary))
        .child(text(label).font(font(11.0, 400.0)).color(t.text_muted))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn durations_match_the_csharp_formatting() {
        assert_eq!(format_duration(0.0), "Empty");
        assert_eq!(format_duration(29.4), "0:29");
        assert_eq!(format_duration(95.6), "1:36");
        assert_eq!(format_duration(3661.0), "1:01:01");
    }

    #[test]
    fn last_edited_matches_the_csharp_labels() {
        use chrono::TimeZone;
        let now = Local::now();
        let just_now = format_last_edited((now - chrono::Duration::seconds(5)).fixed_offset(), now);
        assert_eq!(just_now, "Just now");
        let minutes = format_last_edited((now - chrono::Duration::minutes(4)).fixed_offset(), now);
        assert_eq!(minutes, "4 min ago");
        let yesterday = format_last_edited((now - chrono::Duration::days(1)).fixed_offset(), now);
        assert_eq!(yesterday, "Yesterday");
    }
}
