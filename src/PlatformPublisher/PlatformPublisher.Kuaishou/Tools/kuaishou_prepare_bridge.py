"""Rebuild Kuaishou upload artifacts with the short-drama assistant's own functions."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def _workspace_for(workflow: Path) -> Path:
    if workflow.parent.name.lower() == "workflow":
        return workflow.parent.parent
    return workflow.parent


def _settings_for_workspace(tool_root: Path, settings_path: Path, workspace: Path):
    sys.path.insert(0, str(tool_root))
    from app_settings import GlobalSettings, load_settings
    from app_settings_constants import ACCOUNT_PROFILE_SYNC_FIELDS
    import dataclasses

    del settings_path
    settings = load_settings()
    profiles = json.loads(settings.account_profiles_json or "[]")
    profile = next(
        (
            item for item in profiles
            if any(str(item.get(key) or "").strip() and Path(str(item.get(key))).resolve() == workspace.resolve()
                   for key in ("kuaishou_personal_last_workspace", "kuaishou_enterprise_last_workspace",
                               "kuaishou_last_workspace", "last_task_queue_workspace"))
        ),
        None,
    )
    data = dataclasses.asdict(settings)
    if profile is not None:
        for key in ACCOUNT_PROFILE_SYNC_FIELDS:
            if key in profile and profile.get(key) not in (None, ""):
                data[key] = profile[key]
    allowed = set(GlobalSettings.__dataclass_fields__)
    return GlobalSettings(**{key: value for key, value in data.items() if key in allowed})


def _apply_overrides(settings, overrides: Path | None):
    if overrides is None or not overrides.is_file():
        return settings
    import dataclasses

    from app_settings import GlobalSettings

    raw = json.loads(overrides.read_text(encoding="utf-8"))
    data = dataclasses.asdict(settings)
    fields = GlobalSettings.__dataclass_fields__
    for key, value in raw.items():
        field = fields.get(key)
        if field is None or value is None:
            continue
        data[key] = _coerce_setting(field.type, value)
    return GlobalSettings(**{key: data[key] for key in fields})


def _coerce_setting(annotation, value):
    args = getattr(annotation, "__args__", None)
    if args:
        concrete = [item for item in args if item is not type(None)]
        if len(concrete) == 1:
            annotation = concrete[0]
    if annotation is bool:
        if isinstance(value, bool):
            return value
        return str(value).strip().lower() in {"1", "true", "yes"}
    if annotation is int:
        return int(float(value))
    if annotation is float:
        return float(value)
    return value


def prepare(project: Path, tool_root: Path, settings_path: Path, workspace: Path | None, overrides: Path | None, args) -> dict:
    project = project.resolve()
    settings = _apply_overrides(
        _settings_for_workspace(tool_root, settings_path, workspace or _workspace_for(project)),
        overrides,
    )
    from modules.kuaishou.settlement_project_info_service import (
        KUAISHOU_SETTLEMENT_REWRITE_INFO_VERSION,
        kuaishou_settlement_rewrite_project_info,
    )
    from modules.kuaishou.upload_service import KuaishouPrepareOptions, prepare_kuaishou_project

    if args.edition == "enterprise":
        from modules.kuaishou_enterprise.state_service import (
            KUAISHOU_ENTERPRISE_AUTO_FILL_NAME,
            KUAISHOU_ENTERPRISE_PAYLOAD_PREVIEW_NAME,
            load_kuaishou_enterprise_prepare_state,
            save_kuaishou_enterprise_prepare_state,
        )
        state_loader = load_kuaishou_enterprise_prepare_state
        state_saver = save_kuaishou_enterprise_prepare_state
        auto_fill_name = KUAISHOU_ENTERPRISE_AUTO_FILL_NAME
        preview_name = KUAISHOU_ENTERPRISE_PAYLOAD_PREVIEW_NAME
    else:
        from modules.kuaishou_personal.state_service import (
            load_kuaishou_personal_prepare_state,
            save_kuaishou_personal_prepare_state,
        )
        state_loader = load_kuaishou_personal_prepare_state
        state_saver = save_kuaishou_personal_prepare_state
        auto_fill_name = "kuaishou-auto-fill.json"
        preview_name = "kuaishou-payload-preview.json"

    def log(message: str) -> None:
        print(f"LOG\t{message}", flush=True)

    def step(step_key: str, _phase: str, status: str, _elapsed, error: str) -> None:
        print(f"STEP\t{step_key}\t{status}\t{error or ''}", flush=True)

    state = prepare_kuaishou_project(
        project,
        settings,
        options=KuaishouPrepareOptions(
            download=args.download,
            rewrite_info=args.rewrite,
            generate_poster=args.poster,
            auto_fill_info=args.auto_fill,
            generate_project_images=args.project_images,
            project_image_template_id=args.template_id or "",
            project_image_template_root=args.template_root or None,
            generate_guarantee_letter=False,
            force_rerun_completed_steps=args.force,
            auto_fill_file_name=auto_fill_name,
            payload_preview_file_name=preview_name,
            rewrite_project_info_fn=kuaishou_settlement_rewrite_project_info,
            rewrite_existing_info=True,
            rewrite_info_version=KUAISHOU_SETTLEMENT_REWRITE_INFO_VERSION,
        ),
        log_fn=log,
        step_event_fn=step,
        state_loader=state_loader,
        state_saver=state_saver,
    )
    completed = state.get("completed_prepare_steps") if isinstance(state, dict) else []
    return {
        "episode_count": int((state or {}).get("episode_count") or 0),
        "completed_prepare_steps": completed if isinstance(completed, list) else [],
        "workflow_dir": str((state or {}).get("workflow_dir") or ""),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", required=True)
    parser.add_argument("--tool-root", required=True)
    parser.add_argument("--edition", choices=["personal", "enterprise"], default="personal")
    parser.add_argument("--settings", default=str(Path.home() / ".weixin_channel_tool" / "settings.json"))
    parser.add_argument("--workspace", default="")
    parser.add_argument("--overrides", default="")
    parser.add_argument("--template-id", default="")
    parser.add_argument("--template-root", default="")
    parser.add_argument("--download", action="store_true")
    parser.add_argument("--rewrite", action="store_true")
    parser.add_argument("--poster", action="store_true")
    parser.add_argument("--project-images", action="store_true")
    parser.add_argument("--auto-fill", action="store_true")
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()
    workspace = Path(args.workspace) if args.workspace else None
    overrides = Path(args.overrides) if args.overrides else None
    try:
        result = prepare(Path(args.project), Path(args.tool_root), Path(args.settings), workspace, overrides, args)
    except Exception as exc:
        print(f"ERROR\t{exc}", flush=True)
        raise
    print("RESULT\t" + json.dumps(result, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
