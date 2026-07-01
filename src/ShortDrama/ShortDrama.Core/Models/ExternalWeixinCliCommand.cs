namespace ShortDrama.Core.Models;

public sealed record ExternalWeixinCliCommand(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    string ToolDirectory,
    string ScriptPath);
