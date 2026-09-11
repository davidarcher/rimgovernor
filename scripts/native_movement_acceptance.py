"""Real typed pawn arrival through container_scenario.py; no injected movement."""
from __future__ import annotations
import argparse
import asyncio
from copy import deepcopy
import hashlib
import json
from pathlib import Path

from native_package_acceptance import (Evidence, bridge_session, check_startup_log,
    gabs_executable, package_files, payload, prepare, prepare_rendered)
from native_protobuf_acceptance import proto
from native_draft_acceptance import OWNER, pawn_row, target, execute_request, owned_effect, same_control, actual_order
from native_pawn_acceptance import outcome
from native_typed_clock_acceptance import TypedScenarioClock, ScenarioRuntime
from rimgovernor.native_scenario import advance_game


def move_request(identity, grant, row, number, destination):
    request = execute_request(identity, grant, row, number)
    request["operation"] = {"movePawn": {"pawn": target(row), "destination": deepcopy(destination)}}
    return request


def candidates(snapshot, origin):
    counts = snapshot["completeness"]
    assert counts["page"] == {"complete": True} and int(counts["unreadable"]) == 0
    cells = snapshot.get("cells", [])
    assert int(counts["matched"]) == int(counts["returned"]) == len(cells)
    result = []
    for row in cells:
        cell = row["cell"]
        distance = (cell["x"] - origin["x"]) ** 2 + (cell["z"] - origin["z"]) ** 2
        if 4 <= distance <= 25 and row.get("walkable") is True and row.get("passable") is True and row.get("fogged") is False:
            assert row["terrain"]
            result.append(cell)
    return sorted(result, key=lambda c: ((c["x"]-origin["x"])**2+(c["z"]-origin["z"])**2, c["x"], c["z"]))


def job_effect(receipt, row, destination):
    effect = receipt["applied"]["observed"]["job"]
    assert effect["issued"] is True and effect["verified"] is True
    assert effect["pawnId"] == row["pawn"]["id"] and effect["jobDef"] == "Goto"
    assert effect["targetA"]["cell"] == destination
    assert row["job"]["loadId"] == str(effect["jobId"]) and row["job"]["defName"] == "Goto"
    assert effect["draftClaimId"] == row["draftClaim"]["owned"]["claimId"]
    return effect


def arrival(progress, row, destination, issued):
    assert row["pawn"]["position"] == destination
    assert progress["completeInspection"] is True and "completed" in progress
    effect = progress["completed"]["evidence"]["job"]
    assert effect["jobId"] == issued["jobId"] and effect["targetA"]["cell"] == destination
    assert effect["draftClaimId"] == issued["draftClaimId"] == row["draftClaim"]["owned"]["claimId"]
    assert effect["verified"] is True


async def run(root: Path, output: Path, *, headless=True):
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report = {"passed": False, "headless": headless, "scope": "Real ordinary native Goto arrival under typed authority/clock, exact CAS/replay, no-op, Manual and player override. No teleport or completion injection."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, method, arguments=None):
                return payload(await evidence.call(bridge, label, method, arguments))
            async def wire(label, method, request):
                return proto(await call(label, "rimgovernor/" + method, {"request": json.dumps(request)}))
            try:
                async with asyncio.timeout(900):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    await call("new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    initial = outcome(await wire("identity-before", "lifecycle_read_identity", {}), "loaded")
                    assert initial["paused"] is True
                    identity = initial["context"]["identity"]
                    async def read(label, pawn_id=None):
                        query = {"scope": {"expectedIdentity": identity}, "filter": {"colonist": True, "downed": False}}
                        if pawn_id is not None: query["filter"]["ids"] = [pawn_id]
                        return pawn_row(await wire(label, "observations_list_pawns", query), identity, pawn_id)
                    async def acquire(label):
                        status = outcome(await wire(label+"-status", "authority_read_status", {"identity": identity}), "status")
                        return outcome(await wire(label, "authority_control", {"acquire": {"identity": identity,
                            "expectedGeneration": status["context"]["nativeGeneration"], "owner": OWNER, "leaseMs": 30000}}), "granted")
                    before = await read("initial-pawn")
                    pawn_id = before["pawn"]["id"]
                    assert before["drafted"] is False and before["draftClaim"] == {"unowned": {}}
                    grant = await acquire("acquire")
                    draft = outcome(await wire("draft", "operations_execute", execute_request(identity, grant, before, 1)), "receipt")
                    owned = await read("owned", pawn_id)
                    owned_effect(draft, owned)
                    origin = owned["pawn"]["position"]
                    exact_cells = [{"x": origin["x"]+dx, "z": origin["z"]+dz} for dx in range(-5,6) for dz in range(-5,6)
                        if 4 <= dx*dx+dz*dz <= 25 and origin["x"]+dx >= 0 and origin["z"]+dz >= 0]
                    fields = {key: key in ("terrain", "visibility", "traversal") for key in
                        ("terrain", "roof", "visibility", "traversal", "zone", "areas", "things", "designations", "room", "growth")}
                    cells = outcome(await wire("candidate-cells", "observations_get_cells", {"scope": {"expectedIdentity": identity},
                        "exactCells": {"cells": exact_cells}, "fields": fields}), "observed")
                    assert cells["context"]["identity"] == identity
                    destination = None
                    for number, cell in enumerate(candidates(cells, origin)[:24]):
                        preview = await wire("preview-"+str(number), "operations_preview", {"identity": identity,
                            "operation": {"movePawn": {"pawn": target(owned), "destination": cell}}})
                        if preview.get("evaluated", {}).get("accepted") is True:
                            destination = cell; break
                        assert "failure" in preview or preview.get("evaluated", {}).get("accepted") is False
                    assert destination is not None, "No exact nearby normally reachable destination"
                    request = move_request(identity, grant, owned, 2, destination)
                    receipt = outcome(await wire("move", "operations_execute", request), "receipt")
                    moving = await read("moving", pawn_id)
                    issued = job_effect(receipt, moving, destination)
                    attempt = {"identity": identity, "attempt": request["precondition"]["attempt"]}
                    progress = outcome(await wire("pending", "receipts_observe_progress", attempt), "progress")
                    assert "pending" in progress and progress["completeInspection"] is True
                    assert outcome(await wire("replay", "operations_execute", request), "receipt") == receipt
                    replay = await read("replay-unchanged", pawn_id)
                    same_control(moving, replay); assert moving["job"] == replay["job"]
                    stale = move_request(identity, grant, owned, 3, origin)
                    assert outcome(await wire("stale-snapshot", "operations_execute", stale), "failure")["code"] == "FAILURE_CODE_OWNER_CONFLICT"
                    assert outcome(await wire("outside-map", "operations_execute", move_request(identity, grant, moving, 4, {"x": -1, "z": -1})), "failure")["code"] == "FAILURE_CODE_INVALID_REQUEST"
                    supervisor = TypedScenarioClock(wire, identity, OWNER["controllerSessionId"], report)
                    supervisor.grant = grant
                    await supervisor.renew_authority()
                    runtime = ScenarioRuntime(bridge, supervisor, report)
                    await advance_game(runtime, 240, report, timeout=180)
                    grant = supervisor.grant
                    arrived = await read("arrived", pawn_id)
                    complete = outcome(await wire("completed", "receipts_observe_progress", attempt), "progress")
                    arrival(complete, arrived, destination, issued)
                    assert arrived["pawn"]["snapshot"]["context"]["nativeGeneration"] == receipt["admittedContext"]["nativeGeneration"]
                    no_op = outcome(await wire("same-position", "operations_execute", move_request(identity, grant, arrived, 5, destination)), "receipt")
                    effect = no_op["noChange"]["observed"]["job"]
                    assert effect["issued"] is False and effect["verified"] is True and "jobId" not in effect
                    same_control(arrived, await read("no-op-unchanged", pawn_id))
                    second_request = move_request(identity, grant, arrived, 6, origin)
                    outcome(await wire("return-order", "operations_execute", second_request), "receipt")
                    pending_return = await read("before-player-order", pawn_id)
                    external = await call("external-order", "test/b04f_setup", {"op": "external-order", "pawn": pawn_id})
                    overridden = await read("after-player-order", pawn_id)
                    actual_order(external, overridden)
                    assert overridden["draftClaim"] == {"unowned": {}} and target(overridden) != target(pending_return)
                    interrupted = outcome(await wire("interrupted", "receipts_observe_progress", {"identity": identity,
                        "attempt": second_request["precondition"]["attempt"]}), "progress")
                    assert interrupted["unsuccessful"]["reason"] == "UNSUCCESSFUL_REASON_INTERRUPTED"
                    grant = await acquire("unowned-acquire")
                    assert outcome(await wire("unowned-refusal", "operations_execute", move_request(identity, grant, overridden, 7, origin)), "failure")["code"] == "FAILURE_CODE_OWNER_CONFLICT"
                    same_control(overridden, await read("override-preserved", pawn_id))
                    outcome(await wire("manual", "authority_control", {"revoke": {"identity": identity,
                        "expectedGeneration": grant["context"]["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL"}}), "revoked")
                    outcome(await wire("manual-refusal", "operations_execute", move_request(identity, grant, overridden, 8, origin)), "failure")
                    final = outcome(await wire("identity-after", "lifecycle_read_identity", {}), "loaded")
                    assert final["paused"] is True and final["context"]["identity"] == identity
                    assert int(final["context"]["tick"])-int(initial["context"]["tick"]) == 240
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report.update(passed=True, pawn_id=pawn_id, destination=destination, issued=issued, final_context=final["context"])
            finally:
                async with asyncio.timeout(60): report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
    except BaseException as error:
        report.update(passed=False, error=repr(error)); raise
    finally:
        report["artifacts"] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in output.rglob("*") if path.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True); parser.add_argument("--output", type=Path)
    parser.add_argument("--rendered", action="store_true"); args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-movement-acceptance", headless=not args.rendered)) else 1)
