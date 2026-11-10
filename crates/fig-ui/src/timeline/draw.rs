//! Drawing the timeline (`TimelineView.Rendering.cs`): lanes and headers, clips with their filmstrips, waveforms,
//! fades and keyframes, transitions, markers, the pinned ruler, the playhead, and the feedback drags show.

use fig_timeline::{Clip, ClipBody, ClipKind, MediaAsset, Track, TrackKind, clip_time};
use rune::{Color, Cursor, El, PointerButton, Shape, Tx, canvas, el, icon, text};

use super::geometry::*;
use super::input::{self, Gesture};
use crate::app::AppState;
use crate::editor::{Editor, Tool};
use crate::ui::icons;
use crate::ui::theme::{Tokens, font, hex, mono};

/// The canvas: everything below the toolbar.
pub fn canvas_view(app: &AppState, e: &Editor, t: &Tokens) -> El {
    let doc = e.doc();
    let media = e.media();
    let tl = &e.tl;
    let lanes = lanes(doc, media, tl.scale, &tl.folds);
    let w = tl.width.max(HEADER_W + 10.0);
    let h = tl.height.max(RULER_H + 10.0);
    let oy = tl.scroll_y;
    let mut c = el()
        .key("timeline.canvas")
        // zoom and scroll move everything at once: no layout springs (they ghost)
        .instant_changes()
        .w_full()
        .grow(1.0)
        .min_h(0.0)
        .fill(t.track_lane)
        .clip_rounded(8.0)
        .cursor(match (&e.tool, &tl.hover) {
            (Tool::Blade, _) => Cursor::Crosshair,
            (_, Some((_, Part::Body))) | (_, None) => Cursor::Default,
            _ => Cursor::ResizeColumn,
        })
        .on_layout(|s: &mut AppState, r: [f32; 4]| {
            crate::ui::layout::remember("timeline.canvas", r);
            if let Some(e) = s.editor.as_mut() {
                e.tl.width = r[2];
                e.tl.height = r[3];
                e.publish_playhead();
            }
        });

    // lanes
    for (i, track) in doc.tracks.iter().enumerate() {
        let lane = lanes[i];
        let y = lane.top - oy;
        if y + lane.height < RULER_H || y > h {
            continue;
        }
        let selected = tl.track_selected.as_deref() == Some(track.id.as_str());
        c = c.child(el().key(format!("tl.lane.{}", track.id)).absolute(0.0, y).w(w).h(lane.height).fill(if i % 2 == 0 { t.track_lane } else { t.track_lane_alt }));
        if selected {
            c = c.child(el().key(format!("tl.lanesel.{}", track.id)).absolute(0.0, y).w(w).h(lane.height).fill(t.selected_lane_tint));
        }
        c = c.child(el().key(format!("tl.lanerule.{}", track.id)).absolute(0.0, y + lane.height - 1.0).w(w).h(1.0).fill(hex("#00000040")));
    }

    // snap grid while dragging with snapping on
    if e.ed.snap.magnetic && matches!(tl.gesture, Gesture::Clip(_) | Gesture::Copy(_) | Gesture::Marker { .. }) {
        let (major, minor, _) = ruler_scale(f64::from(tl.pps), e.fps());
        let mut s = (tl.scroll / minor).floor() * minor;
        let end = tl.scroll + tl.visible_secs();
        while s <= end {
            let x = tl.x_of(s);
            let is_major = ((s / major) - (s / major).round()).abs() < 1e-6;
            c = c.child(el().key(format!("tl.grid.{}", (s / minor).round() as i64)).absolute(x, RULER_H).w(1.0).h(h - RULER_H).fill(Color::WHITE.alpha(if is_major { 0.12 } else { 0.055 })));
            s += minor;
        }
    }

    // clips and their transitions
    let t0 = tl.scroll - 1.0;
    let t1 = tl.scroll + tl.visible_secs() + 1.0;
    for (i, track) in doc.tracks.iter().enumerate() {
        let lane = lanes[i];
        let y = lane.clip_top() - oy;
        if y + lane.clip_height() < RULER_H || y > h {
            continue;
        }
        for (ci, clip) in track.clips.iter().enumerate() {
            if clip.end_sec() < t0 || clip.start_sec > t1 {
                continue;
            }
            let touching_next = track.clips.get(ci + 1).is_some_and(|n| (n.start_sec - clip.end_sec()).abs() < 1e-4);
            c = c.child(clip_view(app, e, track, clip, &lane, y, touching_next, t).key(format!("tl.clip.{}", clip.id)));
        }
        // the racks under the clips: each clip's, then the track's own
        for clip in track.clips.iter().filter(|c| c.end_sec() >= t0 && c.start_sec <= t1) {
            let x0 = tl.x_of(clip.start_sec + tl.slide_offset(&clip.id));
            c = c.children(rack_bars(e, super::rack::Owner::Clip(clip), x0, lane.clip_rack_top() - oy, lane.rack_open, w, t).into_iter().enumerate().map(|(i, b)| b.key(format!("tl.bar.{}.{i}", clip.id))));
        }
        c = c.children(rack_bars(e, super::rack::Owner::Track(track), tl.x_of(0.0), lane.track_rack_top() - oy, lane.rack_open, w, t).into_iter().enumerate().map(|(i, b)| b.key(format!("tl.bar.{}.{i}", track.id))));
        for cut in fig_edit::transitions::transitions_on(doc, &track.id) {
            let cx = tl.x_of(cut.cut_sec);
            let half = (cut.duration_sec / 2.0) as f32 * tl.pps;
            let body_top = y + lane.label;
            let selected = e.ed.selection.selected_transition_key() == Some(cut.key().as_str());
            c = c
                .child(el().key(format!("tl.trband.{}", cut.key())).absolute(cx - half, body_top).w((half * 2.0).max(2.0)).h(lane.body).fill(hex("#46e0a308")).inert())
                .child(
                    el().key(format!("tl.tr.{}", cut.key())).absolute(cx - 13.0, body_top + lane.body / 2.0 - 8.0)
                        .w(26.0)
                        .h(16.0)
                        .radius(8.0)
                        .fill(if selected { hex("#eab308") } else { hex("#2a2512") })
                        .border(1.0, hex("#eab308"))
                        .center()
                        .child(icon(&icons::ARROW_RIGHT_LEFT, 10.0).color(if selected { hex("#1a1a1a") } else { hex("#eab308") })),
                );
        }
    }

    // drag feedback: magnet hole, blocked ghost, copy ghost, marquee, gap
    match &tl.gesture {
        Gesture::Clip(d) => {
            if let Some((ti, cut, len)) = d.insert_mark(doc) {
                let lane = lanes[ti];
                let (x, y) = (tl.x_of(cut), lane.clip_top() - oy);
                c = c
                    .child(el().absolute(x, y).w(len as f32 * tl.pps).h(lane.clip_height()).radius(RADIUS).fill(hex("#285f9cf8")).inert())
                    .child(el().absolute(x - 1.0, y).w(2.0).h(lane.clip_height()).fill(hex("#f05f9cf8")).inert());
            }
            if let Some((track_id, delta)) = &d.ghost
                && let Some(ti) = doc.tracks.iter().position(|t| &t.id == track_id)
            {
                let lane = lanes[ti];
                for id in e.ed.selection.selected_clip_ids().iter().chain(std::iter::once(&d.clip)) {
                    if let Some(cl) = doc.find_clip(id) {
                        let x = tl.x_of(cl.start_sec + delta);
                        c = c.child(
                            el().absolute(x, lane.clip_top() - oy)
                                .w(cl.dur_sec as f32 * tl.pps)
                                .h(lane.clip_height())
                                .radius(RADIUS)
                                .fill(Color::WHITE.alpha(0.11))
                                .border(1.0, Color::WHITE.alpha(0.6))
                                .border_dash(4.0, 3.0)
                                .inert(),
                        );
                    }
                }
            }
        }
        Gesture::Copy(cd) if cd.started => {
            if let Some(ti) = cd.track {
                let lane = lanes[ti.min(lanes.len() - 1)];
                for id in &cd.ids {
                    if let Some(cl) = doc.find_clip(id) {
                        let x = tl.x_of(cl.start_sec + cd.delta);
                        c = c.child(
                            el().absolute(x, lane.clip_top() - oy)
                                .w(cl.dur_sec as f32 * tl.pps)
                                .h(lane.clip_height())
                                .radius(RADIUS)
                                .fill(hex("#325f9cf8"))
                                .border(2.0, hex("#5f9cf8"))
                                .inert(),
                        );
                    }
                }
            }
        }
        Gesture::Marquee { x0, y0, x1, y1, .. } if (x1 - x0).abs() >= 4.0 || (y1 - y0).abs() >= 4.0 => {
            let (l, r) = (x0.min(*x1), x0.max(*x1));
            let (top, bottom) = (y0.min(*y1) - oy, y0.max(*y1) - oy);
            c = c.child(
                el().absolute(l, top.max(RULER_H))
                    .w(r - l)
                    .h((bottom - top.max(RULER_H)).max(1.0))
                    .fill(hex("#285f9cf8"))
                    .border(1.0, hex("#5f9cf8"))
                    .inert(),
            );
        }
        Gesture::Track { to, .. } => {
            let y = lanes.get(*to).map_or_else(|| lanes.last().map_or(RULER_H, |l| l.top + l.height), |l| l.top) - oy;
            c = c.child(el().absolute(0.0, y - 1.0).w(w).h(2.0).fill(t.accent).inert());
        }
        _ => {}
    }
    if let Some((track_id, a, b)) = &tl.gap
        && let Some(ti) = doc.tracks.iter().position(|t| &t.id == track_id)
    {
        let lane = lanes[ti];
        c = c.child(
            el().absolute(tl.x_of(*a), lane.clip_top() - oy)
                .w(((b - a) as f32 * tl.pps).max(2.0))
                .h(lane.clip_height())
                .radius(RADIUS)
                .fill(hex("#2e5f9cf8"))
                .border(1.5, hex("#5f9cf8"))
                .border_dash(5.0, 3.0)
                .inert(),
        );
    }
    if let Some((ti, at)) = tl.drop_preview {
        let x = tl.x_of(at);
        match ti.and_then(|i| lanes.get(i)) {
            Some(lane) => {
                c = c
                    .child(el().absolute(HEADER_W, lane.top - oy).w(w).h(lane.height).fill(hex("#2244aa44")).inert())
                    .child(el().absolute(x - 1.0, lane.clip_top() - oy).w(2.0).h(lane.clip_height()).fill(t.accent).inert())
            }
            None => c = c.child(el().absolute(x - 1.0, RULER_H).w(2.0).h(h).fill(hex("#ff5a5a")).inert()),
        }
    }

    // header column over the lanes
    c = c.child(el().key("tl.headers").absolute(0.0, 0.0).w(HEADER_W).h(h).fill(t.track_header_bg));
    for (i, track) in doc.tracks.iter().enumerate() {
        let lane = lanes[i];
        let y = lane.top - oy;
        if y + lane.height < RULER_H || y > h {
            continue;
        }
        c = c.child(header_view(e, track, &lane, y, t).key(format!("tl.hdr.{}", track.id)));
    }
    c = c.child(el().key("tl.hdrrule").absolute(HEADER_W - 1.0, 0.0).w(1.0).h(h).fill(hex("#34373c")));
    let add = add_track_rect(&lanes);
    if add[1] - oy + add[3] > RULER_H && add[1] - oy < h {
        c = c.child(
            el().key("tl.addtrack").absolute(add[0], add[1] - oy)
                .w(add[2])
                .h(add[3])
                .radius(6.0)
                .hover(t.chip_bg)
                .center()
                .help("Add a track")
                .child(icon(&icons::PLUS, 14.0).color(t.text_muted)),
        );
    }

    // empty timeline hint
    if doc.tracks.iter().all(|t| t.clips.is_empty()) {
        c = c.child(
            rune::col()
                .absolute(HEADER_W, RULER_H)
                .w(w - HEADER_W)
                .h(h - RULER_H)
                .center()
                .gap(6.0)
                .inert()
                .child(text("Drag media here to start your edit").font(font(14.0, 500.0)).color(t.text_muted))
                .child(text("or double-click a clip in the Media panel to add it at the playhead").font(font(12.0, 400.0)).color(t.text_faint)),
        );
    }

    // the pinned ruler and marker lane
    c = c.child(ruler_view(e, w, t).key("tl.ruler"));

    // snap guide, blade line
    if let Some(at) = tl.snap_guide {
        c = c.child(el().key("tl.snap").absolute(tl.x_of(at) - 0.75, MARKER_LANE_H).w(1.5).h(h).fill(t.snap.alpha(0.9)).inert());
    }
    if e.tool == Tool::Blade
        && let Some((px, py)) = tl.pointer
        && px > HEADER_W
        && py > RULER_H
    {
        c = c.child(el().key("tl.blade").absolute(px - 0.75, RULER_H).w(1.5).h(h - RULER_H).fill(hex("#f0ff5a5a")).inert());
    }

    // the playhead: a line and a cap on the ruler, moved by a live value while playing
    let x = tl.x_of(e.playhead);
    e.live_playhead.set([x, 0.0]);
    c = c.child(
        el().key("tl.playhead").absolute(0.0, MARKER_LANE_H + 6.0)
            .w(1.0)
            .h(h)
            .live_offset(&e.live_playhead)
            .inert()
            .child(el().absolute(0.0, 2.0).w(1.0).h(h).fill(t.playhead))
            .child(canvas(Shape::new().fill(vec![[0.0, 0.0], [10.0, 0.0], [5.0, 8.0]], t.playhead)).absolute(-4.5, -6.0).w(10.0).h(8.0)),
    );

    c.pointer_buttons([PointerButton::Primary, PointerButton::Middle])
        .on_drag(input::on_drag)
        .on_pointer_move_local(|s: &mut AppState, p: rune::PointerEvent, tx: &mut Tx| input::hover(s, p.x, p.y, tx))
        .on_wheel(|s: &mut AppState, w: rune::WheelEvent, _tx: &mut Tx| input::wheel(s, w))
        .on_secondary_press_local(|s: &mut AppState, p: rune::PointerEvent, _tx: &mut Tx| {
            super::menu::open(s, p.x, p.y, p.window_x, p.window_y)
        })
        .on_drop_files(|s: &mut AppState, files: &[std::path::PathBuf], tx: &mut Tx| {
            s.drop_hover = false;
            let place = tx.drop_point().and_then(|p| {
                let e = s.editor.as_ref()?;
                let lanes = super::geometry::lanes(e.doc(), e.media(), e.tl.scale, &e.tl.folds);
                let track = e.tl.track_at(&lanes, p.y).map(|i| e.doc().tracks[i].id.clone());
                Some(crate::editor::media::Placement { track_id: track, at_sec: e.ed.snap_time(e.tl.t_of(p.x).max(0.0)) })
            });
            if let Some(e) = s.editor.as_mut() {
                e.tl.drop_preview = None;
            }
            crate::editor::media::drop_files(s, files, place, tx);
        })
        .on_drop_move(|s: &mut AppState, p: rune::PointerEvent, _tx: &mut Tx| {
            if let Some(e) = s.editor.as_mut() {
                let lanes = super::geometry::lanes(e.doc(), e.media(), e.tl.scale, &e.tl.folds);
                let ti = e.tl.track_at(&lanes, p.y);
                e.tl.drop_preview = Some((ti, e.ed.snap_time(e.tl.t_of(p.x).max(0.0))));
            }
        })
        .on_drop_hover(|s: &mut AppState, over: bool, _tx: &mut Tx| {
            s.drop_hover = over;
            if !over && let Some(e) = s.editor.as_mut() {
                e.tl.drop_preview = None;
            }
        })
}

fn header_view(e: &Editor, track: &Track, lane: &Lane, y: f32, t: &Tokens) -> El {
    let selected = e.tl.track_selected.as_deref() == Some(track.id.as_str());
    let mid = y + lane.height / 2.0 - HEADER_BUTTON / 2.0;
    let mut h = el().absolute(0.0, y).w(HEADER_W).h(lane.height);
    if selected {
        h = h.child(el().absolute(0.0, 0.0).w(2.0).h(lane.height).fill(t.track_accent));
    }
    for (button, bx) in header_buttons(track) {
        let (glyph, on) = match button {
            HeaderButton::Toggle if track.kind == TrackKind::Video => (if track.visible { &icons::EYE } else { &icons::EYE_OFF }, !track.visible),
            HeaderButton::Toggle | HeaderButton::Mute => (if track.muted { &icons::VOLUME_X } else { &icons::VOLUME_2 }, track.muted),
            HeaderButton::Delete => (&icons::TRASH_2, false),
            HeaderButton::Rack => continue,
        };
        let color = if on { hex("#ff7a7a") } else { t.text_muted };
        let tip = match button {
            HeaderButton::Toggle if track.kind == TrackKind::Video => "Show or hide this track",
            HeaderButton::Delete => "Delete this track",
            _ => "Mute this track's sound",
        };
        h = h.child(
            el().absolute(bx, mid - y)
                .w(HEADER_BUTTON)
                .h(HEADER_BUTTON)
                .radius(5.0)
                .hover(t.chip_bg)
                .center()
                .help(tip)
                .child(icon(glyph, 13.0).color(color)),
        );
    }
    if super::rack::has_rack(track) {
        let r = rack_toggle_rect(lane);
        let open = lane.rack_open;
        h = h.child(
            el().absolute(r[0], r[1] - lane.top)
                .w(r[2])
                .h(r[3])
                .radius(3.0)
                .fill(Color::WHITE.alpha(0.08))
                .hover(Color::WHITE.alpha(0.16))
                .center()
                .help(if open { "Fold the effect bars" } else { "Show the effect bars" })
                .child(icon(if open { &icons::CHEVRON_DOWN } else { &icons::CHEVRON_RIGHT }, 12.0).color(t.text_secondary)),
        );
    }
    let name = super::track_name(e.doc(), track);
    let label_x = 7.0 + 3.0 * (HEADER_BUTTON + 4.0) + 2.0;
    h.child(text(name).font(font(11.0, 500.0)).color(t.text_muted).absolute(label_x, lane.height / 2.0 - 7.0).inert())
}

fn clip_label(e: &Editor, clip: &Clip) -> String {
    match &clip.body {
        ClipBody::Text(b) => b.text.lines().next().unwrap_or("").to_string(),
        _ => clip
            .media_id()
            .and_then(|id| e.find_media(id))
            .map_or_else(|| "Missing media".to_string(), |m| m.file_name().to_string()),
    }
}

#[allow(clippy::too_many_arguments)]
fn clip_view(app: &AppState, e: &Editor, track: &Track, clip: &Clip, lane: &Lane, y: f32, touching_next: bool, t: &Tokens) -> El {
    let tl = &e.tl;
    let x = tl.x_of(clip.start_sec + tl.slide_offset(&clip.id));
    let mut w = clip.dur_sec as f32 * tl.pps;
    if touching_next {
        w -= 1.0;
    }
    let w = w.max(1.0);
    let h = lane.clip_height();
    let (fill, border) = clip_color(clip.kind(), t);
    let selected = e.ed.selection.is_selected(&clip.id);
    if w < DETAIL_W {
        return el().absolute(x, y).w(w).h(h).fill(if selected { t.selection } else { fill });
    }
    // only the visible part is drawn in detail
    let vis_l = (HEADER_W - x).max(0.0);
    let vis_r = (tl.width - x).min(w);
    let radius = RADIUS.min(w / 2.0);
    let mut c = el().absolute(x, y).w(w).h(h).radius(radius).clip_rounded(radius).fill(fill).shadow(1.0, 0.0, hex("#00000060"));
    let body_top = lane.label;
    let picture_h = lane.body - lane.strip;
    match clip.kind() {
        ClipKind::Video => {
            c = c.child(el().absolute(0.0, body_top).w(w).h(picture_h).fill(hex("#0e2a47")));
            if let Some(asset) = clip.media_id().and_then(|id| e.find_media(id)) {
                c = filmstrip(app, c, clip, asset, body_top, picture_h, vis_l, vis_r, tl.pps);
            }
            if let Some(line) = speed_line(e, clip, lane, y, vis_l, vis_r) {
                c = c.child(line);
            }
            if lane.strip > 0.0 && clip.as_video().is_some_and(|v| v.plays_audio) {
                c = c.child(el().absolute(0.0, body_top + picture_h).w(w).h(lane.strip).fill(hex("#15241d")));
                if let Some(asset) = clip.media_id().and_then(|id| e.find_media(id)) {
                    c = c.children(waveform(clip, asset, body_top + picture_h, lane.strip, vis_l, vis_r, tl.pps, t.audio_wave));
                }
            }
        }
        ClipKind::Audio => {
            c = c.child(el().absolute(0.0, body_top).w(w).h(lane.body).fill(hex("#15241d")));
            c = c.child(el().absolute(0.0, body_top + lane.body / 2.0).w(w).h(1.0).fill(hex("#4674d6a0")));
            if let Some(asset) = clip.media_id().and_then(|id| e.find_media(id)) {
                c = c.children(waveform(clip, asset, body_top, lane.body, vis_l, vis_r, tl.pps, t.audio_wave));
            }
        }
        ClipKind::Image => {
            if let Some(asset) = clip.media_id().and_then(|id| e.find_media(id))
                && let Some(p) = asset.thumbnail.as_deref().and_then(|p| app.media.thumbnail(p)).or_else(|| app.media.thumbnail(&asset.url))
            {
                let aspect = if asset.height > 0 { asset.width as f32 / asset.height as f32 } else { 16.0 / 9.0 };
                let tile = (lane.body * aspect).max(8.0);
                let mut tx = (vis_l / tile).floor() * tile;
                while tx < vis_r {
                    c = c.child(el().absolute(tx, body_top).w(tile).h(lane.body).pixels(p));
                    tx += tile;
                }
            }
        }
        ClipKind::Text => {
            c = c
                .child(
                    el().absolute(6.0 + vis_l, (h - 12.0) / 2.0)
                        .w(12.0)
                        .h(12.0)
                        .radius(3.0)
                        .border(1.0, border)
                        .center()
                        .child(text("T").font(font(8.0, 700.0)).color(Color::WHITE)),
                )
                .child(
                    text(clip_label(e, clip))
                        .font(font(10.5, 500.0))
                        .color(Color::WHITE)
                        .truncate()
                        .absolute(22.0 + vis_l, (h - 14.0) / 2.0)
                        .max_w((vis_r - vis_l - 26.0).max(0.0)),
                );
        }
    }
    // label strip
    if lane.label > 0.0 {
        c = c.child(el().absolute(0.0, 0.0).w(w).h(lane.label).fill(fill));
        if w - vis_l >= MIN_LABEL_W {
            let speed = if (clip.speed - 1.0).abs() > 1e-3 || clip_time::has_curve(clip) {
                if clip_time::has_curve(clip) { "  · curve".to_string() } else { format!("  · {:.2}×", clip.speed) }
            } else {
                String::new()
            };
            c = c.child(
                text(format!("{}{}", clip_label(e, clip), speed))
                    .font(font(10.0, 500.0))
                    .color(Color::WHITE)
                    .truncate()
                    .absolute(vis_l + 4.0, 0.5)
                    .max_w((vis_r - vis_l - 8.0).max(0.0)),
            );
        }
    }
    // fades: a shaded ramp from each corner
    for of in fig_timeline::fade::channels(clip) {
        let (fi, fo) = fig_timeline::fade::get(clip, *of);
        let (top, fh) = if *of == fig_timeline::fade::FadeOf::Sound { (body_top + picture_h, lane.strip) } else { (body_top, picture_h.max(lane.body - lane.strip)) };
        if fi > 0.0 {
            let fw = (fi as f32 * tl.pps).min(w);
            c = c.child(canvas(Shape::new().fill(vec![[0.0, 0.0], [fw, 0.0], [0.0, fh]], hex("#78000000"))).absolute(0.0, top).w(fw).h(fh).inert());
        }
        if fo > 0.0 {
            let fw = (fo as f32 * tl.pps).min(w);
            c = c.child(canvas(Shape::new().fill(vec![[0.0, 0.0], [fw, 0.0], [fw, fh]], hex("#78000000"))).absolute(w - fw, top).w(fw).h(fh).inert());
        }
        if selected {
            let tips = [(fi as f32 * tl.pps, top), (w - fo as f32 * tl.pps, top)];
            for (tx, ty) in tips {
                c = c.child(el().absolute(tx - 4.0, ty - 1.0).w(8.0).h(8.0).radius(4.0).fill(Color::WHITE).border(1.0, hex("#00000080")).inert());
            }
        }
    }
    // keyframe diamonds on the body's top edge
    for k in input::clip_keyframe_times(clip) {
        let kx = k as f32 * tl.pps;
        c = c.child(
            el().absolute(kx - 4.0, body_top - 4.0)
                .w(8.0)
                .h(8.0)
                .rotate(std::f32::consts::FRAC_PI_4)
                .fill(if selected { Color::WHITE } else { hex("#9cc0ff") })
                .border(1.0, hex("#00000090"))
                .inert(),
        );
    }
    // clip markers: a pennant at the top, a pole down the picture
    for m in &clip.markers {
        let mx = m.start_sec as f32 * tl.pps;
        let color = if m.color.is_empty() { t.marker_default } else { hex(&m.color) };
        c = c
            .child(el().absolute(mx, 0.0).w(1.0).h(h).fill(color.alpha(0.7)).inert())
            .child(canvas(Shape::new().fill(vec![[0.0, 0.0], [10.0, 3.5], [0.0, 7.0]], color)).absolute(mx, 0.0).w(10.0).h(7.0).inert());
    }
    if !clip.enabled {
        c = c.child(el().absolute(0.0, 0.0).w(w).h(h).fill(hex("#a6101012")).inert());
    }
    if selected {
        c = c.child(el().absolute(0.0, 0.0).w(w).h(h).radius(radius).border(2.0, t.selection).inert());
    }
    // the resize handles (`DrawResizeHandle`): both edges of a selected clip, or the edge or fade under the pointer
    let hovered = tl.hover.as_ref().filter(|(id, _)| *id == clip.id).map(|(_, p)| *p);
    let handle = |x: f32, top: f32, height: f32| el().absolute((x - 1.5).clamp(0.0, (w - 3.0).max(0.0)), top + 3.0).w(3.0).h((height - 6.0).max(2.0)).radius(1.5).fill(hex("#c84da3ff")).inert();
    if selected || hovered == Some(Part::Left) {
        c = c.child(handle(0.0, 0.0, h));
    }
    if selected || hovered == Some(Part::Right) {
        c = c.child(handle(w, 0.0, h));
    }
    for of in fig_timeline::fade::channels(clip) {
        let (fi, fo) = fig_timeline::fade::get(clip, *of);
        let (top, fh) = if *of == fig_timeline::fade::FadeOf::Sound { (body_top + picture_h, lane.strip) } else { (body_top, picture_h.max(lane.body - lane.strip)) };
        if (selected && fi > 1e-6) || hovered == Some(Part::FadeIn(*of)) {
            c = c.child(handle(fi.min(clip.dur_sec) as f32 * tl.pps, top, fh));
        }
        if (selected && fo > 1e-6) || hovered == Some(Part::FadeOut(*of)) {
            c = c.child(handle(w - fo.min(clip.dur_sec) as f32 * tl.pps, top, fh));
        }
    }
    let _ = track;
    c
}

/// The filmstrip: tiles at the clip's source times, each as wide as the picture's shape makes it.
#[allow(clippy::too_many_arguments)]
fn filmstrip(app: &AppState, mut c: El, clip: &Clip, asset: &MediaAsset, top: f32, h: f32, vis_l: f32, vis_r: f32, pps: f32) -> El {
    let aspect = if asset.height > 0 { asset.width as f32 / asset.height as f32 } else { 16.0 / 9.0 };
    let tile = (h * aspect).max(8.0);
    let strip = asset.filmstrip.as_deref().and_then(|p| app.media.strip(p));
    let thumb = asset.thumbnail.as_deref().and_then(|p| app.media.thumbnail(p));
    let interval = asset.filmstrip_frame_interval_sec.max(1e-3);
    let mut tx = (vis_l / tile).floor() * tile;
    while tx < vis_r {
        let pixels = match strip {
            Some(tiles) if !tiles.is_empty() => {
                // the file's time at the middle of this tile
                let local = (f64::from((tx + tile / 2.0) / pps)).min(clip.dur_sec);
                let src = clip_time::source_at(clip, local) - asset.source_start_sec.max(0.0);
                let i = (src / interval).floor().max(0.0) as usize;
                tiles.get(i.min(tiles.len() - 1))
            }
            _ => thumb,
        };
        if let Some(p) = pixels {
            c = c
                .child(el().absolute(tx, top).w(tile).h(h).pixels(p).inert())
                .child(el().absolute(tx + tile - 1.0, top).w(1.0).h(h).fill(hex("#46000000")).inert());
        }
        tx += tile;
    }
    c
}

/// The waveform: the loudest peak under each column, mirrored around the middle.
#[allow(clippy::too_many_arguments)]
fn waveform(clip: &Clip, asset: &MediaAsset, top: f32, h: f32, vis_l: f32, vis_r: f32, pps: f32, color: Color) -> Option<El> {
    let peaks = asset.waveform_peaks.as_ref()?;
    if peaks.is_empty() || asset.duration_sec <= 0.0 || vis_r <= vis_l {
        return None;
    }
    let per_bucket = asset.duration_sec / peaks.len() as f64;
    let step = 2.0f32;
    let mid = h / 2.0;
    let mut upper = Vec::new();
    let mut x = vis_l;
    while x <= vis_r {
        let a = clip_time::source_at(clip, f64::from(x / pps).min(clip.dur_sec));
        let b = clip_time::source_at(clip, f64::from((x + step) / pps).min(clip.dur_sec));
        let (lo, hi) = if a <= b { (a, b) } else { (b, a) };
        let i0 = ((lo / per_bucket).floor().max(0.0) as usize).min(peaks.len() - 1);
        let i1 = ((hi / per_bucket).ceil().max(0.0) as usize).clamp(i0 + 1, peaks.len());
        let peak = peaks[i0..i1].iter().copied().fold(0.0f32, f32::max).min(1.0);
        let gain = clip.volume.clamp(0.0, 2.0) as f32;
        upper.push([x - vis_l, mid - (peak * gain).min(1.0) * (mid - 1.0)]);
        x += step;
    }
    let mut points = upper.clone();
    for p in upper.iter().rev() {
        points.push([p[0], h - p[1]]);
    }
    Some(canvas(Shape::new().fill(points, color)).absolute(vis_l, top).w(vis_r - vis_l).h(h).inert())
}

fn ruler_view(e: &Editor, w: f32, t: &Tokens) -> El {
    let tl = &e.tl;
    let mut r = el().absolute(0.0, 0.0).w(w).h(RULER_H).fill(t.ruler_bg).child(el().absolute(0.0, RULER_H - 1.0).w(w).h(1.0).fill(hex("#34373c")));
    let fps = e.fps();
    let (major, minor, div) = ruler_scale(f64::from(tl.pps), fps);
    let first = (tl.scroll / minor).floor() * minor;
    let end = tl.scroll + tl.visible_secs() + major;
    let mut s = first;
    let mut i = (first / minor).round() as i64;
    while s <= end {
        let x = tl.x_of(s);
        if x >= HEADER_W {
            let is_major = i % i64::from(div) == 0;
            let is_mid = div % 2 == 0 && i % i64::from(div) == i64::from(div / 2);
            let (len, color) = if is_major { (12.0, hex("#8b9098")) } else if is_mid { (8.0, hex("#5a5f67")) } else { (5.0, hex("#40444b")) };
            if minor * f64::from(tl.pps) >= 5.0 || is_major {
                r = r.child(el().key(format!("tick.{i}")).absolute(x, RULER_H - len).w(1.0).h(len).fill(color));
            }
            if is_major {
                r = r.child(text(ruler_label(s, major, fps)).key(format!("label.{i}")).font(mono(10.0, 400.0)).color(t.text_muted).absolute(x + 4.0, MARKER_LANE_H + 2.0));
            }
        }
        s += minor;
        i += 1;
    }
    // timeline markers in the top lane
    for m in &e.doc().markers {
        let x = tl.x_of(m.start_sec);
        let color = if m.color.is_empty() { t.marker_default } else { hex(&m.color) };
        let hot = e.ed.selection.is_marker_selected(&m.id);
        if m.is_range() {
            let xe = tl.x_of(m.end_sec());
            let bw = (xe - x).max(4.0);
            r = r.child(
                el().key(format!("mk.{}", m.id)).absolute(x, 2.0)
                    .w(bw)
                    .h(16.0)
                    .radius(3.0)
                    .fill(color.alpha(0.35))
                    .border(if hot { 1.5 } else { 1.0 }, color)
                    .child(text(m.name.clone()).font(font(9.5, if hot { 700.0 } else { 500.0 })).color(Color::WHITE).truncate().absolute(4.0, 1.5).max_w((bw - 8.0).max(0.0))),
            );
        } else if x >= HEADER_W - 10.0 {
            r = r
                .child(el().key(format!("mk.{}", m.id)).absolute(x, 2.0).w(1.0).h(MARKER_LANE_H - 2.0).fill(color))
                .child(canvas(Shape::new().fill(vec![[0.0, 0.0], [10.0, 5.0], [0.0, 10.0]], color)).key(format!("mkflag.{}", m.id)).absolute(x + 1.0, 2.0).w(10.0).h(10.0))
                .child(text(m.name.clone()).key(format!("mkname.{}", m.id)).font(font(9.5, if hot { 700.0 } else { 500.0 })).color(color).absolute(x + 13.0, 3.0));
        }
    }
    // beat preview: dashed blue poles (not yet applied)
    if let Some(preview) = e.ed.beat_preview()
        && let Some(clip) = e.ed.find_clip(&preview.clip_id)
    {
        for local in &preview.local_times {
            let x = tl.x_of(clip.start_sec + local);
            r = r.child(el().key(format!("beat.{x}")).absolute(x, 2.0).w(1.0).h(RULER_H - 2.0).fill(hex("#5ac8fa")));
        }
    }
    r.child(el().key("cover").absolute(0.0, 0.0).w(HEADER_W).h(RULER_H).fill(t.ruler_bg))
        .child(el().key("coverrule").absolute(HEADER_W - 1.0, 0.0).w(1.0).h(RULER_H).fill(hex("#34373c")))
        .child(text("").key("timecode").live_text(&e.live_time).font(mono(11.0, 500.0)).color(t.text_secondary).absolute(10.0, 14.0).w(120.0))
}

/// The bars of one owner's rack (`DrawRack`): `x0` is its origin on screen, `top` its first row (canvas coordinates).
#[allow(clippy::too_many_arguments)]
fn rack_bars(e: &Editor, owner: super::rack::Owner, x0: f32, top: f32, open: bool, w: f32, t: &Tokens) -> Vec<El> {
    use super::rack;
    let tl = &e.tl;
    let mut out = Vec::new();
    for item in rack::items(owner) {
        let r = rack::bar_rect(&item, x0, top, tl.pps, open);
        if r[0] + r[2] < HEADER_W || r[0] > w || r[1] + r[3] < RULER_H {
            continue;
        }
        let alpha = if item.enabled { 1.0 } else { 0.43 };
        let edge = hex(item.edge).alpha(alpha);
        if r[2] < DETAIL_W {
            // a sliver of a bar: its colour only
            out.push(el().absolute(r[0], r[1]).w(r[2]).h(r[3]).fill(edge).inert());
            continue;
        }
        let selected = item.effect.as_deref().is_some_and(|id| e.ed.selection.is_effect_selected(id));
        let mut bar = el()
            .absolute(r[0], r[1])
            .w(r[2])
            .h(r[3])
            .radius(3.0)
            .fill(hex(item.fill).alpha(alpha))
            .border(if selected { 1.5 } else { 1.0 }, if selected { Color::WHITE } else { edge })
            .inert();
        // keyframes: diamonds on an open bar's key lane, joined by a line; small ticks on a folded one
        if !item.keys.is_empty() && item.effect.is_some() {
            let ky = if open { rack::LABEL_ROW + (rack::BAR_FULL - rack::LABEL_ROW) / 2.0 } else { r[3] / 2.0 };
            let picked = e.ed.selection.keyframe().filter(|k| k.host.effect_id == item.effect).map(|k| k.time_sec);
            let lx = |k: f64| x0 + k as f32 * tl.pps - r[0];
            if open && item.keys.len() > 1 {
                let (a, b) = (lx(item.keys[0]), lx(item.keys[item.keys.len() - 1]));
                bar = bar.child(el().absolute(a, ky - 0.6).w((b - a).max(1.0)).h(1.2).fill(edge.alpha(0.5)));
            }
            let size = if open { 5.0 } else { 3.0 };
            for &k in &item.keys {
                let kx = lx(k);
                if kx < -6.0 || kx > r[2] + 6.0 {
                    continue;
                }
                let hot = picked.is_some_and(|p| (p - k).abs() < 1e-4);
                let d = vec![[size, 0.0], [2.0 * size, size], [size, 2.0 * size], [0.0, size]];
                bar = bar.child(
                    canvas(Shape::new().fill(d.clone(), if hot { Color::WHITE } else { hex(item.edge) }).stroke(
                        vec![[size, 0.0], [2.0 * size, size], [size, 2.0 * size], [0.0, size], [size, 0.0]],
                        1.0,
                        hex("#c8000000"),
                    ))
                    .absolute(kx - size, ky - size)
                    .w(2.0 * size)
                    .h(2.0 * size),
                );
            }
        }
        if open && r[2] >= MIN_LABEL_W {
            let vis_l = (HEADER_W - r[0]).max(0.0);
            let buttons = r[2] >= rack::BUTTONS_W && item.effect.is_some();
            let room = (r[2] - vis_l - if buttons { 72.0 } else { 14.0 }).max(0.0);
            bar = bar.child(text(item.label.clone()).font(font(10.0, 500.0)).color(Color::WHITE).truncate().absolute(vis_l + 8.0, 3.5).max_w(room));
            if buttons {
                let animated = !item.keys.is_empty();
                let [a, p, x] = rack::buttons(r);
                let at = |b: [f32; 4]| (b[0] - r[0], b[1] - r[1]);
                let (ax, ay) = at(a);
                bar = bar.child(
                    el().absolute(ax - 1.0, ay - 1.0)
                        .w(18.0)
                        .h(18.0)
                        .radius(3.0)
                        .fill(if animated { Color::WHITE.alpha(0.27) } else { Color::TRANSPARENT })
                        .center()
                        .child(icon(&icons::TIMER, 12.0).color(if animated { Color::WHITE } else { Color::WHITE.alpha(0.6) })),
                );
                for (b, glyph) in [(p, &icons::POWER), (x, &icons::X)] {
                    let (bx, by) = at(b);
                    bar = bar.child(el().absolute(bx, by).w(16.0).h(16.0).center().child(icon(glyph, 12.0).color(Color::WHITE)));
                }
            }
        }
        let _ = t;
        out.push(bar);
    }
    out
}

/// The speed line of a clip with a speed curve (`DrawSpeedCurve`), over its picture: up is faster on a log scale, 1×
/// dashed in the middle; where a fast curve runs out of footage the picture holds its last frame, shaded.
fn speed_line(e: &Editor, clip: &Clip, lane: &Lane, y: f32, vis_l: f32, vis_r: f32) -> Option<El> {
    let keys = clip_time::curve(clip)?;
    let tl = &e.tl;
    let (band_top, band_h) = speed_band(lane);
    // band relative to the clip element
    let band = (band_top - lane.clip_top(), band_h);
    let (pic_top, pic_h) = (lane.label, lane.body - lane.strip);
    let w = vis_r - vis_l;
    if w < 2.0 {
        return None;
    }
    let mut layer = el().absolute(vis_l, pic_top).w(w).h(pic_h).inert();
    let rel = |p: [f32; 2]| [p[0] - vis_l, p[1] - pic_top];
    if let Some(asset) = clip.media_id().and_then(|id| e.find_media(id))
        && asset.duration_sec > 0.0
        && clip_time::source_at(clip, clip.dur_sec) > asset.duration_sec + 1e-3
    {
        let runs_out = (clip_time::local_at(clip, asset.duration_sec) as f32 * tl.pps - vis_l).max(0.0);
        if runs_out < w {
            let held = el().absolute(runs_out, 0.0).w(w - runs_out).h(pic_h).fill(hex("#78000000"));
            let held = if w - runs_out > 70.0 {
                held.child(text("out of footage").font(font(10.0, 400.0)).color(Color::WHITE).absolute(6.0, pic_h - 16.0))
            } else {
                held
            };
            layer = layer.child(held);
        }
    }
    let unity = speed_to_y(band, 1.0) - pic_top;
    let mut dash = 0.0;
    while dash < w {
        layer = layer.child(el().absolute(dash, unity).w(4.0f32.min(w - dash)).h(1.0).fill(hex("#46ffd166")));
        dash += 8.0;
    }
    let mut points = Vec::new();
    let mut x = vis_l.floor();
    while x <= vis_r + 2.0 {
        let local = f64::from(x / tl.pps).min(clip.dur_sec);
        points.push(rel([x, speed_to_y(band, clip_time::speed_at(clip, local))]));
        x += 2.0;
    }
    layer = layer.child(canvas(Shape::new().stroke(points, 1.8, hex("#ffd166"))).absolute(0.0, 0.0).w(w).h(pic_h));
    let picked = e.ed.selection.keyframe().filter(|k| k.host.lane.is_some() && k.host.owner_id == clip.id).map(|k| k.time_sec);
    for k in keys.iter().filter(|k| k.time_sec <= clip.dur_sec + 1e-6) {
        let [kx, ky] = rel([k.time_sec as f32 * tl.pps, speed_to_y(band, k.value.as_number())]);
        let hot = picked.is_some_and(|p| (p - k.time_sec).abs() < 1e-4);
        let r = if hot { 5.5 } else { 4.5 };
        layer = layer.child(el().absolute(kx - r, ky - r).w(2.0 * r).h(2.0 * r).radius(r).fill(if hot { Color::WHITE } else { hex("#ffd166") }).border(1.0, Color::BLACK));
        if hot {
            let easing = super::menu::EASINGS.iter().find(|(e, _)| *e == k.easing).map_or("", |(_, n)| n);
            let label = if k.easing == fig_timeline::KeyframeEasing::Linear {
                format!("{:.2}×", k.value.as_number())
            } else {
                format!("{:.2}×  {}", k.value.as_number(), easing.to_lowercase())
            };
            layer = layer.child(
                el().absolute(kx + 8.0, if ky > 24.0 { ky - 22.0 } else { ky + 8.0 })
                    .px(4.0)
                    .py(1.0)
                    .radius(3.0)
                    .fill(hex("#c8141418"))
                    .child(text(label).font(font(10.0, 400.0)).color(Color::WHITE)),
            );
        }
    }
    let _ = y;
    Some(layer)
}
