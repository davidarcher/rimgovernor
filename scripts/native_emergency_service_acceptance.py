"""Paused native emergency admission gate; launch only through container_scenario.py."""
from __future__ import annotations
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import shutil
from native_building_service_acceptance import (Evidence, bridge_session, gabs_executable, payload, prepare, prepare_rendered,
    package_files, proto, outcome, discovery, validate_discovery, service, poll, http_building, action, verify_progress,
    READS, DIAGNOSTICS, CONTROL, EXECUTE, add_handoff_arguments, validate_go_handoff, verify_go_binary)

STATUS = "rimgovernor/observations_read_status"
PREVIEWS = {"rimgovernor/placement_preview", "rimgovernor/operations_preview"}


def pending(plan: dict, submission: dict) -> None:
    progress = action(plan, submission)
    assert progress["stage"] == "pending" and progress["attempt"] == "0"
    assert progress["unresolved"] is False and progress["receipt"] is None


def complete(section: dict, count: int) -> None:
    value = section["completeness"]
    assert value["page"]["complete"] is True and not value["page"].get("nextCursor")
    assert int(value["matched"]) == int(value["returned"]) == count and int(value["unreadable"]) == 0


def status_facts(value: dict, identity: dict, tick: int, fixture_ids: set[str], unsafe: bool) -> None:
    assert value["context"]["identity"] == identity and int(value["context"]["tick"]) == tick
    colonists = value["colonists"]
    assert colonists["context"] == value["context"]
    people = colonists.get("pawns", [])
    complete(colonists, len(people))
    assert people and len({row["pawn"]["id"] for row in people}) == len(people)
    for row in people:
        assert row["dead"] is False and row["downed"] is False
        assert row["health"]["bleeding"] is False and row["health"]["needsTend"] is False
    threats = value["threats"]
    keys = ("hostiles", "huntingPredators", "ignoredHunters", "wildPredatorsNear", "downedNear")
    complete(threats, sum(len(threats.get(key, [])) for key in keys))
    dangerous = []
    for key in ("hostiles", "huntingPredators"):
        for threat in threats.get(key, []):
            row = threat["pawn"]
            assert type(row["dead"]) is bool and type(row["downed"]) is bool
            if not row["dead"] and not row["downed"]: dangerous.append(row)
    if unsafe:
        actual = {row["pawn"]["id"] for row in dangerous}
        assert fixture_ids <= actual and len(fixture_ids) == 2
        for row in dangerous:
            if row["pawn"]["id"] in fixture_ids:
                assert row["hostile"] is True and row["mentalState"] == "ManhunterPermanent"
    else:
        assert not dangerous, "Safe admission needs positively complete clear threats"
        assert not fixture_ids.intersection(row["pawn"]["pawn"]["id"] for key in keys for row in threats.get(key, []))


def audit(events: list[dict], baseline: int, capabilities: dict[str, set[str]], phase: str) -> list[str]:
    ordered = sorted(events, key=lambda row: row["Sequence"])
    sequences = [row["Sequence"] for row in ordered]
    assert sequences and sequences == list(range(baseline + 1, sequences[-1] + 1)), "SDK event sequence incomplete or duplicated"
    assert phase in {"unsafe", "safe", "orchestrator"}
    allowed = READS | DIAGNOSTICS | PREVIEWS
    if phase == "orchestrator": allowed |= {"test/b04f_setup"}
    else: allowed |= {CONTROL}
    if phase == "safe": allowed |= {EXECUTE}
    operations, completed = {}, {}
    for row in ordered:
        capability = row.get("CapabilityId")
        if not capability:
            assert not row.get("OperationId"), "Unattributed SDK operation"
            continue
        selected = capabilities.get(capability, set()) & allowed
        assert len(selected) == 1, f"Unapproved/ambiguous capability in {phase}: {capability}"
        name = next(iter(selected)); identifier = row["OperationId"]
        assert operations.setdefault(identifier, name) == name, "Operation capability changed"
        if name == "test/b04f_setup":
            assert phase == "orchestrator" and row["Metadata"]["arguments"]["op"] == "clear-opponents"
        if row.get("EventType") == "operation.completed": completed[identifier] = row
    names = list(operations.values())
    assert "rimgovernor/lifecycle_read_identity" in names
    if phase == "orchestrator": assert names.count("test/b04f_setup") == 1
    if phase != "orchestrator":
        controls = [completed[key] for key, name in operations.items() if name == CONTROL]
        verbs = []
        for row in controls:
            assert row["Success"] is True and row["HasResult"] is True
            request = json.loads(row["Metadata"]["arguments"]["request"])
            assert len(request) == 1 and next(iter(request)) in {"acquire", "renew", "revoke"}
            verbs.append(next(iter(request)))
        assert verbs.count("acquire") == verbs.count("revoke") == 1 and verbs[0] == "acquire" and verbs[-1] == "revoke"
        statuses = [completed[key] for key, name in operations.items() if name == STATUS and key in completed]
        statuses = [row for row in statuses if controls[0]["Sequence"] < row["Sequence"] < controls[-1]["Sequence"]]
        assert all(row["Success"] is True and row["HasResult"] is True for row in statuses)
        emergency_reads = []
        for row in statuses:
            request = json.loads(row["Metadata"]["arguments"]["request"])
            # The service also polls minimal status for its dashboard. Those calls
            # remain audited, but do not establish emergency inspection evidence.
            if request.get("colonists") is True and request.get("threats") is True:
                emergency_reads.append(row)
        assert len(emergency_reads) >= (2 if phase == "unsafe" else 1), "Full emergency reads did not repeat during enabled admission"
        assert names.count(EXECUTE) == (0 if phase == "unsafe" else 1)
        if phase == "safe": assert names.index(CONTROL) < names.index(EXECUTE) < len(names)-1-names[::-1].index(CONTROL)
    return names


async def run(root: Path, output: Path, binary: Path, *, go_source: str, go_sha256: str, headless=True) -> bool:
    validate_go_handoff(go_source, go_sha256)
    assert Path("/.dockerenv").is_file(), "Run network/game processes only in container_scenario.py Docker worker"
    output.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "source": go_source, "expected_binary_sha256": go_sha256, "scope": "Paused unsafe-threat hold then safe admission via explicit Go controls; no pawn completion or simulation advance claim."}
    evidence = Evidence(output)
    launched = False
    try:
        configuration = prepare(root) if headless else prepare_rendered(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        gabs = Path(gabs_executable(root, configuration))
        profile = root / ("headless-profile" if headless else "profile")
        private = output / "rimgovernor-go"
        shutil.copyfile(binary, private); private.chmod(0o700)
        report["binary_sha256"] = verify_go_binary(private, go_source=go_source, go_sha256=go_sha256)
        database = output / "service.sqlite"
        assert not database.exists()

        async def wire(bridge, label, name, request):
            return proto(payload(await evidence.call(bridge, label, "rimgovernor/" + name, {"request": json.dumps(request)})))

        async def identity_read(bridge, label):
            return outcome(await wire(bridge, label, "lifecycle_read_identity", {}), "loaded")

        async def buildings(bridge, label):
            anchor = {"x": site["x"], "z": site["z"]}
            value = outcome(await wire(bridge, label, "observations_list_buildings", {"scope": {"expectedIdentity": identity},
                "defNames": [site["defName"]], "category": "all", "region": {"minimum": anchor, "maximum": anchor}, "page": {"limit": 16}}), "observed")
            assert value["context"]["identity"] == identity and value["completeness"]["page"]["complete"] is True
            assert int(value["completeness"]["unreadable"]) == 0
            rows = value.get("buildings", [])
            assert len(rows) == int(value["completeness"]["matched"]) == int(value["completeness"]["returned"])
            for row in rows:
                assert row["building"]["position"] == anchor and row["stuff"] == site["stuff"]
            return rows

        async def capture(bridge, label, baseline=None, phase=None):
            catalog = payload(await evidence.call(bridge, label + "-capabilities", "rimbridge/list_capabilities", {"limit": 10000, "includeParameters": False}))
            assert catalog["success"] is True and catalog["truncated"] is False
            assert catalog["returnedCount"] == catalog["totalCount"] == len(catalog["capabilities"])
            caps = {row["id"]: set(row["aliases"]) for row in catalog["capabilities"]}
            request = {"limit": 5000, "includeDiagnostics": True}
            if baseline is not None:
                request["afterSequence"] = baseline
            journal = payload(await evidence.call(bridge, label + "-events", "rimbridge/list_operation_events", request))
            events = journal["events"]
            assert events and len(events) < 5000, "Journal page may be truncated; cannot claim complete history"
            if baseline is not None:
                report.setdefault("traces", {})[label] = {"baseline": baseline, "last_sequence": max(r["Sequence"] for r in events),
                    "operations": audit(events, baseline, caps, phase), "capabilities": {key: sorted(value) for key, value in caps.items()}}
            return max(row["Sequence"] for row in events)

        async def observed(bridge, label, *, unsafe):
            loaded = await identity_read(bridge, label + "-identity")
            assert loaded["context"]["identity"] == identity and int(loaded["context"]["tick"]) == tick and loaded["paused"] is True
            facts = outcome(await wire(bridge, label + "-status", "observations_read_status", {
                "scope": {"expectedIdentity": identity}, "colonists": True, "threats": True, "colonistDetail": False, "page": {"limit": 256}}), "observed")
            status_facts(facts, identity, tick, fixture_ids, unsafe)
            report.setdefault("native_facts", {})[label] = facts

        async def legal(bridge, label):
            batch = outcome(await wire(bridge, label, "placement_preview", {"identity": identity, "placements": [site]}), "batch")
            assert batch["context"]["identity"] == identity and int(batch["context"]["tick"]) == tick
            assert len(batch["results"]) == 1
            value = outcome(batch["results"][0], "evaluated")
            assert value["canPlace"] is True and value["buildableByPlayer"] is True and value["researchFinished"] is True
            assert value["rotations"] and all(row["accepted"] is True for row in value["rotations"])
            costs = value["costList"]; stock = {row["defName"]: row["available"] for row in value["materials"]["known"]["rows"]}
            assert costs and all(row["count"] > 0 and stock[row["defName"]] >= row["count"] for row in costs)
            assert not await buildings(bridge, label + "-empty")

        async with bridge_session(gabs, configuration) as bridge:
            await bridge.core("games_start", gameId=bridge.game_id); launched = True
            await bridge.connect()
            names = await discovery(bridge, evidence)
            fixtures = {"test/guarded_construction_prepare", "test/guarded_construction_control", "test/b04f_setup"}
            inventory = json.loads((Path(__file__).resolve().parents[1] / "contracts/domain-inventory.json").read_text())
            rows = inventory["native_surface"]["tools"]
            production = {row["name"] for row in rows if row["build_role"] == "production"}
            all_fixtures = {row["name"] for row in rows if row["build_role"] == "fixture"}
            validate_discovery(names, production, all_fixtures, fixtures)
            report["discovery"] = {"production": len(production), "fixtures": len(fixtures), "names": names}
            async with asyncio.timeout(180):
                await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
            await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
            loaded = await identity_read(bridge, "before")
            identity, tick = loaded["context"]["identity"], int(loaded["context"]["tick"])
            assert loaded["paused"] is True
            prepared = payload(await evidence.call(bridge, "prepare", "test/guarded_construction_prepare", {"siteCount": 1}))
            assert prepared["success"] is True and len(prepared["sites"]) == 1
            assert all(prepared[key] == identity[key] for key in identity)
            site = prepared["sites"][0]; report["prepared"] = prepared
            assert not await buildings(bridge, "initial-empty")
            opponents = payload(await evidence.call(bridge, "setup-opponents", "test/b04f_setup", {"op": "opponents"}))
            assert opponents["success"] is True and opponents["setupOnly"] is True
            fixture_ids = set(opponents["opponents"])
            assert len(fixture_ids) == len(opponents["opponents"]) == 2
            report["fixture_opponents"] = sorted(fixture_ids)
            await observed(bridge, "unsafe-before", unsafe=True)
            await legal(bridge, "unsafe-legal")
            baseline = await capture(bridge, "setup")

        async def ready(http, expected_tick):
            value = await poll(http, "/api/state", lambda v: v.get("connected") is True and v.get("game", {}).get("stale") is False)
            assert value["identity"] == identity and value["game"]["paused"] is True and value["game"]["tick"] == expected_tick
            return value

        async def manual(http, request_id):
            value = await http("POST", "/api/player/control/manual", body={"requestId": request_id, "expected": identity})
            assert value["record"]["phase"] == "disabled" and value["state"]["enabled"] is False
            current = await http("GET", "/api/player/control")
            assert current["record"]["requestId"] == request_id and current["state"]["enabled"] is False
            return current["record"]["direction"]

        async with service(private, gabs, configuration, profile, database, output / "unsafe", report) as http:
            await ready(http, tick)
            assert (await http("GET", "/api/player/control"))["state"]["enabled"] is False
            building = http_building(site)
            body = {"requestId": "emergency-submit-1", "expected": identity, "building": building}
            submission = await http("POST", "/api/buildings/plans", body=body, expected=201)
            assert submission["requestId"] == body["requestId"] and submission["expected"] == identity and submission["building"] == building
            assert await http("GET", "/api/buildings/submission?requestId=emergency-submit-1") == submission
            report["submission"] = submission
            pending(await http("GET", "/api/plan?id=" + submission["planId"]), submission)
            grant = await http("POST", "/api/player/control/acquire", body={"requestId": "emergency-acquire-1", "expected": identity,
                "planId": submission["planId"], "revision": submission["revision"], "expectedDirection": "0"})
            assert grant["record"]["phase"] == "granted" and grant["state"]["enabled"] is True and grant["state"]["observationKnown"] is True
            started = asyncio.get_running_loop().time(); observations = 0
            while asyncio.get_running_loop().time() - started < 10:
                pending(await http("GET", "/api/plan?id=" + submission["planId"]), submission)
                await ready(http, tick); observations += 1
                await asyncio.sleep(.5)
            assert observations >= 2
            report["unsafe_interval"] = {"seconds": asyncio.get_running_loop().time() - started, "polls": observations}
            direction = await manual(http, "emergency-manual-1")
            pending(await http("GET", "/api/plan?id=" + submission["planId"]), submission)
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            baseline = await capture(bridge, "unsafe-service", baseline, "unsafe")
            await observed(bridge, "unsafe-after", unsafe=True)
            await legal(bridge, "unsafe-after-legal")
            cleared = payload(await evidence.call(bridge, "clear-fixture-opponents", "test/b04f_setup", {"op": "clear-opponents"}))
            assert cleared["success"] is True and set(cleared["cleared"]) == fixture_ids and len(cleared["cleared"]) == 2
            assert cleared["setupOnly"] is True and cleared["completedWorkInjected"] is False
            await observed(bridge, "safe-before", unsafe=False)
            await legal(bridge, "safe-legal")
            baseline = await capture(bridge, "clear-orchestrator", baseline, "orchestrator")
        async with service(private, gabs, configuration, profile, database, output / "safe", report) as http:
            await ready(http, tick)
            disabled = await http("GET", "/api/player/control")
            assert disabled["state"]["enabled"] is False and disabled["record"]["direction"] == direction
            assert await http("GET", "/api/buildings/submission?requestId=emergency-submit-1") == submission
            pending(await http("GET", "/api/plan?id=" + submission["planId"]), submission)
            grant = await http("POST", "/api/player/control/acquire", body={"requestId": "emergency-acquire-2", "expected": identity,
                "planId": submission["planId"], "revision": submission["revision"], "expectedDirection": disabled["record"]["direction"]})
            assert grant["record"]["phase"] == "granted" and grant["state"]["enabled"] is True and grant["state"]["observationKnown"] is True
            plan = await poll(http, "/api/plan?id=" + submission["planId"], lambda value: action(value, submission)["stage"] == "awaiting_observation")
            verify_progress(plan, submission, False); report["admitted_plan"] = plan
            await manual(http, "emergency-manual-2")
            verify_progress(await http("GET", "/api/plan?id=" + submission["planId"]), submission, False)
            await ready(http, tick)
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            await capture(bridge, "safe-service", baseline, "safe")
            rows = await buildings(bridge, "accepted-blueprint")
            assert len(rows) == 1 and rows[0]["status"] == "blueprint"
            report["blueprint"] = rows[0]
            await observed(bridge, "safe-after", unsafe=False)
        report["passed"] = True
    except BaseException as error:
        report["error"] = repr(error)
    finally:
        incomplete = any(not phase.get("joined") for phase in report.get("service_phases", []))
        if launched and not incomplete:
            try:
                async with bridge_session(gabs, configuration) as bridge:
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
            except BaseException as error:
                report.update(passed=False, cleanup_error=repr(error))
        elif incomplete:
            report.update(passed=False, resources_retained=True)
        report["artifacts"] = {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in output.rglob("*") if path.is_file()}
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--go-binary", type=Path, required=True)
    add_handoff_arguments(parser)
    parser.add_argument("--rendered", action="store_true")
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-emergency-service-acceptance", args.go_binary, go_source=args.go_source, go_sha256=args.go_sha256, headless=not args.rendered)) else 1)
