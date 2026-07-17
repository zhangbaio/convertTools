namespace TikTokPublisher.Core.Licensing;

public sealed class LicenseState
{
    public string LicenseKey { get; set; } = "";
    public string LicenseKeyMasked { get; set; } = "";
    public string AccountUsername { get; set; } = "";
    public string Email { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Token { get; set; } = "";
    public string ActivatedAt { get; set; } = "";
    public string LastVerifiedAt { get; set; } = "";
    public string OfflineGraceUntil { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public string Edition { get; set; } = "";
    public string Licensee { get; set; } = "";
    public string ServerUrl { get; set; } = "";

    public bool IsActivated()
    {
        var account = (LicenseKey ?? "").Trim();
        if (account.Length == 0)
            account = (AccountUsername ?? "").Trim();
        return account.Length > 0
               && !string.IsNullOrWhiteSpace(MachineId)
               && !string.IsNullOrWhiteSpace(Token);
    }

    public bool IsExpired(DateTimeOffset? now = null)
    {
        var expiresAt = (ExpiresAt ?? "").Trim();
        if (expiresAt.Length == 0)
            return false;
        if (!DateTimeOffset.TryParse(expiresAt, out var expires))
            return false;
        var compareNow = now ?? DateTimeOffset.Now;
        if (expires.Offset != TimeSpan.Zero && compareNow.Offset == TimeSpan.Zero)
            compareNow = compareNow.ToOffset(expires.Offset);
        return expires <= compareNow;
    }
}
