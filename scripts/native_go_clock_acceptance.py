"""Go-owned supervised clock and pawn work; launch through container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import traceback

from native_building_service_acceptance import (
    Evidence, bridge_session, gabs_executable, payload, prepare, package_files,
    proto, outcome, service, poll, http_building, action, verify_progress,
    READS, DIAGNOSTICS, CONTROL, EXECUTE, add_handoff_arguments, verify_go_binary,
)


def audit(events, baseline, capabilities, *, restart, routine_reviews=False):
    ordered = sorted(events, key=lambda row: row["Sequence"])
    sequences = [row["Sequence"] for row in ordered]
    assert sequences and sequences == list(range(baseline + 1, sequences[-1] + 1)), "Incomplete SDK operation history"
    clocks = {"rimgovernor/clock_" + name for name in ("read_status", "read_events", "read_attempt", "pause")}
    allowed = READS | DIAGNOSTICS | clocks | {"rimgovernor/observations_list_pawns"}
    if routine_reviews and not restart:
        allowed |= {"rimgovernor/observations_read_colony_facts"}
    if not restart:
        allowed |= {CONTROL, EXECUTE, "rimgovernor/operations_preview", "rimgovernor/placement_preview",
                    "rimgovernor/clock_start", "rimgovernor/clock_renew"}
    operations = {}
    for row in ordered:
        capability = row.get("CapabilityId")
        if not capability:
            assert not row.get("OperationId")
            continue
        selected = capabilities.get(capability, set()) & allowed
        assert len(selected) == 1, f"Unexpected operation: {capabilities.get(capability)}"
        name = next(iter(selected))
        assert operations.setdefault(row["OperationId"], name) == name
    names = list(operations.values())
    assert "rimgovernor/clock_read_events" in names
    if not restart:
        assert "rimgovernor/clock_start" in names and "rimgovernor/clock_pause" in names
        assert names.count(EXECUTE) >= 1
        if routine_reviews:
            assert "rimgovernor/observations_read_colony_facts" in names
    return names


def interrupted_by_letter(events, letter_id):
    matching = []
    for event in events:
        stop = event.get("stopped", {})
        letter = stop.get("pause", {}).get("letter", {})
        if stop.get("reason") == "STOP_REASON_LETTER_PAUSE" and letter.get("id") == letter_id:
            matching.append(event)
    assert len(matching) == 1, "Missing real LetterStack interruption for the scheduled letter"
    return matching[0]


def require_healthy_colonists(reply):
    colony = outcome(reply, "observed")["colonists"]
    complete = colony["completeness"]
    assert complete["page"]["complete"] and int(complete.get("filtered", -1)) == 0 and int(complete.get("unreadable", -1)) == 0
    pawns = colony.get("pawns", [])
    assert pawns and len(pawns) == int(complete["returned"]) == int(complete["matched"])
    for pawn in pawns:
        health = pawn.get("health", {})
        blocked = [name for name, value in (("dead", pawn.get("dead")), ("downed", pawn.get("downed")),
                   ("bleeding", health.get("bleeding")), ("needsTend", health.get("needsTend"))) if value is not False]
        assert not blocked, f"Healthy-clock fixture prerequisite failed for {pawn['pawn']['id']}: {blocked}"


def routine_evidence(database, identity, *, enabled, expected_food_need=None, allow_methods=False):
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as db:
        db.execute("BEGIN")
        row = db.execute("SELECT payload FROM routine_review WHERE singleton=1").fetchone()
        assert row is not None, "No durable routine review"
        review = json.loads(row[0])
        assert review["Revision"] > 0 and review["Enabled"] is enabled
        scope = review["Snapshot"]
        assert scope["Colony"] == identity["colonyId"] and scope["Load"] == identity["loadToken"] and scope["Map"] == identity["mapId"]
        bindings = review["Goals"]
        expected = {"ConfirmColonyNames", "ActiveCombat", "CriticalMedical", "RestoreWorkers", "AllowStartingSupplies",
                    "EnsureWorkAssignments", "EnsureFoodSupply", "EnsureInitialShelter", "EnsureTemperatureSafety",
                    "EnsureCooking", "EnsureBasicPower", "EnsureFoodStorage", "EnsureBasicDefense", "MaintainWood", "MaintainMedicalCare", "EnsureComfort", "EnsureExpansion", "MaintainEquipment",
                    "MaintainFireSafety", "SecureSupplies", "MaintainEssentialRepairs", "MaintainCleanFacilities", "MaintainMedicalReserves", "MaintainAnimalContainment", "MaintainAnimalFeed", "MaintainSleeping", "MaintainHomeCoverage", "MaintainStoneShell"}
        mood = (review.get('Mood') or {}).get('States') or []
        assert len(mood) <= 256 and len({s['Pawn']['ID'] for s in mood}) == len(mood)
        expected |= {('EnsureMood-' + s['Pawn']['ID']) if len(s['Pawn']['ID'].encode()) <= 210 else
                     ('EnsureMoodHash-' + hashlib.sha256(s['Pawn']['ID'].encode()).hexdigest()[:32]) for s in mood}
        assert len(bindings) == len(expected) and {v["Need"] for v in bindings} == expected
        goals = {}
        for binding in bindings:
            row = db.execute("SELECT payload FROM goals WHERE id=?", (binding["Goal"],)).fetchone()
            assert row is not None, "Missing bound goal"
            goal = json.loads(row[0])
            assert goal["Source"] == "autopilot" and goal["Snapshot"] == scope
            assert goal["Tick"] <= review["Tick"]
            if not enabled:
                assert goal["Status"] in {"invalidated", "cancelled"}
            goals[binding["Need"]] = goal
        if expected_food_need is not None:
            assert goals["EnsureFoodSupply"]["Need"] == expected_food_need, "Food need differs from reference forecast"
        if not allow_methods:
            assert db.execute("SELECT count(*) FROM goal_methods").fetchone()[0] == 0, "Review unexpectedly created methods"
        return {"review": review, "goals": goals}


async def run(root, output, binary, *, go_source, go_sha256, routine_reviews=False):
    assert Path("/.dockerenv").is_file(), "Use the isolated scenario launcher"
    output.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "source": go_source,
              "scope": "Go player service owns ordinary construction, finite clock windows, letter interruption, explicit acknowledgement, Manual, joined shutdown and disabled restart."}
    evidence = Evidence(output)
    launched = False
    try:
        configuration = prepare(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        fixture = Path(game["workingDir"]) / "Mods/RimGovernor/BridgeTools/InterruptionFixtures/RimGovernor.InterruptionFixtures.BridgeTools.dll"
        report["interruption_fixture_sha256"] = hashlib.sha256(fixture.read_bytes()).hexdigest()
        gabs = Path(gabs_executable(root, configuration))
        profile = root / "headless-profile"
        private = output / "rimgovernor-go"
        shutil.copyfile(binary, private)
        private.chmod(0o700)
        report["binary_sha256"] = verify_go_binary(private, go_source=go_source, go_sha256=go_sha256)
        database = output / "service.sqlite"

        async def wire(bridge, label, name, request):
            return proto(payload(await evidence.call(bridge, label, "rimgovernor/" + name, {"request": json.dumps(request)})))

        async def capture(bridge, label, baseline=None, restart=False):
            catalog = payload(await evidence.call(bridge, label + "-capabilities", "rimbridge/list_capabilities", {"limit": 10000, "includeParameters": False}))
            assert catalog["success"] and not catalog["truncated"]
            caps = {row["id"]: set(row["aliases"]) for row in catalog["capabilities"]}
            request = {"limit": 5000, "includeDiagnostics": True}
            if baseline is not None:
                request["afterSequence"] = baseline
            rows = payload(await evidence.call(bridge, label + "-events", "rimbridge/list_operation_events", request))["events"]
            assert rows and len(rows) < 5000
            if baseline is not None:
                report.setdefault("traces", {})[label] = audit(rows, baseline, caps, restart=restart, routine_reviews=routine_reviews)
            return max(row["Sequence"] for row in rows)

        async with bridge_session(gabs, configuration) as bridge:
            await bridge.core("games_start", gameId=bridge.game_id)
            launched = True
            await bridge.connect()
            await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
            await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
            loaded = outcome(await wire(bridge, "before", "lifecycle_read_identity", {}), "loaded")
            identity, tick = loaded["context"]["identity"], int(loaded["context"]["tick"])
            assert loaded["paused"]
            prepared = payload(await evidence.call(bridge, "prepare", "test/guarded_construction_prepare", {"siteCount": 2}))
            assert prepared["success"] and len(prepared["sites"]) == 2
            assert all(prepared[key] == identity[key] for key in identity)
            report["prepared"] = prepared
            report["prepared_colony"] = await wire(bridge, "prepared-colony", "observations_read_status", {
                "scope": {"expectedIdentity": identity}, "colonists": True, "threats": True,
                "colonistDetail": False, "page": {"limit": 256}})
            require_healthy_colonists(report["prepared_colony"])
            letter = payload(await evidence.call(bridge, "schedule-letter", "test/interruption_letter", {
                "definition": "ThreatBig", "after": "none", "label": "Go clock acceptance", "delayTicks": 60}))
            assert letter["success"] and letter["scheduledTick"] == tick + 60
            report["scheduled_letter"] = letter
            baseline = await capture(bridge, "setup")

        async def state(http, *, paused=None):
            value = await poll(http, "/api/state", lambda v: v.get("connected") and not v.get("game", {}).get("stale", True)
                               and (paused is None or v["game"]["paused"] is paused), timeout=90)
            assert value["identity"] == identity and value["game"]["tick"] <= tick + 7200
            return value

        async def acquire(http, submission, request):
            current = await http("GET", "/api/player/control")
            direction = current["record"]["direction"] if current["record"] else "0"
            result = await http("POST", "/api/player/control/acquire", body={"requestId": request, "expected": identity,
                "planId": submission["planId"], "revision": submission["revision"], "expectedDirection": direction})
            assert result["record"]["phase"] == "granted" and result["state"]["enabled"]
            return result

        async with service(private, gabs, configuration, profile, database, output / "operate", report, clock_control=True, routine_reviews=routine_reviews) as http:
            initial = await state(http, paused=True)
            assert initial["game"]["tick"] == tick
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            submission = await http("POST", "/api/buildings/plans", body={"requestId": "clock-wall-1", "expected": identity,
                "building": http_building(prepared["sites"][0])}, expected=201)
            report["submission"] = submission
            await acquire(http, submission, "clock-acquire-1")
            review = await poll(http, "/api/player/clock", lambda v: bool(v["holds"]), timeout=90)
            await poll(http, "/api/player/control", lambda v: not v["state"]["enabled"])
            stopped = await state(http, paused=True)
            assert stopped["game"]["tick"] == letter["scheduledTick"]
            report["interrupted_state"] = stopped
            if routine_reviews:
                report["routine_after_interruption"] = routine_evidence(database, identity, enabled=True)
            # Let the poller catch the stopped event as well as its notification.
            await asyncio.sleep(2)
            review = await http("GET", "/api/player/clock")
            assert review["holds"] and review["reviewedCursor"] == review["inboxCursor"]
            report["interruption_review"] = review
            ack = {"requestId": "inspect-letter", "expectedRevision": review["revision"], "throughCursor": review["reviewedCursor"]}
            acknowledged = await http("POST", "/api/player/clock/acknowledge", body=ack)
            assert not acknowledged["holds"]
            assert await http("POST", "/api/player/clock/acknowledge", body=ack) == acknowledged
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            assert (await state(http, paused=True))["game"]["tick"] == stopped["game"]["tick"]
            report["acknowledgement"] = acknowledged

            await acquire(http, submission, "clock-acquire-2")
            await state(http, paused=False)
            manual = await http("POST", "/api/player/control/manual", body={"requestId": "clock-manual", "expected": identity})
            assert not manual["state"]["enabled"] and manual["record"]["phase"] == "disabled"
            paused = await state(http, paused=True)
            await asyncio.sleep(2)
            assert (await state(http, paused=True))["game"]["tick"] == paused["game"]["tick"]
            report["manual_state"] = paused
            if routine_reviews:
                report["routine_after_manual"] = routine_evidence(database, identity, enabled=False)
            manual_review = await poll(http, "/api/player/clock", lambda v: bool(v["holds"]) and v["reviewedCursor"] == v["inboxCursor"])
            report["manual_review"] = manual_review
            manual_ack = await http("POST", "/api/player/clock/acknowledge", body={"requestId": "inspect-manual",
                "expectedRevision": manual_review["revision"], "throughCursor": manual_review["reviewedCursor"]})
            assert not manual_ack["holds"]
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            await acquire(http, submission, "clock-acquire-3")
            plan = await poll(http, "/api/plan?id=" + submission["planId"], lambda v: action(v, submission)["stage"] == "completed", timeout=180)
            verify_progress(plan, submission, True)
            report["completed_plan"] = plan
            assert (await state(http))["game"]["tick"] > tick

            second = await http("POST", "/api/buildings/plans", body={"requestId": "clock-wall-2", "expected": identity,
                "building": http_building(prepared["sites"][1])}, expected=201)
            report["second_submission"] = second
            running = await state(http, paused=False)
            report["shutdown_running_state"] = await poll(http, "/api/state", lambda v: v.get("connected")
                and not v.get("game", {}).get("stale", True) and not v["game"]["paused"]
                and v["game"]["tick"] > running["game"]["tick"])
        # The Go process must join and pause before the orchestrator reattaches.
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            await capture(bridge, "operate", baseline)
            loaded = outcome(await wire(bridge, "after-shutdown", "lifecycle_read_identity", {}), "loaded")
            assert loaded["paused"] and loaded["context"]["identity"] == identity
            final_tick = int(loaded["context"]["tick"])
            report["shutdown_paused"] = loaded
            page = outcome(await wire(bridge, "clock-events", "clock_read_events", {"identity": identity, "afterCursor": "0", "limit": 128}), "page")
            assert not page["gap"] and int(page["nextCursor"]) == int(page["newestCursor"])
            interruption = interrupted_by_letter(page.get("events", []), letter["letterId"])
            assert int(interruption["context"]["tick"]) == letter["scheduledTick"]
            report["native_interruption"] = interruption
            site = prepared["sites"][0]
            anchor = {"x": site["x"], "z": site["z"]}
            built = outcome(await wire(bridge, "completed-building", "observations_list_buildings", {"scope": {"expectedIdentity": identity},
                "defNames": [site["defName"]], "category": "all", "region": {"minimum": anchor, "maximum": anchor}, "page": {"limit": 16}}), "observed")
            assert built["completeness"]["page"]["complete"] and int(built["completeness"]["unreadable"]) == 0
            rows = built.get("buildings", [])
            assert len(rows) == 1 and rows[0]["status"] == "built" and rows[0]["stuff"] == site["stuff"]
            report["completed_building"] = rows[0]
            baseline = await capture(bridge, "restart-baseline")

        async with service(private, gabs, configuration, profile, database, output / "restart", report, clock_control=True, routine_reviews=routine_reviews) as http:
            assert (await state(http, paused=True))["game"]["tick"] == final_tick
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            assert await http("POST", "/api/player/clock/acknowledge", body=ack) == acknowledged
            verify_progress(await http("GET", "/api/plan?id=" + submission["planId"]), submission, True)
            await asyncio.sleep(3)
            assert (await state(http, paused=True))["game"]["tick"] == final_tick
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            await capture(bridge, "restart", baseline, restart=True)
            final = outcome(await wire(bridge, "final", "lifecycle_read_identity", {}), "loaded")
            assert final["paused"] and int(final["context"]["tick"]) == final_tick
        report["passed"] = True
    except BaseException as error:
        report["error"] = repr(error)
        report["traceback"] = traceback.format_exc()
    finally:
        incomplete = any(not phase.get("joined") for phase in report.get("service_phases", []))
        if launched and not incomplete:
            try:
                async with bridge_session(gabs, configuration) as bridge:
                    if not report["passed"]:
                        await bridge.connect()
                        await evidence.call(bridge, "failure-operations", "rimbridge/list_operation_events", {"limit": 5000, "includeDiagnostics": True})
                        await wire(bridge, "failure-clock", "clock_read_status", {"identity": identity})
                        await wire(bridge, "failure-colony", "observations_read_status", {"scope": {"expectedIdentity": identity},
                            "colonists": True, "threats": True, "colonistDetail": False, "page": {"limit": 256}})
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
            except BaseException as error:
                report.update(passed=False, cleanup_error=repr(error))
        elif incomplete:
            report.update(passed=False, resources_retained=True)
        report["artifacts"] = {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest() for p in output.rglob("*") if p.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--go-binary", type=Path, required=True)
    parser.add_argument("--routine-reviews", action="store_true")
    add_handoff_arguments(parser)
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-go-clock-acceptance", args.go_binary,
        go_source=args.go_source, go_sha256=args.go_sha256, routine_reviews=args.routine_reviews)) else 1)
