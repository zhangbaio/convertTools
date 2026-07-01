namespace ShortDrama.Core.Interfaces;

public interface IConfigLocator
{
    Task<string> FindConfigDirAsync(string projectDir, CancellationToken cancellationToken);
}
