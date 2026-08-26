using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhotoGrouper.Application.Ports;
using PhotoGrouper.Domain.Faces;
using PhotoGrouper.Domain.Photos;

namespace PhotoGrouper.App.ViewModels;

/// <summary>
/// Draws a photo with its detected faces marked, for checking that detection is working.
/// </summary>
/// <remarks>
/// Deliberately a first-class screen rather than throwaway debugging. Almost every way this
/// pipeline can be wrong produces well-formed output: a missed orientation puts boxes at right
/// angles to the faces, a forgotten scale factor puts them in the top-left corner at a fraction
/// of their size, and a mirrored landmark order marks the wrong eye. None of those raise an
/// error, and none are visible in a list of face counts. They are all obvious the moment the
/// boxes are drawn over the photograph.
/// </remarks>
public sealed partial class FaceOverlayViewModel(IImageDecoder decoder, IFaceRepository faces) : ObservableObject
{
    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private string _caption = string.Empty;

    [ObservableProperty]
    private bool _isVisible;

    /// <summary>Where this photograph sits in the library, such as "3 of 21".</summary>
    [ObservableProperty]
    private string _position = string.Empty;

    /// <summary>Face marks in the coordinate space of the displayed bitmap.</summary>
    public List<FaceMark> Marks { get; } = [];

    public async Task ShowAsync(Photo photo, string detectorId, CancellationToken ct)
    {
        Marks.Clear();
        Image = null;

        var stored = await faces.GetByPhotoAsync(photo.Id, detectorId, ct).ConfigureAwait(true);

        // Decoded at a bounded size for display, which means the stored coordinates, which are in
        // full-resolution space, have to be brought into the same space before being drawn.
        var decoded = await decoder.DecodeAsync(photo.Path, 1200, ct).ConfigureAwait(true);
        if (decoded is null)
        {
            Caption = $"{Path.GetFileName(photo.Path)} — could not be decoded";
            IsVisible = true;
            return;
        }

        Image = ToBitmap(decoded);

        var scale = decoded.Scale;
        foreach (var face in stored)
        {
            Marks.Add(new FaceMark(
                new Rect(
                    face.Box.X * scale,
                    face.Box.Y * scale,
                    face.Box.Width * scale,
                    face.Box.Height * scale),
                face.Landmarks.Scale(scale).ToArray()
                    .Select(p => new Point(p.X, p.Y))
                    .ToArray(),
                $"{face.Box.Score:F2}"));
        }

        Caption = $"{Path.GetFileName(photo.Path)} — {stored.Count} face(s), {decoded.OriginalWidth}×{decoded.OriginalHeight}";
        IsVisible = true;
        OnPropertyChanged(nameof(Marks));
    }

    public void Hide()
    {
        IsVisible = false;
        Image = null;
        Marks.Clear();
    }

    private static Bitmap ToBitmap(DecodedImage decoded)
    {
        var buffer = decoded.Buffer;
        var pixels = buffer.Pixels.Span;

        // Avalonia wants four bytes per pixel; the decoder produces three. Converted here rather
        // than in the decoder, because this is a display concern and the pipeline's own consumers
        // want the compact form.
        var bgra = new byte[buffer.Width * buffer.Height * 4];

        for (var y = 0; y < buffer.Height; y++)
        {
            var source = y * buffer.Stride;
            var target = y * buffer.Width * 4;

            for (var x = 0; x < buffer.Width; x++)
            {
                bgra[target + (x * 4)] = pixels[source + (x * 3)];
                bgra[target + (x * 4) + 1] = pixels[source + (x * 3) + 1];
                bgra[target + (x * 4) + 2] = pixels[source + (x * 3) + 2];
                bgra[target + (x * 4) + 3] = 255;
            }
        }

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Bitmap(
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Opaque,
                handle.AddrOfPinnedObject(),
                new PixelSize(buffer.Width, buffer.Height),
                new Vector(96, 96),
                buffer.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <param name="Bounds">The face box, in displayed-image coordinates.</param>
/// <param name="Landmarks">The five points, in canonical order, in displayed-image coordinates.</param>
public sealed record FaceMark(Rect Bounds, Point[] Landmarks, string Label);
