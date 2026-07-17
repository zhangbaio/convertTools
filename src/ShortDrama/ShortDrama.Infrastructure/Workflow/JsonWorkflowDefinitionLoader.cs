using ShortDrama.Core.Interfaces;
using ShortDrama.Core.Models;
using System.Text.Json;

namespace ShortDrama.Infrastructure.Workflow;

public sealed class JsonWorkflowDefinitionLoader : IWorkflowDefinitionLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<WorkflowDefinition> LoadAsync(string configPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new ArgumentException("工作流配置路径不能为空。", nameof(configPath));
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"未找到工作流配置文件: {configPath}", configPath);
        }

        await using var stream = File.OpenRead(configPath);
        var definition = await JsonSerializer.DeserializeAsync<WorkflowDefinition>(
            stream,
            SerializerOptions,
            cancellationToken);

        if (definition is null)
        {
            throw new InvalidOperationException($"无法解析工作流配置: {configPath}");
        }

        if (string.IsNullOrWhiteSpace(definition.ProjectDir))
        {
            throw new InvalidOperationException("工作流缺少 projectDir。");
        }

        if (definition.Steps.Count == 0)
        {
            throw new InvalidOperationException("工作流 steps 不能为空。");
        }

        return definition;
    }
}
