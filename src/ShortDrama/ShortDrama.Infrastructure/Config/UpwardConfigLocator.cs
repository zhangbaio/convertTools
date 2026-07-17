using ShortDrama.Core.Interfaces;

namespace ShortDrama.Infrastructure.Config;

public sealed class UpwardConfigLocator : IConfigLocator
{
    public Task<string> FindConfigDirAsync(string projectDir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(projectDir))
        {
            throw new ArgumentException("项目目录不能为空。", nameof(projectDir));
        }

        if (!Directory.Exists(projectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {projectDir}");
        }

        var current = new DirectoryInfo(projectDir);

        while (current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var configDir = Path.Combine(current.FullName, "config");
            var signPath = Path.Combine(configDir, "sign.png");
            var sealPath = Path.Combine(configDir, "seal.png");

            if (Directory.Exists(configDir) && File.Exists(signPath) && File.Exists(sealPath))
            {
                return Task.FromResult(configDir);
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"未找到可用的 config 目录。要求目录中包含 sign.png 和 seal.png，起始搜索目录: {projectDir}");
    }
}
