using PlatformPublisher.Common.Services;

namespace PlatformPublisher.Desktop.Services;

internal static class KuaishouWorkspaceLease
{
    public static FileStream Acquire()
    {
        Directory.CreateDirectory(PlatformPublisherPaths.DataRoot);
        var path = Path.Combine(PlatformPublisherPaths.DataRoot, "kuaishou-workspace.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("快手工作区正在另一个客户端运行，请关闭该客户端后重试。", ex);
        }
    }
}
