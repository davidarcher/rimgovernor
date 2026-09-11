"""Read-only research acceptance, using a private fingerprint fixture in Docker."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path

from native_package_acceptance import Evidence, bridge_session, check_startup_log, gabs_executable, package_files, payload, prepare, prepare_rendered
from native_compatibility_acceptance import discovery
from native_protobuf_acceptance import proto
from native_supplies_acceptance import complete


def fingerprint_state(value):
    assert value["success"] is True
    return {field: value[field] for field in ("current", "progress", "knowledge", "slots", "techprints", "tick", "paused")}


def compare_projects(observed, legacy):
    rows = observed.get("projects", [])
    complete(observed["completeness"], len(rows))
    assert [row["project"]["defName"] for row in rows] == sorted({row["project"]["defName"] for row in rows})
    native = {row["defName"]: row for row in legacy["available"] + legacy["locked"]}
    finished = set(legacy["finished"])
    assert {row["project"]["defName"] for row in rows} == set(native) | finished
    for row in rows:
        name = row["project"]["defName"]
        assert row["finished"] is (name in finished)
        if name in finished:
            assert row["canStart"] is False and row["available"] is False
            continue
        source = native[name]
        for key, old in (("progress", "progress"), ("baseCost", "baseCost"), ("apparentCost", "costApparent"),
                         ("costFactor", "costFactor"), ("progressFraction", "progressPercent")):
            assert source[old] is not None and abs(row[key] - source[old]) <= .0011, (name, key, row.get(key), source[old])
        for key, old in (("canStart", "canStartNow"), ("current", "isCurrent"), ("techprintsApplied", "techprintsApplied"), ("techprintsNeeded", "techprintsNeeded")):
            assert row[key] == source[old], (name, key)
        assert set(row.get("prerequisites", [])) == set(source["prerequisites"])
        assert set(row.get("hiddenPrerequisites", [])) == set(source["hiddenPrerequisites"])
        assert row.get("requiredBuilding") == source["requiredResearchBuilding"]
        assert set(row.get("requiredFacilities", [])) == set(source["requiredResearchFacilities"])
        if "unlocksCompleteness" in row:
            complete(row["unlocksCompleteness"], len(row.get("unlocks", [])))
            assert source["unlocksReadable"] is True and len(row.get("unlocks", [])) == source["unlockCount"]
            actual_unlocks = {(item["defName"], item["nativeType"]) for item in row.get("unlocks", [])}
            assert {(item["defName"], item["type"]) for item in source["unlocks"]} <= actual_unlocks
    slots = {slot.get("category"): slot.get("currentProject") for slot in observed["slots"]}
    assert slots.pop(None) == (legacy["current"]["defName"] if legacy["current"] else None)
    assert slots == {name: row["defName"] if row else None for name, row in legacy["currentByCategory"].items()}
    assert observed["anomalyActive"] is legacy["anomalyActive"]


def compare_capability(observed, legacy):
    capability = legacy["researchBenches"]
    assert capability["readable"] is True
    benches = observed.get("benches", [])
    assert len(benches) == capability["count"] == len(capability["benches"])
    assert sorted(row["building"]["building"]["defName"] for row in benches) == sorted(row["defName"] for row in capability["benches"])
    researchers = {row["pawn"]["id"]: row for row in observed.get("researchers", [])}
    assert len(researchers) == len(capability["researchers"]) > 0
    for native in capability["researchers"]:
        # Native GetUniqueLoadID() is Thing_ + the ThingID exposed by home/research.
        row = researchers["Thing_" + native["thingId"]]
        for field in ("intellectual", "priority", "disabled", "everWork", "active"):
            assert row.get(field) == native[field], (native["thingId"], field)


async def run(root, output, *, headless=True):
    assert Path("/.dockerenv").is_file(), "Native/network acceptance runs only inside Docker"
    output.mkdir(parents=True, exist_ok=False)
    evidence = Evidence(output)
    report = {"passed": False, "scope": "Private read-only research fingerprint fixture; typed read invariance before separate native getter audit. No selection, save or pawn work."}
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        async with bridge_session(gabs_executable(root, configuration), configuration) as bridge:
            async def call(label, name, arguments=None):
                async with asyncio.timeout(180):
                    return payload(await evidence.call(bridge, label, name, arguments))

            async def wire(label, name, request):
                return proto(await call(label, "rimgovernor/" + name, {"request": json.dumps(request)}))

            try:
                async with asyncio.timeout(600):
                    await bridge.core("games_start", gameId=bridge.game_id)
                    await bridge.connect()
                    names = await discovery(bridge, evidence)
                    assert names.count("rimgovernor/observations_read_research") == 1
                    assert {name for name in names if name.startswith("test/")} == {"test/research_observation_fingerprint"}
                    report["discovery"] = names
                    await call("new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
                    await call("pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
                    before = (await wire("identity-before", "lifecycle_read_identity", {}))["loaded"]
                    assert before["paused"] is True
                    advertised = [row for row in before["capabilities"] if row.get("fullMethodName") == "rimgovernor.observations.v1.Observations/ReadResearch"]
                    assert len(advertised) == 1 and advertised[0]["support"] == "CAPABILITY_SUPPORT_SUPPORTED"
                    identity = before["context"]["identity"]
                    fingerprint = await call("fingerprint-before", "test/research_observation_fingerprint")
                    assert fingerprint["success"] is True and fingerprint["paused"] is True
                    assert isinstance(fingerprint["progress"], list) and isinstance(fingerprint["knowledge"], list)
                    scope = {"scope": {"expectedIdentity": identity}}
                    full = scope | {"includeLocked": True, "includeFinished": True, "includeCapability": True}
                    observed = (await wire("full", "observations_read_research", full))["observed"]
                    assert observed["context"] == before["context"] and "snapshot" not in observed
                    assert len(observed["projects"]) > 1
                    assert (await wire("repeat", "observations_read_research", full))["observed"] == observed
                    defaults = (await wire("defaults", "observations_read_research", scope))["observed"]
                    assert {row["project"]["defName"] for row in defaults.get("projects", [])} == {row["project"]["defName"] for row in observed["projects"] if row["canStart"] and not row["finished"]}
                    assert not defaults.get("benches") and not defaults.get("researchers")
                    assert all(not row.get("unlocks") for row in defaults.get("projects", []))
                    needle = observed["projects"][0]["project"]["defName"]
                    filtered = (await wire("filtered", "observations_read_research", full | {"nameContains": needle}))["observed"]
                    expected = [row for row in observed["projects"] if needle.lower() in row["project"]["defName"].lower() or needle.lower() in row["project"].get("label", "").lower()]
                    assert filtered.get("projects", []) == expected
                    complete(filtered["completeness"], len(expected))
                    unlock_rows = []
                    for index, candidate in enumerate([row for row in observed["projects"] if not row["finished"]][:16]):
                        reply = await wire(f"unlocks-{index}", "observations_read_research", full | {"nameContains": candidate["project"]["defName"], "includeUnlocks": True})
                        if "unavailable" in reply:
                            assert reply["unavailable"]["reason"] == "UNAVAILABLE_REASON_LIMIT_EXCEEDED"
                            continue
                        unlock_rows = reply["observed"].get("projects", [])
                        if any(row.get("unlocks") for row in unlock_rows):
                            break
                    assert any(row.get("unlocks") for row in unlock_rows), "No populated bounded unlock collection was verified"
                    for label, change in (("limit", {"page": {"limit": 257}}), ("cursor", {"page": {"cursor": "stale"}}), ("unknown-write", {"set": "Electricity"})):
                        refusal = await wire(label, "observations_read_research", scope | change)
                        assert set(refusal) == {"failure"} and refusal["failure"]["code"] == "FAILURE_CODE_INVALID_REQUEST"
                    limited = await wire("overflow", "observations_read_research", full | {"page": {"limit": 1}})
                    assert limited["unavailable"]["reason"] == "UNAVAILABLE_REASON_LIMIT_EXCEEDED"
                    assert fingerprint_state(await call("fingerprint-after", "test/research_observation_fingerprint")) == fingerprint_state(fingerprint)
                    report["saved_state_unchanged"] = True
                    # Native CanStartNow populates saved zero rows. Only call after the
                    # independent invariant proof; never use this as the before snapshot.
                    legacy = await call("separate-native-getter-audit", "home/research", {"locked": True, "finished": True, "unlocks": True})
                    assert legacy["success"] is True and legacy["applied"] is False
                    compare_projects(observed, legacy)
                    native_rows = {row["defName"]: row for row in legacy["available"] + legacy["locked"]}
                    for row in unlock_rows:
                        complete(row["unlocksCompleteness"], len(row.get("unlocks", [])))
                        source = native_rows[row["project"]["defName"]]
                        assert source["unlocksReadable"] is True and len(row.get("unlocks", [])) == source["unlockCount"]
                        assert {(item["defName"], item["type"]) for item in source["unlocks"]} <= {(item["defName"], item["nativeType"]) for item in row.get("unlocks", [])}
                    report["unlock_projects"] = [row["project"]["defName"] for row in unlock_rows]
                    compare_capability(observed, legacy)
                    after = (await wire("identity-after", "lifecycle_read_identity", {}))["loaded"]
                    assert after["context"] == before["context"] and after["paused"] is True
                    report.update(projects=len(observed["projects"]), researchers=len(observed.get("researchers", [])), benches=len(observed.get("benches", [])))
                    check_startup_log((root / ("HeadlessPlayer.log" if headless else "Player.log")).read_text(errors="replace"), headless=headless)
                    report["passed"] = True
            finally:
                async with asyncio.timeout(60):
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
    except BaseException as error:
        report["error"] = repr(error)
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
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-research-acceptance", headless=not args.rendered)) else 1)
