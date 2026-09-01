using PlatformPublisher.Common.Models;
using ShortDrama.Core.Interfaces;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Weixin.Publishing;

public sealed record WeixinProofArtifactsResult(string WorkflowDirectory, string AiProofPath, string TimestampCertificatePath);

public sealed class WeixinProofArtifactsService
{
    private readonly IWorkService _workService;
    public WeixinProofArtifactsService(IWorkService workService) => _workService = workService;

    public async Task<string> GenerateAiProofAsync(
        PublishJob job,
        ClientSettings settings,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var workflowDir = await ResolveWorkflowDirectoryAsync(job, cancellationToken);
        var output = Path.Combine(workflowDir, TikTokProofMaterialService.ProofPdfFileName);
        if (!force && File.Exists(output) && new FileInfo(output).Length > 100)
        {
            progress?.Report($"AI 制作证明已存在，跳过：{output}");
            return output;
        }
        var info = ParseInfo(Path.Combine(workflowDir, "短剧信息.txt"));
        var title = First(info.GetValueOrDefault("新剧名"), job.ProjectName);
        var company = First(info.GetValueOrDefault("制作公司"), settings.TiktokProofDeclarantCompanyName, "未填写制作公司");
        var template = settings.TiktokProofTemplateDocxPath;
        if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
            throw new FileNotFoundException("未配置可用的 AI 制作证明 Word 模板。", template);
        var request = new TikTokProofMaterialRequest(
            template,
            output,
            company,
            company,
            title,
            DateOnly.FromDateTime(DateTime.Today))
        {
            SealImagePath = settings.TiktokProofSealPath,
            PreferredPdfRenderer = TikTokProofMaterialPdfRendererPreferenceExtensions.Parse(settings.TiktokProofPdfRenderer),
            KeepIntermediateDocx = settings.TiktokProofKeepDocx,
            GenerateProductionAgreement = true,
            GenerateAiGenerationScreenshots = true,
            GenerateEditingProjectFiles = true,
        };
        var result = await new TikTokProofMaterialService().GenerateAsync(
            request,
            message => progress?.Report(message),
            cancellationToken);
        var alias = Path.Combine(workflowDir, "AI制作证明.pdf");
        File.Copy(result.PdfPath, alias, overwrite: true);
        progress?.Report($"AI 制作证明完成：{alias}");
        return alias;
    }

    public async Task<string> GenerateTimestampCertificateAsync(
        PublishJob job,
        ClientSettings settings,
        bool force,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var workflowDir = await ResolveWorkflowDirectoryAsync(job, cancellationToken);
        var info = ParseInfo(Path.Combine(workflowDir, "短剧信息.txt"));
        var item = new QueueProjectItem
        {
            ProjectDir = job.ProjectDirectory,
            DisplayName = job.ProjectName,
            OriginalTitle = First(info.GetValueOrDefault("原剧名"), job.ProjectName),
            NewTitle = First(info.GetValueOrDefault("新剧名"), job.ProjectName),
            AccountProfileName = job.AccountName,
        };
        var path = await TikTokTimestampCertificateService.GenerateAsync(
            item,
            settings,
            account: null,
            force,
            message => progress?.Report(message),
            cancellationToken);
        progress?.Report($"可信时间戳本地模板证书完成：{path}（未调用第三方 TSA 服务）");
        return path;
    }

    private async Task<string> ResolveWorkflowDirectoryAsync(PublishJob job, CancellationToken cancellationToken)
    {
        var config = await _workService.EnsureWeixinUploadConfigAsync(job.ProjectDirectory, null, cancellationToken);
        return Path.GetDirectoryName(config) ?? throw new InvalidOperationException("无法定位工作项目目录。");
    }

    private static Dictionary<string, string> ParseInfo(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        foreach (var line in File.ReadLines(path))
        {
            var index = line.IndexOfAny([':', '：']);
            if (index > 0) result[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        return result;
    }

    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
}
