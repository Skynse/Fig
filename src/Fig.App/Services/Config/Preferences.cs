using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fig.App.Services
{
    /// <summary>
    /// Everything the user can set, as one typed object saved to preferences.json. A property's initial
    /// value is its default; a missing or new property in the file simply keeps that default.
    /// </summary>
    public sealed class Preferences
    {
        public int SchemaVersion { get; set; } = 1;

        public GeneralPreferences General { get; set; } = new();
        public EditingPreferences Editing { get; set; } = new();
        public PlaybackPreferences Playback { get; set; } = new();
        public ExportPreferences Export { get; set; } = new();
        public RenderingPreferences Rendering { get; set; } = new();
        public KeyboardPreferences Keyboard { get; set; } = new();
        public CaptionPreferences Captions { get; set; } = new();

        /// <summary>Keys this build doesn't know about (written by a newer one); kept so saving doesn't erase them.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Unknown { get; set; }

        /// <summary>Puts every value back in range after loading, so a hand-edited file can't break the app.</summary>
        public void Normalize()
        {
            General ??= new();
            Editing ??= new();
            Playback ??= new();
            Export ??= new();
            Rendering ??= new();
            Keyboard ??= new();
            Keyboard.Shortcuts ??= new();
            Captions ??= new();
            if (!Enum.IsDefined(Rendering.Renderer))
                Rendering.Renderer = RendererChoice.Automatic;

            General.AutosaveSeconds = Math.Clamp(General.AutosaveSeconds, 0.2, 30);
            Editing.SnapReachPx = Math.Clamp(Editing.SnapReachPx, 2, 40);
            Editing.UndoSteps = Math.Clamp(Editing.UndoSteps, 10, 1000);
            Export.Quality = Math.Clamp(Export.Quality, 0, 51);
        }
    }

    public sealed class GeneralPreferences
    {
        public double AutosaveSeconds { get; set; } = 0.8;
        public bool AskBeforeClosingUnsaved { get; set; } = true;
    }

    public sealed class EditingPreferences
    {
        /// <summary>Clips snap to edges, markers and the playhead (the Snap toggle).</summary>
        public bool Snap { get; set; } = true;
        public double SnapReachPx { get; set; } = 9;
        /// <summary>Selecting a clip selects its linked audio or video too (the Link toggle).</summary>
        public bool LinkedSelection { get; set; } = true;
        public int UndoSteps { get; set; } = 100;
    }

    public sealed class PlaybackPreferences
    {
        public bool AudioScrub { get; set; } = true;
        public bool Loop { get; set; }
    }

    public sealed class ExportPreferences
    {
        /// <summary>H.264 CRF the export dialog starts at (lower is better).</summary>
        public int Quality { get; set; } = 23;
        /// <summary>Where exports go; null means the Videos folder.</summary>
        public string? Folder { get; set; }
        /// <summary>The H.264 encoder: a GPU one when there is one (Automatic), or a named one, or the CPU.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<Fig.Core.Media.VideoEncoderChoice>))]
        public Fig.Core.Media.VideoEncoderChoice Encoder { get; set; } = Fig.Core.Media.VideoEncoderChoice.Automatic;
    }

    /// <summary>What the captions dialog last used.</summary>
    public sealed class CaptionPreferences
    {
        /// <summary>The speech model's id (<see cref="Fig.Core.Captions.SpeechModels.All"/>).</summary>
        public string Model { get; set; } = "base";
        /// <summary>The language spoken, as a Whisper code, or "auto".</summary>
        public string Language { get; set; } = "auto";
        public bool Translate { get; set; }
        /// <summary>Which of <see cref="Fig.Core.Captions.CaptionGrouping.Choices"/>.</summary>
        public int Grouping { get; set; } = 1;
        /// <summary>A style change to one caption restyles every caption on its track (the text stays its own).</summary>
        public bool StyleAll { get; set; } = true;
    }

    /// <summary>What draws the preview and export frames.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RendererChoice>))]
    public enum RendererChoice
    {
        /// <summary>The GPU when the window's canvas is GPU-backed, otherwise the CPU.</summary>
        Automatic,
        /// <summary>Always the shaders (on a software canvas they run on the CPU, slowly).</summary>
        Gpu,
        /// <summary>Always the CPU kernels.</summary>
        Cpu,
    }

    public sealed class RenderingPreferences
    {
        public RendererChoice Renderer { get; set; } = RendererChoice.Automatic;
    }

    public sealed class KeyboardPreferences
    {
        /// <summary>Only the shortcuts the user changed: action id → key gesture ("" means no shortcut).</summary>
        public Dictionary<string, string> Shortcuts { get; set; } = new();
    }
}
