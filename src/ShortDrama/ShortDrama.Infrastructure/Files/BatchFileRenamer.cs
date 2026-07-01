using Microsoft.Extensions.Logging;
using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using ShortDrama.Infrastructure.Config;

namespace ShortDrama.Infrastructure.Files;

public sealed class BatchFileRenamer : IBatchFileRenamer
{
    private static readonly string[] SupportedExtensions = [".mp4", ".mov", ".m4v", ".mkv", ".avi", ".flv", ".wmv", ".webm"];

    private readonly IProjectInfoParser _projectInfoParser;
    private readonly ILogger<BatchFileRenamer> _logger;

    public BatchFileRenamer(
        IProjectInfoParser projectInfoParser,
        ILogger<BatchFileRenamer> logger)
    {
        _projectInfoParser = projectInfoParser;
        _logger = logger;
    }

    public async Task<BatchFileRenameResult> RenameAsync(
        BatchFileRenameRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(request.ProjectDir))
        {
            throw new DirectoryNotFoundException($"项目目录不存在: {request.ProjectDir}");
        }

        if (!Directory.Exists(request.InputDir))
        {
            throw new DirectoryNotFoundException($"批量重命名输入目录不存在: {request.InputDir}");
        }

        var project = await _projectInfoParser.ParseAsync(request.ProjectDir, cancellationToken);
        var files = Directory.EnumerateFiles(request.InputDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException($"输入目录中没有可重命名的视频文件: {request.InputDir}");
        }

        var nameTemplate = ResolveNameTemplate(request.ConfigFile, request.NameTemplate);
        var renamePlan = new List<(string Source, string Temp, string Final)>();
        var items = new List<BatchFileRenameItem>();
        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < files.Count; index++)
        {
            var source = files[index];
            var extension = Path.GetExtension(source);
            var stem = BuildFileStem(nameTemplate, project.Title, index + 1);
            var finalPath = Path.Combine(request.InputDir, $"{stem}{extension}");

            if (!targetPaths.Add(finalPath))
            {
                throw new InvalidOperationException($"批量重命名产生了重复目标文件名: {finalPath}");
            }

            if (!string.Equals(source, finalPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(finalPath) &&
                !request.Overwrite)
            {
                throw new InvalidOperationException($"目标文件已存在: {finalPath}");
            }

            var tempPath = Path.Combine(request.InputDir, $".rename-{Guid.NewGuid():N}{extension}");
            renamePlan.Add((source, tempPath, finalPath));
            items.Add(new BatchFileRenameItem(source, finalPath));
        }

        foreach (var step in renamePlan)
        {
            if (string.Equals(step.Source, step.Final, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Move(step.Source, step.Temp);
        }

        foreach (var step in renamePlan)
        {
            if (string.Equals(step.Source, step.Final, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(step.Final) && request.Overwrite)
            {
                File.Delete(step.Final);
            }

            File.Move(step.Temp, step.Final);
            _logger.LogInformation("Renamed file: {Source} -> {Final}", step.Source, step.Final);
        }

        return new BatchFileRenameResult(items.Count(item => !string.Equals(item.InputFilePath, item.OutputFilePath, StringComparison.OrdinalIgnoreCase)), items);
    }

    private static string ResolveNameTemplate(string? configFile, string? explicitTemplate)
    {
        if (!string.IsNullOrWhiteSpace(explicitTemplate))
        {
            return explicitTemplate;
        }

        if (!string.IsNullOrWhiteSpace(configFile))
        {
            var config = KeyValueConfigReader.Read(configFile);
            if (config.TryGetValue("VideoNameTemplate", out var configuredTemplate) &&
                !string.IsNullOrWhiteSpace(configuredTemplate))
            {
                return configuredTemplate;
            }
        }

        return "{name}-第{index}集";
    }

    private static string BuildFileStem(string template, string projectTitle, int index)
    {
        var stem = template
            .Replace("{name}", projectTitle, StringComparison.Ordinal)
            .Replace("{index}", index.ToString(), StringComparison.Ordinal);

        var invalidChars = Path.GetInvalidFileNameChars();
        stem = new string(stem.Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(stem))
        {
            throw new InvalidOperationException("批量重命名模板生成了空文件名。");
        }

        return stem;
    }
}
