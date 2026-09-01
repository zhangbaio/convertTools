using PlatformPublisher.Core.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed class PublishAccountItemViewModel
{
    public PublishAccountItemViewModel(PublishAccount model) => Model = model;

    public PublishAccount Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string ConfigSummary => string.IsNullOrWhiteSpace(Model.BaseConfigPath)
        ? "使用独立默认会话"
        : Path.GetFileName(Model.BaseConfigPath);
}
