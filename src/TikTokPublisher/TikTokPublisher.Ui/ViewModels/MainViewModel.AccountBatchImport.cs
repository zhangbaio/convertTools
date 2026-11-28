using TikTokPublisher.Core.Services;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Ui.ViewModels;

public sealed partial class MainViewModel
{
    internal TikTokAccountBatchImporter CreateAccountBatchImporter() =>
        new(_store, VideoDedupTemplateCatalog.OpenDefault());

    internal void ApplyAccountBatchImport(TikTokAccountImportResult result)
    {
        foreach (var account in result.Accounts)
            Accounts.Add(new AccountItemViewModel(account));
        RefreshFilteredAccounts();
        StatusMessage = $"批量添加完成，已创建 {result.Accounts.Count} 个 TikTok 账号。";
    }
}
