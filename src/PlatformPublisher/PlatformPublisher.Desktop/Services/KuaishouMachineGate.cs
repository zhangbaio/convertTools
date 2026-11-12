using PlatformPublisher.Common.Services;
using TikTokPublisher.Core.Queue;
using TikTokPublisher.Core.Services;

namespace PlatformPublisher.Desktop.Services;

/// <summary>
/// 本机所有快手账号共享的剧集名额、工程图名额和上传名额。一部剧从开始执行到上传结束、失败或停止占 1 个剧集名额。
/// 配置来自系统设置里的全局账号配置，修改后只影响之后新开始的剧。
/// </summary>
public sealed class KuaishouMachineGate
{
    public const int DefaultProjectImageConcurrency = 3;
    public const int MaxProjectImageConcurrency = 8;

    public static KuaishouMachineGate Shared { get; } = new();

    private readonly QueueProjectSlotCoordinator _projects = new();
    private readonly UploadSlotCoordinator _uploads = new();
    private readonly Func<(int Projects, string Mode, int Uploads, int ProjectImages)> _settings;
    private readonly object _uploadLock = new();
    private readonly object _projectImageLock = new();
    private readonly List<UploadHolder> _uploadHolders = new();
    private int _projectImagesInUse;

    public KuaishouMachineGate(Func<(int Projects, string Mode, int Uploads, int ProjectImages)>? settings = null)
    {
        _settings = settings ?? ReadSettings;
    }

    public async Task<IDisposable> AcquireProjectAsync(
        string accountKey,
        int accountOrder,
        CancellationToken cancellationToken,
        Action? onWait = null)
    {
        var owner = new object();
        var announced = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var settings = _settings();
                _projects.Configure(settings.Projects, settings.Mode);
                _projects.SetDemand(owner, [new QueueProjectSlotDemand(accountKey, accountOrder, 1)]);
                if (_projects.TryAcquire(accountKey, accountOrder))
                {
                    _projects.ClearDemand(owner);
                    return new Release(() => _projects.Release(accountKey));
                }

                if (!announced)
                {
                    announced = true;
                    onWait?.Invoke();
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _projects.ClearDemand(owner);
            throw;
        }
    }

    public async Task<IDisposable> AcquireProjectImageAsync(CancellationToken cancellationToken, Action? onWait = null)
    {
        var announced = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = _settings().ProjectImages;
            var limit = Math.Clamp(raw <= 0 ? DefaultProjectImageConcurrency : raw, 1, MaxProjectImageConcurrency);
            lock (_projectImageLock)
            {
                if (_projectImagesInUse < limit)
                {
                    _projectImagesInUse++;
                    return new Release(() =>
                    {
                        lock (_projectImageLock)
                        {
                            if (_projectImagesInUse > 0)
                                _projectImagesInUse--;
                        }
                    });
                }
            }

            if (!announced)
            {
                announced = true;
                onWait?.Invoke();
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IDisposable> AcquireUploadAsync(
        CancellationToken cancellationToken,
        string? holder = null,
        Func<string, Task>? onWait = null,
        string? accountKey = null)
    {
        var key = string.IsNullOrWhiteSpace(accountKey) ? "default" : accountKey.Trim();
        var readyOwner = new object();
        var readyPending = true;
        _uploads.SetReady(readyOwner, [new UploadReadyDemand(key, 1)]);
        var announced = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var limit = Math.Clamp(_settings().Uploads <= 0 ? 2 : _settings().Uploads, 1, 8);
                _uploads.Configure(limit);
                string? waitingFor = null;
                IDisposable? acquired = null;
                lock (_uploadLock)
                {
                    if (_uploads.TryAcquire(key))
                    {
                        _uploads.ClearReady(readyOwner);
                        readyPending = false;
                        var hold = new UploadHolder(holder?.Trim() ?? "");
                        _uploadHolders.Add(hold);
                        acquired = new Release(() =>
                        {
                            lock (_uploadLock)
                            {
                                _uploadHolders.Remove(hold);
                                _uploads.Release(key);
                            }
                        });
                    }
                    else if (!announced)
                    {
                        announced = true;
                        var labels = new List<string>();
                        foreach (var item in _uploadHolders)
                        {
                            if (!string.IsNullOrWhiteSpace(item.Label))
                                labels.Add(item.Label);
                        }

                        waitingFor = string.Join("、", labels);
                    }
                }

                if (acquired is not null)
                    return acquired;

                if (waitingFor is not null && onWait is not null)
                    await onWait(waitingFor).ConfigureAwait(false);

                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (readyPending)
                _uploads.ClearReady(readyOwner);
        }
    }

    private static (int Projects, string Mode, int Uploads, int ProjectImages) ReadSettings()
    {
        var settings = ClientSettingsStore.Load(PlatformPublisherPaths.SettingsDatabasePath);
        return (settings.TiktokGlobalProjectConcurrency, settings.TiktokAccountScheduleMode, settings.TiktokUploadConcurrency, settings.KuaishouProjectImageConcurrency);
    }

    private sealed class UploadHolder(string label)
    {
        public string Label { get; } = label;
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                release();
        }
    }
}
