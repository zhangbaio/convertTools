namespace TikTokPublisher.Core.Licensing;

public enum LicenseStartupAction
{
    PromptLogin,
    VerifyExistingState,
}

public static class LicenseStartupPolicy
{
    public static LicenseStartupAction Decide(LicenseState? state) =>
        state?.IsActivated() == true
            ? LicenseStartupAction.VerifyExistingState
            : LicenseStartupAction.PromptLogin;
}
