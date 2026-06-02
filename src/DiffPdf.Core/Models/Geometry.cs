namespace DiffPdf.Core.Models;

/// <summary>
/// Axis-aligned rectangle in PDF user-space points (origin bottom-left) unless
/// stated otherwise by the producer.
/// </summary>
public readonly record struct RectangleD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Top => Y + Height;
}
