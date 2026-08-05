using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Fig.App.Services;

namespace Fig.App.Views
{
    /// <summary>
    /// Renders a stroke-based SVG icon (stroke="currentColor", fill="none") the correct way.
    /// <see cref="PathIcon"/> fills geometries, which turns line icons into solid blobs.
    /// This control strokes them, matching how the timeline draws them.
    /// </summary>
    public class StrokeIcon : Control
    {
        public static readonly StyledProperty<string> IconProperty =
            AvaloniaProperty.Register<StrokeIcon, string>(nameof(Icon));

        public string Icon
        {
            get => GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly StyledProperty<IBrush> IconBrushProperty =
            AvaloniaProperty.Register<StrokeIcon, IBrush>(nameof(IconBrush), Brushes.White);

        public IBrush IconBrush
        {
            get => GetValue(IconBrushProperty);
            set => SetValue(IconBrushProperty, value);
        }

        public static readonly StyledProperty<double> StrokeWidthProperty =
            AvaloniaProperty.Register<StrokeIcon, double>(nameof(StrokeWidth), 1.5);

        public double StrokeWidth
        {
            get => GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        public StrokeIcon()
        {
            ClipToBounds = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IconProperty || change.Property == IconBrushProperty || change.Property == StrokeWidthProperty)
                InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var icon = Icon;
            if (string.IsNullOrEmpty(icon))
                return;
            IconService.DrawStroked(context, icon, new Rect(0, 0, Bounds.Width, Bounds.Height), IconBrush, StrokeWidth);
        }
    }
}
