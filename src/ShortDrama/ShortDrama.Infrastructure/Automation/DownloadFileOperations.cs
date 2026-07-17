namespace ShortDrama.Infrastructure.Automation;

internal static class DownloadFileOperations
{
    private const int ReplaceRetries = 60;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(500);

    public static async Task SafeReplaceAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ReplaceRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsRetryableFileLock(ex) && attempt < ReplaceRetries)
            {
                await Task.Delay(ReplaceRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        File.Move(sourcePath, targetPath, overwrite: true);
    }

    public static async Task DelayAfterWriteAsync(CancellationToken cancellationToken) =>
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);

    private static bool IsRetryableFileLock(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;
}
