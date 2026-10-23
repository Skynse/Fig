using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fig.App.Services;
using Fig.App.ViewModels;
using Fig.Core.Input;
using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.App.Views
{
    /// <summary>
    /// The timeline's colours and sizes.
    /// </summary>
    public partial class TimelineView
    {
        private static readonly IBrush SelectionBrush = EditorTheme.AccentBrush;

        private static readonly IBrush SurfaceBackground = EditorTheme.CardBrush;
        private static readonly IBrush TrackLaneBrush = EditorTheme.CardBrush;
        private static readonly IBrush TrackLaneAltBrush = new SolidColorBrush(EditorTheme.TrackLaneAlt);
        private static readonly IBrush RulerBackground = new SolidColorBrush(EditorTheme.RulerBackground);
        private static readonly IBrush BorderBrush = EditorTheme.BorderBrush;
        private static readonly IBrush RulerTextBrush = EditorTheme.TextMutedBrush;
        private static readonly IBrush SelectedLaneTintBrush = new SolidColorBrush(EditorTheme.SelectedLaneTint);
        private static readonly IBrush DeleteHoverBrush = new SolidColorBrush(Color.Parse("#5a1f1f"));

        // Clip fills
        private static readonly IBrush VideoBrush = new SolidColorBrush(Color.Parse("#1679db"));
        private static readonly IBrush AudioBrush = new SolidColorBrush(Color.Parse("#2d674a"));
        private static readonly IBrush AudioWaveformBrush = new SolidColorBrush(Color.Parse("#8bc9a3"));
        private static readonly IBrush TextClipBrush = new SolidColorBrush(Color.Parse("#a84f7d"));
        private static readonly IBrush ImageClipBrush = new SolidColorBrush(Color.Parse("#5e57a8"));

        private static readonly IBrush VideoBorder = new SolidColorBrush(Color.Parse("#55a2b2"));
        private static readonly IBrush AudioBorder = new SolidColorBrush(Color.Parse("#589b74"));
        private static readonly IBrush TextBorder = new SolidColorBrush(Color.Parse("#d17aa6"));
        private static readonly IBrush ImageBorder = new SolidColorBrush(Color.Parse("#8b84d6"));

        private static readonly IBrush ClipShadow = new SolidColorBrush(Color.Parse("#00000060"));
        private static readonly IBrush TrackHeaderTextBrush = EditorTheme.TextMutedBrush;
        private static readonly IBrush HeaderIconDimBrush = new SolidColorBrush(Color.Parse("#6a6a6a"));
        private static readonly IBrush TrackHeaderBrush = new SolidColorBrush(Color.Parse("#191b1e"));
        private static readonly IBrush ClipSelectionBrush = new SolidColorBrush(Color.Parse("#d9e0e7"));

        // transitions
        private static readonly IBrush TransitionBandBrush = new SolidColorBrush(Color.FromArgb(70, 0xe0, 0xa3, 0x08));
        private static readonly IBrush TransitionBandSelectedBrush = new SolidColorBrush(Color.FromArgb(120, 0xea, 0xb3, 0x08));
        private static readonly IBrush TransitionBadgeBrush = new SolidColorBrush(Color.FromArgb(235, 0xe0, 0xa3, 0x08));

        /// <summary>Top strip of the ruler area: range bars and bookmark flags.</summary>
        private const double MarkerLaneHeight = 20;
        private const double PlayheadCapHeight = 8;
        private const double TickRulerHeight = 22;
        private const double RulerHeight = MarkerLaneHeight + TickRulerHeight;
        private const double ClipCornerRadius = 6;
        /// <summary>Gap each side of a cut between clips that butt together, so both keep their rounded ends.</summary>
        private const double SplitGap = 1;
        internal const double TrackHeaderWidth = 140;
        private const double ZoomFactor = 1.25;
        private const double TrackScaleStep = 1.2;
        /// <summary>Below this track height scale the effect racks fold to thin bars on their own.</summary>
        private const double RackFoldScale = 0.75;
        private const double HeaderButtonSize = 22;
        private const double HeaderButtonGap = 4;
        private const double HeaderPaddingLeft = 7;
        private const double TrackAccentBarWidth = 2;
        private const double EmptyTrackHeight = 34;
    }
}
