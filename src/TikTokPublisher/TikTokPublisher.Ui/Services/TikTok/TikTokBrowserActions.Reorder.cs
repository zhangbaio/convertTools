using System.Text.Json;
using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private const string SortModeHint = "拖拽调整顺序";
    private const string ReorderApiPath = "/series/videos/reorder";

    /// <summary>一次拖动：把第 <see cref="Real"/> 集从 <see cref="From"/> 移到 <see cref="To"/>（从 0 开始的位置）。</summary>
    internal readonly record struct EpisodeReorderMove(int Real, int From, int To);

    /// <summary>
    /// 按集号升序排好需要的拖动，每次只把应在当前位置的那一集往上移。
    /// 有读不出集号或重复的卡片时返回 null，这种情况不能靠排序修好。
    /// </summary>
    internal static IReadOnlyList<EpisodeReorderMove>? PlanEpisodeReorder(IReadOnlyList<int> reals)
    {
        if (reals.Any(real => real <= 0) || reals.Distinct().Count() != reals.Count)
            return null;

        var current = reals.ToList();
        var target = reals.OrderBy(real => real).ToList();
        var moves = new List<EpisodeReorderMove>();
        for (var index = 0; index < target.Count; index++)
        {
            var from = current.IndexOf(target[index]);
            if (from == index)
                continue;
            moves.Add(new EpisodeReorderMove(target[index], from, index));
            current.RemoveAt(from);
            current.Insert(index, target[index]);
        }

        return moves;
    }

    internal static bool ReorderResponseRejected(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var name in new[] { "code", "status_code", "StatusCode" })
            {
                if (root.TryGetProperty(name, out var code) &&
                    code.ValueKind == JsonValueKind.Number &&
                    code.TryGetInt64(out var value) &&
                    value != 0)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>
    /// 用编辑页「开始排序」按集号把正片排好。卡片支持键盘拖动：聚焦后空格拿起、方向键移动、空格放下。
    /// 「确认」会立即调用排序接口保存。任何一步对不上都点「取消」并返回 false，由调用方走删行兜底。
    /// </summary>
    public static async Task<bool> ReorderEditVideosByEpisodeAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureEditContentUploadTabAsync(page, ct);

        var order = await ReadSortableEpisodeOrderAsync(page);
        if (order.Count == 0)
        {
            Log(log, "拖动排序：页面上没有正片卡片，无法排序。");
            return false;
        }

        var plan = PlanEpisodeReorder(order);
        if (plan is null)
        {
            Log(log, "拖动排序：正片里有重复或读不出集号的卡片，不能靠排序修复。");
            return false;
        }

        if (plan.Count == 0)
            return true;

        var total = await ReadEpisodeListCountAsync(page) ?? 0;
        if (total != order.Count)
        {
            Log(log, $"拖动排序：正片列表只渲染了 {order.Count}/{total} 张卡片，无法拖动排序。");
            return false;
        }

        Log(log,
            $"正片顺序不对，拖动排序 {plan.Count} 次：" +
            string.Join("；", plan.Take(5).Select(move => $"第 {move.Real} 集从第 {move.From + 1} 位移到第 {move.To + 1} 位")) +
            (plan.Count > 5 ? $" 等 {plan.Count} 次。" : "。"));

        if (!await EnterSortModeAsync(page, ct))
        {
            Log(log, "拖动排序：「开始排序」不可用，未能进入排序模式。");
            return false;
        }

        try
        {
            foreach (var move in plan)
            {
                if (!await MoveEpisodeCardAsync(page, move, ct))
                {
                    Log(log, $"拖动排序：第 {move.Real} 集没有移到第 {move.To + 1} 位，已取消排序。");
                    await ClickSortPanelButtonAsync(page, "取消");
                    return false;
                }
            }

            var sorted = await ReadSortableEpisodeOrderAsync(page);
            if (PlanEpisodeReorder(sorted) is not { Count: 0 })
            {
                Log(log, "拖动排序：拖动后集号仍不是升序，已取消排序。");
                await ClickSortPanelButtonAsync(page, "取消");
                return false;
            }

            var response = await page.RunAndWaitForResponseAsync(
                async () =>
                {
                    if (!await ClickSortPanelButtonAsync(page, "确认"))
                        throw new InvalidOperationException("未找到排序面板上的「确认」按钮");
                },
                candidate => candidate.Url.Contains(ReorderApiPath, StringComparison.OrdinalIgnoreCase),
                new PageRunAndWaitForResponseOptions { Timeout = 20000 });
            var body = "";
            try { body = await response.TextAsync(); }
            catch { /* 读不到正文时只看状态码 */ }
            if (!response.Ok || ReorderResponseRejected(body))
            {
                Log(log, $"拖动排序：TikTok 排序接口未成功（HTTP {response.Status}，{Truncate(body, 160)}）。");
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(log, $"拖动排序未完成：{ex.Message}");
            await ClickSortPanelButtonAsync(page, "取消");
            return false;
        }

        await page.WaitForTimeoutAsync(1500);
        var rows = await ReadEditVideoRowsAsync(page, ct);
        if (rows.Count == order.Count && FindAlignedEditVideoPrefixCount(rows) >= rows.Count)
        {
            Log(log, $"拖动排序完成：正片 {rows.Count} 行集号与位置一致。");
            return true;
        }

        Log(log, $"拖动排序已确认，但正片位置仍不对（读到 {rows.Count} 行，前 {FindAlignedEditVideoPrefixCount(rows)} 行对齐）。");
        return false;
    }

    private static async Task<List<int>> ReadSortableEpisodeOrderAsync(IPage page)
    {
        try
        {
            var reals = await page.EvaluateAsync<int[]>(
                """
                () => Array.from(document.querySelectorAll('[data-episode-uid]')).map((card) => {
                  const text = (card.querySelector('[class*="listBody"]') || card).textContent || '';
                  const file = text.match(/-\s*第\s*(\d+)\s*集/);
                  if (file) return Number.parseInt(file[1], 10) || 0;
                  const all = Array.from(text.matchAll(/第\s*(\d+)\s*集/g));
                  return all.length ? (Number.parseInt(all[all.length - 1][1], 10) || 0) : 0;
                })
                """);
            return reals.ToList();
        }
        catch
        {
            return new List<int>();
        }
    }

    private static async Task<bool> EnterSortModeAsync(IPage page, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await SortModeActiveAsync(page))
                return true;

            var button = page.Locator("button:visible").Filter(new() { HasText = "开始排序" }).First;
            try
            {
                if (await button.CountAsync() > 0 && await button.IsEnabledAsync())
                {
                    await button.ClickAsync(new() { Timeout = 5000 });
                    await page.WaitForTimeoutAsync(800);
                    if (await SortModeActiveAsync(page))
                        return true;
                }
            }
            catch (PlaywrightException)
            {
            }

            await page.WaitForTimeoutAsync(2000);
        }

        return false;
    }

    private static async Task<bool> SortModeActiveAsync(IPage page)
    {
        try
        {
            return await page.GetByText(SortModeHint, new() { Exact = true }).CountAsync() > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> MoveEpisodeCardAsync(IPage page, EpisodeReorderMove move, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var order = await ReadSortableEpisodeOrderAsync(page);
        var from = order.IndexOf(move.Real);
        if (from < 0)
            return false;
        if (from == move.To)
            return true;

        var uid = await page.EvaluateAsync<string>(
            "(index) => document.querySelectorAll('[data-episode-uid]')[index]?.getAttribute('data-episode-uid') || ''",
            from);
        if (string.IsNullOrWhiteSpace(uid))
            return false;

        var card = page.Locator($"[data-episode-uid=\"{uid.Replace("\"", "\\\"")}\"]").First;
        await card.ScrollIntoViewIfNeededAsync(new() { Timeout = 5000 });
        await card.FocusAsync(new() { Timeout = 5000 });
        await page.Keyboard.PressAsync("Space");
        await page.WaitForTimeoutAsync(250);
        var key = from > move.To ? "ArrowUp" : "ArrowDown";
        for (var step = 0; step < Math.Abs(from - move.To); step++)
        {
            ct.ThrowIfCancellationRequested();
            await page.Keyboard.PressAsync(key);
            await page.WaitForTimeoutAsync(100);
        }

        await page.WaitForTimeoutAsync(200);
        await page.Keyboard.PressAsync("Space");
        await page.WaitForTimeoutAsync(600);

        order = await ReadSortableEpisodeOrderAsync(page);
        return move.To < order.Count && order[move.To] == move.Real;
    }

    private static async Task<bool> ClickSortPanelButtonAsync(IPage page, string label)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                """
                ([hintText, label]) => {
                  const hint = Array.from(document.querySelectorAll('body *'))
                    .find((el) => el.childElementCount === 0 && (el.textContent || '').trim() === hintText);
                  for (let el = hint; el; el = el.parentElement) {
                    const button = Array.from(el.querySelectorAll('button'))
                      .find((item) => (item.innerText || '').trim() === label && !item.disabled);
                    if (button) {
                      button.click();
                      return true;
                    }
                  }
                  return false;
                }
                """,
                new object[] { SortModeHint, label });
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(string? value, int max)
    {
        var text = (value ?? "").Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
