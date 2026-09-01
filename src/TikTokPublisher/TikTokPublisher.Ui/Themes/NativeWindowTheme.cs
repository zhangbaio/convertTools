using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TikTokPublisher.Ui.Themes;

/// <summary>Applies the deep-ocean palette to native Windows title bars.</summary>
public static class NativeWindowTheme
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;
    private static readonly ConditionalWeakTable<Window, Registration> Registrations = new();

    public static readonly AttachedProperty<bool> UseDeepOceanTitleBarProperty =
        AvaloniaProperty.RegisterAttached<Window, Window, bool>(
            "UseDeepOceanTitleBar",
            defaultValue: false);

    static NativeWindowTheme()
    {
        UseDeepOceanTitleBarProperty.Changed.AddClassHandler<Window>(
            (window, args) => SetRegistration(window, args.NewValue is true));
    }

    public static bool GetUseDeepOceanTitleBar(Window window) =>
        window.GetValue(UseDeepOceanTitleBarProperty);

    public static void SetUseDeepOceanTitleBar(Window window, bool value) =>
        window.SetValue(UseDeepOceanTitleBarProperty, value);

    private static void SetRegistration(Window window, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (enabled)
        {
            _ = Registrations.GetValue(window, static target => new Registration(target));
            return;
        }

        if (Registrations.TryGetValue(window, out var registration))
        {
            registration.Dispose();
            Registrations.Remove(window);
        }
    }

    internal static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    private static void Apply(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;

        try
        {
            var darkMode = 1;
            var captionColor = ToColorRef(Color.Parse("#0D243A"));
            var textColor = ToColorRef(Color.Parse("#F7FBFF"));
            _ = DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkMode,
                ref darkMode,
                sizeof(int));
            _ = DwmSetWindowAttribute(
                handle,
                DwmCaptionColor,
                ref captionColor,
                sizeof(int));
            _ = DwmSetWindowAttribute(
                handle,
                DwmTextColor,
                ref textColor,
                sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Non-DWM or older Windows environments retain the system title bar.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows environments retain the system title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    private sealed class Registration : IDisposable
    {
        private readonly Window _window;

        public Registration(Window window)
        {
            _window = window;
            _window.Opened += OnOpened;
            Apply(_window);
        }

        public void Dispose() => _window.Opened -= OnOpened;

        private void OnOpened(object? sender, EventArgs args) => Apply(_window);
    }
}
