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
    private const string DefaultTemplateResourceName =
        "TikTokPublisher.Core.Resources.RoleVectorTemplate.png";
    private const string TwoCharacterTemplateResourceName =
        "TikTokPublisher.Core.Resources.RoleVectorTemplate2.png";
    private const string ThreeCharacterTemplateResourceName =
        "TikTokPublisher.Core.Resources.RoleVectorTemplate3.png";
    private const string FourCharacterTemplateResourceName =
        "TikTokPublisher.Core.Resources.RoleVectorTemplate4.png";
    private const string FiveCharacterTemplateResourceName =
        "TikTokPublisher.Core.Resources.RoleVectorTemplate5.png";

    private static readonly Rgba32 EmptySlotColor = new(14, 15, 17, 255);

    internal static IReadOnlyList<RoleVectorGroup> TwoCharacterGroups { get; } =
    [
        new(
            CharacterSlots:
            [
                new Rectangle(676, 270, 104, 200),
                new Rectangle(824, 238, 108, 160),
                new Rectangle(968, 190, 112, 156),
                new Rectangle(968, 358, 112, 164),
            ],
            ReferenceSlots: [new Rectangle(1488, 242, 144, 268)]),
        new(
            CharacterSlots:
            [
                new Rectangle(676, 750, 104, 194),
                new Rectangle(824, 710, 108, 160),
                new Rectangle(968, 670, 112, 164),
                new Rectangle(968, 846, 112, 160),
            ],
            ReferenceSlots: [new Rectangle(1488, 722, 148, 268)]),
    ];

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

    internal static IReadOnlyList<RoleVectorGroup> ThreeCharacterGroups { get; } =
    [
        new(
            CharacterSlots:
            [
                new Rectangle(676, 152, 104, 200),
                new Rectangle(824, 120, 108, 160),
                new Rectangle(968, 72, 112, 156),
                new Rectangle(968, 240, 112, 164),
            ],
            ReferenceSlots: [new Rectangle(1488, 124, 144, 268)]),
        new(
            CharacterSlots:
            [
                new Rectangle(676, 512, 104, 194),
                new Rectangle(824, 472, 108, 160),
                new Rectangle(968, 432, 112, 164),
                new Rectangle(968, 608, 112, 160),
            ],
            ReferenceSlots: [new Rectangle(1488, 512, 148, 268)]),
        new(
            CharacterSlots:
            [
                new Rectangle(676, 876, 104, 200),
                new Rectangle(824, 840, 108, 160),
                new Rectangle(968, 796, 112, 168),
                new Rectangle(968, 972, 112, 160),
            ],
            ReferenceSlots: [new Rectangle(1488, 848, 144, 268)]),
    ];

    internal static IReadOnlyList<RoleVectorGroup> FourCharacterGroups { get; } =
    [
        new(
            CharacterSlots:
            [
                new Rectangle(200, 220, 76, 160),
                new Rectangle(276, 220, 76, 160),
                new Rectangle(412, 148, 116, 172),
                new Rectangle(608, 132, 96, 168),
                new Rectangle(780, 184, 112, 172),
            ],
            ReferenceSlots: [new Rectangle(988, 224, 116, 180)]),
        new(
            CharacterSlots:
            [
                new Rectangle(200, 560, 76, 156),
                new Rectangle(276, 560, 76, 156),
                new Rectangle(416, 500, 116, 164),
                new Rectangle(604, 472, 100, 172),
                new Rectangle(780, 524, 112, 176),
            ],
            ReferenceSlots: [new Rectangle(988, 548, 116, 180)]),
        new(
            CharacterSlots:
            [
                new Rectangle(228, 896, 100, 168),
                new Rectangle(416, 844, 112, 164),
                new Rectangle(596, 816, 96, 172),
                new Rectangle(780, 876, 108, 172),
            ],
            ReferenceSlots: [new Rectangle(992, 908, 116, 180)]),
        new(
            CharacterSlots:
            [
                new Rectangle(1440, 412, 116, 152),
                new Rectangle(1612, 380, 100, 184),
                new Rectangle(1448, 600, 112, 160),
                new Rectangle(1612, 672, 108, 164),
            ],
            ReferenceSlots: [new Rectangle(2032, 504, 160, 260)]),
    ];

    internal static IReadOnlyList<RoleVectorGroup> FiveCharacterGroups { get; } =
    [
        new(
            CharacterSlots:
            [
                new Rectangle(228, 184, 104, 164),
                new Rectangle(376, 116, 116, 148),
                new Rectangle(548, 76, 104, 152),
                new Rectangle(400, 280, 108, 156),
            ],
            ReferenceSlots: [new Rectangle(908, 172, 136, 212)]),
        new(
            CharacterSlots:
            [
                new Rectangle(212, 568, 116, 168),
                new Rectangle(372, 492, 112, 160),
                new Rectangle(540, 468, 112, 152),
                new Rectangle(428, 668, 112, 144),
            ],
            ReferenceSlots: [new Rectangle(908, 536, 148, 220)]),
        new(
            CharacterSlots:
            [
                new Rectangle(256, 944, 104, 160),
                new Rectangle(404, 868, 108, 160),
                new Rectangle(564, 840, 104, 144),
                new Rectangle(452, 1040, 108, 156),
            ],
            ReferenceSlots: [new Rectangle(908, 904, 156, 228)]),
        new(
            CharacterSlots:
            [
                new Rectangle(1268, 364, 108, 152),
                new Rectangle(1400, 276, 116, 156),
                new Rectangle(1564, 204, 116, 168),
                new Rectangle(1464, 444, 116, 164),
            ],
            ReferenceSlots: [new Rectangle(1952, 292, 152, 248)]),
        new(
            CharacterSlots:
            [
                new Rectangle(1352, 752, 112, 176),
                new Rectangle(1516, 704, 116, 156),
                new Rectangle(1484, 924, 108, 156),
            ],
            ReferenceSlots: [new Rectangle(2004, 768, 124, 268)]),
    ];

    internal static void Render(
        string outputPath,
        IReadOnlyList<string> characterImages,
        IReadOnlyList<string> referenceFrames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (characterImages.Count is < 2 or > 6)
            throw new ArgumentOutOfRangeException(
                nameof(characterImages),
                characterImages.Count,
                "角色矢量图仅支持 2、3、4、5、6 个人物。");
        var layout = ResolveLayout(characterImages.Count);
        using var canvas = LoadTemplate(layout.ResourceName);
        for (var groupIndex = 0; groupIndex < layout.Groups.Count; groupIndex++)
        {
            var group = layout.Groups[groupIndex];
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

    internal static RoleVectorLayout ResolveLayout(int characterCount) => characterCount switch
    {
        2 => new RoleVectorLayout(TwoCharacterTemplateResourceName, TwoCharacterGroups),
        3 => new RoleVectorLayout(ThreeCharacterTemplateResourceName, ThreeCharacterGroups),
        4 => new RoleVectorLayout(FourCharacterTemplateResourceName, FourCharacterGroups),
        5 => new RoleVectorLayout(FiveCharacterTemplateResourceName, FiveCharacterGroups),
        6 => new RoleVectorLayout(DefaultTemplateResourceName, Groups),
        _ => throw new ArgumentOutOfRangeException(
            nameof(characterCount),
            characterCount,
            "角色矢量图仅支持 2、3、4、5、6 个人物。"),
    };

    private static Image<Rgba32> LoadTemplate(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"未找到角色矢量图模板资源：{resourceName}");
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

    internal sealed record RoleVectorLayout(
        string ResourceName,
        IReadOnlyList<RoleVectorGroup> Groups);
}
