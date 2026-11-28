using System.Text.Json;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Publishing;
using TikTokPublisher.Core.Services;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokApiUploadService
{
    private const string PublishingProfilePath = "/api/content-partner/account/profile";
    private const string InstitutionDetailPath = "/api/content-partner/institution/detail";
    private const string InstitutionContractsPath = "/api/content-partner/enterprise/general-contract/enterprises";
    private const string PublishingSubmissionPath = "/api/content-partner/institution/creator-hosting";
    internal const string PublishingAvatarServiceId = "fjfgkmyddx";

    internal static async Task<PublishAccountSubmission> CreatePublishingAccountAsync(
        TikTokAccountProfile account,
        PublishAccountMaterial material,
        Action<TikTokLoginResult> onLoginSaved,
        Action markSubmitting,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = TikTokPublishOptions.FromAccount(account);
        var authPath = EmbeddedBrowserLoginHelper.ResolveAuthPath(account);
        var session = PartnerSession.TryOpenForPublishing(authPath, options);
        try
        {
            return await SubmitPublishingAccountAsync(material,
                (path, body, ct) => session?.SendAsync("POST", path, body, ct)
                    ?? Task.FromResult(new FetchReply(401, "")),
                async ct =>
                {
                    var attempt = await TikTokApiLoginService.TryLoginAsync(account, ct);
                    if (attempt.State is null)
                        throw new InvalidOperationException(attempt.FailureReason +
                            "。请完成平台要求的登录或验证后再提交。");

                    ct.ThrowIfCancellationRequested();
                    var saved = TikTokApiLoginService.SaveVerifiedLogin(authPath, account.TiktokLoginEmail.Trim(), attempt.State);
                    session?.Dispose();
                    session = PartnerSession.TryOpenForPublishing(authPath, options);
                    onLoginSaved(saved);
                },
                async ct =>
                {
                    var previous = PartnerSession.Current.Value;
                    PartnerSession.Current.Value = session;
                    try
                    {
                        return await UploadImageAsync(null, material.AvatarPath, options, "头像", null, ct,
                            PublishingAvatarServiceId);
                    }
                    finally
                    {
                        PartnerSession.Current.Value = previous;
                    }
                },
                markSubmitting, progress, cancellationToken);
        }
        finally
        {
            session?.Dispose();
        }
    }

    internal static async Task<PublishAccountSubmission> SubmitPublishingAccountAsync(
        PublishAccountMaterial material,
        Func<string, string?, CancellationToken, Task<FetchReply>> post,
        Func<CancellationToken, Task> login,
        Func<CancellationToken, Task<string>> uploadAvatar,
        Action markSubmitting,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("正在核对已有登录状态…");
        var profile = await post(PublishingProfilePath, null, cancellationToken);
        if (PublishingSessionExpired(profile))
        {
            progress?.Report("正在通过接口登录…");
            await login(cancellationToken);
            profile = await post(PublishingProfilePath, null, cancellationToken);
            if (PublishingSessionExpired(profile))
                throw new InvalidOperationException("接口登录后的授权未通过校验，请先完成平台登录或验证。");
        }

        var accountData = ReadPublishingData(profile, "读取主账号");
        var identity = PublishingField(accountData, "profile");
        if (PublishingField(identity, "isParent", "is_parent").ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("平台只允许主账号提交创建申请。");
        if (ReadId(identity, "email").Length == 0 && ReadId(identity, "mobile").Length == 0 &&
            PublishingField(identity, "hasMobile", "has_mobile").ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("平台要求先绑定邮箱或手机号。");

        var institution = PublishingField(accountData, "institution");
        var institutionId = PublishingField(institution, "institutionId", "institution_id", "institutionID").ToString();
        if (string.IsNullOrWhiteSpace(institutionId) || !institutionId.All(char.IsAsciiDigit) || institutionId.All(ch => ch == '0'))
            throw new InvalidOperationException("平台未返回当前主账号所属机构。");

        var detail = ReadPublishingData(await post(InstitutionDetailPath,
            JsonSerializer.Serialize(new { institutionID = institutionId, withAccountList = true, withRegisterTaskStatus = true }),
            cancellationToken), "读取机构状态");
        if (PublishingField(detail, "isRegistering", "is_registering").ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("该机构已有创建申请正在处理，请先核对平台记录。");
        var hosting = PublishingField(detail, "creatorHostingConfig", "creator_hosting_config");
        if (hosting.ValueKind == JsonValueKind.Undefined)
            hosting = PublishingField(institution, "creatorHostingConfig", "creator_hosting_config");
        if (PublishingField(hosting, "enabled").ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("该机构已启用批量创建，请先核对平台记录。");

        var convertedInstitution = int.TryParse(PublishingField(institution, "sourceV2", "source_v2").ToString(), out var source)
            ? source is >= 2 and <= 50
            : ReadId(institution, "source") is "2" or "3";
        if (!convertedInstitution)
        {
            var contracts = ReadPublishingData(await post(InstitutionContractsPath, null, cancellationToken), "读取机构创建资格");
            if (PublishingField(contracts, "hasKybVerifiedEnterprise", "has_kyb_verified_enterprise").ValueKind != JsonValueKind.True ||
                PublishingField(contracts, "hasRegisteredVendor", "has_registered_vendor").ValueKind != JsonValueKind.True ||
                PublishingField(contracts, "hasValidSignedContract", "has_valid_signed_contract").ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("该机构尚未满足平台的企业认证、供应商登记或签约要求。");
        }

        progress?.Report("正在通过接口上传头像…");
        var avatarUri = await uploadAvatar(cancellationToken);
        if (string.IsNullOrWhiteSpace(avatarUri))
            throw new InvalidOperationException("头像上传接口未返回有效结果。");
        var body = JsonSerializer.Serialize(new
        {
            institutionID = institutionId,
            enabled = true,
            uniqueName = material.Nickname,
            avatarUri,
            description = material.Biography,
        });
        progress?.Report("正在通过接口提交申请…");
        cancellationToken.ThrowIfCancellationRequested();
        markSubmitting();
        var submitted = await post(PublishingSubmissionPath, body, cancellationToken);
        return submitted.Status is >= 200 and < 300
            ? ParsePublishingSubmission(submitted.Text)
            : new(PublishAccountCreationState.Pending, $"平台返回 HTTP {submitted.Status}，请核对是否受理");
    }

    private static bool PublishingSessionExpired(FetchReply reply)
    {
        if (reply.RetryAfter is not null)
            return false;
        if (reply.Status == 401 ||
            (reply.Status is >= 300 and < 400 && TikTokApiSites.IsLoginUrl(reply.Redirect?.AbsoluteUri)))
            return true;
        if (reply.Status is < 200 or >= 300)
            return false;

        using var json = JsonDocument.Parse(reply.Text);
        return ReadPublishingCode(json.RootElement) == "401";
    }

    private static JsonElement ReadPublishingData(FetchReply reply, string stage)
    {
        if (reply.Status is < 200 or >= 300)
            throw new InvalidOperationException($"{stage}失败（HTTP {reply.Status}），请检查接口状态后重试。");
        using var json = JsonDocument.Parse(reply.Text);
        var code = ReadPublishingCode(json.RootElement);
        var data = PublishingField(json.RootElement, "data");
        if (code == "0" && data.ValueKind == JsonValueKind.Object)
            return data.Clone();

        throw new InvalidOperationException(code.Length > 0
            ? $"{stage}未成功（业务码 {code}）。"
            : $"{stage}返回无法确认，请稍后重试。");
    }

    private static string ReadPublishingCode(JsonElement root)
    {
        var code = ReadId(root, "code");
        return code.Length == 0 ? ReadId(root, "statusCode") : code;
    }

    private static JsonElement PublishingField(JsonElement data, params string[] names)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
                if (data.TryGetProperty(name, out var value))
                    return value;
        }
        return default;
    }

    internal static PublishAccountSubmission ParsePublishingSubmission(string body)
    {
        using var json = JsonDocument.Parse(body);
        if (!int.TryParse(ReadId(json.RootElement, "code"), out var code))
            return new(PublishAccountCreationState.Pending, "平台未明确确认受理，请核对平台记录");
        if (code != 0)
            return new(PublishAccountCreationState.Retryable, $"平台未受理（业务码 {code}）");
        var receipt = PublishingField(PublishingField(json.RootElement, "data"), "request_id", "application_id", "requestId", "applicationId");
        return new(PublishAccountCreationState.Accepted, "平台已受理，后续状态以平台审核为准",
            receipt.ValueKind is JsonValueKind.String or JsonValueKind.Number ? receipt.ToString() : "");
    }

    internal static string ReadPublishingCookies(string storageStatePath, string site)
    {
        var host = new Uri(TikTokApiSites.Origin(site)).Host;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var json = JsonDocument.Parse(File.ReadAllText(storageStatePath));
        var cookies = PublishingField(json.RootElement, "cookies");
        if (cookies.ValueKind != JsonValueKind.Array)
            return "";

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cookie in cookies.EnumerateArray())
        {
            var domain = ReadString(cookie, "domain");
            // Some saved Drama Center sessions store the domestic root without a leading dot.
            if (domain.Equals("tiktokdramacenter.com", StringComparison.OrdinalIgnoreCase))
                domain = ".tiktokdramacenter.com";
            if (!host.Equals(domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase) &&
                !(domain.StartsWith('.') && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase)))
                continue;

            var expires = PublishingField(cookie, "expires");
            if (expires.ValueKind == JsonValueKind.Number && expires.TryGetDouble(out var expiry) && expiry >= 0 && expiry <= now)
                continue;
            var path = ReadString(cookie, "path");
            if (path is not ("" or "/"))
                continue;
            var name = ReadString(cookie, "name");
            var value = ReadString(cookie, "value");
            if (name.Length > 0 && value.Length > 0 && !name.Any(ch => char.IsWhiteSpace(ch) || ch is ';' or '=') &&
                !value.Any(ch => char.IsControl(ch) || ch is ';'))
                values.TryAdd(name, value);
        }
        return string.Join("; ", values.Select(pair => $"{pair.Key}={pair.Value}"));
    }
}

internal sealed record PublishAccountSubmission(PublishAccountCreationState State, string Message, string ReceiptId = "");
