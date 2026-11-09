//! Doing each command (the table is in `commands`). Menus, keys and toolbar buttons all end up here.

use fig_media::library::Library;
use fig_timeline::{ClipKind, MediaAsset};
use rune::Tx;

use crate::app::AppState;
use crate::commands::Command;
use crate::editor::{self, Tool};

pub fn run(app: &mut AppState, command: Command, tx: &mut Tx) {
    use Command::*;
    // commands that work without a project
    match command {
        Preferences => {
            app.preferences_open = Some(crate::preferences::PreferencesState::default());
            return;
        }
        Exit => {
            if app.editor.is_some() {
                app.leave_project(true, tx);
            } else {
                tx.quit();
            }
            return;
        }
        Shortcuts => {
            app.dialog = Some(crate::dialogs::Dialog::Shortcuts);
            return;
        }
        _ => {}
    }
    let Some(e) = app.editor.as_ref() else { return };
    // edits that can close up a track: the clips they move slide into place
    let slides = matches!(command, Undo | Redo | Cut | Paste | Duplicate | RippleDelete | Delete | LiftDelete)
        .then(|| crate::timeline::starts(e.doc()));
    edit(app, command, tx);
    if let Some(before) = slides
        && let Some(e) = app.editor.as_mut()
    {
        e.tl.slide_from(&before, e.ed.doc());
    }
}

fn edit(app: &mut AppState, command: Command, tx: &mut Tx) {
    use Command::*;
    match command {
        Save => editor::save_now(app),
        SaveAs => crate::dialogs::save_as(app, tx),
        SavePackage => crate::dialogs::save_package(app, tx),
        CloseProject => app.close_project(tx),
        Import => crate::media::import_dialog(tx),
        Export => crate::dialogs::export::open(app, false),
        ExportRanges => crate::dialogs::export::open(app, true),
        ExportCaptions => crate::dialogs::export_captions(app, tx),
        Undo | Redo => {
            let e = app.editor.as_mut().unwrap();
            let done = if command == Undo { e.ed.undo() } else { e.ed.redo() };
            if !done {
                e.say(if command == Undo { "Nothing to undo" } else { "Nothing to redo" });
            }
        }
        Cut | Copy => {
            let e = app.editor.as_mut().unwrap();
            if !e.ed.selection.selected_effects().is_empty() {
                let n = e.ed.copy_selected_effects();
                if command == Cut {
                    let items = e.ed.selection.selected_effects();
                    e.ed.remove_effects(&items);
                }
                e.say(format!("{n} effect(s) copied"));
            } else {
                let done = if command == Cut { e.ed.cut_selected() } else { e.ed.copy_selected() };
                if done {
                    crate::toasts::show(app, if command == Cut { "Clip cut" } else { "Clip copied" });
                }
            }
        }
        Paste => {
            let e = app.editor.as_mut().unwrap();
            let at = e.playhead;
            let track = e.tl.track_selected.clone();
            if e.ed.clipboard_holds_effects() {
                e.ed.paste_effects(at, track.as_deref());
            } else {
                let pasted = e.ed.paste(at, track.as_deref());
                if pasted.is_empty() {
                    e.say("Nothing to paste, or no room at the playhead");
                }
            }
        }
        Duplicate => {
            let e = app.editor.as_mut().unwrap();
            e.ed.duplicate_selected();
        }
        Split => {
            let e = app.editor.as_mut().unwrap();
            let at = e.playhead;
            let cut = if e.ed.selection.is_empty() {
                // nothing selected: the main track's clip under the playhead
                match e.ed.main_track_id().map(str::to_string) {
                    Some(track) => e.ed.split_at_playhead_on(&track, at),
                    None => Vec::new(),
                }
            } else {
                e.ed.split_at_playhead(at)
            };
            if cut.is_empty() {
                e.say("Put the playhead over a clip to split it");
            }
        }
        SplitAll => {
            let e = app.editor.as_mut().unwrap();
            let at = e.playhead;
            e.ed.split_all_at_playhead(at);
        }
        TrimStart | TrimEnd => {
            let e = app.editor.as_mut().unwrap();
            let at = e.playhead;
            let track = e.tl.track_selected.clone();
            match e.ed.trim_to_playhead(at, command == TrimStart, track.as_deref()) {
                Some(new_playhead) => editor::seek(app, new_playhead, false),
                None => e.say("Put the playhead over a clip"),
            }
        }
        Freeze => freeze_frame(app),
        RippleDelete => {
            let e = app.editor.as_mut().unwrap();
            e.ed.ripple_delete_selected();
        }
        LiftDelete => {
            let e = app.editor.as_mut().unwrap();
            e.ed.lift_selected();
        }
        Delete => {
            let e = app.editor.as_mut().unwrap();
            if let Some((track, a, _)) = e.tl.gap.take() {
                e.ed.close_gap(&track, a + 1e-6);
                return;
            }
            if let Some((n, noun)) = e.ed.delete_selection() {
                e.say(format!("{n} {noun} deleted"));
            }
        }
        ToggleEnabled => {
            let e = app.editor.as_mut().unwrap();
            e.ed.toggle_enabled_selected();
        }
        Group => {
            let e = app.editor.as_mut().unwrap();
            let ids = e.ed.selection.selected_clip_ids();
            if ids.len() > 1 {
                e.ed.link_clips(&ids);
                e.say("Clips grouped: they move and cut together");
            }
        }
        Ungroup => {
            let e = app.editor.as_mut().unwrap();
            let ids = e.ed.selection.selected_clip_ids();
            e.ed.unlink_clips(&ids);
        }
        Magnet => {
            let e = app.editor.as_mut().unwrap();
            let on = !e.doc().main_track_magnet;
            e.ed.set_main_track_magnet(on);
            e.say(if on { "Main track magnet on" } else { "Main track magnet off" });
        }
        Snapping => {
            app.preferences.editing.snap = !app.preferences.editing.snap;
            let on = app.preferences.editing.snap;
            app.save_preferences();
            if let Some(e) = app.editor.as_mut() {
                e.say(if on { "Snapping on" } else { "Snapping off" });
            }
        }
        SelectTool => app.editor.as_mut().unwrap().tool = Tool::Select,
        BladeTool => {
            let e = app.editor.as_mut().unwrap();
            e.tool = if e.tool == Tool::Blade { Tool::Select } else { Tool::Blade };
        }
        SelectAll => {
            let e = app.editor.as_mut().unwrap();
            let ids: Vec<String> = e.doc().clips().map(|c| c.id.clone()).collect();
            e.ed.selection.set_clips(ids);
        }
        DetachAudio => {
            let e = app.editor.as_mut().unwrap();
            let ids = e.ed.selection.selected_clip_ids();
            if e.ed.detach_audio(&ids).is_empty() {
                e.say("Only a video playing its own sound can have its audio detached");
            }
        }
        Reverse => reverse_selected(app),
        AddMarker => {
            let e = app.editor.as_mut().unwrap();
            let at = e.playhead;
            let n = e.doc().markers.iter().filter(|m| !m.is_range()).count() + 1;
            e.ed.add_timeline_marker(at, &format!("Marker {n}"), "#eab308");
        }
        DeleteMarker => {
            let e = app.editor.as_mut().unwrap();
            let ids = e.ed.selection.selected_marker_ids();
            if ids.is_empty() {
                e.say("Select a marker first");
            } else {
                e.ed.delete_markers(&ids);
            }
        }
        RangesFromBookmarks => app.dialog = Some(crate::dialogs::Dialog::Bookmarks { include_bounds: true }),
        AutoCaptions => crate::dialogs::captions::open(app),
        PlayPause => editor::toggle_play(app),
        Loop => {
            let e = app.editor.as_mut().unwrap();
            e.loop_playback = !e.loop_playback;
            app.preferences.playback.loop_playback = e.loop_playback;
            app.save_preferences();
        }
        FrameBack | FrameForward | SecondBack | SecondForward => {
            if app.playback.is_playing() {
                editor::pause(app);
            }
            let e = app.editor.as_ref().unwrap();
            let step = match command {
                FrameBack => -1.0 / e.fps(),
                FrameForward => 1.0 / e.fps(),
                SecondBack => -1.0,
                _ => 1.0,
            };
            let t = e.snap_frame(e.playhead + step);
            editor::seek(app, t, false);
        }
        ShuttleForward => editor::shuttle(app, true),
        ShuttleBack => editor::shuttle(app, false),
        ShuttleStop => {
            if app.playback.is_playing() {
                editor::pause(app);
            }
        }
        AudioScrub => {
            let e = app.editor.as_mut().unwrap();
            e.audio_scrub = !e.audio_scrub;
            let on = e.audio_scrub;
            e.say(if on { "Audio scrubbing on" } else { "Audio scrubbing off" });
            app.preferences.playback.audio_scrub = on;
            app.save_preferences();
        }
        PreviousEdit | NextEdit => {
            let e = app.editor.as_ref().unwrap();
            let at = if command == NextEdit { e.ed.next_edit_point(e.playhead) } else { e.ed.previous_edit_point(e.playhead) };
            if let Some(at) = at {
                editor::seek(app, at, false);
            }
        }
        GoStart => editor::seek(app, 0.0, false),
        GoEnd => {
            let end = app.editor.as_ref().unwrap().duration();
            editor::seek(app, end, false);
        }
        MediaPanel | Inspector => {
            let e = app.editor.as_mut().unwrap();
            if command == MediaPanel {
                e.panels.left_collapsed = !e.panels.left_collapsed;
            } else {
                e.panels.right_collapsed = !e.panels.right_collapsed;
            }
            app.save_workspace();
        }
        ResetLayout => {
            app.editor.as_mut().unwrap().panels = editor::Panels::defaults();
            app.save_workspace();
        }
        ZoomIn | ZoomOut => {
            let e = app.editor.as_mut().unwrap();
            let p = e.playhead;
            e.tl.zoom(if command == ZoomIn { 1.25 } else { 0.8 }, p);
        }
        ZoomFit => {
            let e = app.editor.as_mut().unwrap();
            let end = e.duration();
            e.tl.zoom_to_fit(end);
        }
        FitTracks => {
            let e = app.editor.as_mut().unwrap();
            let doc = e.doc().clone();
            let media = e.project.media.clone();
            e.tl.fit_tracks(&doc, &media);
        }
        TallerTracks => app.editor.as_mut().unwrap().tl.taller(1.2),
        ShorterTracks => app.editor.as_mut().unwrap().tl.taller(1.0 / 1.2),
        Preferences | Exit | Shortcuts => {}
    }
}

/// Holds the picture under the playhead for three seconds: the frame is saved as an image, imported, and put in.
fn freeze_frame(app: &mut AppState) {
    let cache = app.editor.as_ref().map(|e| app.store.cache_directory(&e.id));
    let Some(e) = app.editor.as_mut() else { return };
    let at = e.playhead;
    let clip = e
        .ed
        .selection
        .selected_clip_ids()
        .into_iter()
        .chain(e.doc().clips().filter(|c| c.kind() == ClipKind::Video).map(|c| c.id.clone()))
        .find(|id| e.ed.find_clip(id).is_some_and(|c| c.kind() == ClipKind::Video && at > c.start_sec && at < c.end_sec()));
    let Some(clip_id) = clip else {
        e.say("Put the playhead over a video clip to freeze its frame");
        return;
    };
    let c = e.ed.find_clip(&clip_id).unwrap().clone();
    let Some(asset) = c.media_id().and_then(|id| e.find_media(id)).cloned() else { return };
    let source_sec = fig_timeline::clip_time::source_at(&c, at - c.start_sec);
    let cache = cache.unwrap();
    let known = e.project.media.clone();
    e.say("Making a freeze frame…");
    crate::app::spawn(
        move || -> Result<MediaAsset, String> {
            std::fs::create_dir_all(&cache).map_err(|e| e.to_string())?;
            let out = cache.join(format!("freeze_{}_{:.0}.jpg", asset.hash, source_sec * 1000.0));
            fig_media::artifacts::save_frame_as_jpeg(std::path::Path::new(&asset.url), source_sec, &out, asset.width.max(2) as u32)
                .map_err(|e| e.to_string())?;
            let lib = Library::new(cache);
            let mut scratch = fig_project::Project::default();
            scratch.media = known;
            let id = lib.import(&mut scratch, &out).map_err(|e| e.to_string())?;
            scratch.media.into_iter().find(|m| m.id == id).ok_or_else(|| "The frame couldn't be read back".into())
        },
        move |app, result| {
            let Some(e) = app.editor.as_mut() else { return };
            match result {
                Ok(still) => {
                    if !e.project.media.iter().any(|m| m.id == still.id) {
                        e.project.media.push(still.clone());
                        e.media_rev += 1;
                    }
                    if e.ed.freeze_frame(&clip_id, at, &still).is_none() {
                        e.say("No room to hold the frame there");
                    }
                    crate::media::load_pictures(app);
                }
                Err(err) => crate::toasts::show(app, format!("Couldn't make the freeze frame: {err}")),
            }
        },
    );
}

/// Reverses the selected clips: footage gets a backwards copy made once (in the background), titles run their
/// animation backwards.
pub fn reverse_selected(app: &mut AppState) {
    let cache = app.editor.as_ref().map(|e| app.store.cache_directory(&e.id));
    let Some(e) = app.editor.as_mut() else { return };
    let ids = e.ed.selection.selected_clip_ids();
    if ids.is_empty() {
        e.say("Select a clip to reverse");
        return;
    }
    let targets = e.ed.reverse_targets(&ids);
    // footage that has no reversed copy yet
    let mut needed: Vec<MediaAsset> = Vec::new();
    for id in &targets {
        let Some(asset) = e.ed.find_clip(id).and_then(|c| c.media_id()).and_then(|m| e.find_media(m)) else { continue };
        let has = asset.reverse_of.is_some() || e.project.media.iter().any(|m| m.reverse_of.as_deref() == Some(&asset.id));
        if !has && !needed.iter().any(|n| n.id == asset.id) {
            needed.push(asset.clone());
        }
    }
    if needed.is_empty() {
        finish_reverse(app, &ids);
        return;
    }
    e.say("Reversing clip…");
    let cache = cache.unwrap();
    crate::app::spawn(
        move || {
            let mut made = Vec::new();
            for asset in needed {
                let ext = if asset.kind == fig_timeline::MediaKind::Audio { "m4a" } else { "mp4" };
                let out = cache.join(format!("{}_reversed.{ext}", asset.hash));
                let _ = std::fs::create_dir_all(&cache);
                if !out.exists() {
                    if let Err(err) = fig_media::reverse::reverse(std::path::Path::new(&asset.url), &out, &mut |_| {}, &|| false) {
                        return Err(err.to_string());
                    }
                }
                let mut copy = fig_media::probe::probe(&out).map_err(|e| e.to_string())?;
                copy.reverse_of = Some(asset.id.clone());
                made.push(copy);
            }
            Ok(made)
        },
        move |app, result: Result<Vec<MediaAsset>, String>| match result {
            Ok(made) => {
                if let Some(e) = app.editor.as_mut() {
                    for m in made {
                        e.project.media.push(m);
                    }
                    e.media_rev += 1;
                }
                finish_reverse(app, &ids);
            }
            Err(err) => crate::toasts::show(app, format!("Couldn't reverse the clip: {err}")),
        },
    );
}

fn finish_reverse(app: &mut AppState, ids: &[String]) {
    let Some(e) = app.editor.as_mut() else { return };
    let media = e.project.media.clone();
    let find = |id: &str| media.iter().find(|m| m.id == id).cloned();
    let counterpart = |a: &MediaAsset| match &a.reverse_of {
        Some(original) => media.iter().find(|m| &m.id == original).cloned(),
        None => media.iter().find(|m| m.reverse_of.as_deref() == Some(&a.id)).cloned(),
    };
    if e.ed.reverse(ids, find, counterpart) {
        e.say("Reversed");
    }
}

/// Finds the beats of the selected clip's sound and shows them as a preview to apply.
pub fn analyze_beats(app: &mut AppState) {
    let Some(e) = app.editor.as_mut() else { return };
    let Some(clip) = e.ed.selection.selected_clip_ids().into_iter().find_map(|id| e.ed.find_clip(&id).cloned()) else {
        e.say("Select a clip with sound first");
        return;
    };
    let Some(asset) = clip.media_id().and_then(|id| e.find_media(id)).cloned().filter(|a| a.has_audio) else {
        e.say("That clip has no sound to find beats in");
        return;
    };
    e.say("Listening for beats…");
    let (start, dur) = (clip.source_in(), clip.source_out() - clip.source_in());
    crate::app::spawn(
        move || fig_media::beats::analyze(std::path::Path::new(&asset.url), start, Some(dur)).map_err(|e| e.to_string()),
        move |app, result| {
            let Some(e) = app.editor.as_mut() else { return };
            match result {
                Ok(analysis) => {
                    let times: Vec<f64> = analysis
                        .select(0.5, false)
                        .into_iter()
                        .map(|src| fig_timeline::clip_time::local_at(&clip, src))
                        .filter(|t| *t >= 0.0 && *t <= clip.dur_sec)
                        .collect();
                    let n = times.len();
                    e.ed.set_beat_preview(&clip.id, &times);
                    e.say(format!("{n} beats found · Beats ▸ Apply to keep them"));
                }
                Err(err) => crate::toasts::show(app, format!("Couldn't analyze the beats: {err}")),
            }
        },
    );
}
