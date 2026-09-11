"""Bounded actual combat outcome acceptance through container_scenario.py."""
from __future__ import annotations
import argparse
import asyncio
from copy import deepcopy
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace

from native_package_acceptance import Evidence, bridge_session, check_startup_log, gabs_executable, package_files, payload, prepare, prepare_rendered
from native_protobuf_acceptance import proto
from native_pawn_acceptance import outcome
from native_draft_acceptance import OWNER, pawn_row, target, execute_request, owned_effect, same_control, release_request, actual_order
from native_typed_clock_acceptance import TypedScenarioClock, ScenarioRuntime
from rimgovernor.native_scenario import advance_game

COMBAT_WINDOWS = 20
TICKS_PER_WINDOW = 240


class CombatScenarioClock(TypedScenarioClock):
    """Translate the shared helper's exact committed targets to official policy."""
    def __init__(self, wire, identity, owner, report, targets):
        super().__init__(wire, identity, owner, report)
        assert targets and len(set(targets)) == len(targets) and all(isinstance(t, str) and t for t in targets)
        self.targets = list(targets)

    async def change(self, speed, *, max_ticks, **arguments):
        assert arguments == {"mode": "combat", "ignored_hostiles": ",".join(self.targets)}
        return await super().change(speed, max_ticks=max_ticks)

    async def control(self, method, request):
        if method == "start":
            request = deepcopy(request)
            request["policy"]["mode"] = "WATCH_MODE_COMBAT"
            request["policy"]["acknowledgedHostileIds"] = list(self.targets)
        return await super().control(method, request)


def observed_rows(reply, identity):
    observed = outcome(reply, "observed")
    assert observed["context"]["identity"] == identity
    rows = observed.get("pawns", [])
    counts = observed["completeness"]
    assert counts["page"] == {"complete": True} and int(counts["unreadable"]) == 0
    assert int(counts["matched"]) == int(counts["returned"]) == len(rows)
    assert len({r["pawn"]["id"] for r in rows}) == len(rows)
    return rows


def healthy_candidates(rows, *, ranged=False):
    assert rows and all(r["dead"] is False and r["downed"] is False and r["health"]["summaryFraction"] > .5005 for r in rows)
    return [r for r in rows if r["drafted"] is False and "Violent" not in r["biography"].get("disabledWorkTags", [])
        and (not ranged or "Shooting" not in r["biography"].get("disabledWorkTags", []))
        and not any(i["field"] == "disabled_work_tags" for i in r["biography"].get("issues", []))]


def attack_request(identity, grant, actor, victim, number, mode="ATTACK_MODE_MELEE"):
    request = execute_request(identity, grant, actor, number)
    request["operation"] = {"attackTarget": {"pawn": target(actor), "target": target(victim), "mode": mode,
        "requireHostile": True, "requireStanding": True, "requireCombatHealth": True}}
    return request


def terminal(progress, receipt, victim, target_id, *, ranged=False):
    assert progress["completeInspection"] is True and "completed" in progress
    original = receipt["applied"]["observed"]["job"]
    effect = progress["completed"]["evidence"]["job"]
    assert effect["jobId"] == original["jobId"] and effect["jobDef"] == ("AttackStatic" if ranged else "AttackMelee")
    assert effect["pawnId"] == original["pawnId"] and effect["targetA"] == {"thingId": target_id}
    assert effect["verified"] is True
    assert victim["pawn"]["id"] == target_id and (victim["dead"] is True or victim["downed"] is True)
    assert effect["verifiedReason"].startswith("Native positive damage by this exact attacker/job caused")


def overridden_attack(progress, before, after, external):
    assert progress["completeInspection"] is True
    assert progress["unsuccessful"]["reason"] == "UNSUCCESSFUL_REASON_INTERRUPTED"
    assert after["draftClaim"] == {"unowned": {}} and after["drafted"] is True
    assert target(before) != target(after)
    actual_order(external, after)


async def run(root: Path, output: Path, *, headless=True, ranged=False):
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    mode = "ATTACK_MODE_RANGED" if ranged else "ATTACK_MODE_MELEE"
    job_def = "AttackStatic" if ranged else "AttackMelee"
    report = {"passed": False, "headless": headless, "ranged": ranged, "combat_tick_budget": COMBAT_WINDOWS*TICKS_PER_WINDOW,
        "scope": "Actual attributed combat terminal outcome, player override and fresh claim, replay, completed-before-Manual retention; bounded shared clock waits. No damage or completion injection."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text(encoding="utf-8"))["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, method, arguments=None): return payload(await evidence.call(bridge, label, method, arguments))
            async def wire(label, method, request): return proto(await call(label, "rimgovernor/"+method, {"request": json.dumps(request)}))
            try:
                async with asyncio.timeout(900):
                    await bridge.core("games_start", gameId=bridge.game_id); await bridge.connect()
                    await call("new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    initial = outcome(await wire("identity", "lifecycle_read_identity", {}), "loaded")
                    assert initial["paused"] is True
                    identity = initial["context"]["identity"]
                    async def query(label, **filters):
                        return await wire(label, "observations_list_pawns", {"scope": {"expectedIdentity": identity}, "filter": filters})
                    async def read(label, pawn_id): return pawn_row(await query(label, ids=[pawn_id]), identity, pawn_id)
                    people = healthy_candidates(observed_rows(await query("healthy-colonists", colonist=True), identity), ranged=ranged)
                    assert people, "No healthy observed violence-capable colonist"
                    actor_id = people[0]["pawn"]["id"]
                    if ranged:
                        gear = await call("ranged-equipment", "test/b04f_setup", {"op": "ranged-equipment", "pawn": actor_id})
                        assert gear["success"] is True and gear["completedWorkInjected"] is False and len(gear["weapons"]) == 1
                        equipped = await read("equipped-attacker", actor_id)
                        assert equipped["equipment"]["primaryId"] == gear["weapons"][0]
                        assert any(item["thing"]["id"] == gear["weapons"][0] and item["thing"]["defName"] == "Gun_AssaultRifle"
                            for item in equipped["equipment"]["equipped"])
                    setup = await call("opponents", "test/b04f_setup", {"op": "ranged-opponents" if ranged else "opponents", "pawn": actor_id})
                    assert setup["success"] is True and setup["completedWorkInjected"] is False
                    targets = setup["opponents"]
                    assert len(targets) == len(set(targets)) == 2
                    targets_before = observed_rows(await query("observed-opponents", ids=targets), identity)
                    assert {r["pawn"]["id"] for r in targets_before} == set(targets)
                    assert all(r["pawn"]["defName"] == ("Tortoise" if ranged else "Hare") and r["animal"] is True and r["hostile"] is True
                        and r["dead"] is False and r["downed"] is False for r in targets_before)
                    actor = await read("attacker-before", actor_id)
                    victim = await read("target-before", targets[0])
                    status = outcome(await wire("authority-status", "authority_read_status", {"identity": identity}), "status")
                    grant = outcome(await wire("acquire", "authority_control", {"acquire": {"identity": identity,
                        "expectedGeneration": status["context"]["nativeGeneration"], "owner": OWNER, "leaseMs": 30000}}), "granted")
                    drafted = outcome(await wire("draft", "operations_execute", execute_request(identity, grant, actor, 1)), "receipt")
                    actor = await read("owned-attacker", actor_id); owned_effect(drafted, actor)
                    request = attack_request(identity, grant, actor, victim, 3, mode)
                    preview = outcome(await wire("attack-preview", "operations_preview", {"identity": identity, "operation": request["operation"]}), "evaluated")
                    assert preview["accepted"] is True
                    receipt = outcome(await wire("attack", "operations_execute", request), "receipt")
                    effect = receipt["applied"]["observed"]["job"]
                    assert effect["issued"] is True and effect["verified"] is True and effect["jobDef"] == job_def
                    assert effect["targetA"] == {"thingId": targets[0]} and effect["pawnId"] == actor_id
                    attacking = await read("attack-job", actor_id)
                    assert attacking["job"]["loadId"] == str(effect["jobId"]) and attacking["job"]["defName"] == job_def
                    attempt = {"identity": identity, "attempt": request["precondition"]["attempt"]}
                    assert "pending" in outcome(await wire("attack-pending", "receipts_observe_progress", attempt), "progress")
                    assert outcome(await wire("attack-replay", "operations_execute", request), "receipt") == receipt
                    replay = await read("replay-unchanged", actor_id); same_control(attacking, replay)
                    assert replay["job"] == attacking["job"]
                    external = await call("player-override", "test/b04f_setup", {"op": "external-order", "pawn": actor_id})
                    player = await read("player-job", actor_id)
                    interrupted = outcome(await wire("override-progress", "receipts_observe_progress", attempt), "progress")
                    overridden_attack(interrupted, attacking, player, external)
                    report["interrupted_attempt"] = deepcopy(attempt)
                    report["interrupted_progress"] = interrupted
                    status = outcome(await wire("override-authority", "authority_read_status", {"identity": identity}), "status")
                    grant = outcome(await wire("unowned-acquire", "authority_control", {"acquire": {"identity": identity,
                        "expectedGeneration": status["context"]["nativeGeneration"], "owner": OWNER, "leaseMs": 30000}}), "granted")
                    refusal = outcome(await wire("unowned-draft-refused", "operations_execute", execute_request(identity, grant, player, 4)), "failure")
                    assert refusal["code"] == "FAILURE_CODE_OWNER_CONFLICT"
                    preserved = await read("player-job-preserved", actor_id)
                    same_control(player, preserved); actual_order(external, preserved)
                    undraft = await call("player-undraft", "test/b04f_setup", {"op": "external-draft", "pawn": actor_id, "drafted": False})
                    assert undraft["success"] is True and undraft["after"] is False
                    actor = await read("fresh-undrafted", actor_id)
                    assert actor["draftClaim"] == {"unowned": {}} and actor["drafted"] is False
                    status = outcome(await wire("fresh-authority-status", "authority_read_status", {"identity": identity}), "status")
                    grant = outcome(await wire("fresh-acquire", "authority_control", {"acquire": {"identity": identity,
                        "expectedGeneration": status["context"]["nativeGeneration"], "owner": OWNER, "leaseMs": 30000}}), "granted")
                    fresh_draft = outcome(await wire("fresh-draft", "operations_execute", execute_request(identity, grant, actor, 5)), "receipt")
                    actor = await read("fresh-owned-attacker", actor_id); owned_effect(fresh_draft, actor)
                    assert actor["draftClaim"]["owned"]["claimId"] != attacking["draftClaim"]["owned"]["claimId"]
                    victim = await read("fresh-target-snapshot", targets[0])
                    request = attack_request(identity, grant, actor, victim, 6, mode)
                    receipt = outcome(await wire("fresh-attack", "operations_execute", request), "receipt")
                    effect = receipt["applied"]["observed"]["job"]
                    assert effect["issued"] is True and effect["verified"] is True and effect["jobDef"] == job_def
                    assert effect["targetA"] == {"thingId": targets[0]} and effect["pawnId"] == actor_id
                    attempt = {"identity": identity, "attempt": request["precondition"]["attempt"]}
                    assert "pending" in outcome(await wire("fresh-pending", "receipts_observe_progress", attempt), "progress")
                    supervisor = CombatScenarioClock(wire, identity, OWNER["controllerSessionId"], report, targets)
                    supervisor.grant = grant
                    runtime = ScenarioRuntime(bridge, supervisor, report)
                    runtime.current_plan = SimpleNamespace(control={"combat": {"targets": targets}})
                    completed = None
                    for window in range(COMBAT_WINDOWS):
                        await supervisor.renew_authority()
                        await advance_game(runtime, TICKS_PER_WINDOW, report, timeout=180, combat_targets=targets)
                        progress = outcome(await wire("progress-"+str(window), "receipts_observe_progress", attempt), "progress")
                        victims = observed_rows(await query("target-state-"+str(window), ids=[targets[0]], includeDead=True), identity)
                        assert len(victims) == 1, "Exact native target state unavailable"
                        report["last_attacker"] = await read("attacker-state-"+str(window), actor_id)
                        report["last_progress"] = progress
                        report["last_target"] = victims[0]
                        if "completed" in progress:
                            terminal(progress, receipt, victims[0], targets[0], ranged=ranged); completed = progress; break
                        assert "pending" in progress, progress
                    report["combat_windows_used"] = window+1
                    report["last_combat_progress"] = progress
                    report["last_target_state"] = victims[0]
                    assert completed is not None, f"Combat budget exhausted after {COMBAT_WINDOWS*TICKS_PER_WINDOW} ticks without causally verified terminal damage"
                    status = outcome(await wire("manual-status", "authority_read_status", {"identity": identity}), "status")
                    outcome(await wire("manual-after-completion", "authority_control", {"revoke": {"identity": identity,
                        "expectedGeneration": status["context"]["nativeGeneration"], "reason": "REVOCATION_REASON_MANUAL"}}), "revoked")
                    retained = outcome(await wire("completed-after-manual", "receipts_observe_progress", attempt), "progress")
                    assert retained["completeInspection"] is True and retained["completed"] == completed["completed"]
                    actor = await read("cleanup-attacker", actor_id)
                    cleanup = release_request(identity, actor)
                    released = outcome(await wire("cleanup-draft", "operations_release_owned_draft", cleanup), "released")
                    assert released["observed"]["drafted"] is False and released["observed"]["verified"] is True
                    assert outcome(await wire("immutable-attack-replay", "operations_execute", request), "receipt") == receipt
                    final = outcome(await wire("identity-after", "lifecycle_read_identity", {}), "loaded")
                    assert final["paused"] is True and final["context"]["identity"] == identity
                    ticks = int(final["context"]["tick"])-int(initial["context"]["tick"])
                    assert 0 < ticks <= COMBAT_WINDOWS*TICKS_PER_WINDOW
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(encoding="utf-8", errors="replace"), headless=headless)
                    report.update(passed=True, pawn_id=actor_id, target_ids=targets, completed=completed, ticks=ticks)
            finally:
                async with asyncio.timeout(60): report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
    except BaseException as error:
        report.update(passed=False, error=repr(error)); raise
    finally:
        report["artifacts"] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in output.rglob("*") if path.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument("--root",type=Path,required=True)
    parser.add_argument("--output",type=Path); parser.add_argument("--rendered",action="store_true"); parser.add_argument("--ranged",action="store_true"); args=parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root,args.output or args.root/"native-combat-acceptance",headless=not args.rendered,ranged=args.ranged)) else 1)
