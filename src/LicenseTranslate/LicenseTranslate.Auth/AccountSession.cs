namespace LicenseTranslate.Auth;

public sealed class AccountSession
{
    public string ServerUrl { get; set; } = "";
    public string Account { get; set; } = "";
    public string Password { get; set; } = "";
    public string Token { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string Email { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public string LastVerifiedAt { get; set; } = "";

    public bool IsSignedIn() =>
        !string.IsNullOrWhiteSpace(Account)
        && !string.IsNullOrWhiteSpace(Token)
        && !string.IsNullOrWhiteSpace(MachineId);

    public bool IsExpired(DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(ExpiresAt) || !DateTimeOffset.TryParse(ExpiresAt, out var expires))
            return false;
        return expires <= (now ?? DateTimeOffset.Now);
    }

    public bool NeedsVerification(DateTimeOffset? now = null)
    {
        if (!DateTimeOffset.TryParse(LastVerifiedAt, out var verified))
            return true;
        return (now ?? DateTimeOffset.Now) >= verified.Add(AccountProduct.VerifyInterval);
    }
}
