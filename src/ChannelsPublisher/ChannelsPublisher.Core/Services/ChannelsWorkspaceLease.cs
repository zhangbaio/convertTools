namespace ChannelsPublisher.Core.Services;

public static class ChannelsWorkspaceLease
{
    public static FileStream Acquire()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        var path = Path.Combine(AppPaths.DataRoot, "workspace.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("另一客户端正在使用视频号工作区，请先退出该客户端后重试。", ex);
        }
    }
}
