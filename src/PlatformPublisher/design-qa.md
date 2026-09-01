# System Settings Design QA

Reference: `codex-clipboard-51ffc8bf-eefc-4505-93d6-0bb8e7539caf.png`

Implementation: `PlatformPublisher.Desktop` → `系统设置`

## Comparison

- Top navigation uses the same light surface, blue active text, and bottom active indicator.
- The account/sidebar column remains visible while settings are open.
- Settings content reuses the TikTok assistant's production `SystemSettingsView`, including the secondary tabs, field sizing, scrolling, and fixed save action.
- The host-specific login hint now points to the multi-platform account profiles.
- Multi-platform settings use an isolated SQLite database and do not read or write the TikTok assistant settings database.

## Remaining P3 polish

- The multi-platform brand/sidebar is slightly wider than the screenshot reference to accommodate longer Kuaishou platform labels.
- The top navigation contains fewer enabled destinations because those pages have not yet been migrated.

final result: passed
