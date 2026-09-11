"""Paused typed draft acceptance; run inside container_scenario.py with private fixtures."""
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
from native_pawn_acceptance import outcome


OWNER = {"controllerSessionId": "native-draft-acceptance", "playerDirection": "1"}


def pawn_row(reply, identity, pawn_id=None):
    observed = outcome(reply, "observed")
    assert observed["context"]["identity"] == identity
    completeness = observed["completeness"]
    rows = observed.get("pawns", [])
    assert completeness["page"] == {"complete": True}
    assert int(completeness["matched"]) == int(completeness["returned"]) == len(rows)
    assert int(completeness["unreadable"]) == 0
    if pawn_id is not None:
        assert len(rows) == 1 and rows[0]["pawn"]["id"] == pawn_id
    assert rows
    row = rows[0]
    snapshot = row["pawn"]["snapshot"]
    assert snapshot["entityId"] == row["pawn"]["id"] and snapshot["token"]
    assert snapshot["context"] == observed["context"]
    assert type(row["drafted"]) is bool
    claim = row["draftClaim"]
    assert set(claim) in ({"owned"}, {"unowned"}), claim
    if "owned" in claim:
        assert claim["owned"]["pawnSnapshot"] == snapshot
        assert claim["owned"]["claimId"] and claim["owned"]["owner"]
    return row


def target(row):
    return {"entityId": row["pawn"]["id"], "expectedSnapshotToken": row["pawn"]["snapshot"]["token"]}


def execute_request(identity, grant, row, number):
    return {"precondition": {"identity": deepcopy(identity),
        "expectedGeneration": grant["context"]["nativeGeneration"], "leaseId": grant["leaseId"],
        "attempt": dict(controllerSessionId=OWNER["controllerSessionId"], actionId="draft-" + str(number), attemptId="1")},
        "operation": {"setDrafted": {"pawn": target(row), "drafted": True, "allowPersistentDraft": False}}}


def release_request(identity, row):
    claim = row["draftClaim"]["owned"]
    assert claim["owner"] == OWNER
    return {"identity": deepcopy(identity), "pawn": target(row),
        "expectedClaimId": claim["claimId"], "originalOwner": deepcopy(claim["owner"])}


def same_control(before, after):
    assert before["drafted"] is after["drafted"]
    assert target(before) == target(after)
    assert before["draftClaim"] == after["draftClaim"]


def actual_order(external, row):
    assert external["success"] is True and external["accepted"] is True
    if external["jobId"] is None:
        assert external["jobDef"] is None and "job" not in row
    else:
        assert row["job"]["loadId"] == str(external["jobId"])
        assert row["job"]["defName"] == external["jobDef"]


def owned_effect(receipt, row, case="applied", issued=True):
    effect = receipt[case]["observed"]["job"]
    claim = row["draftClaim"]["owned"]
    assert row["drafted"] is True and claim["owner"] == OWNER
    assert effect["pawnId"] == row["pawn"]["id"]
    assert effect["drafted"] is True and effect["verified"] is True and effect["issued"] is issued
    assert effect["draftClaimId"] == claim["claimId"]
    assert effect["draftOwner"] == OWNER["controllerSessionId"]
    assert effect["resultingSnapshotToken"] == target(row)["expectedSnapshotToken"]


async def run(root: Path, output: Path, *, headless=True):
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report = {"passed": False, "headless": headless,
        "scope": "Paused real native draft CAS, owned claims, replay/no-op, Manual cleanup and player override refusal. Capacity is covered by compiled ledger tests, not injected native outcomes."}
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
                        if pawn_id is not None:
                            query["filter"]["ids"] = [pawn_id]
                        row = pawn_row(await wire(label, "observations_list_pawns", query), identity, pawn_id)
                        assert row["pawn"]["snapshot"]["context"]["tick"] == initial["context"]["tick"]
                        return row

                    async def acquire(label, duration=30000):
                        status = outcome(await wire(label + "-status", "authority_read_status", {"identity": identity}), "status")
                        return outcome(await wire(label, "authority_control", {"acquire": {"identity": identity,
                            "expectedGeneration": status["context"]["nativeGeneration"], "owner": OWNER, "leaseMs": duration}}), "granted")

                    before = await read("initial-pawn")
                    assert before["drafted"] is False and before["draftClaim"] == {"unowned": {}}
                    pawn_id = before["pawn"]["id"]
                    grant = await acquire("acquire")
                    request = execute_request(identity, grant, before, 1)
                    receipt = outcome(await wire("draft", "operations_execute", request), "receipt")
                    drafted = await read("drafted", pawn_id)
                    owned_effect(receipt, drafted)
                    assert target(before) != target(drafted)
                    attempt = {"identity": identity, "attempt": request["precondition"]["attempt"]}
                    progress = outcome(await wire("progress", "receipts_observe_progress", attempt), "progress")
                    assert progress["completeInspection"] is True and "completed" in progress
                    assert progress["completed"]["evidence"]["job"]["draftClaimId"] == drafted["draftClaim"]["owned"]["claimId"]
                    assert outcome(await wire("replay", "operations_execute", request), "receipt") == receipt
                    same_control(drafted, await read("replay-unchanged", pawn_id))
                    assert outcome(await wire("lookup", "receipts_lookup", attempt), "receipt") == receipt
                    conflict = deepcopy(request)
                    conflict["operation"]["setDrafted"]["drafted"] = False
                    assert outcome(await wire("attempt-conflict", "operations_execute", conflict), "failure")["code"] == "FAILURE_CODE_ATTEMPT_CONFLICT"
                    malformed = execute_request(identity, grant, drafted, 99)
                    del malformed["operation"]["setDrafted"]["drafted"]
                    assert outcome(await wire("missing-drafted-presence", "operations_execute", malformed), "failure")["code"] == "FAILURE_CODE_INVALID_REQUEST"
                    same_control(drafted, await read("refusals-unchanged", pawn_id))
                    no_op = outcome(await wire("owned-no-op", "operations_execute", execute_request(identity, grant, drafted, 2)), "receipt")
                    unchanged = await read("no-op-unchanged", pawn_id)
                    owned_effect(no_op, unchanged, "noChange", False)
                    same_control(drafted, unchanged)
                    stale = outcome(await wire("stale-snapshot", "operations_execute", execute_request(identity, grant, before, 3)), "failure")
                    assert stale["code"] == "FAILURE_CODE_OWNER_CONFLICT"
                    same_control(drafted, await read("stale-unchanged", pawn_id))
                    outcome(await wire("manual", "authority_control", {"revoke": {"identity": identity,
                        "expectedGeneration": grant["context"]["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL"}}), "revoked")
                    fresh = await read("after-manual", pawn_id)
                    cleanup = release_request(identity, fresh)
                    released = outcome(await wire("cleanup", "operations_release_owned_draft", cleanup), "released")
                    assert released["request"] == cleanup and released["observed"]["drafted"] is False
                    assert released["observed"]["verified"] is True and released["observed"]["issued"] is True
                    undrafted = await read("after-cleanup", pawn_id)
                    assert undrafted["drafted"] is False and undrafted["draftClaim"] == {"unowned": {}}
                    assert released["observed"]["resultingSnapshotToken"] == target(undrafted)["expectedSnapshotToken"]
                    again = outcome(await wire("cleanup-retry", "operations_release_owned_draft", cleanup), "alreadyReleased")
                    assert again["request"] == cleanup and again["observed"]["issued"] is False and again["observed"]["verified"] is True
                    same_control(undrafted, await read("cleanup-retry-unchanged", pawn_id))
                    assert outcome(await wire("replay-after-cleanup", "operations_execute", request), "receipt") == receipt

                    expiring = await acquire("expiry-acquire", 1000)
                    expiry_receipt = outcome(await wire("expiry-draft", "operations_execute", execute_request(identity, expiring, undrafted, 6)), "receipt")
                    expiry_owned = await read("expiry-owned", pawn_id)
                    owned_effect(expiry_receipt, expiry_owned)
                    await asyncio.sleep(1.2)  # Monotonic lease time only; simulation remains paused.
                    expired = outcome(await wire("expiry-status", "authority_read_status", {"identity": identity}), "status")
                    assert expired["inactive"]["reason"] == "REVOCATION_REASON_LEASE_EXPIRED"
                    expiry_cleanup = release_request(identity, await read("expiry-fresh-snapshot", pawn_id))
                    expiry_release = outcome(await wire("expiry-cleanup", "operations_release_owned_draft", expiry_cleanup), "released")
                    assert expiry_release["request"] == expiry_cleanup
                    assert expiry_release["observed"]["verified"] is True and expiry_release["observed"]["drafted"] is False
                    undrafted = await read("expiry-undrafted", pawn_id)
                    assert undrafted["drafted"] is False and undrafted["draftClaim"] == {"unowned": {}}

                    grant = await acquire("override-acquire")
                    second = outcome(await wire("second-draft", "operations_execute", execute_request(identity, grant, undrafted, 4)), "receipt")
                    owned = await read("before-player-order", pawn_id)
                    owned_effect(second, owned)
                    obsolete_cleanup = release_request(identity, owned)
                    external = await call("external-order", "test/b04f_setup", {"op": "external-order", "pawn": pawn_id})
                    overridden = await read("after-player-order", pawn_id)
                    actual_order(external, overridden)
                    assert overridden["draftClaim"] == {"unowned": {}}
                    assert target(overridden) != target(owned)
                    outcome(await wire("refuse-old-cleanup", "operations_release_owned_draft", obsolete_cleanup), "failure")
                    preserved = await read("player-order-preserved", pawn_id)
                    same_control(overridden, preserved)
                    actual_order(external, preserved)
                    player_effect = await call("player-draft", "test/b04f_setup", {"op": "external-draft", "pawn": pawn_id, "drafted": True})
                    assert player_effect["success"] is True and player_effect["after"] is True
                    player = await read("player-drafted", pawn_id)
                    assert player["drafted"] is True and player["draftClaim"] == {"unowned": {}}
                    grant = await acquire("unowned-acquire")
                    assert outcome(await wire("refuse-adoption", "operations_execute", execute_request(identity, grant, player, 5)), "failure")["code"] == "FAILURE_CODE_OWNER_CONFLICT"
                    same_control(player, await read("unowned-preserved", pawn_id))
                    final = outcome(await wire("identity-after", "lifecycle_read_identity", {}), "loaded")
                    assert final["paused"] is True and final["context"]["identity"] == identity
                    assert final["context"]["tick"] == initial["context"]["tick"]
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report.update(passed=True, initial_context=initial["context"], final_context=final["context"], pawn_id=pawn_id)
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
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-draft-acceptance", headless=not args.rendered)) else 1)
