using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private const string EpisodeListSelector = "[data-episode-uid], [class*='episodeCount'], .semi-table-body";

    /// <summary>
    /// 编辑页「剧集正片」列表的 DOM 适配（注入到各个 Evaluate 脚本开头）。
    /// 新版为卡片列表（<c>[data-episode-uid]</c>，滚动容器 <c>listScroll-*</c>），旧版为 <c>semi-table</c>；
    /// 两种都找不到时返回 null，调用方必须按「列表不存在」处理，不能当作 0 行。
    /// </summary>
    internal const string EpisodeListScript =
        """
        const xgNormalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
        const xgSleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
        const xgEpisodeList = () => {
          const cardRows = () => Array.from(document.querySelectorAll('[data-episode-uid]'));
          const firstCard = cardRows()[0];
          if (firstCard || document.querySelector('[class*="episodeCount"]')) {
            return {
              kind: 'cards',
              scroller: firstCard?.closest('[class*="listScroll"]')
                || document.querySelector('[class*="listScroll"]')
                || firstCard?.parentElement
                || null,
              rows: cardRows,
              key: (row) => row.getAttribute('data-episode-uid') || '',
              text: (row) => xgNormalize((row.querySelector('[class*="listBody"]') || row).textContent),
              declaredCount: () => 0,
            };
          }

          const body = document.querySelector('.semi-table-body');
          if (!body) return null;
          return {
            kind: 'table',
            scroller: body,
            rows: () => Array.from(body.querySelectorAll('tr.semi-table-row')),
            key: (row) => row.getAttribute('data-row-key') || row.getAttribute('aria-rowindex') || xgNormalize(row.textContent),
            text: (row) => xgNormalize(Array.from(row.querySelectorAll('td')).map((td) => td.textContent || '').join(' ')),
            declaredCount: () => {
              const raw = Number.parseInt(body.querySelector('table')?.getAttribute('aria-rowcount') || '', 10);
              return Number.isFinite(raw) && raw > 0 ? raw : 0;
            },
          };
        };
        const xgScrollList = async (list, top) => {
          const scroller = list.scroller;
          if (!scroller) return;
          scroller.scrollTop = Math.max(0, top);
          try { scroller.dispatchEvent(new Event('scroll', { bubbles: true })); } catch {}
          await xgSleep(180);
        };
        // 列表可能虚拟化，只渲染视口内的行：自顶向下滚动一遍访问所有行，结束后恢复原滚动位置。
        const xgScanList = async (list, visit) => {
          list.rows().forEach(visit);
          const scroller = list.scroller;
          if (!scroller || scroller.scrollHeight <= scroller.clientHeight + 2) return;

          const original = scroller.scrollTop;
          await xgScrollList(list, 0);
          list.rows().forEach(visit);
          let bottomStable = 0;
          for (let i = 0; i < 300 && bottomStable < 3; i++) {
            const maxTop = Math.max(0, scroller.scrollHeight - scroller.clientHeight);
            const before = scroller.scrollTop;
            await xgScrollList(list, Math.min(maxTop, before + Math.max(200, Math.floor(scroller.clientHeight * 0.8))));
            list.rows().forEach(visit);
            if (scroller.scrollTop >= maxTop - 2 && scroller.scrollTop === before) bottomStable++;
            else bottomStable = 0;
          }
          await xgScrollList(list, original);
        };
        const xgCountEpisodes = async (list) => {
          const declared = list.declaredCount();
          if (declared > 0) return declared;
          const keys = new Set();
          await xgScanList(list, (row) => {
            const key = list.key(row);
            if (key) keys.add(key);
          });
          return keys.size;
        };
        """;

    /// <summary>正片列表的全量行数；页面上找不到正片列表（新旧版都不匹配）时返回 null。</summary>
    internal static async Task<int?> ReadEpisodeListCountAsync(IPage page)
    {
        try
        {
            var count = await page.EvaluateAsync<int>(
                "async () => {\n" + EpisodeListScript +
                """

                  const list = xgEpisodeList();
                  return list ? await xgCountEpisodes(list) : -1;
                }
                """);
            return count >= 0 ? count : null;
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<IReadOnlyList<string>> ReadEpisodeListTextsAsync(IPage page)
    {
        try
        {
            var texts = await page.Locator("[data-episode-uid]").AllInnerTextsAsync();
            if (texts.Count > 0)
                return texts;
            return await page.Locator(".semi-table-body").AllInnerTextsAsync();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 视频上传框打分；返回 null 表示不可用。剧集卡片内的上传框只替换该集视频，
    /// 绝不能用于追加新集（会把多集塞进同一张卡片，造成乱序与重复）。
    /// </summary>
    internal static int? ScoreVideoFileInputCandidate(
        string? accept,
        bool multiple,
        string? className,
        bool insideEpisodeCard)
    {
        if (insideEpisodeCard)
            return null;

        var normalizedAccept = (accept ?? "").Trim().ToLowerInvariant();
        var isVideo = normalizedAccept.Contains("mp4") || normalizedAccept.Contains("mov") || normalizedAccept.Contains("video");
        if (normalizedAccept.Length > 0 && !isVideo)
            return null;

        var score = 0;
        if (isVideo) score += 5;
        if (normalizedAccept.Length > 0) score += 2;
        if (multiple) score += 4;
        var classes = (className ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classes.Contains("semi-upload-hidden-input")) score += 3;
        if (classes.Contains("semi-upload-hidden-input-replace")) score -= 1;
        return score;
    }
}
