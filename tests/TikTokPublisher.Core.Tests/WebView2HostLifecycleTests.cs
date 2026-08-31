using FluentAssertions;

namespace TikTokPublisher.Core.Tests;

public sealed class WebView2HostLifecycleTests
{
    [Fact]
    public void Host_guards_async_initialization_and_disposed_controller_bounds_updates()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src", "TikTokPublisher", "TikTokPublisher.Ui", "Controls", "WebView2Host.cs"));

        source.Should().Contain("_lifecycleGeneration");
        source.Should().Contain("IsLifecycleCurrent(generation)");
        source.Should().Contain("Interlocked.Exchange(ref _controller, null)");
        source.Should().Contain("Interlocked.CompareExchange(ref _controller, null, controller)");
        source.Should().Contain("SizeChanged -= _sizeChangedHandler");
        source.Should().Contain("catch (Exception ex) when (IsDisposedControllerException(ex))");
        source.Should().Contain("0x8007139F");
        source.Should().Contain("InvalidateDisposedController(controller, ex)");

        source.IndexOf("_closed = true;", StringComparison.Ordinal)
            .Should().BeLessThan(
                source.IndexOf("SafeCloseController(Interlocked.Exchange(ref _controller, null))", StringComparison.Ordinal),
                "the host must stop callbacks before closing the COM controller");
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
