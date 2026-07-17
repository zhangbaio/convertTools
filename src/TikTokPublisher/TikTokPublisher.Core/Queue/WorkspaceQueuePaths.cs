namespace TikTokPublisher.Core.Queue;

public static class WorkspaceQueuePaths
{
    public const string QueueDatabaseFileName = ".tiktok-task-queue.db";
    public const string LegacyQueueJsonFileName = ".tiktok-task-queue.json";

    public static string QueueDatabasePath(string workspaceRoot) =>
        Path.Combine(Path.GetFullPath(workspaceRoot), QueueDatabaseFileName);

    public static string LegacyQueueJsonPath(string workspaceRoot) =>
        Path.Combine(Path.GetFullPath(workspaceRoot), LegacyQueueJsonFileName);
}
