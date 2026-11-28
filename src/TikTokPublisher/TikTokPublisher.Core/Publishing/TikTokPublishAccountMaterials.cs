using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;

namespace TikTokPublisher.Core.Publishing;

public enum PublishAccountMaterialState { Ready, Invalid, Used, Pending }

public sealed record PublishAccountMaterial(
    string DirectoryPath, string Nickname, string Biography, string AvatarPath,
    PublishAccountMaterialState State, string Detail);

public static class TikTokPublishAccountMaterials
{
    public const string StatusFileName = "creation-status.json";
    private static readonly string[] AvatarNames = ["avatar.png", "avatar.jpg", "avatar.jpeg"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IReadOnlyList<PublishAccountMaterial> Scan(string root, CancellationToken ct)
    {
        var rows = new List<PublishAccountMaterial>();
        foreach (var path in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(Read(path));
        }

        var duplicates = rows.GroupBy(row => row.Nickname, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Select(row => row.State == PublishAccountMaterialState.Ready && duplicates.Contains(row.Nickname)
            ? row with { State = PublishAccountMaterialState.Invalid, Detail = "昵称与其他素材目录重复" }
            : row).ToArray();
    }

    private static PublishAccountMaterial Read(string path)
    {
        var nickname = Path.GetFileName(path);
        PublishAccountMaterial Row(PublishAccountMaterialState state, string detail) => new(path, nickname, "", "", state, detail);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return Row(PublishAccountMaterialState.Invalid, "素材目录是链接");
            var record = ReadStatus(path);
            if (record?.State == "accepted")
                return Row(PublishAccountMaterialState.Used, "已受理，自动跳过");
            if (record is not null && record.State != "rejected")
                return Row(PublishAccountMaterialState.Pending, "上次提交结果待核对，自动跳过");
            if (!Regex.IsMatch(nickname, @"\A[A-Za-z0-9_.]{2,18}\z") || nickname.All(char.IsAsciiDigit) ||
                nickname.EndsWith('.'))
                return Row(PublishAccountMaterialState.Invalid, "昵称须为 2–18 个英文字母、数字、下划线或句点，不能全为数字或以句点结尾");

            var bio = new FileInfo(Path.Combine(path, "bio.txt"));
            if (!bio.Exists || (bio.Attributes & FileAttributes.ReparsePoint) != 0 || bio.Length > 65536)
                return Row(PublishAccountMaterialState.Invalid, "缺少可读取的 bio.txt 或文件过大");
            var biography = File.ReadAllText(bio.FullName, new UTF8Encoding(false, true)).Trim();
            if (biography.Length is 0 or > 160)
                return Row(PublishAccountMaterialState.Invalid, "简介须为 1–160 个字符");

            var avatars = AvatarNames.Select(name => Path.Combine(path, name)).Where(File.Exists).ToArray();
            if (avatars.Length != 1 || (File.GetAttributes(avatars[0]) & FileAttributes.ReparsePoint) != 0)
                return Row(PublishAccountMaterialState.Invalid, "请放入一张 avatar.png、avatar.jpg 或 avatar.jpeg");
            var info = Image.Identify(avatars[0]);
            if (info is null || info.Width != info.Height || info.Width < 500)
                return Row(PublishAccountMaterialState.Invalid, "头像须为至少 500 × 500 的正方形");
            var format = Image.DetectFormat(avatars[0]).Name;
            if (format is not ("PNG" or "JPEG"))
                return Row(PublishAccountMaterialState.Invalid, "头像实际格式须为 PNG 或 JPEG");
            return new(path, nickname, biography, avatars[0], PublishAccountMaterialState.Ready,
                record is null ? "可提交" : "上次未受理，可重试");
        }
        catch (JsonException)
        {
            return Row(PublishAccountMaterialState.Pending, "使用标记无法读取，自动跳过");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException
                                   or UnknownImageFormatException or InvalidImageContentException)
        {
            return Row(PublishAccountMaterialState.Invalid, "无法读取素材文件或图片已损坏");
        }
    }

    public static MaterialReservation Reserve(PublishAccountMaterial material, string accountId)
    {
        var directory = material.DirectoryPath;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("素材目录已变为链接");
        var leasePath = Path.Combine(directory, ".creation-status.lock");
        if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("素材占用文件不可用");
        var file = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var latest = Read(directory);
            if (latest.State != PublishAccountMaterialState.Ready)
                throw new InvalidOperationException(latest.Detail);
            if (latest.Biography != material.Biography || latest.AvatarPath != material.AvatarPath)
                throw new InvalidOperationException("素材已改动，请刷新预览");
            return new MaterialReservation(file, material, accountId);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static MaterialStatus? ReadStatus(string directory)
    {
        var path = Path.Combine(directory, StatusFileName);
        if (!File.Exists(path))
            return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new JsonException("Invalid marker.");
        return JsonSerializer.Deserialize<MaterialStatus>(File.ReadAllText(path), JsonOptions)
            ?? throw new JsonException("Empty marker.");
    }

    public sealed class MaterialReservation : IDisposable
    {
        private readonly FileStream _file;
        private readonly PublishAccountMaterial _material;
        private readonly string _accountId;
        private DateTimeOffset? _submittedAt;

        internal MaterialReservation(FileStream file, PublishAccountMaterial material, string accountId)
        {
            _file = file;
            _material = material;
            _accountId = accountId;
        }
        public bool SubmissionStarted => _submittedAt.HasValue;

        public void BeginSubmission()
        {
            var submittedAt = DateTimeOffset.UtcNow;
            WriteStatus("submitting", submittedAt, "");
            _submittedAt = submittedAt;
        }

        public void Complete(bool accepted, string receiptId)
        {
            if (_submittedAt is not { } submittedAt)
                throw new InvalidOperationException("尚未开始提交");
            WriteStatus(accepted ? "accepted" : "rejected", submittedAt, receiptId);
        }

        private void WriteStatus(string state, DateTimeOffset submittedAt, string receiptId)
        {
            var target = Path.Combine(_material.DirectoryPath, StatusFileName);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var marker = new MaterialStatus(state, _accountId, _material.Nickname, submittedAt, receiptId);
                File.WriteAllText(temporary, JsonSerializer.Serialize(marker, JsonOptions));
                File.Move(temporary, target, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        public void Dispose() => _file.Dispose();
    }

    private sealed record MaterialStatus(string State, string AccountId, string Nickname, DateTimeOffset SubmittedAt, string ReceiptId);
}
