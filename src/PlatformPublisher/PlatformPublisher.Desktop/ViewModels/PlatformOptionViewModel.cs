using PlatformPublisher.Core.Models;

namespace PlatformPublisher.Desktop.ViewModels;

public sealed record PlatformOptionViewModel(PublishPlatform Value, string Name, string Description);
