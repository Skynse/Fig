//! Auto captions: the dialog (`CaptionsDialog.axaml`, inventory §8.3) and the caption list.
//!
//! Whisper lives in `fig-captions` (model download, transcription of the mix). Without a model the
//! dialog still lays out and the list shows the project's caption/text clips, which is what the
//! editor itself edits (`TimelineEditor.SetText`/`SplitCaption`/`MergeCaptionWithNext`).

use rune::{El, col, el, icon, row, text};

use crate::editor_state::EditorState;
use crate::icons;
use crate::theme::{font, mono, Tokens, RADIUS_CONTROL};

pub fn dialog(editor: Option<&EditorState>, t: &Tokens) -> El {
    let mut body = col().w_full().gap(10.0).child(
        row()
            .w_full()
            .h(30.0)
            .center_y()
            .gap(8.0)
            .child(crate::widgets::chip("Small · 190 MB", t))
            .child(crate::widgets::chip("Detect language", t))
            .child(crate::widgets::chip("Whole sequence", t))
            .child(el().grow(1.0))
            .child(
                el().h(28.0)
                    .padding(10.0)
                    .radius(RADIUS_CONTROL)
                    .fill(t.accent_soft)
                    .center()
                    .child(text("Transcribe").font(font(11.0, 600.0)).color(t.text_primary)),
            ),
    );

    let mut list = col().w_full().grow(1.0).min_h(0.0).gap(2.0);
    let mut found = 0;
    if let Some(editor) = editor {
        for track in &editor.doc().tracks {
            for clip in &track.clips {
                if let fig_timeline::ClipBody::Text(text_body) = &clip.body {
                    found += 1;
                    if found > 6 {
                        break;
                    }
                    list = list.child(caption_row(clip.start_sec, clip.end_sec(), &text_body.text, found == 1, t));
                }
            }
        }
    }
    if found == 0 {
        list = list.child(
            col()
                .w_full()
                .grow(1.0)
                .center()
                .gap(10.0)
                .child(icon(&icons::CAPTIONS, 28.0).color(t.text_faint))
                .child(text("No captions in this sequence").font(font(12.0, 400.0)).color(t.text_muted))
                .child(
                    text("Auto captions transcribe the mix and add timed text clips.")
                        .font(font(11.0, 400.0))
                        .color(t.text_faint),
                ),
        );
    }

    body = body.child(
        el().w_full()
            .grow(1.0)
            .min_h(0.0)
            .radius(RADIUS_CONTROL)
            .fill(t.surface_bg)
            .padding(8.0)
            .child(list),
    );
    body.child(
        text("Speech models download on first use to ~/.fig/models/whisper.")
            .font(font(11.0, 400.0))
            .color(t.text_faint),
    )
}

fn caption_row(start: f64, end: f64, caption: &str, selected: bool, t: &Tokens) -> El {
    let mut row_el = row()
        .w_full()
        .h(34.0)
        .radius(6.0)
        .fill(if selected { t.card_bg_alt } else { t.surface_bg })
        .padding(8.0)
        .center_y()
        .child(
            text(format!("{} → {}", crate::editor_state::format_timecode(start), crate::editor_state::format_timecode(end)))
                .font(mono(10.0, 400.0))
                .color(t.text_faint),
        )
        .child(el().w(12.0))
        .child(text(caption.to_string()).font(font(12.0, 400.0)).color(t.text_secondary).truncate());
    if selected {
        row_el = row_el.border(1.0, t.accent_soft);
    }
    row_el
}
