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

    /// <summary>运行期状态，不持久化。</summary>
    [JsonIgnore]
    public AccountStatus Status { get; set; } = AccountStatus.Offline;
}
