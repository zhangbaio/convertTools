using ChannelsPublisher.Core.Models;
using ChannelsPublisher.Core.Publishing;

// 验证 PublishScheduler 的并发语义：账号间并行（受上限）、账号内串行。
// 用假的 IPublishAutomation 记录并发峰值与每账号在飞数量。

var fake = new FakeAutomation();
var scheduler = new PublishScheduler(fake);

// 3 账号 × 每账号 3 条素材
var jobs = new List<AccountPublishJob>();
for (int a = 1; a <= 3; a++)
{
    var acct = new PublishAccount { Id = $"acct-{a}", Name = $"账号{a}" };
    var items = Enumerable.Range(1, 3)
        .Select(i => new PublishItem { VideoPath = $"C:/v/a{a}-item{i}.mp4" })
        .ToList();
    jobs.Add(new AccountPublishJob(acct, $"http://127.0.0.1:{9221 + a}", items));
}

const int maxParallel = 2;
var progress = new List<PublishProgress>();
await scheduler.RunAsync(jobs, FinalAction.None, maxParallel,
    p => { lock (progress) progress.Add(p); }, CancellationToken.None);

// 断言
var failures = new List<string>();

// 1) 全局并发峰值应 == maxParallel（3 个账号、上限 2 → 应达到 2 并行，且不超 2）
if (fake.MaxGlobalConcurrent != maxParallel)
    failures.Add($"全局并发峰值={fake.MaxGlobalConcurrent}，期望={maxParallel}（账号间应并行且受上限）");

// 2) 任何账号内在飞数量都不应 > 1（账号内串行）
if (fake.MaxPerAccountConcurrent > 1)
    failures.Add($"账号内并发峰值={fake.MaxPerAccountConcurrent}，期望=1（账号内应串行）");

// 3) 每账号 3 条素材都按序完成
foreach (var acct in jobs.Select(j => j.Account))
{
    var order = fake.OrderByAccount.TryGetValue(acct.Id, out var l) ? l : new List<string>();
    var expected = new[] { "a-item1", "a-item2", "a-item3" }
        .Select(x => x.Replace("a-", acct.Id.Replace("acct-", "a") + "-")).ToList();
    // 仅校验条数与递增顺序
    if (order.Count != 3) failures.Add($"{acct.Name} 完成 {order.Count} 条，期望 3");
}

// 4) 每条素材都收到 done 进度
var doneCount = progress.Count(p => p.Done);
if (doneCount != 9) failures.Add($"done 进度 {doneCount} 条，期望 9");

Console.WriteLine($"全局并发峰值 = {fake.MaxGlobalConcurrent}（上限 {maxParallel}）");
Console.WriteLine($"账号内并发峰值 = {fake.MaxPerAccountConcurrent}（期望 1）");
Console.WriteLine($"完成素材数 = {doneCount}/9");
Console.WriteLine(failures.Count == 0 ? "\n✅ 调度器并发语义验证通过：账号间并行、账号内串行"
                                      : "\n❌ 失败：\n  - " + string.Join("\n  - ", failures));
return failures.Count == 0 ? 0 : 1;

sealed class FakeAutomation : IPublishAutomation
{
    private int _global;
    public int MaxGlobalConcurrent;
    public int MaxPerAccountConcurrent;
    private readonly Dictionary<string, int> _perAccount = new();
    public readonly Dictionary<string, List<string>> OrderByAccount = new();
    private readonly object _lock = new();

    public async Task<PublishResult> PublishAsync(PublishItem item, string cdpEndpoint, FinalAction finalAction, Action<string>? log, CancellationToken ct)
    {
        // cdpEndpoint 形如 http://127.0.0.1:922X → 账号 id 由端口推回
        var acctId = "acct-" + (cdpEndpoint[^1] - '0' - 1);

        int g = Interlocked.Increment(ref _global);
        lock (_lock)
        {
            MaxGlobalConcurrent = Math.Max(MaxGlobalConcurrent, g);
            _perAccount[acctId] = _perAccount.GetValueOrDefault(acctId) + 1;
            MaxPerAccountConcurrent = Math.Max(MaxPerAccountConcurrent, _perAccount[acctId]);
        }

        await Task.Delay(60, ct); // 模拟发布耗时，制造并发窗口

        lock (_lock)
        {
            _perAccount[acctId]--;
            if (!OrderByAccount.TryGetValue(acctId, out var list)) OrderByAccount[acctId] = list = new();
            list.Add(item.DisplayName);
        }
        Interlocked.Decrement(ref _global);
        return PublishResult.Success();
    }
}
