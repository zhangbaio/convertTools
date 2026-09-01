using System.Text.Json;
using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Common.Services;

public sealed class PublishAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public PublishAccountStore(string? storePath = null)
    {
        StorePath = string.IsNullOrWhiteSpace(storePath)
            ? PlatformPublisherPaths.AccountStorePath
            : Path.GetFullPath(storePath);
    }

    public string StorePath { get; }

    public async Task<IReadOnlyList<PublishAccount>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(StorePath))
                return [];
            await using var stream = File.OpenRead(StorePath);
            return await JsonSerializer.DeserializeAsync<List<PublishAccount>>(stream, JsonOptions, cancellationToken)
                   ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<PublishAccount> accounts, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temporary = StorePath + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, accounts.ToArray(), JsonOptions, cancellationToken);
            File.Move(temporary, StorePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
