using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class MainViewModel
{
    internal VideoDedupTemplateSnapshot LoadVideoDedupTemplates() =>
        VideoDedupTemplateCatalog.OpenDefault().Load(_store.Accounts);
}
