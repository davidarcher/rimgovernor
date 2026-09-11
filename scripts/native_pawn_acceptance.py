"""Typed pawn read acceptance in a fresh colony through container_scenario.py."""
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
from native_compatibility_acceptance import discovery


DETAILS = ("needs", "health", "equipment", "biography", "settings", "social", "animals")


def outcome(reply, case):
    assert set(reply) == {case}, reply
    return reply[case]


def rows(snapshot, expected_context=None):
    if expected_context is not None:
        assert snapshot["context"] == expected_context
    result = snapshot.get("pawns", [])
    ids = [row["pawn"]["id"] for row in result]
    assert len(set(ids)) == len(ids)
    counts = snapshot["completeness"]
    assert counts["page"] == {"complete": True}, counts
    assert int(counts["matched"]) == int(counts["returned"]) == len(result), counts
    assert int(counts["unreadable"]) == 0 and int(counts["filtered"]) >= 0, counts
    for row in result:
        assert "snapshot" not in row["pawn"], row
        assert row["draftClaim"]["unavailable"]["reason"] == "UNAVAILABLE_REASON_UNSUPPORTED", row
    return result


def compare_core(typed, legacy):
    original = {row["thingId"]: row for row in legacy}
    assert {row["pawn"]["id"] for row in typed} == set(original)
    for row in typed:
        old = original[row["pawn"]["id"]]
        assert row["pawn"]["defName"] == old["defName"]
        assert row["kindDefName"] == old["kindDef"]
        assert row["pawn"]["position"] == {key: old["position"][key] for key in ("x", "z")}
        for new, prior in {"colonist": "isColonist", "freeColonist": "isFreeColonist", "prisoner": "isPrisoner",
            **{key: key for key in ("animal", "humanlike", "mechanoid", "tame", "wild", "hostile", "dead", "downed", "drafted")}}.items():
            assert type(row[new]) is bool and row[new] is old[prior], (new, row, old)


def compare_details(typed, legacy):
    originals = {row["thingId"]: row for row in legacy}
    for row in typed:
        old = originals[row["pawn"]["id"]]
        for key in ("food", "rest", "joy", "mood"):
            if old["needs"][key] is not None:
                assert abs(row["needs"][key] - old["needs"][key]) <= 0.000501, (key, row, old)
        assert abs(row["health"]["summaryFraction"] - old["health"]["summaryPct"]) <= 0.000501
        assert row["health"]["needsTend"] is old["health"]["needsTend"]
        assert row["health"]["bleeding"] is old["health"]["bleeding"]
        assert len(row["health"].get("hediffs", [])) == len(old["health"]["hediffs"])
        assert row["equipment"]["armed"] is old["equipment"]["armed"]
        for field, prior in (("equipped", "equipped"), ("apparel", "apparel"), ("inventoryWeapons", "inventoryWeapons")):
            assert {item["thing"]["id"] for item in row["equipment"].get(field, [])} == {item["thingId"] for item in old["equipment"][prior]}
        for field, prior in (("medicalCare", "medCare"), ("selfTend", "selfTend"), ("hostilityResponse", "hostilityResponse")):
            assert row["settings"][field] == old["settings"][prior]
        assert len(row["settings"]["schedule"]) == 24
        assert row["settings"]["work"] and row["biography"]["skills"]
        assert {item["definition"]["defName"]: item["level"] for item in row["biography"]["skills"]} == {
            item["name"]: item["level"] for item in old["bio"]["skills"] if item["present"]}
        assert "snapshot" not in row["settings"] and "snapshot" not in row["health"]
        assert any(issue["field"] == "social" and issue["unavailable"]["reason"] == "UNAVAILABLE_REASON_UNSUPPORTED" for issue in row["issues"])


async def run(root: Path, output: Path, *, headless=True):
    output.mkdir(parents=True, exist_ok=False)
    evidence, report = Evidence(output), {"passed": False, "headless": headless,
        "scope": "Fresh native pawn read facts, exact filters, explicit detail presence, bounded refusals and paused identity/tick invariance. No pawn operations or CAS capability claim."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, tool, arguments=None):
                return payload(await evidence.call(bridge, label, tool, arguments))

            async def wire(label, method, request):
                return proto(await call(label, "rimgovernor/" + method, {"request": json.dumps(request)}))

            try:
                async with asyncio.timeout(900):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    report["discovery"] = await discovery(bridge, evidence)
                    assert "rimgovernor/observations_list_pawns" in report["discovery"]
                    await call("new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    loaded = outcome(await wire("identity-before", "lifecycle_read_identity", {}), "loaded")
                    assert loaded["paused"] is True
                    context, identity = loaded["context"], loaded["context"]["identity"]
                    scope = {"scope": {"expectedIdentity": identity}}

                    async def read(label, **fields):
                        return rows(outcome(await wire(label, "observations_list_pawns", dict(scope, **fields)), "observed"), context)

                    baseline = await read("default-pawns")
                    assert baseline and len(baseline) > 3
                    legacy = await call("legacy-pawns", "home/list_pawns", {})
                    assert legacy["success"] is True
                    compare_core(baseline, legacy["pawns"])
                    colonists = await read("colonists", filter={"colonist": True, "humanlike": True, "animal": False})
                    assert len(colonists) == 3
                    old_colonists = await call("legacy-colonist-details", "home/list_pawns", {
                        "colonistsOnly": True, "health": True, "needs": True, "equipment": True,
                        "bio": True, "work": True, "schedule": True, "settings": True, "visibleHediffsOnly": False})
                    compare_details(colonists, old_colonists["pawns"])
                    target = colonists[0]["pawn"]["id"]
                    exact = await read("exact-id", filter={"ids": [target], "colonist": True, "animal": False})
                    assert len(exact) == 1 and exact[0] == colonists[0]
                    assert await read("contradictory-filter", filter={"ids": [target], "colonist": False}) == []
                    assert await read("unknown-id", filter={"ids": ["missing-pawn-id"]}) == []
                    animals = await read("animals", filter={"colonist": False, "animal": True})
                    assert animals and all(row["animalState"]["gender"] and row["animal"] is True for row in animals)
                    assert {row["pawn"]["id"] for row in animals} == {row["pawn"]["id"] for row in baseline if row["animal"] and not row["colonist"]}
                    disabled = await read("details-disabled", filter={"ids": [target]}, details={key: False for key in DETAILS})
                    assert len(disabled) == 1
                    for field in ("needs", "health", "equipment", "biography", "settings", "social", "animalState"):
                        assert field not in disabled[0], (field, disabled)
                    assert {i["field"] for i in disabled[0]["issues"] if i["unavailable"]["reason"] == "UNAVAILABLE_REASON_NOT_REQUESTED"} == set(DETAILS) - {"animals"} | {"animal_state"}
                    known_false = await read("known-false", filter={"drafted": False, "downed": False})
                    assert all(row["drafted"] is False and row["downed"] is False for row in known_false)
                    assert {row["pawn"]["id"] for row in known_false} == {row["pawn"]["id"] for row in baseline if not row["drafted"] and not row["downed"]}
                    overflow = outcome(await wire("whole-query-limit", "observations_list_pawns", dict(scope, page={"limit": 1})), "unavailable")
                    assert overflow["reason"] == "UNAVAILABLE_REASON_LIMIT_EXCEEDED"
                    for label, fields in (("zero-limit", {"page": {"limit": 0}}), ("cursor", {"page": {"cursor": "old"}}), ("duplicate-id", {"filter": {"ids": [target, target]}}), ("negative-distance", {"filter": {"withinColonistDistance": -1}})):
                        assert outcome(await wire(label, "observations_list_pawns", dict(scope, **fields)), "failure")["code"] == "FAILURE_CODE_INVALID_REQUEST"
                    stale = deepcopy(scope); stale["scope"]["expectedIdentity"]["loadToken"] = "stale-load"
                    assert outcome(await wire("stale-identity", "observations_list_pawns", stale), "failure")["code"] == "FAILURE_CODE_STALE_IDENTITY"
                    final = outcome(await wire("identity-after", "lifecycle_read_identity", {}), "loaded")
                    assert final["context"] == context and final["paused"] is True
                    assert await read("repeat-default") == baseline
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report.update(passed=True, context=context, pawns=len(baseline), colonists=len(colonists), animals=len(animals))
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
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-pawn-acceptance", headless=not args.rendered)) else 1)
