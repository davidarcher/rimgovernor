"""Fresh production presentation read checks via container_scenario.py."""
from __future__ import annotations
import argparse
import asyncio
import hashlib
import json
import math
from pathlib import Path
from native_package_acceptance import Evidence, bridge_session, check_startup_log, gabs_executable, package_files, payload, prepare, prepare_rendered
from native_protobuf_acceptance import proto, unchanged
from native_compatibility_acceptance import discovery


def listing(value: dict, count: int) -> None:
    assert value == {"totalCount": count, "returnedCount": count, "complete": True, "truncated": False}, value


def camera_matches(typed: dict, native: dict) -> None:
    assert native["success"] is True
    fields = {"rootSize": native["rootSize"], "zoomRootSize": native["zoomRootSize"],
        "minimumRootSize": native["sizeRange"]["min"], "maximumRootSize": native["sizeRange"]["max"]}
    for key, value in fields.items():
        assert math.isfinite(typed[key]) and math.isclose(typed[key], value, rel_tol=1e-6, abs_tol=1e-6)
    for key in ("x", "z"):
        assert math.isclose(typed["mapPosition"].get(key, 0), native["mapPosition"][key], rel_tol=1e-6, abs_tol=1e-6)
    assert typed["nativeZoomRange"] == native["zoomRange"]
    assert typed["zoomExtensionEnabled"] == native["cameraZoomExtensionEnabled"]
    # Viewport coordinates may be negative; preserve signed native bounds.
    assert typed["viewRect"] == {key: native["viewRect"][key] for key in ("minX", "minZ", "maxX", "maxZ") if native["viewRect"][key] != 0}


def roster_matches(typed: dict, native: dict, identity: dict) -> None:
    assert native["success"] is True and native["count"] > 0
    rows = typed.get("colonists", [])
    listing(typed["listing"], len(rows))
    source = {row["pawnId"]: row for row in native["colonists"]}
    assert len(source) == native["count"] == len(rows)
    assert {row["pawnId"] for row in rows} == set(source)
    for row in rows:
        observed = source[row["pawnId"]]
        assert row["spawned"] is observed["spawned"] is True
        assert row["name"] == observed["name"]
        assert row["position"] == {k: v for k, v in observed["position"].items() if v != 0}
        assert row["mapId"] == identity["mapId"]  # Fresh scenario has one observed loaded map.


def selection_matches(typed: dict, native: dict, pawn_id: str, identity: dict) -> None:
    assert native["success"] is True and native["selectedCount"] == 1
    rows = typed["selectedObjects"]
    listing(typed["listing"], 1)
    assert len(rows) == 1 and rows[0]["id"] == pawn_id
    row, observed = rows[0], native["selectedObjects"][0]
    assert row["id"] == observed["id"] and row["nativeKind"] == observed["kind"] == "pawn"
    assert row["nativeType"] == observed["type"] and row["label"] == observed["label"]
    assert row["defName"] == observed["details"]["defName"] and row["mapId"] == identity["mapId"]
    assert row["position"] == {k: v for k, v in observed["details"]["position"].items() if v != 0}
    assert "fingerprint" not in typed and "visibleGizmoCount" not in typed
    assert "inspectText" not in row and "inspectLabel" not in row


async def run(root: Path, output: Path, *, headless: bool) -> bool:
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report = {"passed": False, "headless": headless, "scope": "Fresh one-loaded-map native presentation reads. Graphical exact observed pawn selection is setup only; no camera/input/capture mutation. No multi-map, zone/plan or overflow gameplay claim."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, name, arguments=None, timeout=30):
                async with asyncio.timeout(timeout):
                    return payload(await evidence.call(bridge, label, name, arguments))
            async def wire(label, method, arguments):
                return proto(await call(label, "rimgovernor/" + method, {"request": json.dumps(arguments)}))
            try:
                async with asyncio.timeout(600):
                    await evidence.record("start", {"tool": "games_start"}, bridge.core("games_start", gameId=bridge.game_id))
                    await bridge.connect()
                    names = await discovery(bridge, evidence)
                    for method in ("camera", "selection", "colonists"):
                        name = "rimgovernor/presentation_" + method
                        assert names.count(name) == 1
                        await evidence.record(method + "-detail", {"name": name}, bridge.detail(name))
                    assert not any(name.startswith("test/") or "fixture" in name.lower() for name in names)
                    await call("new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000}, 180)
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    initial = (await wire("initial", "lifecycle_read_identity", {}))["loaded"]
                    identity = initial["context"]["identity"]
                    native_roster = await call("setup-roster", "rimworld/list_colonists", {"currentMapOnly": True})
                    assert native_roster["success"] is True and native_roster["colonists"]
                    pawn_id = native_roster["colonists"][0]["pawnId"]
                    if not headless:
                        selected = await call("setup-select-observed-pawn", "rimworld/select_pawn", {"pawnId": pawn_id, "append": False})
                        assert selected["success"] is True and selected["selectedCount"] == 1
                    before = (await wire("identity-before", "lifecycle_read_identity", {}))["loaded"]
                    assert before["paused"] is True
                    if not headless:
                        camera_before = await call("camera-before", "rimworld/get_camera_state")
                        selection_before = await call("selection-before", "rimworld/get_selection_semantics")
                    for method, variant in (("camera", "camera"), ("selection", "selection")):
                        reply = await wire("typed-" + method, "presentation_" + method, {"identity": identity})
                        if headless:
                            assert set(reply) == {"failure"} and reply["failure"]["code"] == "FAILURE_CODE_UNAVAILABLE"
                        else:
                            assert set(reply) == {variant} and reply[variant]["context"] == before["context"]
                            if method == "camera": camera_matches(reply[variant], camera_before)
                            else: selection_matches(reply[variant], selection_before, pawn_id, identity)
                    for label, option in (("default", {}), ("current", {"currentMapOnly": True}), ("all", {"currentMapOnly": False})):
                        native = await call(label + "-native-roster", "rimworld/list_colonists", option)
                        reply = await wire(label + "-typed-roster", "presentation_colonists", {"identity": identity, **option})
                        assert set(reply) == {"roster"} and reply["roster"]["context"] == before["context"]
                        roster_matches(reply["roster"], native, identity)
                    for method in ("camera", "selection", "colonists"):
                        for label, raw in (("missing-identity", "{}"), ("unknown", json.dumps({"identity": identity, "unknown": True})), ("malformed", "{"), ("raw-object", {})):
                            reply = proto(await call(method + "-" + label, "rimgovernor/presentation_" + method, {"request": raw}))
                            assert set(reply) == {"failure"} and reply["failure"]["code"] == "FAILURE_CODE_INVALID_REQUEST"
                    if not headless:
                        unchanged(camera_before, await call("camera-after", "rimworld/get_camera_state"), "camera")
                        selection_after = await call("selection-after", "rimworld/get_selection_semantics")
                        keys = ("selectedObjects", "selectedCount", "selectionFingerprint", "selectionDetailsTruncated")
                        assert {key: selection_before[key] for key in keys} == {key: selection_after[key] for key in keys}
                    after = (await wire("identity-after", "lifecycle_read_identity", {}))["loaded"]
                    assert before["context"] == after["context"] and after["paused"] is True
                    report["context"] = after["context"]
                    report["colonists_checked"] = native_roster["count"]
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report["passed"] = True
            finally:
                async with asyncio.timeout(60):
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
    except BaseException as error:
        report.update(passed=False, error=repr(error))
        raise
    finally:
        report["artifacts"] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in output.rglob("*") if path.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--rendered", action="store_true")
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-presentation-acceptance", headless=not args.rendered)) else 1)
