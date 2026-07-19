using Microsoft.Playwright;

namespace TikTokPublisher.Ui.Services.TikTok;

public static partial class TikTokBrowserActions
{
    private static async Task EnsureAllPublishAccountsSelectedAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var field = page.Locator("[x-field-id='accountIds']").First;
        if (await field.CountAsync() == 0)
            throw new InvalidOperationException("未找到 TikTok「发布账号」字段（accountIds）。");

        var selectedBefore = await CountSelectedPublishAccountsAsync(field);
        if (selectedBefore > 0)
        {
            Log(log, $"TikTok 发布账号已自动选择 {selectedBefore} 个，跳过兜底全选。");
            return;
        }

        var cascader = field.Locator(".semi-cascader[role='combobox']").First;
        if (await cascader.CountAsync() == 0)
            throw new InvalidOperationException("未找到 TikTok「发布账号」级联选择器。");

        await cascader.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
        await ClickWithFallbackAsync(cascader, ct);
        await page.WaitForTimeoutAsync(500);

        var changed = await page.EvaluateAsync<int>(
            """
            async () => {
              const visible = element => {
                const style = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
              };
              const popup = Array.from(document.querySelectorAll('.semi-portal, [role="dialog"]'))
                .filter(visible)
                .find(node => node.querySelector('.semi-cascader-option, .semi-cascader-option-list'));
              if (!popup) return -1;

              const lists = Array.from(popup.querySelectorAll('.semi-cascader-option-list'))
                .filter(visible);
              const root = lists[0] || popup;
              let changed = 0;
              let previousTop = -1;

              for (let pass = 0; pass < 80; pass += 1) {
                const inputs = Array.from(root.querySelectorAll('input[type="checkbox"]'))
                  .filter(input => !input.disabled);
                for (const input of inputs) {
                  if (input.checked && input.getAttribute('aria-checked') !== 'mixed') continue;
                  const target = input.closest('label, .semi-checkbox, .semi-cascader-option') || input;
                  target.click();
                  changed += 1;
                  await new Promise(resolve => setTimeout(resolve, 30));
                }

                if (root.scrollHeight <= root.clientHeight + 1 ||
                    root.scrollTop >= root.scrollHeight - root.clientHeight - 1) break;
                previousTop = root.scrollTop;
                root.scrollTop = Math.min(root.scrollTop + Math.max(80, root.clientHeight * 0.8), root.scrollHeight);
                root.dispatchEvent(new Event('scroll', { bubbles: true }));
                await new Promise(resolve => setTimeout(resolve, 100));
                if (root.scrollTop === previousTop) break;
              }
              return changed;
            }
            """);

        if (changed < 0)
            throw new InvalidOperationException("TikTok「发布账号」下拉框已打开，但未找到账号选项列表。");

        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(400);

        var selectedAfter = await CountSelectedPublishAccountsAsync(field);
        if (selectedAfter == 0)
            throw new InvalidOperationException("TikTok 发布账号未自动选择，执行全选后仍未检测到已选账号。");

        Log(log, $"TikTok 发布账号原本未选择，已自动全选 {selectedAfter} 个账号。");
    }

    private static async Task<int> CountSelectedPublishAccountsAsync(ILocator field)
    {
        return await field.EvaluateAsync<int>(
            """
            element => {
              const tags = element.querySelectorAll('.semi-cascader-selection .semi-tag').length;
              const more = element.querySelector('.semi-tagInput-wrapper-n');
              const match = (more?.textContent || '').match(/\+(\d+)/);
              return tags + (match ? Number(match[1]) : 0);
            }
            """);
    }
}
