namespace PlatformPublisher.Desktop.Services;

public static class YunfanFeatures
{
    // 视频号助手页面仍作为账号栏和账号设置的宿主存在；关闭时只隐藏入口并跳过视频号专属页面和后台任务。
    public static bool WeixinAssistantEnabled { get; } = false;
}
