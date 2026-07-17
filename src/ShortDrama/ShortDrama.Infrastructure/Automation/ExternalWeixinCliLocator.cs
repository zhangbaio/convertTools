using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;

namespace ShortDrama.Infrastructure.Automation;

public sealed class ExternalWeixinCliLocator : IExternalWeixinCliLocator
{
    private readonly PythonToolResolver _pythonToolResolver;

    public ExternalWeixinCliLocator(PythonToolResolver pythonToolResolver)
    {
        _pythonToolResolver = pythonToolResolver;
    }

    public async Task<ExternalWeixinCliCommand> ResolveAsync(CancellationToken cancellationToken)
    {
        var python = await _pythonToolResolver.ResolvePythonCommandAsync(cancellationToken);
        var toolDirectory = _pythonToolResolver.ResolveRepositoryToolDirectory("weixin-channel-tool");
        var scriptPath = Path.Combine(toolDirectory, "main.py");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"未找到微信外部上传脚本: {scriptPath}", scriptPath);
        }

        return new ExternalWeixinCliCommand(
            python.FileName,
            python.PrefixArguments,
            toolDirectory,
            scriptPath);
    }
}
