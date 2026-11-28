using Avalonia.Controls;
using TikTokPublisher.Core.Services.VideoDedup;

namespace PlatformPublisher.Desktop.Views;

public partial class KuaishouVideoDedupView : UserControl
{
    public KuaishouVideoDedupView() => InitializeComponent();

    internal void LoadAccount(string? accountId, string? accountName) =>
        TemplateEditor.LoadAccount(string.IsNullOrWhiteSpace(accountId) ? null : VideoDedupTemplateAccount.ForKuaishou(accountId), accountName);

    internal Task<bool> TryLeaveAsync(Window owner) => TemplateEditor.TryLeaveAsync(owner);
}
