using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;
using TikTokPublisher.Ui.Services.TikTok;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokCopyrightMaterialUploadCombinationTests
{
    private static readonly string[] ManagedTypes =
        TikTokPublishConstants.AutoManagedCopyrightMaterialTypes.ToArray();

    private static readonly string[] AllKnownTypes =
        TikTokPublishConstants.CopyrightMaterialLabels.Keys.ToArray();

    public static IEnumerable<object[]> LegalManagedCombinations()
    {
        for (var mask = 1; mask < 1 << ManagedTypes.Length; mask++)
        {
            var selected = SelectByMask(ManagedTypes, mask);
            if (IsPlatformMinimumValid(selected))
                yield return [selected];
        }
    }

    public static IEnumerable<object[]> EveryManagedMaterialType() =>
        ManagedTypes.Select(type => new object[] { type });

    [Fact]
    public void Managed_matrix_contains_all_27_legal_combinations()
    {
        LegalManagedCombinations().Should().HaveCount(27);
    }

    [Fact]
    public void Material_minimum_rule_matches_every_nonempty_known_checkbox_combination()
    {
        for (var mask = 1; mask < 1 << AllKnownTypes.Length; mask++)
        {
            var selected = SelectByMask(AllKnownTypes, mask);
            var action = () => TikTokPublishConstants.ValidateCopyrightMaterialTypes(selected);

            if (IsPlatformMinimumValid(selected))
                action.Should().NotThrow($"组合 {string.Join(',', selected)} 应满足平台最低选择规则");
            else
                action.Should().Throw<InvalidOperationException>(
                    $"组合 {string.Join(',', selected)} 不满足 1 个核心或 2 个辅助材料规则");
        }
    }

    [Fact]
    public void Automation_support_rule_classifies_every_platform_legal_checkbox_combination()
    {
        var legalCount = 0;
        var autoManagedCount = 0;
        for (var mask = 1; mask < 1 << AllKnownTypes.Length; mask++)
        {
            var selected = SelectByMask(AllKnownTypes, mask);
            if (!IsPlatformMinimumValid(selected))
                continue;

            legalCount++;
            var fullyAutoManaged = selected.All(
                TikTokPublishConstants.AutoManagedCopyrightMaterialTypes.Contains);
            var action = () => TikTokPublishConstants.ValidateAutoManagedCopyrightMaterialTypes(selected);
            if (fullyAutoManaged)
            {
                autoManagedCount++;
                action.Should().NotThrow(
                    $"组合 {string.Join(',', selected)} 的每一项都支持自动上传");
            }
            else
            {
                action.Should().Throw<NotSupportedException>(
                    $"组合 {string.Join(',', selected)} 包含尚未支持自动上传的材料");
            }
        }

        legalCount.Should().Be(122);
        autoManagedCount.Should().Be(27);
    }

    [Theory]
    [MemberData(nameof(LegalManagedCombinations))]
    public async Task Validate_only_accepts_every_legal_managed_combination_when_selected_files_exist(
        string[] selected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"copyright-material-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var options = CreateOptionsWithSelectedFiles(root, selected);

            var action = () => TikTokBrowserActions.ConfigureCopyrightProofAsync(
                page: null!,
                options,
                existingMaterialTypes: [],
                log: null,
                CancellationToken.None,
                validateOnly: true);

            await action.Should().NotThrowAsync(
                $"合法组合 {string.Join(',', selected)} 的本地材料均已准备完成");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(LegalManagedCombinations))]
    public async Task Existing_selected_materials_do_not_require_local_files_for_any_legal_combination(
        string[] selected)
    {
        var options = new TikTokPublishOptions
        {
            CopyrightMaterialTypes = selected,
            CopyrightMaterialFilePaths = new Dictionary<string, string>(StringComparer.Ordinal),
            SourceInfoPackageSelection = new TikTokSourceFileInfoPackageSelection(
                IncludeOutline: false,
                IncludeScript: false,
                IncludeRoleVector: false,
                IncludeRoleSceneScreenshot: false),
        };

        var action = () => TikTokBrowserActions.ConfigureCopyrightProofAsync(
            page: null!,
            options,
            existingMaterialTypes: selected,
            log: null,
            CancellationToken.None,
            validateOnly: true);

        await action.Should().NotThrowAsync(
            $"组合 {string.Join(',', selected)} 已存在于平台时应跳过所有本地文件检查");
    }

    [Theory]
    [MemberData(nameof(EveryManagedMaterialType))]
    public async Task Every_selected_managed_material_rejects_its_missing_local_file(
        string materialType)
    {
        var root = Path.Combine(Path.GetTempPath(), $"copyright-material-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string[] selected = string.Equals(
                materialType,
                TikTokPublishConstants.ProductionAgreementMaterialType,
                StringComparison.Ordinal)
                ? [TikTokPublishConstants.ProductionAgreementMaterialType]
                : [TikTokPublishConstants.ProductionAgreementMaterialType, materialType];
            var options = CreateOptionsWithSelectedFiles(root, selected);
            RemoveOneSelectedMaterialFile(root, options, materialType);

            var action = () => TikTokBrowserActions.ConfigureCopyrightProofAsync(
                page: null!,
                options,
                existingMaterialTypes: [],
                log: null,
                CancellationToken.None,
                validateOnly: true);

            await action.Should().ThrowAsync<Exception>(
                $"已勾选的材料 {materialType} 缺少本地文件时必须在打开上传页面前失败");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TikTokPublishOptions CreateOptionsWithSelectedFiles(
        string workflow,
        IReadOnlyCollection<string> selected)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceSelection = new TikTokSourceFileInfoPackageSelection(
            IncludeOutline: true,
            IncludeScript: true,
            IncludeRoleVector: false,
            IncludeRoleSceneScreenshot: true);

        if (selected.Contains(TikTokPublishConstants.ProductionAgreementMaterialType))
        {
            var path = Path.Combine(workflow, "证明材料.pdf");
            WritePdf(path);
            paths[TikTokPublishConstants.ProductionAgreementMaterialType] = path;
        }

        if (selected.Contains(TikTokPublishConstants.FilingOrDistributionLicenseMaterialType))
        {
            var path = Path.Combine(workflow, "可信时间戳认证证书.pdf");
            WritePdf(path);
            paths[TikTokPublishConstants.FilingOrDistributionLicenseMaterialType] = path;
        }

        if (selected.Contains(TikTokPublishConstants.SourceFileInformationMaterialType))
        {
            foreach (var path in TikTokSourceFileInfoUploadPackageService.GetExpectedOutputPaths(
                         workflow,
                         sourceSelection))
            {
                if (string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                    WritePdf(path);
                else
                    WritePng(path);
            }
            paths[TikTokPublishConstants.SourceFileInformationMaterialType] =
                TikTokSourceFileInfoUploadPackageService.GetOutputDirectory(workflow);
        }

        if (selected.Contains(TikTokPublishConstants.AiGenerationScreenshotsMaterialType))
        {
            foreach (var path in TikTokAiGenerationScreenshotService.GetExpectedOutputPaths(workflow))
                WritePng(path);
            paths[TikTokPublishConstants.AiGenerationScreenshotsMaterialType] =
                TikTokAiGenerationScreenshotService.GetOutputDirectory(workflow);
        }

        if (selected.Contains(TikTokPublishConstants.EditingProjectFilesMaterialType))
        {
            var output = TikTokProjectImageService.GetOutputDirectory(workflow);
            for (var index = 1; index <= TikTokProjectImageService.MinUploadImageCount; index++)
                WritePng(Path.Combine(output, $"工程图_{index}.png"));
            paths[TikTokPublishConstants.EditingProjectFilesMaterialType] = output;
        }

        return new TikTokPublishOptions
        {
            CopyrightMaterialTypes = selected.ToArray(),
            CopyrightMaterialFilePaths = paths,
            CopyrightMaterialFilePath = paths.GetValueOrDefault(
                TikTokPublishConstants.ProductionAgreementMaterialType,
                string.Empty),
            SourceInfoPackageSelection = sourceSelection,
        };
    }

    private static bool IsPlatformMinimumValid(IReadOnlyCollection<string> selected)
    {
        var coreCount = selected.Count(TikTokPublishConstants.CoreCopyrightMaterialTypes.Contains);
        var auxiliaryCount = selected.Count(TikTokPublishConstants.AuxiliaryCopyrightMaterialTypes.Contains);
        return coreCount > 0 || auxiliaryCount >= 2;
    }

    private static void RemoveOneSelectedMaterialFile(
        string workflow,
        TikTokPublishOptions options,
        string materialType)
    {
        string path;
        if (string.Equals(materialType, TikTokPublishConstants.SourceFileInformationMaterialType, StringComparison.Ordinal))
        {
            path = TikTokSourceFileInfoUploadPackageService.GetExpectedOutputPaths(
                workflow,
                options.SourceInfoPackageSelection)[0];
        }
        else if (string.Equals(materialType, TikTokPublishConstants.AiGenerationScreenshotsMaterialType, StringComparison.Ordinal))
        {
            path = TikTokAiGenerationScreenshotService.GetExpectedOutputPaths(workflow)[0];
        }
        else if (string.Equals(materialType, TikTokPublishConstants.EditingProjectFilesMaterialType, StringComparison.Ordinal))
        {
            path = Path.Combine(TikTokProjectImageService.GetOutputDirectory(workflow), "工程图_1.png");
        }
        else
        {
            path = options.ResolveCopyrightMaterialFilePath(materialType);
        }

        File.Delete(path);
    }

    private static string[] SelectByMask(IReadOnlyList<string> types, int mask) =>
        types.Where((_, index) => (mask & (1 << index)) != 0).ToArray();

    private static void WritePdf(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, "%PDF-1.7\nvalid test pdf"u8.ToArray());
    }

    private static void WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(32, 32, new Rgba32(60, 80, 100));
        image.SaveAsPng(path);
    }
}
