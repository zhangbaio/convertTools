using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using TikTokPublisher.Core.Models;
using TikTokPublisher.Core.Services.VideoDedup;

namespace TikTokPublisher.Core.Services;

public enum TikTokAccountImportStatus { Ready, Succeeded, Skipped, Failed }

public sealed record TikTokAccountImportOptions(
    string Market, bool UseEnglish, string SourceLanguage, string TemplateId,
    string Declarant, string SealPath, bool CreateWorkspaces, string WorkspaceRoot);

public sealed record TikTokAccountImportRow(
    int Number, string Company, string MaskedEmail, string Workspace, TikTokAccountImportStatus Status, string Reason);

public sealed record TikTokAccountImportResult(
    IReadOnlyList<TikTokAccountImportRow> Rows, IReadOnlyList<TikTokAccountProfile> Accounts);

public sealed class TikTokAccountImportPlan
{
    internal TikTokAccountImportPlan(TikTokAccountImportOptions options, IReadOnlyList<ImportCandidate> candidates)
    {
        Options = options;
        Candidates = candidates;
        Rows = candidates.Select(item => item.Row).ToArray();
    }

    internal TikTokAccountImportOptions Options { get; }
    internal IReadOnlyList<ImportCandidate> Candidates { get; }
    public IReadOnlyList<TikTokAccountImportRow> Rows { get; }
}

internal sealed class ImportCandidate(TikTokAccountImportRow row, string email = "", string password = "")
{
    public TikTokAccountImportRow Row { get; } = row;
    public string Email { get; } = email;
    public string Password { get; } = password;
}

public sealed class TikTokAccountBatchImporter(AccountStore store, VideoDedupTemplateCatalog catalog)
{
    public TikTokAccountImportPlan Preview(string text, TikTokAccountImportOptions options,
        IReadOnlyDictionary<int, string>? workspaceOverrides = null)
    {
        var existing = new AccountKeys(store.Accounts);
        var candidates = new List<ImportCandidate>();
        var root = NormalizeDirectory(options.WorkspaceRoot);
        var rootValid = root.Length > 0 && Directory.Exists(root);
        var settingsError = CheckSeal(options);
        foreach (var lines in SplitRecords(text))
        {
            var number = candidates.Count + 1;
            var (company, email, password, error) = ParseRecord(lines);
            var workspace = "";
            var status = TikTokAccountImportStatus.Ready;
            var reason = error;
            if (error.Length > 0)
                status = TikTokAccountImportStatus.Failed;
            else if (existing.Conflict(company, email) is { } duplicate)
            {
                status = TikTokAccountImportStatus.Skipped;
                reason = duplicate;
            }
            else
            {
                reason = settingsError;
                var manual = workspaceOverrides?.GetValueOrDefault(number) ?? "";
                if (reason.Length == 0 && options.CreateWorkspaces)
                {
                    if (!rootValid)
                        reason = "请选择存在的工作目录父目录。";
                    else
                        workspace = FindAvailablePath(root, company, existing.Workspaces);
                }
                else if (reason.Length == 0 && !string.IsNullOrWhiteSpace(manual))
                {
                    workspace = NormalizeDirectory(manual);
                    if (workspace.Length == 0 || !Directory.Exists(workspace))
                        reason = "逐行指定的工作目录不存在。";
                    else if (existing.Workspaces.Contains(workspace))
                        reason = "工作目录已有账号使用。";
                }

                if (reason.Length > 0)
                    status = TikTokAccountImportStatus.Failed;
                else
                    existing.Add(company, email, workspace);
            }

            var label = company.Length == 0 ? $"第 {number} 条" : company;
            var row = new TikTokAccountImportRow(number, label, MaskEmail(email), workspace, status,
                status == TikTokAccountImportStatus.Ready ? "可导入" : reason);
            candidates.Add(status == TikTokAccountImportStatus.Ready
                ? new ImportCandidate(row, email, password) : new ImportCandidate(row));
        }

        return new TikTokAccountImportPlan(options with { WorkspaceRoot = root }, candidates);
    }

    public TikTokAccountImportResult Import(TikTokAccountImportPlan plan)
    {
        var options = plan.Options;
        var rows = plan.Rows.ToArray();
        var existing = new AccountKeys(store.Accounts);
        var accountIds = store.Accounts.Select(account => account.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prepared = new List<(int Index, TikTokAccountProfile Account, string CreatedWorkspace)>();
        var sealError = CheckSeal(options);
        foreach (var candidate in plan.Candidates.Where(item => item.Row.Status == TikTokAccountImportStatus.Ready))
        {
            var row = candidate.Row;
            var index = row.Number - 1;
            if (existing.Conflict(row.Company, candidate.Email) is { } duplicate)
            {
                rows[index] = row with { Status = TikTokAccountImportStatus.Skipped, Reason = duplicate };
                continue;
            }
            if (sealError.Length > 0)
            {
                rows[index] = row with { Status = TikTokAccountImportStatus.Failed, Reason = sealError };
                continue;
            }

            var created = "";
            var workspace = row.Workspace;
            string? profileDirectory = null;
            var stage = "工作目录不可用";
            try
            {
                if (options.CreateWorkspaces)
                    workspace = created = CreateWorkspace(options.WorkspaceRoot, row.Company, existing.Workspaces);
                else if (workspace.Length > 0 && (!Directory.Exists(workspace) || existing.Workspaces.Contains(workspace)))
                    throw new IOException("工作目录已变更或被占用。");

                stage = "无法创建账号档案";
                var account = CreateProfile(options, candidate, workspace, accountIds);
                stage = "TikTok 密码无法在本机加密保存";
                account.SetProtectedLoginPassword(candidate.Password);
                stage = "无法创建账号会话目录";
                Directory.CreateDirectory(account.ProfileDir);
                profileDirectory = account.ProfileDir;
                prepared.Add((index, account, created));
                rows[index] = row with { Workspace = workspace };
                existing.Add(row.Company, candidate.Email, workspace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or CryptographicException or PlatformNotSupportedException)
            {
                RemoveEmptyDirectory(created);
                RemoveEmptyDirectory(profileDirectory);
                var detail = ex switch
                {
                    DirectoryNotFoundException => "目录已不存在",
                    UnauthorizedAccessException => "没有写入权限",
                    PathTooLongException => "路径过长",
                    _ => "",
                };
                rows[index] = row with { Status = TikTokAccountImportStatus.Failed,
                    Reason = detail.Length == 0 ? stage : $"{stage}，{detail}" };
            }
        }

        var accounts = prepared.Select(item => item.Account).ToArray();
        if (accounts.Length == 0)
            return new TikTokAccountImportResult(rows, []);

        var ids = accounts.Select(account => account.Id).ToArray();
        var bound = false;
        try
        {
            catalog.BindImportedAccounts(options.TemplateId, ids);
            bound = true;
            store.AppendImportedAccounts(accounts);
            foreach (var item in prepared)
                rows[item.Index] = rows[item.Index] with { Status = TikTokAccountImportStatus.Succeeded, Reason = "账号已创建" };
            return new TikTokAccountImportResult(rows, accounts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            var message = ex is InvalidOperationException ? ex.Message
                : bound ? "账号档案保存失败" : "去重模板绑定失败";
            if (bound)
            {
                try { catalog.RemoveImportedAccountBindings(ids); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or JsonException)
                {
                    message += "，临时模板绑定清理失败";
                }
            }
            foreach (var item in prepared)
            {
                RemoveEmptyDirectory(item.CreatedWorkspace);
                RemoveEmptyDirectory(item.Account.ProfileDir);
                rows[item.Index] = rows[item.Index] with { Status = TikTokAccountImportStatus.Failed, Reason = message };
            }
            return new TikTokAccountImportResult(rows, []);
        }
    }

    private static TikTokAccountProfile CreateProfile(TikTokAccountImportOptions options, ImportCandidate candidate,
        string workspace, HashSet<string> accountIds)
    {
        string id;
        do { id = AccountStore.CreateAccountId(); }
        while (!accountIds.Add(id) || Directory.Exists(AppPaths.ProfileDirFor(id)));
        var profile = AccountStore.CreateProfileSkeleton(id, candidate.Row.Company);
        profile.TiktokImportedCompanyName = candidate.Row.Company;
        profile.TiktokAccountNickname = candidate.Row.Company;
        profile.TiktokProofCopyrightCompanyName = candidate.Row.Company;
        profile.TiktokLoginEmail = candidate.Email;
        profile.TiktokProofDeclarantCompanyName = options.Declarant.Trim();
        profile.TiktokProofSealPath = options.SealPath.Trim();
        profile.TiktokUploadProfilePath = profile.LastWorkspace = workspace;
        profile.TiktokDramaMarket = TikTokAccountProfile.NormalizeDramaMarket(options.Market);
        profile.TiktokLocalDramaEnglishPublishText = options.UseEnglish && profile.TiktokDramaMarket == TikTokAccountProfile.DramaMarketLocal;
        profile.TiktokSourceLanguage = TikTokAccountProfile.NormalizeStoredSourceLanguage(options.SourceLanguage, profile.TiktokDramaMarket);
        profile.TiktokRewriteFollowsSourceLanguage = TikTokAccountProfile.RewriteFollowsImportedLanguage(profile.TiktokDramaMarket);
        return profile;
    }

    private static string CheckSeal(TikTokAccountImportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SealPath)) return "";
        if (Path.GetExtension(options.SealPath.Trim()).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
            return "印章图片须为 PNG、JPG、JPEG 或 BMP。";
        try
        {
            using var stream = File.OpenRead(options.SealPath.Trim());
            return Image.Identify(stream) is not null ? "" : "印章图片无法读取。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException or ImageFormatException)
        {
            return "印章图片无法读取。";
        }
    }

    private static IEnumerable<IReadOnlyList<string>> SplitRecords(string text)
    {
        var record = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Trim() == "\\")
            {
                if (record.Count == 0) continue;
                yield return record;
                record = [];
            }
            else
                record.Add(line);
        }
        if (record.Count > 0) yield return record;
    }

    private static (string Company, string Email, string Password, string Error) ParseRecord(IReadOnlyList<string> lines)
    {
        var markdown = lines.Count >= 2 && lines[0].TrimEnd().EndsWith('\\') && lines[1].TrimEnd().EndsWith('\\');
        string Unwrap(string line)
        {
            if (!markdown) return line;
            var last = line.TrimEnd();
            return last.EndsWith('\\') ? last[..^1] : line;
        }

        var company = Unwrap(lines[0]).Trim();
        if (company.Length == 0 || company.Contains('@') || company.Contains('：') || company.Contains(':'))
            return ("", "", "", "首行需要填写公司名称。");
        if (lines.Count < 2)
            return (company, "", "", "缺少 TikTok 邮箱。");

        var email = Unwrap(lines[1]).Trim();
        if (!MailAddress.TryCreate(email, out var parsed) || !string.Equals(parsed.Address, email, StringComparison.OrdinalIgnoreCase))
            return (company, "", "", "TikTok 邮箱格式无效。");

        string? password = null;
        foreach (var raw in lines.Skip(2))
        {
            var line = Unwrap(raw);
            var separator = line.IndexOfAny(['：', ':']);
            var label = separator > 0 ? line[..separator].Trim() : "";
            if (label == "邮箱密码") continue;
            if (label.Equals("TT密码", StringComparison.OrdinalIgnoreCase) && password is null)
            {
                password = line[(separator + 1)..];
                continue;
            }
            return (company, email, "", "密码行格式错误或 TT 密码重复。");
        }
        return string.IsNullOrWhiteSpace(password)
            ? (company, email, "", "缺少 TT 密码。") : (company, email, password, "");
    }

    private static string MaskEmail(string email)
    {
        var separator = email.IndexOf('@');
        return separator > 0 ? email[..1] + "***" + email[separator..] : "";
    }

    private static string NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return ""; }
    }

    private static string SafeFolderName(string company)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(company.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray())
            .Trim().TrimEnd('.');
        if (name.Length == 0) return "TikTok账号";
        if (name.Length > 100) name = name[..100].TrimEnd(' ', '.');
        var stem = name.Split('.')[0].TrimEnd();
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && stem[3] is >= '0' and <= '9' &&
             (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
            name = "_" + name;
        return name;
    }

    private static string FindAvailablePath(string root, string company, HashSet<string> reserved)
    {
        var name = SafeFolderName(company);
        for (var number = 1; ; number++)
        {
            var candidate = Path.Combine(root, number == 1 ? name : $"{name} ({number})");
            if (!reserved.Contains(candidate) && !Path.Exists(candidate))
                return candidate;
        }
    }

    private static string CreateWorkspace(string root, string company, HashSet<string> reserved)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        var temporary = Path.Combine(root, $".tiktok-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            while (true)
            {
                var candidate = FindAvailablePath(root, company, reserved);
                try
                {
                    Directory.Move(temporary, candidate);
                    return candidate;
                }
                catch (IOException) when (Path.Exists(candidate)) { }
            }
        }
        finally { RemoveEmptyDirectory(temporary); }
    }

    private static void RemoveEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class AccountKeys
    {
        private readonly HashSet<string> _companies = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _emails = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Workspaces { get; } = new(StringComparer.OrdinalIgnoreCase);

        public AccountKeys(IEnumerable<TikTokAccountProfile> accounts)
        {
            foreach (var account in accounts)
            {
                if (!string.IsNullOrWhiteSpace(account.TiktokImportedCompanyName))
                    _companies.Add(CompanyKey(account.TiktokImportedCompanyName));
                else
                {
                    _companies.Add(CompanyKey(account.TiktokAccountNickname));
                    _companies.Add(CompanyKey(account.Name));
                }
                _emails.Add(account.TiktokLoginEmail?.Trim() ?? "");
                _emails.Add(account.TiktokLastLoginEmail?.Trim() ?? "");
                Workspaces.Add(NormalizeDirectory(account.TiktokUploadProfilePath));
                Workspaces.Add(NormalizeDirectory(account.LastWorkspace));
            }
        }

        private static string CompanyKey(string? name) => (name ?? "").Trim().Normalize(NormalizationForm.FormKC);

        public string? Conflict(string company, string email)
        {
            if (_companies.Contains(CompanyKey(company))) return "已有同名公司或本批公司重复";
            return _emails.Contains(email) ? "已有相同邮箱或本批邮箱重复" : null;
        }

        public void Add(string company, string email, string workspace)
        {
            _companies.Add(CompanyKey(company));
            _emails.Add(email);
            if (workspace.Length > 0) Workspaces.Add(workspace);
        }
    }
}
