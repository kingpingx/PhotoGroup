using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using PhotoGrouper.App.ViewModels;

namespace PhotoGrouper.App.Views;

/// <summary>
/// Draws face boxes and landmarks over a photo.
/// </summary>
/// <remarks>
/// Custom-drawn rather than assembled from positioned controls. The marks have no interaction and
/// there may be dozens of them, so a control tree would cost layout passes for no benefit.
///
/// The landmarks are drawn in distinguishable colours in canonical order, so a mirrored ordering
/// is visible at a glance rather than needing to be reasoned about: the left eye marker should
/// sit on the eye that appears on the left of the picture.
/// </remarks>
public sealed class FaceOverlayControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<FaceMark>?> MarksProperty =
        AvaloniaProperty.Register<FaceOverlayControl, IReadOnlyList<FaceMark>?>(nameof(Marks));

    public static readonly StyledProperty<Size> SourceSizeProperty =
        AvaloniaProperty.Register<FaceOverlayControl, Size>(nameof(SourceSize));

    private static readonly IBrush BoxBrush = Brushes.Lime;
    private static readonly IBrush LabelBrush = Brushes.Lime;

    /// <summary>Landmark colours in canonical order: left eye, right eye, nose, mouth corners.</summary>
    private static readonly IBrush[] LandmarkBrushes =
    [
        Brushes.Red,        // left eye, as the viewer sees it
        Brushes.DeepSkyBlue, // right eye
        Brushes.Yellow,     // nose
        Brushes.Magenta,    // mouth left
        Brushes.Orange,     // mouth right
    ];

    static FaceOverlayControl()
    {
        AffectsRender<FaceOverlayControl>(MarksProperty, SourceSizeProperty);
    }

    public IReadOnlyList<FaceMark>? Marks
    {
        get => GetValue(MarksProperty);
        set => SetValue(MarksProperty, value);
    }

    /// <summary>Size of the image the marks were computed against.</summary>
    public Size SourceSize
    {
        get => GetValue(SourceSizeProperty);
        set => SetValue(SourceSizeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Marks is not { Count: > 0 } marks || SourceSize.Width <= 0 || SourceSize.Height <= 0)
        {
            return;
        }

        // The image is displayed with uniform scaling inside this control, so the marks have to
        // follow the same fit or they drift away from the faces as the window is resized.
        var scale = Math.Min(Bounds.Width / SourceSize.Width, Bounds.Height / SourceSize.Height);
        var offsetX = (Bounds.Width - (SourceSize.Width * scale)) / 2;
        var offsetY = (Bounds.Height - (SourceSize.Height * scale)) / 2;

        var pen = new Pen(BoxBrush, 2);

        foreach (var mark in marks)
        {
            var rect = new Rect(
                offsetX + (mark.Bounds.X * scale),
                offsetY + (mark.Bounds.Y * scale),
                mark.Bounds.Width * scale,
                mark.Bounds.Height * scale);

            context.DrawRectangle(pen, rect);

            context.DrawText(
                new FormattedText(
                    mark.Label,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    Typeface.Default,
                    12,
                    LabelBrush),
                new Point(rect.X, Math.Max(0, rect.Y - 16)));

            for (var i = 0; i < mark.Landmarks.Length && i < LandmarkBrushes.Length; i++)
            {
                var point = new Point(
                    offsetX + (mark.Landmarks[i].X * scale),
                    offsetY + (mark.Landmarks[i].Y * scale));

                context.DrawEllipse(LandmarkBrushes[i], null, point, 3, 3);
            }
        }
    }
}
