using FluentAssertions;
using Xunit;

namespace TikTokPublisher.Core.Tests;

public sealed class TikTokBrowserActionsOptionalSwitchTests
{
    [Fact]
    public void Removed_anchor_promotion_switch_is_optional_in_shared_form_flow()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src", "TikTokPublisher", "TikTokPublisher.Ui", "Services", "TikTok", "TikTokBrowserActions.cs"));

        var callStart = source.IndexOf("await SetOptionalSwitchAsync(", StringComparison.Ordinal);
        callStart.Should().BeGreaterThanOrEqualTo(0);
        source.IndexOf("\"#anchorPromotionStatus\"", callStart, StringComparison.Ordinal)
            .Should().BeGreaterThan(callStart,
                "TikTok no longer renders the legacy anchor promotion field for every series form");

        var methodStart = source.IndexOf(
            "private static async Task SetOptionalSwitchAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static async Task EnsureCommercialModeStepAsync",
            methodStart,
            StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        method.Should().Contain("if (await locator.CountAsync() == 0)");
        method.Should().Contain("按平台当前表单跳过");
        method.Should().Contain("return;");
    }

    private static string FindRepoFile(params string[] parts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(new[] { current.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
