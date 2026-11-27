using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public event Action? VideoDedupTemplatesChanged;

    internal VideoDedupTemplateSnapshot LoadVideoDedupTemplates() =>
        VideoDedupTemplateCatalog.OpenDefault().Load(_store.Accounts);

    internal void NotifyVideoDedupTemplatesChanged() => VideoDedupTemplatesChanged?.Invoke();
}
