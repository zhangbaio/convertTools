namespace ShortDrama.Core.Models;

public sealed record WeixinAuthStateInfo(
    string AuthFilePath,
    bool Exists,
    bool IsValidJson,
    int CookiesCount,
    int OriginsCount,
    string Message);
