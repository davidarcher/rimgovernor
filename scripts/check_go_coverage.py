"""Validate the coordinated G01 inventory without importing the runtime."""

from __future__ import annotations

import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
INVENTORIES = ("domain", "interface", "state")


def check(root: Path = ROOT) -> list[str]:
    errors: list[str] = []
    ids: set[str] = set()
    revisions: set[str] = set()
    total = 0
    for name in INVENTORIES:
        path = root / "contracts" / f"{name}-inventory.json"
        try:
            manifest = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, ValueError) as exc:
            errors.append(f"{path.name}: {exc}")
            continue
        if manifest.get("version") != 1:
            errors.append(f"{name}: unsupported inventory version")
        revision = manifest.get("source_revision", "")
        if not re.fullmatch(r"[0-9a-f]{40}", revision):
            errors.append(f"{name}: full comparison revision required")
        revisions.add(revision)
        items = manifest.get("items", [])
        if not items:
            errors.append(f"{name}: empty inventory")
        for row in items:
            total += 1
            ident = row.get("id", "")
            if not ident or ident in ids:
                errors.append(f"{name}: missing or duplicate id {ident!r}")
            ids.add(ident)
            for key in ("category", "source", "go_package", "owner_chunk", "status", "notes"):
                if not row.get(key):
                    errors.append(f"{ident}: missing {key}")
            source = row.get("source", {})
            source_path = source.get("path", "") if isinstance(source, dict) else source
            if not source_path or not (root / source_path).exists():
                errors.append(f"{ident}: missing source {source_path}")
            for key in ("fixtures", "native_scenarios"):
                values = row.get(key)
                if not isinstance(values, list):
                    errors.append(f"{ident}: {key} must be an explicit list")
                    continue
                for value in values:
                    ref = value.split("::", 1)[0].split("#", 1)[0]
                    if not (root / ref).exists():
                        errors.append(f"{ident}: missing {key} reference {value}")
    if len(revisions) != 1:
        errors.append("inventory comparison revisions differ")
    print(f"G01 inventory: {total} rows, {len(errors)} structural errors")
    return errors


def main() -> int:
    errors = check()
    for error in errors:
        print(error, file=sys.stderr)
    if errors:
        return 1
    for name in INVENTORIES:
        validator = ROOT / "scripts" / f"check_{name}_inventory.py"
        if validator.exists():
            result = subprocess.run([sys.executable, str(validator), "--check"], cwd=ROOT)
            if result.returncode:
                return result.returncode
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
