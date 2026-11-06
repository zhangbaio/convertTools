namespace PlatformPublisher.Kuaishou.Publishing;

/// <summary>
/// 同一个快手分账账号上，信鸽派来的剧按任务队列并发数同时开工。上传槽位仍由调用方共用。
/// </summary>
public sealed class KuaishouDispatchLane<T>
{
    private readonly Queue<T> _pending = new();
    private int _running;

    public int Running => _running;
    public int Pending => _pending.Count;
    public bool Idle => _running == 0 && _pending.Count == 0;

    public IReadOnlyList<T> Enqueue(T item, int parallelism)
    {
        _pending.Enqueue(item);
        return Take(parallelism);
    }

    public IReadOnlyList<T> Completed(int parallelism, bool stopStarting)
    {
        if (_running > 0)
            _running--;
        if (stopStarting)
            _pending.Clear();
        return stopStarting ? [] : Take(parallelism);
    }

    private IReadOnlyList<T> Take(int parallelism)
    {
        var limit = KuaishouProjectQueue.NormalizeParallelism(parallelism);
        var started = new List<T>();
        while (_running < limit && _pending.Count > 0)
        {
            started.Add(_pending.Dequeue());
            _running++;
        }

        return started;
    }
}
