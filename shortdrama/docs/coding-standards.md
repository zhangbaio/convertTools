# Coding Standards

## Module boundaries

- Desktop views must be split by feature. Do not add new feature UI into `MainWindow.axaml` once a dedicated view exists.
- Feature-specific click handlers belong in the feature view code-behind, not in `MainWindow.axaml.cs`.
- `MainWindowViewModel` must grow through partial files grouped by feature, for example `MainWindowViewModel.TaskQueue.cs`.
- Domain and workflow logic must stay in `ShortDrama.Core` / `ShortDrama.Infrastructure`. Do not move business rules into Avalonia views.

## Weixin feature rules

- Weixin queue UI changes go to `ShortDrama.Desktop/Views/TaskQueueView.*`.
- Weixin upload runtime and page automation stay in `ShortDrama.Infrastructure/Automation/Weixin*`.
- Material publish state, duplicate publish behavior, and source-selection rules must be implemented as dedicated services or helpers. Do not inline them into unrelated UI files.

## File size guidance

- Avoid adding new feature logic to files that already exceed roughly 800 lines.
- When a file starts mixing multiple concerns, extract a new module before adding more behavior.

## UI rules

- Reuse theme tokens from `Assets/AppTheme.axaml`; avoid hard-coded colors in feature views unless adding a new shared token.
- Prefer command bindings for simple actions and reserve code-behind for UI-only tasks such as file pickers, dialogs, and view navigation.

## Testing rules

- Any non-trivial behavior change in `ShortDrama.Infrastructure` must add or update automated tests.
- Keep feature tests close to the feature. Weixin material publish tests belong under `ShortDrama.Infrastructure.Tests/Automation`.

## Local runtime files

- Do not commit local SDKs, cache directories, downloaded archives, or machine-specific runtime assets.
- Keep `.dotnet/`, `.setup-cache/`, build output directories, and other local-only assets ignored unless there is an explicit packaging reason.
