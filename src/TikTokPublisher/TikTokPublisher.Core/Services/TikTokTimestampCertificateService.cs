using PdfSharp.Pdf.IO;
using PublisherShared.Certificates;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokTimestampCertificateService
{
    public const string OutputFileName = TimestampCertificateService.OutputFileName;
    internal const string MissingOutputMessage =
        "备案／发行许可已启用，可信时间戳证书缺失或无效，请先执行「证明材料」。";

    public static string GetOutputPath(QueueProjectItem item) =>
        TimestampCertificateService.GetOutputPath(ProjectWorkspaceService.LoadContext(item.ProjectDir).WorkflowProjectDir);

    public static bool HasCurrentOutput(QueueProjectItem item)
    {
        try
        {
            return HasCurrentOutput(ProjectWorkspaceService.LoadContext(item.ProjectDir).WorkflowProjectDir);
        }
        catch
        {
            return false;
        }
    }

    internal static bool HasCurrentOutput(string workflowDirectory)
    {
        if (!TimestampCertificateService.HasCurrentOutput(workflowDirectory))
            return false;

        try
        {
            var path = TimestampCertificateService.GetOutputPath(workflowDirectory);
            TikTokProofMaterialPdfRenderService.ValidatePdf(path);
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            return document.PageCount > 0;
        }
        catch
        {
            // PDFsharp can throw a plain Exception for malformed cross-reference tables.
            return false;
        }
    }

    public static Task<string> GenerateAsync(QueueProjectItem item, ClientSettings settings, TikTokAccountProfile? account,
        bool forceRerun, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var context = ProjectWorkspaceService.LoadContext(item.ProjectDir);
        ProjectWorkspaceService.ValidateContextOwnership(context);
        var workflowDirectory = context.WorkflowProjectDir;
        var output = TimestampCertificateService.GetOutputPath(workflowDirectory);
        if (!forceRerun && HasCurrentOutput(workflowDirectory))
        {
            log?.Invoke($"可信时间戳复用已有证书：{output}");
            return Task.FromResult(output);
        }

        var title = string.IsNullOrWhiteSpace(item.NewTitle) ? item.Title : item.NewTitle;
        var applicant = ResolveApplicant(workflowDirectory, settings, account);
        var staging = Directory.CreateDirectory(Path.Combine(workflowDirectory, $".timestamp-{Guid.NewGuid():N}"));
        var candidate = TimestampCertificateService.GetOutputPath(staging.FullName);
        try
        {
            TimestampCertificateService.Generate(staging.FullName, title, applicant, forceRerun: true, log, ct);
            if (!HasCurrentOutput(staging.FullName))
                throw new InvalidDataException("生成的可信时间戳 PDF 无法读取。");
            ct.ThrowIfCancellationRequested();
            File.Move(candidate, output, overwrite: true);
            return Task.FromResult(output);
        }
        finally
        {
            try
            {
                File.Delete(candidate);
                staging.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"清理时间戳临时文件失败：{ex.Message}");
            }
        }
    }

    internal static string ResolveApplicant(string projectDir, ClientSettings settings, TikTokAccountProfile? account) =>
        TimestampCertificateService.ResolveApplicant(projectDir, account?.TiktokTimestampApplicantName,
            account?.TiktokProofDeclarantCompanyName, settings.TiktokProofDeclarantCompanyName);

    internal static void EnsureSharedFontResolver() => TimestampCertificateService.EnsureSharedFontResolver();
}
