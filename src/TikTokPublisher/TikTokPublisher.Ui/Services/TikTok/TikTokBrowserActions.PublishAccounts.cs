using System.Text.Json;
using Microsoft.Playwright;
using TikTokPublisher.Core.Models;

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

    internal static async Task<IReadOnlyList<TikTokPublishAccountOption>> CollectPublishAccountsAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        var scraped = await ScrapePublishAccountsAsync(page, log, ct).ConfigureAwait(false);
        return TikTokPublishAccountCatalog.Normalize(scraped.Select(account =>
            new TikTokPublishAccountOption(account.Country, account.DisplayName, account.Handle)));
    }

    private static async Task SelectConfiguredPublishAccountsAsync(
        IPage page,
        IReadOnlyList<string> selectedKeys,
        Action<string>? log,
        CancellationToken ct)
    {
        var wanted = selectedKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (wanted.Length == 0)
        {
            throw new InvalidOperationException(
                "已启用自定义发布账号，但没有勾选任何账号。请至少选择一个发布账号。");
        }

        var scraped = await ScrapePublishAccountsAsync(page, log, ct).ConfigureAwait(false);
        var plan = TikTokPublishAccountCatalog.Plan(
            scraped.Select(account => new TikTokPublishAccountVisibleOption(
                account.Country,
                account.DisplayName,
                account.Handle,
                account.Checked)),
            wanted);
        if (plan.MissingKeys.Count > 0)
        {
            throw new InvalidOperationException(
                "TikTok 发布账号中找不到以下已选账号：" +
                string.Join("、", plan.MissingKeys.Select(TikTokPublishAccountCatalog.DescribeKey)) +
                "。请重新获取已创建账号后再发布。");
        }

        if (plan.KeysToCheck.Count > 0 || plan.KeysToUncheck.Count > 0)
        {
            await ApplyPublishAccountSelectionAsync(page, plan, ct).ConfigureAwait(false);
        }

        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(400);

        var field = page.Locator("[x-field-id='accountIds']").First;
        var selectedAfter = await CountSelectedPublishAccountsAsync(field);
        if (selectedAfter != wanted.Length)
        {
            throw new InvalidOperationException(
                $"TikTok 发布账号勾选结果为 {selectedAfter} 个，与已选 {wanted.Length} 个不一致。");
        }

        Log(log, $"TikTok 发布账号已按配置勾选 {selectedAfter} 个。");
    }

    private static async Task<IReadOnlyList<ScrapedPublishAccount>> ScrapePublishAccountsAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await OpenPublishAccountCascaderAsync(page, ct).ConfigureAwait(false);
        var json = await page.EvaluateAsync<string>(PublishAccountScrapeScript).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        ScrapedPublishAccountPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ScrapedPublishAccountPayload>(json, ScrapeJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("读取 TikTok 发布账号列表失败。", ex);
        }

        if (!string.IsNullOrWhiteSpace(payload?.Error))
            throw new InvalidOperationException($"读取 TikTok 发布账号列表失败：{payload.Error}");

        var accounts = payload?.Accounts ?? [];
        if (accounts.Count == 0)
            throw new InvalidOperationException("TikTok「发布账号」下拉框已打开，但未找到账号选项。");

        Log(log, $"已读取 {accounts.Count} 个 TikTok 发布账号。");
        return accounts;
    }

    private static async Task ApplyPublishAccountSelectionAsync(
        IPage page,
        TikTokPublishAccountSelectionPlan plan,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var actions = plan.KeysToCheck
            .Select(key => PublishAccountToggle.FromKey(key, select: true))
            .Concat(plan.KeysToUncheck.Select(key => PublishAccountToggle.FromKey(key, select: false)))
            .Where(action => action is not null)
            .Cast<PublishAccountToggle>()
            .ToArray();
        var argument = JsonSerializer.Serialize(actions, ScrapeJsonOptions);
        var error = await page.EvaluateAsync<string>(PublishAccountApplyScript, argument).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"勾选 TikTok 发布账号失败：{error}");
    }

    private static async Task OpenPublishAccountCascaderAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var field = page.Locator("[x-field-id='accountIds']").First;
        if (await field.CountAsync() == 0)
            throw new InvalidOperationException("未找到 TikTok「发布账号」字段（accountIds）。");

        var cascader = field.Locator(".semi-cascader[role='combobox']").First;
        if (await cascader.CountAsync() == 0)
            throw new InvalidOperationException("未找到 TikTok「发布账号」级联选择器。");

        await cascader.EvaluateAsync(
            """
            element => {
              element.scrollIntoView({ block: 'center', inline: 'nearest' });
              element.click();
            }
            """);
        var popup = page.Locator(".semi-cascader-popover .semi-cascader-option-list").First;
        try
        {
            await popup.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 8000 });
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("TikTok「发布账号」下拉框未能打开，未找到账号选项列表。");
        }
    }

    private static readonly JsonSerializerOptions ScrapeJsonOptions = new(JsonSerializerDefaults.Web);

    private const string PublishAccountScrapeScript =
        """
        async () => {
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const visible = element => {
            const style = getComputedStyle(element);
            const rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
          };
          const popup = document.querySelector('.semi-cascader-popover')
            || document.getElementById(document.querySelector("[x-field-id='accountIds'] [role='combobox']")?.getAttribute('aria-controls') || '');
          if (!popup) return JSON.stringify({ error: '未找到发布账号选项列表' });
          const lists = () => Array.from(popup.querySelectorAll('.semi-cascader-option-list')).filter(visible);
          const left = lists()[0];
          if (!left) return JSON.stringify({ error: '未找到国家列表' });
          const optionsOf = list => Array.from(list.querySelectorAll('.semi-cascader-option')).filter(visible);
          const linesOf = option => {
            const explicit = option.querySelector('[class*="optionLabel"]');
            const explicitText = (explicit?.textContent || '').replace(/\s+/g, ' ').trim();
            if (explicitText) return [explicitText];
            return Array.from(option.querySelectorAll('.semi-cascader-option-label span'))
              .filter(span => span.childElementCount === 0)
              .map(span => (span.textContent || '').replace(/\s+/g, ' ').trim())
              .filter(line => line.length > 0 && line !== '取消全部');
          };
          const scrollThrough = async (list, visit) => {
            list.scrollTop = 0;
            let previous = -1;
            for (let pass = 0; pass < 40; pass += 1) {
              for (const option of optionsOf(list)) visit(option);
              if (list.scrollHeight <= list.clientHeight + 1 ||
                  list.scrollTop >= list.scrollHeight - list.clientHeight - 1) break;
              previous = list.scrollTop;
              list.scrollTop = Math.min(list.scrollTop + Math.max(80, list.clientHeight * 0.8), list.scrollHeight);
              list.dispatchEvent(new Event('scroll', { bubbles: true }));
              await sleep(80);
              if (list.scrollTop === previous) break;
            }
          };
          const countries = [];
          const seenCountries = new Set();
          await scrollThrough(left, option => {
            const country = linesOf(option)[0] || '';
            if (!country || seenCountries.has(country)) return;
            seenCountries.add(country);
            countries.push(country);
          });
          const accounts = [];
          for (const country of countries) {
            let option = null;
            left.scrollTop = 0;
            for (let pass = 0; pass < 40 && !option; pass += 1) {
              option = optionsOf(left).find(item => (linesOf(item)[0] || '') === country) || null;
              if (option) break;
              const previous = left.scrollTop;
              left.scrollTop = Math.min(left.scrollTop + Math.max(80, left.clientHeight * 0.8), left.scrollHeight);
              await sleep(60);
              if (left.scrollTop === previous) break;
            }
            if (!option) continue;
            option.scrollIntoView({ block: 'nearest' });
            const icon = option.querySelector('.semi-cascader-option-icon');
            if (!icon) continue;
            icon.click();
            await sleep(150);
            const right = lists()[1];
            if (!right) continue;
            const seenAccounts = new Set();
            await scrollThrough(right, item => {
              const lines = linesOf(item);
              const displayName = lines[0] || '';
              if (!displayName || seenAccounts.has(displayName)) return;
              seenAccounts.add(displayName);
              const input = item.querySelector('input[type="checkbox"]');
              const checked = !!(input && (input.checked || input.getAttribute('aria-checked') === 'true'));
              accounts.push({
                country,
                displayName,
                handle: lines[1] || '',
                checked
              });
            });
          }
          return JSON.stringify({ accounts });
        }
        """;

    private const string PublishAccountApplyScript =
        """
        async argument => {
          const actions = JSON.parse(argument);
          const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
          const visible = element => {
            const style = getComputedStyle(element);
            const rect = element.getBoundingClientRect();
            return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
          };
          const popup = document.querySelector('.semi-cascader-popover')
            || document.getElementById(document.querySelector("[x-field-id='accountIds'] [role='combobox']")?.getAttribute('aria-controls') || '');
          if (!popup) return '未找到发布账号选项列表';
          const lists = () => Array.from(popup.querySelectorAll('.semi-cascader-option-list')).filter(visible);
          const optionsOf = list => Array.from(list.querySelectorAll('.semi-cascader-option')).filter(visible);
          const firstLine = option => {
            const explicit = option.querySelector('[class*="optionLabel"]');
            const explicitText = (explicit?.textContent || '').replace(/\s+/g, ' ').trim();
            if (explicitText) return explicitText;
            const textSpan = Array.from(option.querySelectorAll('.semi-cascader-option-label span'))
              .find(span => span.childElementCount === 0 && (span.textContent || '').trim());
            const node = textSpan || option.querySelector('.semi-cascader-option-label') || option;
            return ((node.innerText || '').split(/\n+/)[0] || '').replace(/\s+/g, ' ').trim();
          };
          const findOption = async (list, label) => {
            if (!list) return null;
            list.scrollTop = 0;
            for (let pass = 0; pass < 40; pass += 1) {
              const found = optionsOf(list).find(option => firstLine(option) === label);
              if (found) return found;
              const previous = list.scrollTop;
              if (list.scrollHeight <= list.clientHeight + 1 ||
                  list.scrollTop >= list.scrollHeight - list.clientHeight - 1) break;
              list.scrollTop = Math.min(list.scrollTop + Math.max(80, list.clientHeight * 0.8), list.scrollHeight);
              await sleep(60);
              if (list.scrollTop === previous) break;
            }
            return null;
          };
          for (const action of actions) {
            const left = lists()[0];
            const country = await findOption(left, action.country);
            if (!country) return `未找到国家：${action.country}`;
            country.scrollIntoView({ block: 'nearest' });
            const icon = country.querySelector('.semi-cascader-option-icon');
            if (!icon) return `未找到国家展开按钮：${action.country}`;
            icon.click();
            await sleep(150);
            const account = await findOption(lists()[1], action.displayName);
            if (!account) return `未找到账号：${action.country} / ${action.displayName}`;
            const input = account.querySelector('input[type="checkbox"]');
            const checked = !!(input && (input.checked || input.getAttribute('aria-checked') === 'true'));
            if (checked === action.select) continue;
            const target = input?.closest('label, .semi-checkbox') || input || account;
            target.click();
            await sleep(80);
          }
          return '';
        }
        """;

    private sealed class ScrapedPublishAccountPayload
    {
        public string? Error { get; set; }
        public List<ScrapedPublishAccount> Accounts { get; set; } = [];
    }

    private sealed class ScrapedPublishAccount
    {
        public string Country { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Handle { get; set; } = "";
        public bool Checked { get; set; }
    }

    private sealed record PublishAccountToggle(string Country, string DisplayName, bool Select)
    {
        public static PublishAccountToggle? FromKey(string key, bool select)
        {
            var parts = key.Split(TikTokPublishAccountCatalog.KeySeparator, 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return null;
            return new PublishAccountToggle(parts[0], parts[1], select);
        }
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
