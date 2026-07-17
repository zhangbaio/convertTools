namespace ChannelsPublisher.Core.Models;

/// <summary>账号运行期状态（不持久化）。对应参考图左侧「在线/离线」。</summary>
public enum AccountStatus
{
    Offline,
    LoggingIn,
    Online,
}
