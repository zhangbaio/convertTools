using System.Reflection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace TikTokPublisher.Core.Services;

/// <summary>
/// 在固定的角色工作台截图模板上替换人物和成片图片。除图片槽位外，模板像素保持不变。
/// </summary>
internal static class RoleVectorTemplateRenderer
{
    internal const int CanvasWidth = 2342;
    internal const int CanvasHeight = 1280;
    private const string TemplateResourceName =
        "TikTokPublisher.Core.Resources.RoleVectorTemplate.png";

    private static readonly Rgba32 EmptySlotColor = new(14, 15, 17, 255);

    internal static IReadOnlyList<RoleVectorGroup> Groups { get; } =
    [
        new(
            CharacterSlots:
            [
                new Rectangle(191, 172, 89, 135),
                new Rectangle(320, 173, 95, 134),
                new Rectangle(426, 88, 96, 135),
                new Rectangle(572, 53, 95, 135),
            ],
            ReferenceSlots:
            [
                new Rectangle(858, 238, 89, 134),
                new Rectangle(873, 399, 86, 131),
            ]),
        new(
            CharacterSlots:
            [
                new Rectangle(205, 466, 88, 177),
                new Rectangle(330, 372, 90, 181),
                new Rectangle(485, 335, 86, 174),
            ],
            ReferenceSlots: [new Rectangle(858, 584, 89, 179)]),
        new(
            CharacterSlots:
            [
                new Rectangle(205, 773, 99, 147),
                new Rectangle(334, 682, 91, 135),
                new Rectangle(485, 616, 98, 147),
            ],
            ReferenceSlots: [new Rectangle(858, 856, 107, 160)]),
        new(
            CharacterSlots:
            [
                new Rectangle(1188, 223, 120, 223),
                new Rectangle(1350, 140, 117, 215),
                new Rectangle(1573, 74, 116, 217),
            ],
            ReferenceSlots: [new Rectangle(2037, 322, 131, 242)]),
        new(
            CharacterSlots:
            [
                new Rectangle(1165, 530, 143, 229),
                new Rectangle(1375, 466, 131, 210),
                new Rectangle(1612, 454, 125, 201),
            ],
            ReferenceSlots: [new Rectangle(2037, 700, 89, 143)]),
        new(
            CharacterSlots:
            [
                new Rectangle(1170, 882, 133, 208),
                new Rectangle(1333, 790, 116, 180),
                new Rectangle(1504, 755, 121, 189),
            ],
            ReferenceSlots: [new Rectangle(2023, 882, 150, 233)]),
    ];

    internal static void Render(
        string outputPath,
        IReadOnlyList<string> characterImages,
        IReadOnlyList<string> referenceFrames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        using var canvas = LoadTemplate();
        for (var groupIndex = 0; groupIndex < Groups.Count; groupIndex++)
        {
            var group = Groups[groupIndex];
            if (groupIndex >= characterImages.Count || !File.Exists(characterImages[groupIndex]))
            {
                ClearGroup(canvas, group);
                continue;
            }

            var characterPath = characterImages[groupIndex];
            for (var slotIndex = 0; slotIndex < group.CharacterSlots.Count; slotIndex++)
                DrawImageSlot(canvas, characterPath, group.CharacterSlots[slotIndex], slotIndex);

            for (var slotIndex = 0; slotIndex < group.ReferenceSlots.Count; slotIndex++)
            {
                if (referenceFrames.Count == 0)
                {
                    ClearSlot(canvas, group.ReferenceSlots[slotIndex]);
                    continue;
                }
                var referenceIndex = (groupIndex + slotIndex) % referenceFrames.Count;
                DrawImageSlot(canvas, referenceFrames[referenceIndex], group.ReferenceSlots[slotIndex], 0);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        canvas.SaveAsPng(outputPath);
    }

    private static Image<Rgba32> LoadTemplate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(TemplateResourceName)
            ?? throw new InvalidOperationException($"未找到角色矢量图模板资源：{TemplateResourceName}");
        var image = Image.Load<Rgba32>(stream);
        if (image.Width != CanvasWidth || image.Height != CanvasHeight)
        {
            image.Dispose();
            throw new InvalidDataException(
                $"角色矢量图模板尺寸必须为 {CanvasWidth}×{CanvasHeight}。");
        }
        return image;
    }

    private static void DrawImageSlot(
        Image<Rgba32> canvas,
        string sourcePath,
        Rectangle slot,
        int variation)
    {
        try
        {
            using var source = Image.Load<Rgba32>(sourcePath);
            source.Mutate(context =>
            {
                context.Resize(new ResizeOptions
                {
                    Size = slot.Size,
                    Mode = ResizeMode.Crop,
                    Position = variation switch
                    {
                        1 => AnchorPositionMode.Left,
                        2 => AnchorPositionMode.Right,
                        _ => AnchorPositionMode.Center,
                    },
                });
                if (variation == 2) context.Flip(FlipMode.Horizontal);
            });
            canvas.Mutate(context => context.DrawImage(source, slot.Location, 1f));
        }
        catch
        {
            ClearSlot(canvas, slot);
        }
    }

    private static void ClearGroup(Image<Rgba32> canvas, RoleVectorGroup group)
    {
        foreach (var slot in group.CharacterSlots.Concat(group.ReferenceSlots))
            ClearSlot(canvas, slot);
    }

    private static void ClearSlot(Image<Rgba32> canvas, Rectangle slot) =>
        canvas.Mutate(context => context.Fill(EmptySlotColor, slot));

    internal sealed record RoleVectorGroup(
        IReadOnlyList<Rectangle> CharacterSlots,
        IReadOnlyList<Rectangle> ReferenceSlots);
}
