using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ShortDrama.Infrastructure.Files;

internal static class PosterCoverDifference
{
    private const int SampleSize = 160;
    private const int TileSize = 10;
    private const int Margin = 16;
    private const float MinimumEdgeEnergy = 8f;
    private const float MinimumEdgeAgreement = 0.46f;

    // Input rectangles use relative coordinates in their respective images.
    internal static double Measure(
        Image<Rgba32> original,
        Image<Rgba32> candidate,
        RectangleF? originalTitle = null,
        RectangleF? candidateTitle = null,
        CancellationToken cancellationToken = default)
    {
        var originalEdges = GetEdges(original);
        var candidateEdges = GetEdges(candidate);
        var difference = MeasureBestAlignment(originalEdges, candidateEdges, originalTitle, candidateTitle, cancellationToken);
        var aspectRatio = (candidate.Width / (float)candidate.Height) / (original.Width / (float)original.Height);
        if (difference == 0 || MathF.Abs(aspectRatio - 1f) < 0.03f)
            return difference;

        // Reframing to a different configured aspect ratio can be a center crop.
        // Compare that crop as well so choosing a different aspect alone cannot pass.
        var cropWidth = Math.Min(1f, aspectRatio);
        var cropHeight = Math.Min(1f, 1f / aspectRatio);
        var crop = new RectangleF((1 - cropWidth) / 2, (1 - cropHeight) / 2, cropWidth, cropHeight);
        RectangleF? croppedTitle = originalTitle is { } title
            ? new RectangleF((title.X - crop.X) / crop.Width, (title.Y - crop.Y) / crop.Height,
                title.Width / crop.Width, title.Height / crop.Height)
            : null;
        return Math.Min(difference, MeasureBestAlignment(GetEdges(original, crop), candidateEdges,
            croppedTitle, candidateTitle, cancellationToken));
    }

    private static double MeasureBestAlignment(Vector2[] originalEdges, Vector2[] candidateEdges,
        RectangleF? originalTitle, RectangleF? candidateTitle, CancellationToken cancellationToken)
    {
        // Small framing changes can move every edge. Test a limited set of global
        // alignments and keep the lowest measured difference over their shared area.
        float[] scales = [1f, 0.94f, 1.06f, 0.88f, 1.12f];
        int[] offsets = [0, -4, 4, -8, 8];
        var minimum = 1.0;
        foreach (var scale in scales)
        {
            foreach (var offsetY in offsets)
            {
                foreach (var offsetX in offsets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var difference = MeasureAlignment(originalEdges, candidateEdges,
                        originalTitle, candidateTitle, scale, offsetX, offsetY);
                    minimum = Math.Min(minimum, difference);
                    if (minimum == 0)
                        return 0;
                }
            }
        }

        return minimum;
    }

    private static Vector2[] GetEdges(Image<Rgba32> source, RectangleF? window = null)
    {
        using var sample = source.Clone(context =>
        {
            context.AutoOrient();
            if (window is { } crop)
            {
                var size = context.GetCurrentSize();
                var width = Math.Clamp((int)MathF.Round(crop.Width * size.Width), 1, size.Width);
                var height = Math.Clamp((int)MathF.Round(crop.Height * size.Height), 1, size.Height);
                context.Crop(new Rectangle((size.Width - width) / 2, (size.Height - height) / 2, width, height));
            }
            context.Resize(SampleSize, SampleSize, KnownResamplers.Bicubic);
        });
        using var grayscale = sample.CloneAs<L8>();
        var light = new L8[SampleSize * SampleSize];
        grayscale.CopyPixelDataTo(light);

        var edges = new Vector2[light.Length];
        for (var y = 2; y < SampleSize - 2; y++)
        {
            for (var x = 2; x < SampleSize - 2; x++)
            {
                var index = y * SampleSize + x;
                edges[index] = new(light[index + 2].PackedValue - light[index - 2].PackedValue,
                    light[index + 2 * SampleSize].PackedValue - light[index - 2 * SampleSize].PackedValue);
            }
        }

        return edges;
    }

    private static double MeasureAlignment(
        Vector2[] original,
        Vector2[] candidate,
        RectangleF? originalTitle,
        RectangleF? candidateTitle,
        float scale,
        int offsetX,
        int offsetY)
    {
        var changedArea = 0;
        var evaluatedArea = 0;
        var candidateStructureArea = 0;
        for (var top = Margin; top <= SampleSize - Margin - TileSize; top += TileSize)
        {
            for (var left = Margin; left <= SampleSize - Margin - TileSize; left += TileSize)
            {
                var count = 0;
                var agreement = 0f;
                var originalEnergy = 0f;
                var candidateEnergy = 0f;
                for (var y = top; y < top + TileSize; y++)
                {
                    var otherY = Align(y, scale, offsetY);
                    for (var x = left; x < left + TileSize; x++)
                    {
                        var otherX = Align(x, scale, offsetX);
                        if (IsExcluded(x, y, originalTitle) || IsExcluded(otherX, otherY, candidateTitle))
                            continue;

                        var originalEdge = original[y * SampleSize + x];
                        var candidateEdge = candidate[otherY * SampleSize + otherX];
                        count++;
                        agreement += Vector2.Dot(originalEdge, candidateEdge);
                        originalEnergy += originalEdge.LengthSquared();
                        candidateEnergy += candidateEdge.LengthSquared();
                    }
                }

                if (count < TileSize * TileSize / 2)
                    continue;

                evaluatedArea += count;
                var energyThreshold = count * MinimumEdgeEnergy * MinimumEdgeEnergy;
                if (candidateEnergy >= energyThreshold)
                    candidateStructureArea += count;
                if (Math.Max(originalEnergy, candidateEnergy) >= energyThreshold &&
                    agreement < MathF.Sqrt(originalEnergy * candidateEnergy) * MinimumEdgeAgreement)
                    changedArea += count;
                else if (Math.Min(originalEnergy, candidateEnergy) < energyThreshold &&
                         Math.Max(originalEnergy, candidateEnergy) >= 4 * energyThreshold)
                    changedArea += count;
            }
        }

        // Avoid accepting a blank or almost blank response as an entirely new composition.
        return evaluatedArea > 0 && candidateStructureArea >= evaluatedArea / 4.0
            ? (double)changedArea / evaluatedArea
            : 0;
    }

    private static int Align(int coordinate, float scale, int offset) =>
        (int)MathF.Round((coordinate - SampleSize / 2f) * scale + SampleSize / 2f + offset);

    private static bool IsExcluded(int x, int y, RectangleF? title)
    {
        if (x < Margin || x >= SampleSize - Margin || y < Margin || y >= SampleSize - Margin)
            return true;

        var relativeY = y / (float)SampleSize;
        if (title is not { } box)
            return relativeY >= 0.60f;

        // Include a little padding for outlines. The bottom margin also excludes
        // logos and attribution commonly placed along the lower edge.
        var relativeX = x / (float)SampleSize;
        return relativeX >= box.Left - 0.025f && relativeX <= box.Right + 0.025f &&
               relativeY >= box.Top - 0.025f && relativeY <= box.Bottom + 0.025f;
    }
}
