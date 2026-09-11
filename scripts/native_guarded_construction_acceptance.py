"""Disposable guarded construction acceptance; launch through container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import shutil
from pathlib import Path

from native_package_acceptance import (Evidence, bridge_session, check_startup_log,
    gabs_executable, object_value, package_files, payload, prepare, prepare_rendered)
from native_protobuf_acceptance import proto
from native_compatibility_acceptance import discovery, validate_discovery
from rimgovernor.bridge_game import BridgeGame
from rimgovernor.clock_control import PlayClock
from rimgovernor.native_scenario import advance_game


def outcome(reply: dict, case: str) -> dict:
    assert set(reply) == {case}, f"Expected {case}: {reply}"
    return object_value(reply[case], case)


class ScenarioClock:
    """Only the existing native scenario helper's runtime dependencies."""
    def __init__(self, bridge, report):
        self.game, self.supervisor = BridgeGame(bridge), PlayClock(bridge)
        self.lock, self.review_task = asyncio.Lock(), None
        self.clock_events, self.report = [], report

    def receive_clock_events(self):
        self.report.setdefault("clock_events", []).extend(self.clock_events)
        self.clock_events.clear()

    def note(self, kind, message, **details):
        self.report.setdefault("notes", []).append(dict(kind=kind, message=message, **details))


async def go_phase(binary: Path, root: Path, output: Path, configuration: Path,
                   profile: Path, mode: str, request: Path, report: dict) -> dict:
    destination = output / ("go-" + mode)
    record = {"mode": mode, "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest()}
    report.setdefault("go", []).append(record)
    command = [str(binary.resolve()), "-mode", mode, "-gabs", str(Path(gabs_executable(root, configuration)).resolve()),
        "-config", str(configuration.resolve()), "-profile", str(profile.resolve()),
        "-state", str((output / "building.sqlite").resolve()), "-output", str(destination.resolve()),
        "-game", "rimgovernor-trial", "-force-takeover"]
    if mode == "place":
        command += ["-execute", "-request", str(request.resolve())]
    with (output / ("go-" + mode + "-stdout.txt")).open("wb") as stdout, \
         (output / ("go-" + mode + "-stderr.txt")).open("wb") as stderr:
        process = await asyncio.create_subprocess_exec(*command, stdout=stdout, stderr=stderr)
        try:
            async with asyncio.timeout(150):
                await process.wait()
        finally:
            if process.returncode is None:
                process.kill()
                await process.wait()
            record["exit_code"] = process.returncode
    assert process.returncode == 0, f"Go {mode} failed; retained report and stdout/stderr"
    result = object_value(json.loads((destination / "report.json").read_text()), "Go report")
    assert result.get("passed") is True
    assert result.get("nativeCalled") is (mode == "place")
    if mode == "observe":
        assert result["progress"]["Unresolved"] is False
        assert not any(row["Name"] == "operations_execute" for row in result["calls"])
    record["report"] = result
    return result


async def run(root: Path, output: Path, binary: Path, *, headless=True, timeout_seconds=2400) -> bool:
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report = {"passed": False, "scope": "Disposable ordinary WoodLog Wall construction, guarded authority and attempt semantics, Go durable restart reconciliation."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        private = output / "buildingsmoke-linux-amd64"
        shutil.copyfile(binary, private)
        private.chmod(0o700)
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, name, arguments=None, *, timeout=30):
                async with asyncio.timeout(timeout):
                    return payload(await evidence.call(bridge, label, name, arguments))

            async def wire(label, name, request):
                return proto(await call(label, "rimgovernor/" + name, {"request": json.dumps(request)}))

            async def reconnect(label):
                async with asyncio.timeout(60):
                    await evidence.record(label, {"tool": "games_connect", "forceTakeover": True},
                        bridge.core("games_connect", gameId=bridge.game_id, forceTakeover=True))

            try:
                async with asyncio.timeout(timeout_seconds):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    names = await discovery(bridge, evidence)
                    fixtures = {"test/guarded_construction_prepare", "test/guarded_construction_control"}
                    inventory = json.loads((Path(__file__).resolve().parents[1] / "contracts/domain-inventory.json").read_text())
                    rows = inventory["native_surface"]["tools"]
                    production = {row["name"] for row in rows if row["build_role"] == "production"}
                    all_fixtures = {row["name"] for row in rows if row["build_role"] == "fixture"}
                    validate_discovery(names, production, all_fixtures, fixtures)
                    report["discovery"] = {"production": len(production), "fixtures": len(fixtures), "names": names}
                    await call("new-game", "rimworld/start_debug_game_ready",
                        {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000}, timeout=180)
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    loaded = outcome(await wire("identity", "lifecycle_read_identity", {}), "loaded")
                    identity, tick = loaded["context"]["identity"], loaded["context"]["tick"]
                    prepared = await call("prepare", "test/guarded_construction_prepare", {"siteCount": 3})
                    assert prepared.get("success") is True and len(prepared["sites"]) == 3
                    assert all(prepared[k] == identity[k] for k in ("colonyId", "loadToken", "mapId"))
                    sites = prepared["sites"]
                    report["prepared"] = prepared
                    session = "python-guarded-acceptance"
                    observed = outcome(await wire("typed-status", "observations_read_status",
                        {"scope": {"expectedIdentity": identity}, "colonists": False, "threats": False}), "observed")
                    assert observed["context"]["identity"] == identity and observed["context"]["tick"] == tick
                    fields = {key: False for key in ("terrain", "roof", "visibility", "traversal", "zone",
                        "areas", "things", "designations", "room", "growth")}
                    cells = outcome(await wire("typed-cells", "observations_get_cells",
                        {"scope": {"expectedIdentity": identity}, "exactCells": {"cells": [
                            {"x": sites[0]["x"], "z": sites[0]["z"]}]}, "fields": fields, "page": {"limit": 1}}), "observed")
                    assert cells["appliedFields"] == fields and len(cells["cells"]) == 1
                    assert cells["mapSize"]["width"] > sites[0]["x"] and cells["mapSize"]["height"] > sites[0]["z"]
                    building_args = {"x": sites[1]["x"], "z": sites[1]["z"], "radius": 1, "category": "all", "aggregate": False}
                    before = await call("before-preview", "home/list_buildings", building_args)
                    preview = outcome(await wire("operation-preview", "operations_preview", {"identity": identity,
                        "operation": {"placeBuilding": {"placement": sites[1]}}}), "evaluated")
                    assert preview["accepted"] is True
                    after = await call("after-preview", "home/list_buildings", building_args)
                    assert {k:v for k,v in before.items() if k != "operation"} == {k:v for k,v in after.items() if k != "operation"}

                    async def status(label):
                        value = outcome(await wire(label, "authority_read_status", {"identity": identity}), "status")
                        assert value["context"]["identity"] == identity
                        return value

                    async def acquire(label, duration=30000):
                        state = await status(label + "-before")
                        return outcome(await wire(label, "authority_control", {"acquire": {"identity": identity,
                            "expectedGeneration": state["context"]["nativeGeneration"],
                            "owner": {"controllerSessionId": session, "playerDirection": "1"}, "leaseMs": duration}}), "granted")

                    def execute(grant, number, site):
                        return {"precondition": {"identity": identity, "expectedGeneration": grant["context"]["nativeGeneration"],
                            "leaseId": grant["leaseId"], "attempt": {"controllerSessionId": session,
                                "actionId": "fixture-" + str(number), "attemptId": "1"}},
                            "operation": {"placeBuilding": {"placement": site}}}

                    async def refuse(label, request, code="FAILURE_CODE_STALE_GENERATION"):
                        failure = outcome(await wire(label, "operations_execute", request), "failure")
                        assert failure.get("code") == code, failure

                    grant = await acquire("acquire")
                    await call("ordinary-pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    paused_authority = await status("pause-preserves-authority")
                    assert "active" in paused_authority and paused_authority["context"]["nativeGeneration"] == grant["context"]["nativeGeneration"]
                    renewed = outcome(await wire("renew", "authority_control", {"renew": {"identity": identity,
                        "expectedGeneration": grant["context"]["nativeGeneration"], "leaseId": grant["leaseId"],
                        "controllerSessionId": session, "leaseMs": 30000}}), "granted")
                    assert renewed["leaseId"] == grant["leaseId"]
                    request = execute(renewed, 1, sites[1])
                    receipt = outcome(await wire("place-cancel-site", "operations_execute", request), "receipt")
                    construction = receipt["applied"]["observed"]["construction"]
                    assert construction["stage"] == "CONSTRUCTION_STAGE_BLUEPRINT"
                    attempt = {"identity": identity, "attempt": request["precondition"]["attempt"]}
                    pending = outcome(await wire("initial-progress", "receipts_observe_progress", attempt), "progress")
                    assert "pending" in pending and pending["completeInspection"] is True
                    assert outcome(await wire("replay", "operations_execute", request), "receipt") == receipt
                    conflict = execute(renewed, 1, sites[2])
                    await refuse("attempt-conflict", conflict, "FAILURE_CODE_ATTEMPT_CONFLICT")
                    outcome(await wire("manual", "authority_control", {"revoke": {"identity": identity,
                        "expectedGeneration": renewed["context"]["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL"}}), "revoked")
                    assert outcome(await wire("replay-after-manual", "operations_execute", request), "receipt") == receipt
                    assert outcome(await wire("lookup-after-manual", "receipts_lookup", attempt), "receipt") == receipt
                    await refuse("manual-blocks-new", execute(renewed, 2, sites[2]))
                    outcome(await wire("refusal-not-admitted", "receipts_lookup", {"identity": identity,
                        "attempt": execute(renewed, 2, sites[2])["precondition"]["attempt"]}), "unknown")
                    grant = await acquire("cancel-authority")
                    control = dict(identity, operation="cancel", blueprintId=construction["currentThingId"])
                    assert (await call("external-cancel", "test/guarded_construction_control", control))["success"] is True
                    assert (await status("cancel-revoked"))["inactive"]["reason"] == "REVOCATION_REASON_EXTERNAL_ORDER"
                    progress = outcome(await wire("cancel-progress", "receipts_observe_progress",
                        {"identity": identity, "attempt": request["precondition"]["attempt"]}), "progress")
                    assert progress.get("completeInspection") is True and "unsuccessful" in progress
                    grant = await acquire("draft-authority")
                    control = dict(identity, operation="draft", pawnId=prepared["pawnId"])
                    assert (await call("external-draft", "test/guarded_construction_control", control))["drafted"] is True
                    assert (await status("draft-revoked"))["inactive"]["reason"] == "REVOCATION_REASON_PLAYER_CONTROL"
                    await refuse("draft-blocks-new", execute(grant, 3, sites[2]))
                    assert (await call("undraft", "test/guarded_construction_control", control))["drafted"] is False
                    grant = await acquire("ordered-job-authority")
                    move = dict(identity, operation="move", pawnId=prepared["pawnId"], **prepared["pawnCell"])
                    assert (await call("external-ordered-job", "test/guarded_construction_control", move))["success"] is True
                    assert (await status("ordered-job-revoked"))["inactive"]["reason"] == "REVOCATION_REASON_EXTERNAL_ORDER"
                    grant = await acquire("expiry-authority", 1000)
                    await asyncio.sleep(1.2)  # Wall-clock lease expiry; no simulation ticks.
                    expired = await status("expired")
                    assert expired["inactive"]["reason"] == "REVOCATION_REASON_LEASE_EXPIRED"
                    await refuse("expired-blocks-new", execute(grant, 4, sites[2]))
                    current = outcome(await wire("before-go", "lifecycle_read_identity", {}), "loaded")
                    assert current["paused"] is True and current["context"]["tick"] == tick
                    fixture = output / "go-placement.json"
                    fixture.write_text(json.dumps(sites[0]), encoding="utf8")
                    profile = root / ("headless-profile" if headless else "profile")
                    placed = await go_phase(private, root, output, configuration, profile, "place", fixture, report)
                    await reconnect("python-after-go-place")
                    calls = [row for row in placed["calls"] if row["Name"] == "operations_execute"]
                    assert len(calls) == 1
                    go_receipt = outcome(proto(object_value(calls[0]["Receipt"]["Structured"], "Go structured reply")), "receipt")
                    lookup = {"identity": identity, "attempt": go_receipt["attempt"]}
                    runtime = ScenarioClock(bridge, report)
                    complete = False
                    for window in range(13):
                        progress = outcome(await wire(f"construction-progress-{window}", "receipts_observe_progress", lookup), "progress")
                        if "completed" in progress:
                            assert progress.get("completeInspection") is True
                            effect = progress["completed"]["evidence"]["construction"]
                            assert effect["stage"] == "CONSTRUCTION_STAGE_BUILDING" and effect["present"] is True
                            assert effect["defName"] == "Wall" and effect["stuff"] == "WoodLog"
                            assert effect["cell"] == {"x": sites[0]["x"], "z": sites[0]["z"]}
                            complete = True
                            break
                        assert "pending" in progress, f"Construction did not remain pending: {progress}"
                        if window < 12:
                            await advance_game(runtime, 600, report, timeout=180)
                    assert complete, "Construction not completed within 7200 simulation ticks"
                    await go_phase(private, root, output, configuration, profile, "observe", fixture, report)
                    await reconnect("python-after-go-observe")
                    final = outcome(await wire("final-identity", "lifecycle_read_identity", {}), "loaded")
                    assert final["context"]["identity"] == identity and final["paused"] is True
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report["passed"] = True
            finally:
                async with asyncio.timeout(60):
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
    except BaseException as error:
        report["passed"], report["error"] = False, repr(error)
        raise
    finally:
        report["artifacts"] = {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in output.rglob("*") if p.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--go-building-smoke", type=Path, required=True)
    parser.add_argument("--rendered", action="store_true")
    parser.add_argument("--timeout-seconds", type=int, default=2400)
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-guarded-construction-acceptance",
        args.go_building_smoke, headless=not args.rendered, timeout_seconds=args.timeout_seconds)) else 1)
