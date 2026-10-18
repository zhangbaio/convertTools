using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using TikTokPublisher.Core.Licensing;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokManagementConfigSyncServiceTests
{
    [Fact]
    public async Task UploadAsync_SendsConfigOnlyPackageWithTtHeaders()
    {
        var root = CreateRoot();
        using var scope = new DataRootScope(root);
        WriteMachine(
            root,
            "acct-a",
            "来自电脑A",
            "secret-a",
            "cli-from-a",
            "license-a",
            "queue-a");

        HttpRequestMessage? captured = null;
        byte[]? body = null;
        using var http = new HttpClient(new StubHandler(async request =>
        {
            captured = request;
            body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync();
            return Json(HttpStatusCode.OK, """{"ok":true,"data":{"revision":1}}""");
        }));
        var service = CreateService(http);
        var package = service.PrepareUpload();
        try
        {
            package.Ok.Should().BeTrue();
            package.Summary.Should().Contain("仅配置");
            using (var zip = ZipFile.OpenRead(package.PackagePath!))
            {
                zip.Entries.Select(entry => entry.FullName).Should().NotContain(entry =>
                    entry.Contains(".tiktok-task-queue", StringComparison.OrdinalIgnoreCase)
                    || entry.EndsWith("app.db", StringComparison.OrdinalIgnoreCase));
                using var manifestStream = zip.GetEntry("manifest.json")!.Open();
                using var reader = new StreamReader(manifestStream);
                var manifest = JsonDocument.Parse(reader.ReadToEnd()).RootElement;
                manifest.GetProperty("scope").GetString().Should().Be("config");
                manifest.GetProperty("hasAppDatabase").GetBoolean().Should().BeFalse();
            }

            var result = await service.UploadAsync(package);

            result.Ok.Should().BeTrue();
            captured!.Method.Should().Be(HttpMethod.Put);
            captured.RequestUri!.AbsolutePath.Should().Be("/client-api/tt/config");
            captured.Headers.GetValues("X-TT-Account").Single().Should().Be("software-user");
            captured.Headers.GetValues("X-TT-Machine-Id").Single().Should().Be("machine-a");
            captured.Headers.GetValues("X-TT-Token").Single().Should().Be("signed-token");
            captured.Headers.GetValues("X-TT-Device-Name").Single().Should().Be("Office PC");
            captured.Content!.Headers.ContentType!.MediaType.Should().Be("application/zip");
            body.Should().Equal(File.ReadAllBytes(package.PackagePath!));
        }
        finally
        {
            package.Dispose();
        }
    }

    [Fact]
    public async Task DownloadAndImportAsync_MergesAccountsWithoutTouchingLicenseOrQueue()
    {
        var sourceRoot = CreateRoot();
        string packagePath;
        using (var sourceScope = new DataRootScope(sourceRoot))
        {
            WriteMachine(
                sourceRoot,
                "acct-a",
                "来自电脑A",
                "secret-a",
                "cli-from-a",
                "license-a",
                "queue-a");
            packagePath = Path.Combine(sourceRoot, "upload.zip");
            PublisherConfigTransfer.ExportTo(packagePath, includeRunData: false);
        }

        var packageBytes = File.ReadAllBytes(packagePath);
        var sha = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var targetRoot = CreateRoot();
        using var targetScope = new DataRootScope(targetRoot);
        WriteMachine(
            targetRoot,
            "acct-a",
            "本机旧名",
            "secret-b",
            "cli-from-b",
            "license-b",
            "queue-b");
        WriteAccount(Path.Combine(targetRoot, "accounts.json"), "acct-local", "只在本机", "local-secret", append: true);
        var queuePath = QueuePath(targetRoot);
        var licensePath = Path.Combine(targetRoot, "license_state.bin");
        var queueBefore = File.ReadAllBytes(queuePath);
        var licenseBefore = File.ReadAllBytes(licensePath);
        InsertLocalOnlyRows(Path.Combine(targetRoot, "app.db"));

        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/meta", StringComparison.Ordinal))
            {
                var json = "{\"ok\":true,\"data\":{"
                    + "\"revision\":3,"
                    + "\"updated_at\":\"2026-10-18T16:00:00\","
                    + "\"source_machine_id\":\"machine-a\","
                    + "\"source_device_name\":\"电脑A\","
                    + "\"sha256\":\"" + sha + "\","
                    + "\"summary\":{\"account_count\":1,\"has_app_settings\":true,\"login_state_count\":1,\"seal_count\":0,\"scope\":\"config\"}"
                    + "}}";
                return Task.FromResult(Json(HttpStatusCode.OK, json));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(packageBytes),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            response.Headers.TryAddWithoutValidation("X-TT-Config-Revision", "3");
            response.Headers.TryAddWithoutValidation("X-TT-Config-Sha256", sha);
            return Task.FromResult(response);
        }));
        var service = CreateService(http);

        var meta = await service.FetchMetaAsync();
        meta.Ok.Should().BeTrue();
        meta.Exists.Should().BeTrue();
        var imported = await service.DownloadAndImportAsync(meta.Meta!);

        imported.Ok.Should().BeTrue();
        imported.Message.Should().Contain("重新选择");
        var accounts = ReadAccounts(Path.Combine(targetRoot, "accounts.json"));
        accounts.Select(account => account.Id).Should().BeEquivalentTo("acct-a", "acct-local");
        accounts.Single(account => account.Id == "acct-a").Name.Should().Be("来自电脑A");
        accounts.Single(account => account.Id == "acct-a").TiktokLoginPassword.Should().Be("secret-a");
        accounts.Single(account => account.Id == "acct-local").Name.Should().Be("只在本机");
        ClientSettingsStore.Load().FeishuCommandAppId.Should().Be("cli-from-a");
        ReadSetting(Path.Combine(targetRoot, "app.db"), "custom_marker").Should().Be("\"keep\"");
        CountEvents(Path.Combine(targetRoot, "app.db")).Should().Be(1);
        File.ReadAllBytes(queuePath).Should().Equal(queueBefore);
        File.ReadAllBytes(licensePath).Should().Equal(licenseBefore);
    }

    [Fact]
    public async Task Sync_WithoutLoginDoesNotCallServerOrImportStalePackage()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException("未登录不应访问管理系统")));
        var service = CreateService(http, loggedIn: false);

        var package = service.PrepareUpload();
        var uploaded = await service.UploadAsync(package);
        var meta = await service.FetchMetaAsync();

        package.Ok.Should().BeFalse();
        uploaded.Ok.Should().BeFalse();
        uploaded.Message.Should().Contain("未登录");
        meta.Ok.Should().BeFalse();
        package.Dispose();

        var root = CreateRoot();
        using var scope = new DataRootScope(root);
        WriteMachine(root, "acct-a", "保持不变", "secret-b", "cli-local", "license-b", "queue-b");
        var loggedIn = CreateService(new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x50, 0x4B, 0x00]),
            };
            response.Headers.TryAddWithoutValidation("X-TT-Config-Revision", "9");
            response.Headers.TryAddWithoutValidation("X-TT-Config-Sha256", "ffff");
            return Task.FromResult(response);
        })));
        var rejected = await loggedIn.DownloadAndImportAsync(new TikTokConfigPackageMeta(
            3,
            "2026-10-18T16:00:00",
            "machine-a",
            "电脑A",
            "ffff",
            1,
            true,
            0,
            0));

        rejected.Ok.Should().BeFalse();
        rejected.Message.Should().Contain("重新确认");
        ReadAccounts(Path.Combine(root, "accounts.json")).Single().Name.Should().Be("保持不变");
    }

    private static TikTokManagementConfigSyncService CreateService(HttpClient http, bool loggedIn = true) =>
        new(
            http,
            () => new ClientSettings { AuthServerUrl = "https://manage.example" },
            () => loggedIn
                ? new LicenseState
                {
                    ServerUrl = "https://manage.example/",
                    AccountUsername = "software-user",
                    MachineId = "machine-a",
                    Token = "signed-token",
                }
                : new LicenseState(),
            () => "Office PC");

    private static void WriteMachine(
        string root,
        string accountId,
        string name,
        string password,
        string feishuAppId,
        string licenseText,
        string queueText)
    {
        Directory.CreateDirectory(root);
        ClientSettingsStore.Save(new ClientSettings { FeishuCommandAppId = feishuAppId });
        WriteAccount(Path.Combine(root, "accounts.json"), accountId, name, password, append: false);
        File.WriteAllText(
            Path.Combine(root, "active-account.json"),
            $$"""{"activeAccountId":"{{accountId}}"}""");
        var profileDir = AppPaths.ProfileDirFor(accountId);
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(AppPaths.DefaultStorageStatePath(accountId), """{"cookies":[]}""");
        File.WriteAllBytes(Path.Combine(root, "license_state.bin"), Encoding.UTF8.GetBytes(licenseText));
        var queuePath = QueuePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(queuePath)!);
        File.WriteAllBytes(queuePath, Encoding.UTF8.GetBytes(queueText));
    }

    private static void WriteAccount(string path, string accountId, string name, string password, bool append)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        var accounts = append && File.Exists(path)
            ? JsonSerializer.Deserialize<List<TikTokAccountProfile>>(File.ReadAllText(path), options) ?? []
            : [];
        accounts.Add(new TikTokAccountProfile
        {
            Id = accountId,
            Name = name,
            TiktokLoginEmail = $"{accountId}@example.test",
            TiktokLoginPassword = password,
            TiktokUploadProfilePath = Path.Combine(Path.GetDirectoryName(path)!, "workspace"),
        });
        File.WriteAllText(path, JsonSerializer.Serialize(accounts, options));
    }

    private static List<TikTokAccountProfile> ReadAccounts(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        return JsonSerializer.Deserialize<List<TikTokAccountProfile>>(File.ReadAllText(path), options) ?? [];
    }

    private static string QueuePath(string root) =>
        WorkspaceQueuePaths.QueueDatabasePath(Path.Combine(root, "workspace"));

    private static void InsertLocalOnlyRows(string databasePath)
    {
        using var connection = AppDatabaseInitializer.OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_settings (key, value_json, updated_at)
            VALUES ('custom_marker', '"keep"', '2026-10-18T00:00:00');
            INSERT INTO upload_task_events (event_id, payload_json, created_at)
            VALUES ('evt-local', '{}', '2026-10-18T00:00:00');
            """;
        command.ExecuteNonQuery();
    }

    private static string ReadSetting(string databasePath, string key)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM app_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return (string)command.ExecuteScalar()!;
    }

    private static int CountEvents(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM upload_task_events";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "tiktok-config-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class DataRootScope : IDisposable
    {
        private readonly string? _previous;

        public DataRootScope(string path)
        {
            _previous = AppPaths.DataRootOverride;
            AppPaths.DataRootOverride = path;
        }

        public void Dispose() => AppPaths.DataRootOverride = _previous;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
