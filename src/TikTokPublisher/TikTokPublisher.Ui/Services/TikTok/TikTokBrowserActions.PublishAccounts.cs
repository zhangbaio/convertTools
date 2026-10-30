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

        var control = await DetectPublishAccountControlAsync(page, ct);
        var selectedBefore = await CountSelectedPublishAccountsAsync(page, field, control);
        if (selectedBefore > 0)
        {
            Log(log, $"TikTok 发布账号已自动选择 {selectedBefore} 个，跳过兜底全选。");
            return;
        }

        if (control == PublishAccountControl.RegionPicker)
        {
            await field.ScrollIntoViewIfNeededAsync(new() { Timeout = 10000 });
            await ApplyPublishAccountPickerAsync(page, "all", [], ct);
            var pickedCount = await CountSelectedPublishAccountsAsync(page, field, control);
            if (pickedCount == 0)
                throw new InvalidOperationException("TikTok 发布账号未自动选择，执行全选后仍未检测到已选账号。");
            Log(log, $"TikTok 发布账号原本未选择，已自动全选 {pickedCount} 个国家/地区账号。");
            return;
        }

        var cascader = field.Locator(".semi-cascader[role='combobox']").First;
        if (await cascader.CountAsync() == 0)
            throw new InvalidOperationException("未找到 TikTok「发布账号」选择器（新版国家/地区选择器与旧版级联选择器均不存在）。");

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

        var selectedAfter = await CountSelectedPublishAccountsAsync(page, field, control);
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
        var wanted = KeepOneAccountPerCountry(selectedKeys);
        if (wanted.Length == 0)
        {
            throw new InvalidOperationException(
                "已启用自定义发布账号，但没有勾选任何账号。请至少选择一个发布账号。");
        }

        var control = await DetectPublishAccountControlAsync(page, ct);
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
            if (control == PublishAccountControl.RegionPicker)
                await ApplyPublishAccountPickerAsync(page, "wanted", wanted, ct).ConfigureAwait(false);
            else
                await ApplyPublishAccountSelectionAsync(page, plan, ct).ConfigureAwait(false);
        }

        if (control != PublishAccountControl.RegionPicker)
        {
            await page.Keyboard.PressAsync("Escape");
            await page.WaitForTimeoutAsync(400);
        }

        var field = page.Locator("[x-field-id='accountIds']").First;
        var selectedAfter = await CountSelectedPublishAccountsAsync(page, field, control);
        if (selectedAfter != wanted.Length)
        {
            throw new InvalidOperationException(
                $"TikTok 发布账号勾选结果为 {selectedAfter} 个，与已选 {wanted.Length} 个不一致。");
        }

        if (control == PublishAccountControl.RegionPicker)
        {
            var finalState = await ReadPublishAccountPickerAsync(page, ct).ConfigureAwait(false);
            var remaining = TikTokPublishAccountCatalog.Plan(
                finalState.Select(account => new TikTokPublishAccountVisibleOption(
                    account.Country, account.DisplayName, account.Handle, account.Checked)),
                wanted);
            if (remaining.KeysToCheck.Count > 0 || remaining.KeysToUncheck.Count > 0)
            {
                throw new InvalidOperationException(
                    "TikTok 发布账号勾选后校验失败：" +
                    string.Join("、", remaining.KeysToCheck.Select(k => "未选中 " + TikTokPublishAccountCatalog.DescribeKey(k))
                        .Concat(remaining.KeysToUncheck.Select(k => "多选了 " + TikTokPublishAccountCatalog.DescribeKey(k)))) +
                    "。");
            }
        }

        Log(log, $"TikTok 发布账号已按配置勾选 {selectedAfter} 个。");
    }

    private static string[] KeepOneAccountPerCountry(IEnumerable<string> selectedKeys)
    {
        var seenCountries = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var raw in selectedKeys)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var parts = raw.Split(TikTokPublishAccountCatalog.KeySeparator, 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                continue;
            if (!seenCountries.Add(parts[0]))
                continue;
            if (!kept.Contains(raw, StringComparer.Ordinal))
                kept.Add(raw);
        }

        return kept.ToArray();
    }

    private static async Task<IReadOnlyList<ScrapedPublishAccount>> ScrapePublishAccountsAsync(
        IPage page,
        Action<string>? log,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<ScrapedPublishAccount> accounts;
        if (await DetectPublishAccountControlAsync(page, ct).ConfigureAwait(false) == PublishAccountControl.RegionPicker)
        {
            accounts = await ReadPublishAccountPickerAsync(page, ct).ConfigureAwait(false);
        }
        else
        {
            await OpenPublishAccountCascaderAsync(page, ct).ConfigureAwait(false);
            var json = await page.EvaluateAsync<string>(PublishAccountScrapeScript).ConfigureAwait(false);
            accounts = ParseScrapedPublishAccounts(json);
        }

        ct.ThrowIfCancellationRequested();
        if (accounts.Count == 0)
            throw new InvalidOperationException("TikTok「发布账号」已显示，但未找到账号选项。");

        Log(log, $"已读取 {accounts.Count} 个 TikTok 发布账号。");
        return accounts;
    }

    private static List<ScrapedPublishAccount> ParseScrapedPublishAccounts(string json)
    {
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
        return payload?.Accounts ?? [];
    }

    private enum PublishAccountControl
    {
        None,
        RegionPicker,
        Cascader,
    }

    private static async Task<PublishAccountControl> DetectPublishAccountControlAsync(IPage page, CancellationToken ct)
    {
        var script = "() => {\n" + PublishAccountPickerScript + """
              if (xgReadPicker()?.regions) return 'picker';
              return document.querySelector("[x-field-id='accountIds'] .semi-cascader") ? 'cascader' : '';
            }
            """;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var kind = await page.EvaluateAsync<string>(script);
            if (kind == "picker") return PublishAccountControl.RegionPicker;
            if (kind == "cascader") return PublishAccountControl.Cascader;
            if (DateTime.UtcNow >= deadline) return PublishAccountControl.None;
            await Task.Delay(300, ct);
        }
    }

    private static async Task<List<ScrapedPublishAccount>> ReadPublishAccountPickerAsync(IPage page, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var script = "() => {\n" + PublishAccountPickerScript + """
              const picker = xgReadPicker();
              if (!picker) return JSON.stringify({ error: '未找到国家/地区账号选择器' });
              if (picker.error) return JSON.stringify({ error: picker.error });
              const accounts = [];
              for (const region of picker.regions)
                for (const account of region.accounts)
                  accounts.push({ country: region.name, displayName: account.name, handle: '', checked: region.selected && account.on });
              return JSON.stringify({ accounts });
            }
            """;
        return ParseScrapedPublishAccounts(await page.EvaluateAsync<string>(script));
    }

    private static async Task ApplyPublishAccountPickerAsync(
        IPage page,
        string mode,
        IReadOnlyList<string> wantedKeys,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var wanted = wantedKeys
            .Select(key => PublishAccountToggle.FromKey(key, select: true))
            .Where(action => action is not null)
            .Cast<PublishAccountToggle>()
            .ToArray();
        var argument = JsonSerializer.Serialize(new { mode, wanted }, ScrapeJsonOptions);
        var script = "async (argument) => {\n" + PublishAccountPickerScript + """
              const { mode, wanted } = JSON.parse(argument);
              const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
              const wantedByCountry = new Map(wanted.map(item => [xgNorm(item.country), xgNorm(item.displayName)]));
              const regionAt = index => {
                const picker = xgReadPicker();
                if (!picker || picker.error) throw new Error(picker?.error || '国家/地区账号选择器已消失');
                return picker.regions[index];
              };
              const first = xgReadPicker();
              if (!first) return '未找到国家/地区账号选择器';
              if (first.error) return first.error;
              const known = new Set(first.regions.map(region => region.name));
              for (const country of wantedByCountry.keys())
                if (!known.has(country)) return `未找到国家/地区：${country}`;

              try {
                for (let index = 0; index < first.regions.length; index += 1) {
                  let region = regionAt(index);
                  const target = mode === 'all'
                    ? (region.accounts.find(account => account.on) || region.accounts[0])?.name
                    : wantedByCountry.get(region.name);
                  if (!target) {
                    if (region.selected) {
                      if (!region.check) return `无法取消国家/地区：${region.name}`;
                      region.check.click();
                      await sleep(250);
                    }
                    continue;
                  }

                  if (!region.selected) {
                    region.row.click();
                    await sleep(300);
                    region = regionAt(index);
                  }
                  const account = region.accounts.find(item => item.name === target);
                  if (!account) return `未找到账号：${region.name} / ${target}`;
                  if (!account.on) {
                    account.button.click();
                    await sleep(250);
                    region = regionAt(index);
                  }
                  for (const other of region.accounts.filter(item => item.on && item.name !== target)) {
                    other.button.click();
                    await sleep(200);
                  }
                }
              } catch (error) {
                return String(error?.message || error);
              }
              await sleep(200);
              return '';
            }
            """;
        var error = await page.EvaluateAsync<string>(script, argument).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"勾选 TikTok 发布账号失败：{error}");
    }

    internal const string PublishAccountPickerScript =
        """
          const xgNorm = text => String(text || '').replace(/\s+/g, ' ').trim();
          const xgHasClass = (element, name) => typeof element.className === 'string' &&
            element.className.split(/\s+/).some(token => token === name || token.startsWith(name + '-'));
          const xgReadPicker = () => {
            const field = document.querySelector("[x-field-id='accountIds']");
            if (!field) return null;
            const all = Array.from(field.querySelectorAll('*'));
            const rows = all.filter(element => xgHasClass(element, 'regionRow'));
            if (!rows.length) return null;
            const groups = all.filter(element => xgHasClass(element, 'accountGroup') || xgHasClass(element, 'accountGroupHidden'));
            if (groups.length !== rows.length)
              return { error: `国家/地区数 ${rows.length} 与账号分组数 ${groups.length} 不一致` };
            const regions = rows.map((row, index) => {
              const inner = Array.from(row.querySelectorAll('*'));
              const accounts = Array.from(groups[index].querySelectorAll('*'))
                .filter(element => xgHasClass(element, 'accountRow'))
                .map(button => {
                  const parts = Array.from(button.querySelectorAll('*'));
                  return {
                    name: xgNorm(parts.find(element => xgHasClass(element, 'accountName'))?.textContent),
                    on: parts.some(element => xgHasClass(element, 'accountRadioOn')),
                    button
                  };
                })
                .filter(account => account.name);
              return {
                name: xgNorm(inner.find(element => xgHasClass(element, 'regionName'))?.textContent),
                selected: row.getAttribute('aria-pressed') === 'true' || xgHasClass(row, 'regionRowSelected'),
                check: inner.find(element => xgHasClass(element, 'regionCheck')) || null,
                row,
                accounts
              };
            });
            return { field, regions };
          };
        """;

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
            throw new InvalidOperationException("未找到 TikTok「发布账号」选择器（新版国家/地区选择器与旧版级联选择器均不存在）。");

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
          const normalize = text => String(text || '').replace(/\s+/g, ' ').trim();
          const labelText = node => {
            if (node == null || node === false) return '';
            if (typeof node === 'string' || typeof node === 'number') return String(node);
            if (Array.isArray(node)) return node.map(labelText).join('');
            const className = String(node.props?.className || '');
            if (className.includes('optionLabel')) return labelText(node.props.children);
            return labelText(node.props?.children);
          };
          const findProps = root => {
            if (!root) return null;
            const fiberKey = Object.keys(root).find(key => key.startsWith('__reactFiber'));
            let fiber = fiberKey ? root[fiberKey] : null;
            while (fiber) {
              const candidate = fiber.memoizedProps || {};
              if (Array.isArray(candidate.value) && Array.isArray(candidate.treeData) && typeof candidate.onChange === 'function')
                return candidate;
              fiber = fiber.return;
            }
            return null;
          };
          const popup = document.querySelector('.semi-cascader-popover')
            || document.getElementById(document.querySelector("[x-field-id='accountIds'] [role='combobox']")?.getAttribute('aria-controls') || '');
          const props = findProps(popup) || findProps(document.querySelector("[x-field-id='accountIds']"));
          if (!props) return '未找到发布账号选项列表';
          const catalog = [];
          const childrenByCountry = new Map();
          for (const countryNode of props.treeData) {
            const country = normalize(labelText(countryNode.label));
            const children = [];
            for (const child of countryNode.children || []) {
              const displayName = normalize(labelText(child.label));
              if (!country || !displayName || child.value == null) continue;
              const path = [countryNode.value, child.value];
              catalog.push({ country, displayName, path });
              children.push(path);
            }
            childrenByCountry.set(String(countryNode.value), children);
          }
          const pathKey = path => String(path[0]) + '\u001f' + String(path[1]);
          const selected = new Set();
          for (const path of props.value || []) {
            if (!Array.isArray(path) || path.length === 0) continue;
            if (path.length === 1) {
              for (const child of childrenByCountry.get(String(path[0])) || [])
                selected.add(pathKey(child));
            } else {
              selected.add(pathKey(path));
            }
          }
          for (const action of actions) {
            const item = catalog.find(entry => entry.country === action.country && entry.displayName === action.displayName);
            if (!item) return `未找到账号：${action.country} / ${action.displayName}`;
            const key = pathKey(item.path);
            if (action.select) selected.add(key);
            else selected.delete(key);
          }
          const next = [];
          const seenCountries = new Set();
          for (const entry of catalog) {
            const key = pathKey(entry.path);
            const countryKey = String(entry.path[0]);
            if (!selected.has(key) || seenCountries.has(countryKey)) continue;
            seenCountries.add(countryKey);
            next.push(entry.path);
          }
          props.onChange(next);
          await sleep(200);
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

    private static async Task<int> CountSelectedPublishAccountsAsync(
        IPage page,
        ILocator field,
        PublishAccountControl control)
    {
        if (control == PublishAccountControl.RegionPicker)
        {
            var accounts = await ReadPublishAccountPickerAsync(page, CancellationToken.None);
            return accounts.Count(account => account.Checked);
        }

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
