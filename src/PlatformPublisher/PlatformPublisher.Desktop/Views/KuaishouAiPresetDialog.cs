using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PlatformPublisher.Kuaishou.Publishing;
using TikTokPublisher.Core.Services;
using static PlatformPublisher.Desktop.Views.KuaishouAccountAiEditor;

namespace PlatformPublisher.Desktop.Views;

internal static class KuaishouAiPresetDialog
{
    internal static Task<KuaishouTextPreset?> EditTextAsync(Window owner, KuaishouTextPreset selected, bool create)
    {
        var isNew = create || selected.Id.Length == 0;
        var name = Input(isNew ? "" : selected.Name);
        var endpoint = Input(selected.Endpoint);
        var model = Input(selected.Model);
        var timeout = Input(selected.Timeout.ToString());
        return EditAsync<KuaishouTextPreset>(owner, "AI 文本模型预设", isNew,
            [Field("预设名称", name), Field("接口地址", endpoint), Field("模型", model), Field("超时（秒）", timeout)],
            () =>
            {
                if (!int.TryParse(Read(timeout), out var seconds) || seconds is < 10 or > 600)
                    throw new ArgumentException("超时请填写 10 到 600 秒。");
                Validate(name, endpoint, model);
                return new(Id(selected.Id, isNew), Read(name), Read(endpoint), Read(model), seconds);
            });
    }

    internal static Task<KuaishouImagePreset?> EditImageAsync(Window owner, KuaishouImagePreset selected, bool create)
    {
        var isNew = create || selected.Id.Length == 0;
        var name = Input(isNew ? "" : selected.Name);
        var endpoint = Input(selected.Endpoint);
        var model = Input(selected.Model);
        var provider = new ComboBox
        {
            ItemsSource = new[] { "豆包 Seedream", "OpenAI Image 2 兼容" },
            SelectedIndex = selected.Provider == "ofox_image2" ? 1 : 0,
        };
        var doubaoRatio = new ComboBox
        {
            ItemsSource = new[] { "3:4", "9:16", "2:3", "4:3", "16:9", "3:2", "1:1", "21:9", "auto" },
            SelectedItem = PosterImageConfigHelper.NormalizeDoubaoImageRatio(selected.Provider == "doubao" ? selected.Size : ""),
        };
        var imageSize = Input(selected.Provider == "ofox_image2" && selected.Size.Length > 0 ? selected.Size : "auto");
        var quality = new ComboBox
        {
            ItemsSource = new[] { "low", "medium", "high", "auto" },
            SelectedItem = selected.Provider == "ofox_image2" && selected.Quality is "low" or "medium" or "high" or "auto"
                ? selected.Quality : "medium",
        };
        var doubaoField = Field("竖屏海报比例（2K）", doubaoRatio);
        var sizeField = Field("竖屏请求尺寸", imageSize);
        var qualityField = Field("图片质量", quality);
        void UpdateImageFields()
        {
            var openAi = provider.SelectedIndex == 1;
            doubaoField.IsVisible = !openAi;
            sizeField.IsVisible = qualityField.IsVisible = openAi;
        }
        provider.SelectionChanged += (_, _) => UpdateImageFields();
        UpdateImageFields();

        return EditAsync<KuaishouImagePreset>(owner, "AI 图片模型预设", isNew,
            [Field("预设名称", name), Field("图片服务", provider), Field("接口地址", endpoint),
                Field("模型", model), doubaoField, sizeField, qualityField],
            () =>
            {
                Validate(name, endpoint, model);
                var openAi = provider.SelectedIndex == 1;
                var size = openAi ? Read(imageSize) : doubaoRatio.SelectedItem as string ?? "3:4";
                if (openAi && !ValidImageSize(size))
                    throw new ArgumentException("图片尺寸请填 auto 或宽x高，例如 1024x1536。");
                return new(Id(selected.Id, isNew), Read(name), Read(endpoint), Read(model),
                    openAi ? "ofox_image2" : "doubao", size,
                    openAi ? quality.SelectedItem as string ?? "medium" : "2K");
            });
    }

    private static Task<T?> EditAsync<T>(Window owner, string title, bool isNew, Control[] fields, Func<T> build)
        where T : class
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            MaxHeight = 680,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var message = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = isNew ? "保存为预设" : "确定", IsDefault = true };
        var cancel = new Button { Content = "取消", IsCancel = true };
        save.Classes.Add("primaryAction");
        save.Click += (_, _) =>
        {
            try { dialog.Close(build()); }
            catch (ArgumentException ex) { message.Text = ex.Message; }
        };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Children = { cancel, save },
        };
        var form = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        foreach (var field in fields) form.Children.Add(field);
        form.Children.Add(message);
        form.Children.Add(buttons);
        dialog.Content = new ScrollViewer { Content = form };
        return dialog.ShowDialog<T?>(owner);
    }

    private static void Validate(TextBox name, TextBox endpoint, TextBox model)
    {
        if (Read(name).Length == 0 || Read(model).Length == 0)
            throw new ArgumentException("请填写预设名称和模型。");
        if (KuaishouAccountAiOptions.EndpointKey(Read(endpoint)).Length == 0)
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 接口地址。");
    }

    private static bool ValidImageSize(string size)
    {
        if (size == "auto") return true;
        var parts = size.Split('x');
        return parts.Length == 2 && parts.All(value => int.TryParse(value, out var number) && number > 0);
    }

    private static string Id(string saved, bool isNew) => isNew ? Guid.NewGuid().ToString("N") : saved;
    private static TextBox Input(string value) => new() { Text = value };
    private static string Read(TextBox value) => value.Text?.Trim() ?? "";
}
