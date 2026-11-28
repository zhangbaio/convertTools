using PublisherShared.Certificates;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Core.Services;

public static class TikTokTimestampCertificateService
{
    public const string OutputFileName = TimestampCertificateService.OutputFileName;

    public static string GetOutputPath(QueueProjectItem item) =>
        TimestampCertificateService.GetOutputPath(ProjectWorkspaceService.LoadContext(item.ProjectDir).WorkflowProjectDir);

    public static bool HasCurrentOutput(QueueProjectItem item)
    {
        try
        {
            return TimestampCertificateService.HasCurrentOutput(ProjectWorkspaceService.LoadContext(item.ProjectDir).WorkflowProjectDir);
        }
        catch
        {
            return false;
        }
    }

    public static Task<string> GenerateAsync(QueueProjectItem item, ClientSettings settings, TikTokAccountProfile? account,
        bool forceRerun, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var workflowDirectory = ProjectWorkspaceService.LoadContext(item.ProjectDir).WorkflowProjectDir;
        var title = string.IsNullOrWhiteSpace(item.NewTitle) ? item.Title : item.NewTitle;
        var applicant = ResolveApplicant(workflowDirectory, settings, account);
        return Task.FromResult(TimestampCertificateService.Generate(workflowDirectory, title, applicant, forceRerun, log, ct));
    }

    internal static string ResolveApplicant(string projectDir, ClientSettings settings, TikTokAccountProfile? account) =>
        TimestampCertificateService.ResolveApplicant(projectDir, account?.TiktokTimestampApplicantName,
            account?.TiktokProofDeclarantCompanyName, settings.TiktokProofDeclarantCompanyName);

    internal static void EnsureSharedFontResolver() => TimestampCertificateService.EnsureSharedFontResolver();
}
