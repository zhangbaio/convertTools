namespace TikTokPublisher.RegistrationPdf;

internal static class RegistrationPdfPaths
{
    public const string AppName = "注册PDF工具";
    public const string DeviceName = "注册PDF工具";
    public const string AppVersion = "1.0.0";

    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".registration_pdf");
}
