namespace ShortDrama.Core.Models;

public sealed record WeixinUploadRequest(
    string ProjectKey,
    string ProjectDir,
    string DisplayName,
    string? ConfigPath,
    string? ConfigName);

public sealed record WeixinUploadResult(
    bool Ok,
    string ProjectDir,
    string? ConfigPath,
    string? Message = null);
