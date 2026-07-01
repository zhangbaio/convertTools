namespace ShortDrama.Core.Models;

public sealed record WeixinBrowserRuntimeStatus(
    bool IsReady,
    string BrowserType,
    string? BrowserRootDirectory,
    string? BrowserExecutablePath,
    string Message,
    bool NeedsInstall);
