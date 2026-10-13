# 素材发布页面 Design QA

- source visual truth path: `C:\Users\PC\AppData\Local\Temp\codex-clipboard-cc8e182a-d0e9-4de8-9164-f8bd1cb54803.png`
- implementation screenshot path: `D:\code\convertTools-main\.codex-build\design-qa\materials-final.png`
- side-by-side comparison path: `D:\code\convertTools-main\.codex-build\design-qa\materials-comparison.png`
- source pixels: 1619 × 861
- implementation pixels: 1498 × 892
- viewport: Windows desktop window at 1498 × 892 logical pixels
- density normalization: both captures reviewed at native 1× density; the implementation is 121 px narrower, so horizontal table overflow was evaluated as responsive behavior rather than scaled imagery
- state: 视频号助手 → 素材上传；账号 1；`E:\峨绮信息\` 已扫描；112 个项目

## Full-view comparison evidence

The page hierarchy matches the reference: title and account selector, two-row tool/workspace card,
search and selection summary, compact project table, fixed action area, and bottom status strip. The
reference and implementation use the same blue-white palette, compact table density, semantic action
colors, and project-first workflow.

## Focused region comparison evidence

- Header/tool card: labels, ordering, 34–36 px controls, dividers, spacing, and primary scan action match.
- Table: column order, selected count, source badge, clickable project paths, and four per-row actions match.
- Dialogs: system-highlight download, directory preview, automatic publishing, and destructive delete
  confirmation were opened and visually inspected. Each exposes its required fields and a clear cancel path.
- No raster content is required inside the scoped material page. The existing application shell logo and
  account avatar system remain outside this page-level implementation.

## Findings

- P3 — At the narrower 1498 px test window, the fourth row action is reached through the table's horizontal
  scrollbar. This is acceptable because the reference also defines a wide fixed action column and horizontal
  overflow; persistent actions are not lost.
- P3 — The surrounding C# application shell uses its existing two-level navigation and simpler account cards.
  This is intentionally outside the material-page scope and does not change the material workflow.

## Primary interactions tested

- Opened the material tab and confirmed automatic project scanning (112 projects).
- Opened and cancelled the system-highlight download dialog.
- Opened and cancelled the directory batch preview after it scanned 92 materials.
- Opened and cancelled the automatic publishing rule editor.
- Opened and cancelled the destructive material-delete confirmation; no deletion was executed.
- Confirmed page switching does not terminate the process.

## Build and automated checks

- PlatformPublisher isolated Release build: passed.
- PlatformPublisher.Materials.Tests: 6 passed, 0 failed.
- PlatformPublisher.Weixin.Tests: 11 passed, 0 failed.

## Comparison history

1. Initial implementation included extra “全选 / 取消全选” controls beside the search field and did not yet
   connect the download/schedule tools. Those controls were removed and the missing tools were implemented.
2. Final capture confirms the corrected search row and working dialogs. No actionable P0/P1/P2 visual issue remains.

## Follow-up polish

- A future shell-wide redesign may align the global navigation and account cards with the reference app, but
  it is not required for the material publishing workflow.

final result: passed
