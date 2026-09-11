"""Capture discovery and copied-save compatibility in one owned native worker.

Run through scripts/container_scenario.py. No fixtures are invoked and no ordinary
game ticks are requested. This does not establish populated state-family recovery.
"""
from __future__ import annotations

import argparse
import asyncio
from collections import Counter
from collections.abc import Awaitable, Mapping
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

from mcp.types import CallToolResult
from rimgovernor.bridge import BridgeClient, BridgeError

SOURCE = Path(__file__).resolve().parents[1]
GAME_COMPONENTS = frozenset("HomeBridge.BridgeTools." + name for name in (
    "ColonyIdentity", "ConstructionLineageState", "GearOwnership", "HaulTrackingState",
    "MiningState", "ProductionPolicyState", "RecoveryAreas", "WallRemovalState"))
MAP_COMPONENT = "HomeBridge.BridgeTools.HomeCoverageState"
UNKNOWN_KEY = "n01UnknownArgumentProbe"


def object_value(value: object, label: str) -> dict[str, object]:
    if not isinstance(value, dict) or not all(isinstance(key, str) for key in value):
        raise AssertionError(f"{label} must be an object")
    return value


def payload(result: CallToolResult) -> dict[str, object]:
    return object_value(result.structuredContent, "structuredContent")


def page_names(page: Mapping[str, object]) -> tuple[list[str], str]:
    rows = page.get("tools")
    if not isinstance(rows, list):
        raise AssertionError("Discovery tools must be an explicit list")
    names: list[str] = []
    for entry in rows:
        name = object_value(entry, "discovery row").get("gabpName")
        if not isinstance(name, str) or not name or "/" not in name:
            raise AssertionError("Discovery row requires its native gabpName")
        names.append(name)
    cursor = page.get("nextCursor")
    if cursor is None:
        cursor = ""
    if not isinstance(cursor, str):
        raise AssertionError("Discovery cursor must be a string or null")
    return names, cursor


def validate_discovery(names: list[str], production: set[str], fixtures: set[str],
                       expected_fixtures: set[str]) -> None:
    duplicates = sorted(name for name, count in Counter(names).items() if count != 1)
    assert not duplicates, f"Duplicate discovery registrations: {duplicates}"
    found = set(names)
    assert production <= found, f"Missing production exports: {sorted(production - found)}"
    assert expected_fixtures <= found, f"Missing expected fixtures: {sorted(expected_fixtures - found)}"
    fixture_names = found & fixtures | {name for name in found if name.startswith("test/") or "fixture" in name.casefold()}
    assert fixture_names == expected_fixtures, f"Unexpected fixture exports: {sorted(fixture_names - expected_fixtures)}"


@dataclass
class Evidence:
    output: Path
    sequence: int = 0

    async def record(self, label: str, request: Mapping[str, object],
                     operation: Awaitable[CallToolResult], *, allow_error: bool = False) -> CallToolResult:
        self.sequence += 1
        path = self.output / f"{self.sequence:04d}-{label}.json"
        row: dict[str, object] = {"request": dict(request)}
        try:
            result = await operation
        except BridgeError as error:
            row.update(result=error.result.model_dump(mode="json"), error=repr(error))
            path.write_text(json.dumps(row, indent=2), encoding="utf-8")
            if allow_error:
                return error.result
            raise
        except BaseException as error:
            row["error"] = repr(error)
            path.write_text(json.dumps(row, indent=2), encoding="utf-8")
            raise
        row["result"] = result.model_dump(mode="json")
        path.write_text(json.dumps(row, indent=2), encoding="utf-8")
        return result

    async def call(self, bridge: BridgeClient, label: str, name: str,
                   arguments: dict[str, object] | None = None, *, allow_error: bool = False) -> CallToolResult:
        arguments = arguments or {}
        return await self.record(label, {"tool": name, "arguments": arguments},
                                 bridge.call(name, **arguments), allow_error=allow_error)


async def discovery(bridge: BridgeClient, evidence: Evidence, *, max_pages: int = 100) -> list[str]:
    names: list[str] = []
    cursor = ""
    seen: set[str] = set()
    for _ in range(max_pages):
        assert cursor not in seen, "Discovery repeated a pagination cursor"
        seen.add(cursor)
        result = await evidence.record("discovery", {"tool": "games_tool_names", "cursor": cursor},
                                       bridge.names(cursor=cursor))
        page, cursor = page_names(payload(result))
        names.extend(page)
        if not cursor:
            return names
    raise AssertionError("Discovery exceeded the bounded page limit")


def verify_reload(before: Mapping[str, object], after: Mapping[str, object],
                  clock: Mapping[str, object]) -> None:
    assert isinstance(before.get("colonyId"), str) and before["colonyId"], "Missing saved colony identity"
    assert after.get("colonyId") == before["colonyId"], "Colony identity changed on reload"
    assert isinstance(before.get("loadToken"), str) and before["loadToken"], "Missing original load token"
    assert isinstance(after.get("loadToken"), str) and after["loadToken"], "Missing reloaded token"
    assert after["loadToken"] != before["loadToken"], "Load token did not rotate"
    assert after.get("mapId") == before.get("mapId"), "Reload changed active map"
    tick = before.get("tick")
    assert type(tick) is int and type(after.get("tick")) is int, "Missing native ticks"
    # Existing paired-resume acceptance permits one load-boundary tick. Observation
    # within each loaded session must remain at the exact same paused tick.
    assert 0 <= after["tick"] - tick <= 1, "Reload advanced beyond the load-boundary tick allowance"
    assert clock.get("paused") is True and clock.get("ticksGame") == after["tick"], "Reload is not paused at the observed tick"


def component_census(path: Path) -> dict[str, object]:
    root = ET.parse(path).getroot()
    game = root.find("game")
    assert game is not None, "Save has no game"
    groups = [("game", game.find("components"))]
    maps = game.findall("maps/li")
    assert maps, "Save has no maps"
    groups += [(f"map-{index}", element.find("components")) for index, element in enumerate(maps)]
    census: dict[str, object] = {}
    for scope, group in groups:
        assert group is not None, f"Save has no component list for {scope}"
        counts = Counter(element.get("Class", "").split(",", 1)[0] for element in group)
        native = {name: count for name, count in counts.items() if name.startswith("HomeBridge.")}
        assert all(count == 1 for count in native.values()), f"Duplicate native components in {scope}: {native}"
        expected = GAME_COMPONENTS if scope == "game" else {MAP_COMPONENT}
        assert expected <= native.keys(), f"Missing native components in {scope}: {sorted(expected - native.keys())}"
        census[scope] = native
    return census


def binder_observation(result: CallToolResult) -> dict[str, object]:
    data = result.structuredContent or {}
    unknown = data.get("unknownArguments")
    if result.isError or data.get("success") is False:
        outcome = "request-refused-inspect-raw-error"
    elif isinstance(unknown, list) and UNKNOWN_KEY in unknown:
        outcome = "unknown-key-reported"
    else:
        outcome = "unknown-key-not-reported-legacy-binder-may-drop-it"
    return {"outcome": outcome, "strict_unknown_key_validation_proven": False,
            "scope": "Observed reply only; an empty list or success does not prove unknown-key rejection."}


async def run(root: Path, output: Path, *, timeout_seconds: int,
              expected_fixtures: set[str], headless: bool = True) -> bool:
    from rimgovernor.bridge_runtime import BridgeRuntime
    from rimgovernor.campaign_manifest import capture_manifest
    from rimgovernor.session_checkpoint import create_checkpoint, prepare_resume, profile_path, stop_for_restart
    from rimgovernor.store import Store
    from deterministic_foothold import NoInference
    from session_checkpoint_acceptance import ready

    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report: dict[str, object] = {"passed": False, "scope": "Actual paginated discovery, source-export presence/fixture isolation, read-only replies, copied-save component census and identity across in-process/fresh-process reload. No populated state-family recovery or pawn-work acceptance.",
                                "expected_fixtures": sorted(expected_fixtures), "reload_tick_allowance": 1,
                                "headless": headless}
    store = Store(root / "native-compatibility.sqlite")
    rt = BridgeRuntime(store, root, fresh=True, headless=headless, model_factory=lambda _: NoInference())
    try:
        async with asyncio.timeout(timeout_seconds):
            inventory_path = SOURCE / "contracts/domain-inventory.json"
            manifest = object_value(json.loads(inventory_path.read_text()), "domain inventory")
            native = object_value(manifest["native_surface"], "native surface")
            tools = native["tools"]
            assert isinstance(tools, list)
            rows = [object_value(row, "native inventory row") for row in tools]
            production = {str(row["name"]) for row in rows if row["build_role"] == "production"}
            fixtures = {str(row["name"]) for row in rows if row["build_role"] == "fixture"}
            assert production, "No production export expectations"
            report["inventory_sha256"] = hashlib.sha256(inventory_path.read_bytes()).hexdigest()
            report["inputs"] = capture_manifest(SOURCE, root, root / ("config-headless" if headless else "config"),
                                                 {"mode": "no inference"}, profile=profile_path(root, headless))
            await ready(rt)
            assert rt.mode == "manual", "Capture requires Manual mode"
            initial = payload(await evidence.call(rt.bridge, "identity-initial", "home/colony_identity"))
            status = payload(await evidence.call(rt.bridge, "status-initial", "home/status"))
            initial_clock = object_value(status["time"], "clock")
            assert initial_clock.get("paused") is True and initial_clock.get("ticksGame") == initial["tick"]
            names = await discovery(rt.bridge, evidence)
            report["discovery_names"] = names
            # Retain every detail before checking migration expectations so failures
            # still leave a complete installed-surface capture where possible.
            detail_failures: list[str] = []
            for name in sorted(set(names)):
                detail = await evidence.record("detail", {"tool": "games_tool_detail", "name": name},
                                                rt.bridge.detail(name), allow_error=True)
                if detail.isError or (detail.structuredContent or {}).get("success") is False:
                    detail_failures.append(name)
            report["detail_failures"] = detail_failures
            assert not detail_failures, f"Discovery detail failures: {detail_failures}"
            validate_discovery(names, production, fixtures, expected_fixtures)
            report["production_exports"] = len(production)
            facts = payload(await evidence.call(rt.bridge, "planning-facts", "home/colony_facts", {"planning": True}))
            definitions = object_value(facts["definitions"], "native definitions")
            definition = object_value(definitions["SleepingSpot"], "observed sleeping spot definition")
            pawn = rt.batch.summary.pawns[0]
            candidate = {"defName": definition["defName"], "stuff": definition.get("stuff") or "", "x": pawn.position.x,
                         "z": pawn.position.z, "rotation": "north"}
            arguments = {"placements": json.dumps([candidate])}
            preview = payload(await evidence.call(rt.bridge, "placement-preview", "home/placement_previews", arguments))
            assert preview.get("success") is True and isinstance(preview.get("results"), list) and len(preview["results"]) == 1
            unknown = await evidence.call(rt.bridge, "placement-unknown-key", "home/placement_previews",
                                          dict(arguments, **{UNKNOWN_KEY: True}), allow_error=True)
            report["binder"] = binder_observation(unknown)
            malformed: dict[str, object] = {}
            for label, invalid in (("missing", {}), ("null", {"placements": None}),
                                   ("wrong-type", {"placements": 17})):
                reply = await evidence.call(rt.bridge, "placement-" + label, "home/placement_previews",
                                             invalid, allow_error=True)
                malformed[label] = {"isError": reply.isError,
                                    "success": (reply.structuredContent or {}).get("success"),
                                    "scope": "Observed legacy response; coercion/rejection policy is not inferred from a receipt."}
            report["malformed_placement_requests"] = malformed
            after = payload(await evidence.call(rt.bridge, "status-after-reads", "home/status", {"colonists": False, "threats": False}))
            assert object_value(after["time"], "clock").get("ticksGame") == initial["tick"], "Read-only capture advanced ticks"
            assert object_value(after["time"], "clock").get("paused") is True

            checkpoint = await create_checkpoint(rt, rt.context_token)
            report["initial_checkpoint"] = checkpoint
            save = Path(checkpoint["manifest_path"]).parent / "game.rws"
            shutil.copy2(save, output / "initial-save.rws")
            report["initial_components"] = component_census(save)
            await evidence.call(rt.bridge, "load-copy", "rimworld/load_game_ready",
                                {"saveName": checkpoint["save_name"], "readiness": "visual", "timeoutMs": 90000})
            await rt.sync_identity()
            reloaded = payload(await evidence.call(rt.bridge, "identity-reloaded", "home/colony_identity"))
            clock = payload(await evidence.call(rt.bridge, "status-reloaded", "home/status", {"colonists": False, "threats": False}))
            verify_reload(initial, reloaded, object_value(clock["time"], "clock"))
            report["in_process_reload"] = {"before": initial, "after": reloaded}
            checkpoint = await create_checkpoint(rt, rt.context_token)
            report["restart_checkpoint"] = checkpoint
            save = Path(checkpoint["manifest_path"]).parent / "game.rws"
            shutil.copy2(save, output / "reloaded-save.rws")
            report["reloaded_components"] = component_census(save)
            report["owned_stop"] = await stop_for_restart(rt, rt.context_token, checkpoint["manifest_path"])
            await rt.stop()
            store.close()
            _, state = prepare_resume(checkpoint["manifest_path"])
            store = Store(state / "bridge.sqlite")
            rt = BridgeRuntime(store, root, fresh=True, headless=headless, resume=checkpoint["manifest_path"], model_factory=lambda _: NoInference())
            await ready(rt)
            restarted = payload(await evidence.call(rt.bridge, "identity-restarted", "home/colony_identity"))
            clock = payload(await evidence.call(rt.bridge, "status-restarted", "home/status", {"colonists": False, "threats": False}))
            verify_reload(reloaded, restarted, object_value(clock["time"], "clock"))
            report["fresh_process_reload"] = {"before": reloaded, "after": restarted}
            checkpoint = await create_checkpoint(rt, rt.context_token)
            save = Path(checkpoint["manifest_path"]).parent / "game.rws"
            shutil.copy2(save, output / "restarted-save.rws")
            report["restarted_components"] = component_census(save)
            assert rt.counters["model_calls"] == 0 and NoInference.attempts == 0
            report["passed"] = True
    except BaseException as error:
        report["error"] = repr(error)
    finally:
        try:
            async with asyncio.timeout(60):
                await rt.stop()
        except BaseException as error:
            report.update(passed=False, cleanup_error=repr(error))
        store.close()
        report["artifacts"] = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                               for path in sorted(output.iterdir()) if path.is_file()}
        (output / "compatibility-result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2), flush=True)
    return report["passed"] is True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(os.environ.get("RIMGOVERNOR_BRIDGE_ROOT", "/worker/run")))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--timeout-seconds", type=int, default=600)
    parser.add_argument("--expect-fixture", action="append", default=[])
    parser.add_argument("--rendered", action="store_true", help="Use the prepared normal graphical profile; no rendering acceptance claim.")
    args = parser.parse_args()
    if not 120 <= args.timeout_seconds <= 3600:
        parser.error("--timeout-seconds must be 120..3600")
    root = args.root.resolve()
    output = args.output.resolve() if args.output else root / "native-compatibility"
    return 0 if asyncio.run(run(root, output, timeout_seconds=args.timeout_seconds,
                                expected_fixtures=set(args.expect_fixture),
                                headless=not args.rendered and os.environ.get("RIMGOVERNOR_HEADLESS", "1") != "0")) else 1


if __name__ == "__main__":
    raise SystemExit(main())
