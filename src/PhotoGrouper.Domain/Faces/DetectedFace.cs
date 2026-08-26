namespace PhotoGrouper.Domain.Faces;

/// <summary>What a detector reports about one face, before anything is stored.</summary>
public readonly record struct DetectedFace(FaceBox Box, FaceLandmarks Landmarks);
