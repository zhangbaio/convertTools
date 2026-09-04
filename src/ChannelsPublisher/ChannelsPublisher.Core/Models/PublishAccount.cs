using System.Text.Json.Serialization;

namespace ChannelsPublisher.Core.Models;

/// <summary>一个视频号发布账号。每账号一个独立的浏览器会话目录（WebView2 UserDataFolder），
/// 扫码登录一次后长期保持在线。P1 已验证：Edge 内核 + 独立 profile 可复用登录态自动发布。</summary>
public sealed class PublishAccount
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>视频号昵称（登录后抓取，用于展示）。</summary>
    public string Nickname { get; set; } = "";
    /// <summary>每账号独立会话目录（WebView2 UserDataFolder / persistent profile）。</summary>
    public string ProfileDir { get; set; } = "";
    public DateTimeOffset? LastLoginAt { get; set; }
    public string CostReportCompanyName { get; set; } = "";
    public string CostReportTemplatePath { get; set; } = "";
    public string CostReportSignPath { get; set; } = "";
    public string CostReportSealPath { get; set; } = "";
    public string CostReportLegalRepresentative { get; set; } = "";
    public string CostReportActorPayRatio { get; set; } = "";
    public string KuaishouPersonalAccount { get; set; } = "";
    public string KuaishouPersonalConfigPath { get; set; } = "";
    public string KuaishouEnterpriseAccount { get; set; } = "";
    public string KuaishouEnterpriseConfigPath { get; set; } = "";
    public string WorkRootDirectory { get; set; } = "";
    public string DownloadDirectory { get; set; } = "";
    public string ArchiveRootDirectory { get; set; } = "";
    public string LegacyProfileId { get; set; } = "";
    public string LegacySessionSourceRoot { get; set; } = "";
    public string WeixinAuthStatePath { get; set; } = "";
    public string KuaishouPersonalAuthStatePath { get; set; } = "";
    public string KuaishouEnterpriseAuthStatePath { get; set; } = "";
    public DateTimeOffset? LegacySessionImportedAt { get; set; }

    /// <summary>运行期状态，不持久化。</summary>
    [JsonIgnore]
    public AccountStatus Status { get; set; } = AccountStatus.Offline;
}
