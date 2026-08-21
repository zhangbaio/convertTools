"""Extract HG high-bitrate startup masters via bundled Frida.

The C# host owns DPAPI caching. This helper only attaches to the official
HongguoHighDownloader process, captures BCrypt hash inputs matching
``master|enc|desktop-v1`` / ``master|sign|desktop-v1``, and writes JSON::

    {"ok": true, "enc": "<b64url>", "sign": "<b64url>"}

Usage::

    python provision_startup_masters.py --exe "C:\\path\\to\\HG....exe" --output out.json
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time

_ENC_RE = re.compile(rb"^([A-Za-z0-9_\-]{43,88})\|enc\|desktop-v1$")
_SIGN_RE = re.compile(rb"^([A-Za-z0-9_\-]{43,88})\|sign\|desktop-v1$")

_JS = r"""
var create = Module.getExportByName("bcrypt.dll", "BCryptCreateHash");
var hashData = Module.getExportByName("bcrypt.dll", "BCryptHashData");
var finish = Module.getExportByName("bcrypt.dll", "BCryptFinishHash");
Interceptor.attach(create, {
  onEnter: function (a) { this.ph = a[1]; },
  onLeave: function () {
    try { send({k: "c", h: this.ph.readPointer().toString()}); } catch (e) {}
  }
});
Interceptor.attach(hashData, {
  onEnter: function (a) {
    var n = a[2].toInt32();
    if (n > 0 && n < 200) send({k: "d", h: a[0].toString(), n: n}, a[1].readByteArray(n));
  }
});
Interceptor.attach(finish, {
  onEnter: function (a) { send({k: "f", h: a[0].toString()}); }
});
"""


def _eprint(message: str) -> None:
    print(message, file=sys.stderr, flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract HG high-bitrate startup masters")
    parser.add_argument("--exe", required=True, help="Official HongguoHighDownloader exe")
    parser.add_argument("--output", required=True, help="JSON output path for enc/sign")
    parser.add_argument("--wait", type=int, default=30, help="Seconds to wait for masters")
    args = parser.parse_args()

    exe_path = os.path.abspath(args.exe)
    if not os.path.isfile(exe_path):
        _eprint(f"客户端 exe 不存在：{exe_path}")
        return 2

    try:
        import frida  # type: ignore
    except ImportError:
        _eprint("内置 Python 未安装 frida。请重新安装本程序，或把 frida 放到 tools/win-x64/python。")
        return 2

    chunks: dict[str, list[bytes]] = {}
    found: dict[str, str] = {}

    def on_message(message, data):
        if message.get("type") != "send":
            return
        payload = message.get("payload") or {}
        kind = payload.get("k")
        handle = payload.get("h") or ""
        if kind == "c":
            chunks[handle] = []
        elif kind == "d" and data:
            chunks.setdefault(handle, []).append(bytes(data))
        elif kind == "f":
            msg = b"".join(chunks.get(handle, []))
            chunks[handle] = []
            match = _ENC_RE.match(msg)
            if match:
                found["enc"] = match.group(1).decode("ascii")
            match = _SIGN_RE.match(msg)
            if match:
                found["sign"] = match.group(1).decode("ascii")

    _eprint("启动官方客户端并挂钩……（请确保此前已完全退出，不只是关窗口）")
    pid = frida.spawn([exe_path])
    session = frida.attach(pid)
    script = session.create_script(_JS)
    script.on("message", on_message)
    script.load()
    frida.resume(pid)

    deadline = time.time() + max(5, int(args.wait))
    while time.time() < deadline and not ("enc" in found and "sign" in found):
        time.sleep(0.3)

    try:
        session.detach()
    except Exception:
        pass

    if "enc" not in found or "sign" not in found:
        _eprint(
            "未抽到完整启动密钥（enc=%s sign=%s）。请完全退出官方客户端后重试。"
            % ("有" if "enc" in found else "无", "有" if "sign" in found else "无")
        )
        return 1

    output_dir = os.path.dirname(os.path.abspath(args.output))
    if output_dir:
        os.makedirs(output_dir, exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump({"ok": True, "enc": found["enc"], "sign": found["sign"]}, handle)
    _eprint("已提取启动密钥。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
