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


def rebuild(workflow: Path, tool_root: Path, settings_path: Path, workspace: Path | None = None) -> dict:
    workflow = workflow.resolve()
    settings = _settings_for_workspace(tool_root, settings_path, workspace or _workspace_for(workflow))
    from modules.kuaishou.auto_fill_service import generate_kuaishou_auto_fill
    from modules.kuaishou.upload_service import _build_local_proof_items, build_kuaishou_form_payload
    from project_info_text import parse_project_info_file
    from runtime_paths import atomic_write_text

    generate_kuaishou_auto_fill(workflow, settings, overwrite_existing=False)
    info = parse_project_info_file(workflow / "短剧信息.txt")
    horizontal = workflow / "快手横屏封面.jpg"
    vertical = workflow / "快手竖屏海报.jpg"
    payload = build_kuaishou_form_payload(
        workflow,
        info,
        settings,
        cover_material_id=str(horizontal),
        poster_material_id=str(vertical),
        proof_items=_build_local_proof_items(workflow, str(workflow / "承诺函.pdf") if (workflow / "承诺函.pdf").exists() else ""),
    )
    preview_path = workflow / "kuaishou-payload-preview.json"
    atomic_write_text(preview_path, json.dumps(payload, ensure_ascii=False, indent=2))
    return {"preview": str(preview_path), "episode_count": payload.get("episode_count"), "title": payload.get("name")}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workflow", required=True)
    parser.add_argument("--tool-root", required=True)
    parser.add_argument("--settings", default=str(Path.home() / ".weixin_channel_tool" / "settings.json"))
    parser.add_argument("--workspace", default="")
    args = parser.parse_args()
    workspace = Path(args.workspace) if args.workspace else None
    result = rebuild(Path(args.workflow), Path(args.tool_root), Path(args.settings), workspace)
    print(json.dumps(result, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
