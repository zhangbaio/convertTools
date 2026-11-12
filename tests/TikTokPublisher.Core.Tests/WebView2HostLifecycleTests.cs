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
        source.Should().Contain("ScheduleControllerRecovery()");
        source.Should().Contain("BeginInitialization(_nativeHandle, generation)");
        source.Should().Contain("_nativeHandle = handle.Handle");
        source.Should().Contain("_nativeHandle = IntPtr.Zero");
        source.Should().Contain("_lastRequestedUrl = url");
        source.Should().Contain(": _lastRequestedUrl;");
        source.Should().Contain("Interlocked.CompareExchange(ref _initializationInProgress, 1, 0)");
        source.Should().Contain("createdController.CoreWebView2.ProcessFailed");
        source.Should().Contain("InvalidateController(createdController, message)");

        source.IndexOf("_closed = true;", StringComparison.Ordinal)
            .Should().BeLessThan(
                source.IndexOf("SafeCloseController(Interlocked.Exchange(ref _controller, null))", StringComparison.Ordinal),
                "the host must stop callbacks before closing the COM controller");
    }

    [Fact]
    public void Hidden_host_sleeps_unless_cdp_automation_holds_it_awake()
    {
        var host = File.ReadAllText(FindRepoFile(
            "src", "TikTokPublisher", "TikTokPublisher.Ui", "Controls", "WebView2Host.cs"));
        var bridge = File.ReadAllText(FindRepoFile(
            "src", "TikTokPublisher", "TikTokPublisher.Ui", "Services", "EmbeddedBrowserAutomationBridge.cs"));

        host.Should().Contain("private bool IsIdle => !_renderedVisible && _automationLeases == 0;");
        host.Should().Contain("controller.IsVisible = false;");
        host.Should().Contain("CoreWebView2MemoryUsageTargetLevel.Low");
        host.Should().Contain("TrySuspendAsync()");
        host.Should().Contain("ResumeIfSuspended(controller);");

        bridge.Should().Contain("host.AcquireAutomation");
        bridge.Should().Contain("chromium.Disconnected += (_, _) => automationLease.Dispose();");
        bridge.IndexOf("host.AcquireAutomation", StringComparison.Ordinal)
            .Should().BeLessThan(
                bridge.IndexOf("ConnectOverCDPAsync(cdp)", StringComparison.Ordinal),
                "a suspended page must be resumed before Playwright attaches to it");
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
