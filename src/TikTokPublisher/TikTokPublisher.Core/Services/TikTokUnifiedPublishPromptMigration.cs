using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TikTokPublisher.Core.Services;

public static class TikTokUnifiedPublishPromptMigration
{
    private const string MarkerFileName = ".tiktok-unified-publish-prompt-v1";

    public static void ApplyOnce()
    {
        var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataRoot));
        var accountsPath = AppPaths.AccountsFile;
        var databasePath = AppPaths.AppDatabaseFile;
        var markerPath = Path.Combine(dataRoot, MarkerFileName);
        if (File.Exists(markerPath))
            return;

        var lockKey = OperatingSystem.IsWindows() ? dataRoot.ToUpperInvariant() : dataRoot;
        var lockHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockKey)));
        using var migrationMutex = new Mutex(false, $"TikTokUnifiedPublishPromptMigration-{lockHash}");
        try
        {
            if (!migrationMutex.WaitOne(TimeSpan.FromSeconds(30)))
                throw new IOException("等待另一个 TikTok 短剧助手更新发布提示词超时。");
        }
        catch (AbandonedMutexException)
        {
            // An interrupted startup did not mark the migration complete. Finish it here.
        }

        try
        {
            if (File.Exists(markerPath))
                return;

            var updatedAccounts = PrepareAccountPromptReset(accountsPath);
            Directory.CreateDirectory(dataRoot);
            ClientSettingsStore.ResetTikTokPublishPromptToDefault(databasePath);
            if (updatedAccounts is not null)
                WriteAccountsAtomically(accountsPath, updatedAccounts);

            // This local marker stays outside account sync and exported app.db snapshots.
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        finally
        {
            migrationMutex.ReleaseMutex();
        }
    }

    private static JsonArray? PrepareAccountPromptReset(string accountsPath)
    {
        if (!File.Exists(accountsPath))
            return null;

        var accounts = JsonNode.Parse(File.ReadAllText(accountsPath)) as JsonArray
            ?? throw new InvalidDataException("账号配置格式无效，无法重置发布信息提示词。");
        var changed = false;
        foreach (var account in accounts.OfType<JsonObject>())
        {
            if (account["aiOptions"] is not JsonObject options ||
                options["publishPrompt"] is not JsonValue value ||
                !value.TryGetValue<string>(out var prompt) || string.IsNullOrEmpty(prompt))
                continue;

            options["publishPrompt"] = string.Empty;
            changed = true;
        }

        return changed ? accounts : null;
    }

    private static void WriteAccountsAtomically(string path, JsonArray accounts)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, accounts.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
