# Platform Publisher Visual QA

Reference: `codex-clipboard-b9e45130-ca11-4191-8c78-8fa525255891.png`

Implementation: `PlatformPublisher.Desktop` with embedded `ChannelsPublisher.Ui.MaterialPublishView`

## Comparison

- The application uses the TikTok assistant's light blue/white palette and Inter/Microsoft YaHei typography.
- Top-level navigation is a white horizontal bar with blue text and a bottom active indicator.
- The brand and account column are aligned to a 198 px left rail, matching the reference proportions.
- The account list uses a pale-blue selected state, compact actions, blue primary actions, and red destructive actions.
- Content uses white cards, subtle `#D9E2EC` borders, 8 px corner radii, compact controls, and pale-blue table headers.
- Queue actions use the same blue primary / blue outline / disabled gray hierarchy as the reference.
- Status bars use the same muted blue-gray surface.
- Publish and clip configuration dialogs use the same light background and primary save action.
- The video-channel page preserves its required embedded WebView2 browser workspace; the reference TikTok screen uses that area for a production table, so content structure intentionally differs while visual language matches.

## Remaining P3 polish

- The video-channel task table is shorter because the embedded browser remains the primary workspace.
- Some legacy NumericUpDown arrows inherit native Avalonia sizing rather than the exact TikTok queue sizing.
- Existing WebView2 dependency version warnings remain unchanged.

final result: passed
