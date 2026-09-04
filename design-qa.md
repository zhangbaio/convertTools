# Design QA

- Source visual truth: `C:\Users\PC\AppData\Local\Temp\codex-clipboard-9aef8a28-7a86-4b0b-975c-b6d580679326.png`
- Implementation screenshot: unavailable because native-app capture is not exposed in the current Computer Use session.
- Viewport: intended desktop application window at approximately 1496 x 860 logical pixels.
- Source pixels: 1496 x 790.
- Implementation pixels: unavailable.
- Density normalization: not applicable; implementation capture unavailable.
- State: 快手分账 · 个人, global account sidebar expanded, empty project queue.

## Full-view comparison evidence

- The source shows an operations-first workspace: action buttons and step toggles at the top, work directory and scan controls below, then search and a wide project table.
- Code inspection confirms the new Kuaishou page uses that same region order and removes the generic task-type/config/scheduling form.
- A rendered full-view comparison could not be completed without native-app screenshot access.

## Focused region comparison evidence

- Source focus: top action/step area and work-directory row.
- Implementation code provides working controls for uploading by title, importing local projects, scanning the selected workspace, adding/running checked jobs, retrying, stopping, selecting preparation steps, opening the operator platform, and opening Kuaishou configuration.
- Pixel-level typography, spacing, wrapping, and overflow remain unverified.

## Findings

- [P1] Rendered implementation capture unavailable.
  - Location: Kuaishou personal and enterprise workflow pages.
  - Evidence: the source screenshot is available, but the native app is not exposed by the current UI-control surface.
  - Impact: visual fidelity and responsive overflow cannot be certified automatically.
  - Fix: capture the running application once native-app control is available, compare at 1496 x 860, then correct any visible drift.

## Required fidelity surfaces

- Fonts and typography: follows the existing Avalonia application theme; rendered fidelity blocked.
- Spacing and layout rhythm: structure matches the source ordering; pixel-level fidelity blocked.
- Colors and visual tokens: reuses existing panel, primary, secondary, and danger styles; rendered fidelity blocked.
- Image quality and asset fidelity: no new raster assets are required for this workflow page.
- Copy and content: task type was removed; Kuaishou-specific work-directory, operator-platform, configuration, preparation, queue, and project-table language is present.

## Interaction checks

- Release build succeeded with zero errors.
- Existing analytics and Kuaishou tests passed: 19/19.
- Native click-through checks are blocked by unavailable native-app control.

## Comparison history

- Initial screen: generic task creation form differed materially from the reference operations workspace.
- Fix: replaced the Kuaishou screen with the existing production workflow surface, removed task-type presentation in Kuaishou mode, hid unrelated steps, and routed import/scan operations to the selected Kuaishou platform and global account.
- Post-fix visual evidence: unavailable; P1 remains blocked.

## Implementation checklist

- Capture both Kuaishou personal and enterprise pages.
- Verify action-row wrapping at 1180 and 1496 pixel window widths.
- Verify directory selection, local import, upload-by-title dialog, and configuration dialog.
- Correct any P1/P2 visual differences and repeat comparison.

final result: blocked
