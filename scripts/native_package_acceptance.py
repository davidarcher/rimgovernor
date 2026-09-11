"""Check the unified package in a new disposable game through container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path
from mcp.types import CallToolResult

from rimgovernor.bridge import bridge_session, gabs_executable
from rimgovernor.headless import prepare, prepare_rendered

from native_compatibility_acceptance import (
    Evidence, component_census, discovery, object_value, payload, validate_discovery,
)


SOURCE = Path(__file__).resolve().parents[1]
PACKAGE_ID = "davidarcher.rimgovernor.native"


def protobuf_outcome(result: CallToolResult, case: str) -> dict[str, object]:
    """Inspect this smoke test's expected official ProtoJSON outcome, retaining raw evidence."""
    assert not result.isError, "SDK refused Protobuf request"
    encoded = payload(result).get("payload")
    assert isinstance(encoded, str) and len(encoded.encode("utf8")) <= 1024 * 1024, "Missing or oversized ProtoJSON payload"
    message = object_value(json.loads(encoded), "ProtoJSON reply")
    assert set(message) == {case}, f"Expected {case} reply, received {sorted(message)}"
    return object_value(message[case], case)


def check_startup_log(log: str, *, headless: bool) -> None:
    """Batch initialization is required; normal startup must leave it inactive."""
    assert "[HeadlessRim] Bootstrap Error:" not in log, "Headless bootstrap failed"
    assert "[HeadlessRim] Post-Init Error:" not in log, "Headless runtime patches failed"
    active = "[HeadlessRim] Headless mode active." in log
    armed = "[HeadlessRim] Bootstrap armed." in log
    assert active == headless and armed == headless, "Headless initialization disagrees with launch mode"


def package_files(installation: Path) -> dict[str, str]:
    package = installation / "Mods/RimGovernor"
    files = ("About/About.xml", "Assemblies/RimGovernor.Runtime.dll",
             "BridgeTools/RimGovernor/RimGovernor.Bridge.dll")
    import xml.etree.ElementTree as ET
    assert ET.parse(package / files[0]).getroot().findtext("packageId") == PACKAGE_ID
    hashes = {name: hashlib.sha256((package / name).read_bytes()).hexdigest() for name in files}
    for old in ("RimGovernorObservations", "RimGovernorHeadless"):
        assert not (installation / "Mods" / old).exists(), f"Mixed package installation: {old}"
    return hashes


async def run(root: Path, output: Path, *, headless: bool, timeout_seconds: int) -> bool:
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report: dict[str, object] = {"passed": False, "headless": headless,
        "scope": "Unified package, newly generated disposable game, production discovery, read-only preview and startup mode. No legacy save acceptance or pawn-work claim."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        config = object_value(json.loads((configuration / "config.json").read_text(encoding="utf8")), "configuration")
        game = object_value(object_value(config["games"], "games")["rimgovernor-trial"], "game")
        report["package_files"] = package_files(Path(str(game["workingDir"])))
        inventory = json.loads((SOURCE / "contracts/domain-inventory.json").read_text(encoding="utf8"))
        rows = inventory["native_surface"]["tools"]
        production = {row["name"] for row in rows if row["build_role"] == "production"}
        fixtures = {row["name"] for row in rows if row["build_role"] == "fixture"}
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            try:
                async with asyncio.timeout(timeout_seconds):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    names = await discovery(bridge, evidence)
                    validate_discovery(names, production, fixtures, set())
                    report["discovery"] = {"names": names, "production": len(production), "fixtures": 0}
                    report["new_game"] = payload(await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready",
                        {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000}))
                    await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    before = payload(await evidence.call(bridge, "status-before", "home/status", {"colonists": False, "threats": False}))
                    loaded = protobuf_outcome(await evidence.call(bridge, "identity", "rimgovernor/lifecycle_read_identity",
                        {"request": "{}"}), "loaded")
                    context = object_value(loaded["context"], "identity context")
                    identity = object_value(context["identity"], "identity")
                    assert identity.get("colonyId") and identity.get("loadToken")
                    authority = protobuf_outcome(await evidence.call(bridge, "authority", "rimgovernor/authority_read_status",
                        {"request": json.dumps({"identity": identity})}), "status")
                    assert "active" not in authority, "Fresh game unexpectedly grants native authority"
                    assert object_value(authority["context"], "authority context")["identity"] == identity
                    camera_before = payload(await evidence.call(bridge, "camera-before", "rimworld/get_camera_state"))
                    building_args = {"x": 0, "z": 0, "radius": 1, "category": "all", "aggregate": False}
                    buildings_before = payload(await evidence.call(bridge, "buildings-before", "home/list_buildings", building_args))
                    # Exact native definition lookup handles this ordinary definition and
                    # edge cell. Refusal is valid; preview must not place anything.
                    candidate = {"defName": "Wall", "x": 0, "z": 0, "rotation": "ROTATION_NORTH", "stuff": ""}
                    preview = protobuf_outcome(await evidence.call(bridge, "preview", "rimgovernor/placement_preview",
                        {"request": json.dumps({"identity": identity, "placements": [candidate]})}), "batch")
                    assert len(preview.get("results", [])) == 1
                    assert object_value(preview["context"], "preview context")["identity"] == identity
                    candidate_reply = object_value(preview["results"][0], "candidate")
                    assert set(candidate_reply) == {"evaluated"}, "Ordinary Wall preview could not be evaluated"
                    assert type(object_value(candidate_reply["evaluated"], "evaluated").get("canPlace")) is bool
                    after = payload(await evidence.call(bridge, "status-after", "home/status", {"colonists": False, "threats": False}))
                    before_clock, after_clock = object_value(before["time"], "clock"), object_value(after["time"], "clock")
                    assert before_clock["ticksGame"] == after_clock["ticksGame"] and after_clock["paused"] is True
                    assert int(object_value(preview["context"], "preview context")["tick"]) == after_clock["ticksGame"]
                    camera_after = payload(await evidence.call(bridge, "camera-after", "rimworld/get_camera_state"))
                    # SDK operation timing/IDs differ between calls; compare game
                    # camera fields only, retaining both raw envelopes for diagnosis.
                    camera_keys = set(camera_before) - {"operation"}
                    assert {k: camera_before[k] for k in camera_keys} == {k: camera_after.get(k) for k in camera_keys}, "Read-only preview moved the camera"
                    buildings_after = payload(await evidence.call(bridge, "buildings-after", "home/list_buildings", building_args))
                    assert {k: v for k, v in buildings_before.items() if k != "operation"} == {
                        k: v for k, v in buildings_after.items() if k != "operation"
                    }, "Preview changed buildings, blueprints or frames near the candidate"
                    saved = payload(await evidence.call(bridge, "save", "rimworld/save_game", {"saveName": "RimGovernor-package-acceptance"}))
                    save = Path(str(saved["path"])).resolve()
                    assert saved.get("exists") is True and save.is_relative_to(root.resolve())
                    report["components"] = component_census(save)
                    report["save_sha256"] = hashlib.sha256(save.read_bytes()).hexdigest()
                    log = root / ("HeadlessPlayer.log" if headless else "Player.log")
                    check_startup_log(log.read_text(encoding="utf8", errors="replace"), headless=headless)
                    report["paused_tick"] = after_clock["ticksGame"]
                    report["passed"] = True
            except BaseException as error:
                report["error"] = repr(error)
                raise
            finally:
                try:
                    async with asyncio.timeout(60):
                        stopped = await bridge.core("games_stop", gameId=bridge.game_id)
                        report["stop"] = stopped.model_dump(mode="json")
                except BaseException as cleanup_error:
                    report["cleanup_error"] = repr(cleanup_error)
                    report["passed"] = False
                    if "error" not in report:
                        raise
    except BaseException as error:
        report["passed"] = False
        report.setdefault("error", repr(error))
        raise
    finally:
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return bool(report["passed"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--rendered", action="store_true")
    parser.add_argument("--timeout-seconds", type=int, default=600)
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-package-acceptance",
        headless=not args.rendered, timeout_seconds=args.timeout_seconds)) else 1)
