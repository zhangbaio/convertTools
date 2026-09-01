using System.Text.Json;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Common.Services;

public sealed class PublishJobStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public PublishJobStore(string? storePath = null)
    {
        StorePath = string.IsNullOrWhiteSpace(storePath)
            ? PlatformPublisherPaths.JobStorePath
            : Path.GetFullPath(storePath);
    }

    public string StorePath { get; }

    public async Task<IReadOnlyList<PublishJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(StorePath))
                return [];

            await using var stream = File.OpenRead(StorePath);
            return await JsonSerializer.DeserializeAsync<List<PublishJob>>(stream, JsonOptions, cancellationToken)
                   ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<PublishJob> jobs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var destination = StorePath;
            var temporary = destination + ".tmp";
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, jobs.ToArray(), JsonOptions, cancellationToken);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
