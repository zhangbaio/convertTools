# Platform Publisher Visual QA

Reference: `codex-clipboard-39690a9d-b454-4114-b3e5-835f14dccb55.png`

Implementation: `PlatformPublisher.Desktop` with embedded `ChannelsPublisher.Ui.MaterialPublishView`

## Comparison

- The application uses the TikTok assistant's light blue/white palette and Inter/Microsoft YaHei typography.
- Top-level navigation is a white horizontal bar with blue text and a bottom active indicator.
- The brand and account column are aligned to a 198 px left rail, matching the reference proportions.
- The account list uses a pale-blue selected state, compact actions, blue primary actions, and red destructive actions.
- The complete left column is reserved for account management; task and log content no longer crosses underneath it.
- Account actions remain at the top of the account rail, while all publish/import/configuration actions are consolidated in the right-side top toolbar.
- Content uses white cards, subtle `#D9E2EC` borders, 8 px corner radii, compact controls, and pale-blue table headers.
- Queue actions use the same blue primary / blue outline / disabled gray hierarchy as the reference.
- Status bars use the same muted blue-gray surface.
- Publish and clip configuration dialogs use the same light background and primary save action.
- A dedicated run-log panel appears below the task list and automatically records timestamped status, progress, stop, and failure messages.
- Kuaishou personal and enterprise pages now use the same white cards, pale-blue headers, dark text, blue primary actions, muted helper text, and white empty table surfaces as the video-channel and settings pages.
- Manual-intervention state uses a light lavender notice card instead of the retired dark panel styling.
- The video-channel page preserves its required embedded WebView2 browser workspace; the reference TikTok screen uses that area for a production table, so content structure intentionally differs while visual language matches.

## Remaining P3 polish

- The browser, task table, and log panel divide the right workspace vertically; their exact heights differ from the annotated reference to keep all three usable at 860 px window height.
- Some legacy NumericUpDown arrows inherit native Avalonia sizing rather than the exact TikTok queue sizing.
- Existing WebView2 dependency version warnings remain unchanged.

final result: passed
