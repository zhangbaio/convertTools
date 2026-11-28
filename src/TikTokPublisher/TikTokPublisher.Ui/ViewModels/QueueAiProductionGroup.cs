using TikTokPublisher.Core.Queue;

namespace TikTokPublisher.Ui.ViewModels;

internal static class QueueAiProductionGroup
{
    internal static IReadOnlyList<string> Steps { get; } = Array.AsReadOnly(new[]
    {
        QueueStepKeys.GenerateEpisodeScript,
        QueueStepKeys.GenerateAiDramaMaterials,
        QueueStepKeys.GenerateAiScriptOutline,
        QueueStepKeys.GenerateRoleVector,
    });

    internal static IReadOnlyList<string> SelectedSteps(QueueRunOptions options) =>
        Steps.Where(options.IsStepEnabled).ToArray();

    internal static bool? CheckState(int selectedCount) => selectedCount switch
    {
        0 => false,
        var count when count == Steps.Count => true,
        _ => null,
    };

    internal static IReadOnlyList<string> ResolveSelection(bool? checkState, QueueRunOptions options) =>
        checkState switch
        {
            true => Steps,
            false => Array.Empty<string>(),
            // 旧配置只启用部分子步骤时，继续沿用原有选择，直到用户显式切换。
            null => SelectedSteps(options),
        };
}
