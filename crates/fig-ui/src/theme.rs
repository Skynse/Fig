//! Fig's design tokens, copied 1:1 from the Avalonia app.
//!
//! Sources: `src/Fig.App/Styles/FigTheme.axaml`, `Styles/FigControls.axaml`, and
//! `src/Fig.App/Services/EditorTheme.cs` (the colours the custom-drawn controls use). The C# app
//! names every colour and radius once; this module is the Rust equivalent, so views never spell a
//! hex value. See `docs/ui-port/inventory.md` §9.

use rune::{Color, TextStyle};

/// A colour from the C# theme: `#rrggbb`, or `#aarrggbb` with the alpha first as Avalonia writes it. Panics on a
/// malformed literal (tokens are compile-time data).
pub fn hex(s: &str) -> Color {
    let fixed;
    let s = if s.len() == 9 && s.starts_with('#') {
        fixed = format!("#{}{}", &s[3..], &s[1..3]);
        fixed.as_str()
    } else {
        s
    };
    rune::color::hex(s).unwrap_or_else(|| panic!("bad colour token: {s}"))
}

/// The app-wide palette (`FigTheme.axaml`) plus the timeline's own brushes (`EditorTheme.cs`).
#[derive(Clone, Copy)]
pub struct Tokens {
    // surfaces, darkest to lightest
    pub surface_bg: Color,
    pub panel_bg: Color,
    pub card_bg: Color,
    pub card_bg_alt: Color,
    pub chip_bg: Color,
    pub hover_bg: Color,
    pub pressed_bg: Color,

    // outlines and text
    pub border: Color,
    pub text_primary: Color,
    pub text_secondary: Color,
    pub text_muted: Color,
    pub text_faint: Color,

    // accent
    pub accent: Color,
    pub accent_soft: Color,
    pub accent_softer: Color,
    pub accent_text: Color,

    // meaning
    pub danger: Color,
    pub warn: Color,
    pub on_accent: Color,

    // media wells and the preview
    pub preview_well: Color,

    // timeline (EditorTheme.cs)
    pub track_lane: Color,
    pub track_lane_alt: Color,
    pub track_header_bg: Color,
    pub ruler_bg: Color,
    pub selected_lane_tint: Color,

    pub video_clip: Color,
    pub video_border: Color,
    pub audio_clip: Color,
    pub audio_border: Color,
    pub audio_wave: Color,
    pub text_clip: Color,
    pub text_border: Color,
    pub image_clip: Color,
    pub image_border: Color,
    pub effect_clip: Color,
    pub effect_border: Color,

    pub playhead: Color,
    pub selection: Color,
    pub snap: Color,
    pub marker_default: Color,
    pub track_accent: Color,
}

impl Tokens {
    pub fn dark() -> Tokens {
        Tokens {
            surface_bg: hex("#131417"),
            panel_bg: hex("#1a1b1f"),
            card_bg: hex("#212328"),
            card_bg_alt: hex("#272a30"),
            chip_bg: hex("#2a2d34"),
            hover_bg: hex("#30343b"),
            pressed_bg: hex("#383c44"),

            border: hex("#2d3037"),
            text_primary: hex("#ececef"),
            text_secondary: hex("#b8bbc4"),
            text_muted: hex("#868a95"),
            text_faint: hex("#5d616b"),

            accent: hex("#6ea5ff"),
            accent_soft: hex("#2a3d61"),
            accent_softer: hex("#222f48"),
            accent_text: hex("#9cc0ff"),

            danger: hex("#ff7a7a"),
            warn: hex("#e8b53a"),
            on_accent: hex("#0e1626"),

            preview_well: hex("#0b0c0e"),

            track_lane: hex("#202226"),
            track_lane_alt: hex("#1d1f22"),
            track_header_bg: hex("#18191c"),
            ruler_bg: hex("#181a1d"),
            selected_lane_tint: hex("#147b9fc6"),

            video_clip: hex("#1679db"),
            video_border: hex("#55a2b2"),
            audio_clip: hex("#2d674a"),
            audio_border: hex("#589b74"),
            audio_wave: hex("#8bc9a3"),
            text_clip: hex("#a84f7d"),
            text_border: hex("#d17aa6"),
            image_clip: hex("#5e57a8"),
            image_border: hex("#8b84d6"),
            effect_clip: hex("#c79a3d"),
            effect_border: hex("#e0bd6e"),

            playhead: hex("#f0645a"),
            selection: hex("#d9e0e7"),
            snap: hex("#ffd60a"),
            marker_default: hex("#ffd60a"),
            track_accent: hex("#5795df"),
        }
    }
}

/// Inter, the C# app's UI font (`WithInterFont()`); Rune bundles Inter as its sans family.
pub fn font(size: f32, weight: f32) -> TextStyle {
    TextStyle::new(size, weight)
}

/// The system monospace family, as the C# timecodes use.
pub fn mono(size: f32, weight: f32) -> TextStyle {
    TextStyle::new(size, weight).mono()
}

// ---- shell metrics (MainWindow.axaml / TitleBar.axaml) ----
pub const TITLEBAR_H: f32 = 46.0;
pub const WINDOW_MIN_W: f32 = 1100.0;
pub const WINDOW_MIN_H: f32 = 700.0;
pub const WINDOW_PAD: f32 = 8.0;
pub const STATUS_H: f32 = 20.0;
pub const SPLITTER: f32 = 8.0;

// ---- shape tokens (FigTheme.axaml) ----
pub const RADIUS_PANEL: f32 = 14.0;
pub const RADIUS_CARD: f32 = 10.0;
pub const RADIUS_CONTROL: f32 = 8.0;
pub const RADIUS_MENU: f32 = 12.0;
pub const RADIUS_CLIP: f32 = 6.0;
