using PlatformPublisher.Common.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed record PublishJobKindOptionViewModel(PublishJobKind Value, string Name, string Description);
