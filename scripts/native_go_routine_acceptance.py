"""Live Go routine needs and Manual; launch through container_scenario.py."""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import math
from pathlib import Path
import shutil
import sqlite3
import traceback

from native_building_service_acceptance import (
    Evidence, bridge_session, gabs_executable, payload, prepare, package_files,
    proto, outcome, service, poll, http_building, READS, DIAGNOSTICS, CONTROL,
    EXECUTE, add_handoff_arguments, verify_go_binary,
)
from native_go_clock_acceptance import routine_evidence
from rimgovernor.food_forecast import food_forecast


def medical_need(reply):
    colony = outcome(reply, "observed")["colonists"]
    census = colony["completeness"]
    rows = colony.get("pawns", [])
    if not census["page"]["complete"] or int(census.get("filtered", -1)) != 0 or int(census.get("unreadable", -1)) != 0:
        return "unknown"
    if len(rows) != int(census["matched"]) or len(rows) != int(census["returned"]):
        return "unknown"
    needed = False
    for pawn in rows:
        if pawn.get("dead") is True:
            continue
        values = [pawn.get("dead"), pawn.get("downed"), pawn.get("health", {}).get("bleeding"), pawn.get("health", {}).get("needsTend")]
        if any(type(value) is not bool for value in values):
            return "unknown"
        needed |= any(values)
    return "deficit" if needed else "recovered"


def audit_routine(events, baseline, capabilities, *, restart):
    rows = sorted(events, key=lambda row: row["Sequence"])
    assert rows and [r["Sequence"] for r in rows] == list(range(baseline + 1, rows[-1]["Sequence"] + 1))
    allowed = READS | DIAGNOSTICS | {"rimgovernor/observations_list_pawns"} | {"rimgovernor/clock_" + n for n in ("read_status", "read_events", "read_attempt", "pause")}
    if not restart:
        allowed |= {CONTROL, EXECUTE, "rimgovernor/operations_preview", "rimgovernor/placement_preview",
                    "rimgovernor/clock_start", "rimgovernor/clock_renew", "rimgovernor/observations_read_colony_facts"}
    operations = {}
    for row in rows:
        if not row.get("CapabilityId"):
            assert not row.get("OperationId")
            continue
        names = capabilities.get(row["CapabilityId"], set()) & allowed
        assert len(names) == 1, f"Unexpected operation {capabilities.get(row['CapabilityId'])}"
        name = next(iter(names))
        assert operations.setdefault(row["OperationId"], name) == name
    names = list(operations.values())
    if not restart:
        assert "rimgovernor/observations_read_colony_facts" in names
    return names


def append_operation_history(history, batch, baseline):
    cursor = history[-1]['Sequence'] if history else baseline
    assert len(history) + len(batch) <= 100000, 'Operation history exceeds scenario bound'
    assert [r['Sequence'] for r in batch] == list(range(cursor + 1, cursor + 1 + len(batch))), 'Operation history gap or duplicate'
    history.extend(batch)


def read_operation_history(path, baseline):
    assert path.stat().st_size <= 64 * 1024 * 1024, 'Native operation history exceeds bound'
    rows = [json.loads(line) for line in path.read_text(encoding='utf8').splitlines()]
    rows = sorted((r for r in rows if r['Sequence'] > baseline), key=lambda r: r['Sequence'])
    history = []
    append_operation_history(history, rows, baseline)
    return history


def audit_resource_rules(plan, names):
    assert plan['actions']
    assert all(a['progress']['stage'] == 'pending' and a['progress']['attempt'] == '0' for a in plan['actions'])
    assert EXECUTE not in names
    assert names.count('rimgovernor/placement_preview') >= 2


def audit_development(review, workers):
    development = review['Development']
    assert development['Snapshot'] == review['Snapshot'] and development['Tick'] == review['Tick']
    assert development['Workers'] == workers and development['Capacity'] == min(2, workers)
    assert len(development['Committed']) == 1, 'Accepted player project must consume optional capacity'
    rows = development['Rows'] or []
    assert len({r['Goal'] for r in rows}) == len(rows)
    for row in rows:
        assert row['Goal'] in {'MaintainWood', 'EnsureBasicDefense'}
        assert row['Deficit'] is None or 0 <= row['Deficit'] <= 1
        assert math.isfinite(row['Score']) and 0 <= row['WaitingSince'] <= review['Tick']
        if row['Selected']:
            assert row['Deficit'] is not None and not row['Reason'] and not row['Committed']
    assert sum(r['Selected'] for r in rows) <= max(0, development['Capacity'] - 1)
    return development


async def wait_review(database, after_revision=0):
    async with asyncio.timeout(60):
        while True:
            with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as db:
                row = db.execute("SELECT payload FROM routine_review WHERE singleton=1").fetchone()
            if row and json.loads(row[0])["Enabled"] and json.loads(row[0])["Revision"] > after_revision:
                return
            await asyncio.sleep(.05)


async def wait_building_method(http, database, definition, count, *, shell=False):
    async with asyncio.timeout(600 if shell else 180):
        while True:
            with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as db:
                rows = db.execute("SELECT DISTINCT m.plan_id FROM goal_methods m JOIN actions a ON a.plan_id=m.plan_id WHERE a.definition=?", (definition,)).fetchall()
            assert len(rows) <= 1, "Duplicate building methods"
            if rows:
                plan = await http("GET", "/api/plan?id=" + rows[0][0])
                assert len(plan["actions"]) == count
                if shell:
                    assert sum(a['building']['defName'] == 'Wall' for a in plan['actions']) == 31
                    assert sum(a['building']['defName'] == 'Door' for a in plan['actions']) == 1
                else:
                    assert all(a["building"]["defName"] == definition for a in plan["actions"])
                if all(a["progress"]["stage"] == "completed" for a in plan["actions"]):
                    assert all(a["progress"]["effect"] == "completed" and not a["progress"]["unresolved"] and a["progress"]["attempt"] == "1" for a in plan["actions"])
                    return plan
            await asyncio.sleep(.2)


def shell_geometry(plan):
    buildings = [a['building'] for a in plan['actions']]
    assert len(buildings) == 32
    cells = {(b['x'], b['z']) for b in buildings}
    assert len(cells) == 32
    x, z = min(c[0] for c in cells), min(c[1] for c in cells)
    assert cells == {(i, j) for i in range(x, x+9) for j in range(z, z+9)
                     if i in (x, x+8) or j in (z, z+8)}
    for b in buildings:
        assert b['stuff'] == 'WoodLog'
        assert b['defName'] == ('Door' if (b['x'], b['z']) == (x+4, z) else 'Wall')
    return {(i, j) for i in range(x+1, x+8) for j in range(z+1, z+8)}


def audit_sleeping(report, database):
    root = report["active_routine"]["review"]["Snapshot"]
    plan = report["sleeping_plan"]
    if "cooking_plan" in report:
        assert len(report["cooking_plan"]["actions"]) == 1
        assert report["cooking_plan"]["actions"][0]["building"]["defName"] == "Campfire"
        assert report["manual_routine"]["goals"]["EnsureCooking"]["Need"] == "deficit"
    interior = {(c["x"], c["z"]) for c in report["sleeping_setup"]["interior"]}
    shell = report.get('shelter_plan')
    if shell:
        assert report['sleeping_setup']['outdoorSite']
        assert report['sleeping_setup']['shellPiecesCreated'] == report['sleeping_setup']['roofCellsCreated'] == 0
        interior = shell_geometry(shell)
    assert len(plan["actions"]) == report["sleeping_setup"]["colonists"]
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as db:
        methods = [plan] + ([report["cooking_plan"]] if "cooking_plan" in report else []) + ([shell] if shell else [])
        door_completed = None
        if shell:
            door = next(a for a in shell['actions'] if a['building']['defName'] == 'Door')
            dependencies = db.execute('SELECT action_id,requires_id FROM action_dependencies WHERE plan_id=?', (shell['id'],)).fetchall()
            assert set(dependencies) == {(a['id'], door['id']) for a in shell['actions'] if a != door}
            door_events = [json.loads(r[0]) for r in db.execute('SELECT payload FROM transitions WHERE action_id=? ORDER BY sequence', (door['id'],))]
            door_completed = next(r['Observation']['Tick'] for r in door_events if r['Kind'] == 'observe' and r['Observation']['Effect'] == 'completed')
        for action in [a for method in methods for a in method["actions"]]:
            progress = action["progress"]
            assert progress["stage"] == progress["effect"] == "completed" and progress["attempt"] == "1" and not progress["unresolved"]
            admission = json.loads(db.execute("SELECT payload FROM admissions WHERE action_id=?", (action["id"],)).fetchone()[0])
            if action['building']['defName'] not in {'Wall', 'Door'}:
                assert all((c["X"], c["Z"]) in interior for c in admission["Footprint"])
            transitions = [json.loads(r[0]) for r in db.execute("SELECT payload FROM transitions WHERE action_id=? ORDER BY sequence", (action["id"],))]
            dispatched = [r for r in transitions if r["Kind"] == "dispatch"]
            assert len(dispatched) == 1
            if shell and action['building']['defName'] == 'Wall':
                assert dispatched[0]['Tick'] >= door_completed
            scope = dict(dispatched[0]["Snapshot"])
            assert scope["Plan"] in {method["id"] for method in methods}
            scope["Plan"], scope["Revision"] = root["Plan"], root["Revision"]
            assert scope == root, "Routine method changed player direction or native authority"
    assert report["traces"]["operate"].count(EXECUTE) == 1 + sum(len(method["actions"]) for method in methods)
    return {"completed_spots": len(plan["actions"]), "completed_shell_pieces": len(shell['actions']) if shell else 0, "completed_cooking_buildings": len(report.get("cooking_plan", {}).get("actions", [])), "single_attempts": True, "indoor_footprints": True, "shared_player_authority": True}


async def run(root, output, binary, *, go_source, go_sha256, sleeping_methods=False, cooking_methods=False, work_project=False, work_overrides=False, power_fixture=False, resource_rules=(), shelter_methods=False):
    assert Path("/.dockerenv").is_file(), "Use the isolated scenario launcher"
    output.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "source": go_source,
              "scope": "Native core/emergency facts reach fourteen durable Go needs; Manual invalidates them; disabled restart neither acquires authority nor reads routine facts. No routine method execution claim."}
    if sleeping_methods:
        report["scope"] = "Reviewed indoor sleeping deficit compiles and executes through shared Hands, with native completion, Manual invalidation and disabled restart. Private fixture supplies only an empty room and healthy starting colonists."
    if shelter_methods:
        report["scope"] = "From an empty outdoor site, shared Go Hands constructs a starter shell, normal pawn work roofs it, and the same maintained goal furnishes indoor sleeping capacity. Door completion gates walls; all native outcomes, Manual and disabled restart are verified."
    if cooking_methods:
        report["scope"] = "Reviewed sleeping and cooking deficits execute ordinary building methods through shared Hands; native completion, shared authority, Manual and disabled restart. Campfire construction does not certify cooking bills or food production."
    evidence = Evidence(output)
    launched = False
    try:
        configuration = prepare(root)
        game = json.loads((configuration / "config.json").read_text())["games"]["rimgovernor-trial"]
        report["package_files"] = package_files(Path(game["workingDir"]))
        gabs, profile = Path(gabs_executable(root, configuration)), root / "headless-profile"
        private, database = output / "rimgovernor-go", output / "service.sqlite"
        shutil.copyfile(binary, private)
        private.chmod(0o700)
        report["binary_sha256"] = verify_go_binary(private, go_source=go_source, go_sha256=go_sha256)

        async def wire(bridge, label, name, request):
            return proto(payload(await evidence.call(bridge, label, "rimgovernor/" + name, {"request": json.dumps(request)})))

        async def capture(bridge, label, baseline=None, restart=False, retained=()):
            catalog = payload(await evidence.call(bridge, label + "-capabilities", "rimbridge/list_capabilities", {"limit": 10000, "includeParameters": False}))
            assert catalog["success"] and not catalog["truncated"]
            caps = {r["id"]: set(r["aliases"]) for r in catalog["capabilities"]}
            request = {"limit": 5000, "includeDiagnostics": True}
            if baseline is not None:
                request["afterSequence"] = retained[-1]['Sequence'] if retained else baseline
            rows = list(retained)
            for page in range(20):
                batch = payload(await evidence.call(bridge, label + f"-events-{page}", "rimbridge/list_operation_events", request))["events"]
                rows.extend(batch)
                if len(batch) < 5000:
                    break
                request['afterSequence'] = max(r['Sequence'] for r in batch)
            else:
                raise AssertionError('Operation audit exceeds bounded pagination')
            assert rows
            if baseline is not None:
                report.setdefault("traces", {})[label] = audit_routine(rows, baseline, caps, restart=restart)
            return max(r["Sequence"] for r in rows)

        async with bridge_session(gabs, configuration) as bridge:
            await bridge.core("games_start", gameId=bridge.game_id)
            launched = True
            await bridge.connect()
            await evidence.call(bridge, "new-game", "rimworld/start_debug_game_ready", {"readiness": "visual", "pauseIfNeeded": True, "timeoutMs": 120000})
            await evidence.call(bridge, "pause", "rimworld/set_time_speed", {"speed": "Paused", "ultraSpeedBoost": False})
            initial = outcome(await wire(bridge, "before", "lifecycle_read_identity", {}), "loaded")
            identity, tick = initial["context"]["identity"], int(initial["context"]["tick"])
            assert initial["paused"]
            prepared = payload(await evidence.call(bridge, "prepare", "test/guarded_construction_prepare", {"siteCount": 1}))
            assert prepared["success"] and all(prepared[k] == identity[k] for k in identity)
            report["prepared"] = prepared
            if power_fixture:
                report['power_setup'] = payload(await evidence.call(bridge, 'power-setup', 'test/routine_power_setup', {}))
                assert report['power_setup']['success']
            if sleeping_methods or resource_rules:
                report["sleeping_setup"] = payload(await evidence.call(bridge, "sleeping-setup", "test/routine_sleeping_prepare", {"outdoorSite": shelter_methods}))
                assert report["sleeping_setup"]["success"] and report["sleeping_setup"]["sleepingSpotsCreated"] == 0
            report["initial_colony"] = await wire(bridge, "initial-colony", "observations_read_status", {
                "scope": {"expectedIdentity": identity}, "colonists": True, "threats": True, "colonistDetail": False, "page": {"limit": 256}})
            if sleeping_methods or resource_rules:
                assert medical_need(report['initial_colony']) == 'recovered', 'Construction fixture requires healthy starting colonists'
            colonists = outcome(report["initial_colony"], "observed")["colonists"]["pawns"]
            pawn_ids = [p["pawn"]["id"] for p in colonists]
            detailed = outcome(await wire(bridge, "initial-equipment", "observations_list_pawns", {
                "scope": {"expectedIdentity": identity}, "filter": {"ids": pawn_ids, "includeDead": True},
                "details": {"needs": False, "health": True, "equipment": True, "biography": True,
                            "settings": False, "social": False, "animals": False, "work": True}, "page": {"limit": len(pawn_ids)}}), "observed")
            assert detailed["context"] == outcome(report["initial_colony"], "observed")["context"]
            assert {p["pawn"]["id"] for p in detailed["pawns"]} == set(pawn_ids)
            assert all(type(p.get("dead")) is bool and type(p.get("downed")) is bool
                       and type(p.get("equipment", {}).get("armed")) is bool for p in detailed["pawns"])
            report["initial_armed"] = sum(not p["dead"] and not p["downed"] and p["equipment"]["armed"] for p in detailed["pawns"])
            from native_work_readback import work_reference
            legacy_work = payload(await evidence.call(bridge, "initial-work", "home/list_pawns", {"colonistsOnly": True, "work": True, "bio": True, "equipment": True}))
            assert legacy_work['success'] and {p['thingId'] for p in legacy_work['pawns']} == set(pawn_ids)
            minimum_construction = 0
            if work_project:
                project_facts = await wire(bridge, 'work-project-definition', 'observations_read_colony_facts', {
                    'scope': {'expectedIdentity': identity}, 'planning': True, 'requestedDefinitionNames': ['HospitalBed']})
                project = outcome(project_facts, 'observed')
                assert project['context'] == detailed['context']
                definitions = project['planning']['observed']['definitions']
                assert len(definitions) == 1 and definitions[0]['definition']['defName'] == 'HospitalBed'
                minimum_construction = definitions[0]['constructionSkill']
                assert minimum_construction > 0
                report['work_project'] = {'definition': 'HospitalBed', 'minimum_construction': minimum_construction}
                (output / 'work-project-definition.json').write_text(json.dumps(project_facts), encoding='utf8')
            overrides = []
            if work_overrides:
                overrides = [{'pawn': p['thingId'], 'work': 'Construction', 'priority': 0} for p in legacy_work['pawns']
                             if any(w['name'] == 'Construction' for w in p['work']['types'])]
                assert overrides
            report['initial_work'] = work_reference(legacy_work['pawns'], minimum_construction, overrides)
            work_colony = await wire(bridge, "work-colony", "observations_read_colony_facts", {"scope": {"expectedIdentity": identity}, "planning": True})
            for name, value in {'work-pawns': {'observed': detailed}, 'work-colony': work_colony,
                                'work-status': report['initial_colony'], 'work-reference': report['initial_work']}.items():
                (output / (name + '.json')).write_text(json.dumps(value), encoding='utf8')
            facts = payload(await evidence.call(bridge, "initial-food", "home/colony_facts", {"planning": False}))
            if power_fixture:
                recovery = payload(await evidence.call(bridge, 'power-recovery', 'home/recovery_state', {}))
                assert recovery['success'] and int(recovery['tick']) == tick
                power = facts['development']['power']
                consumers = [p for p in power if p['baseW'] < 0]
                assert consumers
                headroom = min((sum(p['outputW'] for p in power if p['net'] == consumer['net'])
                               if consumer['net'] is not None and consumer['powered'] else -1 for consumer in consumers), default=0)
                disabled = any(b.get('powerOn') is False and not b.get('forbidden') and b.get('powerConsumer', True)
                               and b.get('switchedOn', True) for b in recovery['buildings'])
                report['power_reference'] = {'required': bool(consumers), 'headroom': headroom, 'disabled': disabled}
                (output / 'power-reference.json').write_text(json.dumps(report['power_reference']), encoding='utf8')
            food = food_forecast(facts['nativeForecastInputs']['combinedFoodSupply'], consumer_ids=[r['id'] for r in facts['foodSupply']['consumers']])
            report['initial_food_forecast'] = food
            expected_food = 'deficit' if food['readable'] and food['runwayDays'] is not None and food['runwayDays'] < 3 else 'unknown'
            baseline = await capture(bridge, "setup")

        async with service(private, gabs, configuration, profile, database, output / "operate", report, clock_control=True, routine_reviews=True, routine_methods=sleeping_methods, routine_cooking=cooking_methods or bool(resource_rules), routine_shelter=shelter_methods, resource_rules=resource_rules) as http:
            await poll(http, "/api/state", lambda v: v.get("connected") and not v.get("game", {}).get("stale", True))
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            building = http_building(prepared['sites'][0])
            if work_project:
                building.update(defName='HospitalBed', stuff='')
            submission = await http("POST", "/api/buildings/plans", body={"requestId": "routine-context", "expected": identity,
                "building": building}, expected=201)
            if work_overrides:
                preference_path = '/api/player/work-preferences?planId=' + submission['planId']
                initial = await http('GET', preference_path)
                assert initial['revision'] == '0' and initial['overrides'] == []
                preference_request = {'requestId': 'work-preferences', 'planId': submission['planId'], 'expected': identity,
                                      'expectedRevision': '0', 'overrides': overrides}
                report['work_preferences'] = await http('POST', '/api/player/work-preferences/replace', body=preference_request)
                assert report['work_preferences']['revision'] == '1' and report['work_preferences']['overrides'] == overrides
            granted = await http("POST", "/api/player/control/acquire", body={"requestId": "routine-acquire", "expected": identity,
                "planId": submission["planId"], "revision": submission["revision"], "expectedDirection": "0"})
            assert granted["record"]["phase"] == "granted"
            await wait_review(database)
            active = routine_evidence(database, identity, enabled=True, expected_food_need=expected_food, allow_methods=sleeping_methods)
            assert active["review"]["Tick"] == tick, "Review did not use the initial paused boundary"
            assert active["goals"]["CriticalMedical"]["Need"] == medical_need(report["initial_colony"])
            if report["initial_armed"] < min(2, len(pawn_ids)):
                assert active["goals"]["EnsureBasicDefense"]["Need"] == "deficit", "Native equipment shortage did not reach routine defense need"
            report["defense_need"] = active["goals"]["EnsureBasicDefense"]["Need"]
            report['work_need'] = active['goals']['EnsureWorkAssignments']['Need']
            assert report['work_need'] == ('recovered' if report['initial_work']['matches'] else 'deficit'), 'Native work readback did not reach routine need'
            report["active_routine"] = active
            report['development'] = audit_development(active['review'], len(report['initial_work']['assignments']))
            if resource_rules:
                await wait_review(database, active['review']['Revision'])
                active = routine_evidence(database, identity, enabled=True, expected_food_need=expected_food)
                assert active['goals']['EnsureCooking']['Need'] == 'deficit'
                plan = await http('GET', '/api/plan?id=' + submission['planId'])
                assert all(a['progress']['stage'] == 'pending' and a['progress']['attempt'] == '0' for a in plan['actions'])
                report['resource_rules'] = list(resource_rules)
                report['resource_policy_pending_plan'] = plan
            if power_fixture:
                report['power_need'] = active['goals']['EnsureBasicPower']['Need']
                assert report['power_need'] == ('deficit' if headroom < 0 or disabled else 'recovered')
            if work_overrides:
                assert active['review']['WorkPreferenceRevision'] == 1
                cleared = await http('POST', '/api/player/work-preferences/replace', body=preference_request | {
                    'requestId': 'clear-work', 'expectedRevision': '1', 'overrides': []})
                assert cleared['revision'] == '2' and cleared['overrides'] == []
                await wait_review(database, active['review']['Revision'])
                after_clear = routine_evidence(database, identity, enabled=True, expected_food_need=expected_food)
                assert after_clear['review']['WorkPreferenceRevision'] == 2
                default_work = work_reference(legacy_work['pawns'], minimum_construction)
                assert after_clear['goals']['EnsureWorkAssignments']['Need'] == ('recovered' if default_work['matches'] else 'deficit')
                replay = await http('POST', '/api/player/work-preferences/replace', body=preference_request)
                assert replay == report['work_preferences'] and await http('GET', preference_path) == cleared
                report['work_preferences'] = await http('POST', '/api/player/work-preferences/replace', body=preference_request | {
                    'requestId': 'restore-work', 'expectedRevision': '2'})
                await wait_review(database, after_clear['review']['Revision'])
                assert routine_evidence(database, identity, enabled=True, expected_food_need=expected_food)['goals']['EnsureWorkAssignments']['Need'] == 'deficit'
                report['work_preference_clear_and_replay'] = True
            if shelter_methods:
                report["shelter_plan"] = await wait_building_method(http, database, "Wall", 32, shell=True)
            if sleeping_methods:
                report["sleeping_plan"] = await wait_building_method(http, database, "SleepingSpot", report["sleeping_setup"]["colonists"])
            if shelter_methods:
                async with asyncio.timeout(300):
                    while True:
                        recovered = routine_evidence(database, identity, enabled=True, expected_food_need=expected_food, allow_methods=True)
                        if recovered['goals']['EnsureInitialShelter']['Need'] == 'recovered':
                            assert recovered['goals']['EnsureInitialShelter']['Status'] == 'satisfied'
                            report['shelter_recovered'] = recovered['goals']['EnsureInitialShelter']
                            break
                        await asyncio.sleep(.2)
            if cooking_methods:
                report["cooking_plan"] = await wait_building_method(http, database, "Campfire", 1)
            manual = await http("POST", "/api/player/control/manual", body={"requestId": "routine-manual", "expected": identity})
            assert manual["record"]["phase"] == "disabled" and not manual["state"]["enabled"]
            report["manual_routine"] = routine_evidence(database, identity, enabled=False, expected_food_need=expected_food, allow_methods=sleeping_methods)
            assert not report['manual_routine']['review']['Development']['Rows']
            assert report['manual_routine']['review']['Development']['Workers'] is None

        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            retained = []
            if shelter_methods:
                history_path = Path(report['sleeping_setup']['operationHistoryPath'])
                assert history_path.is_relative_to(profile)
                retained = read_operation_history(history_path, baseline)
                shutil.copyfile(history_path, output / 'native-operation-history.jsonl')
            await capture(bridge, "operate", baseline, retained=retained)
            if resource_rules:
                audit_resource_rules(report['resource_policy_pending_plan'], report['traces']['operate'])
                report['resource_policy_no_orders'] = True
            final = outcome(await wire(bridge, "after-manual", "lifecycle_read_identity", {}), "loaded")
            assert final["paused"] and final["context"]["identity"] == identity
            if shelter_methods:
                native = outcome(await wire(bridge, 'shelter-outcome', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': True}), 'observed')
                assert int(native['indoorSleepingCapacity']) >= report['sleeping_setup']['colonists']
                report['shelter_outcome'] = native
            baseline = await capture(bridge, "restart-baseline")
        async with service(private, gabs, configuration, profile, database, output / "restart", report, clock_control=True, routine_reviews=True, routine_methods=sleeping_methods, routine_cooking=cooking_methods or bool(resource_rules), routine_shelter=shelter_methods, resource_rules=resource_rules) as http:
            await poll(http, "/api/state", lambda v: v.get("connected") and not v.get("game", {}).get("stale", True))
            await asyncio.sleep(2)
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            assert routine_evidence(database, identity, enabled=False, expected_food_need=expected_food, allow_methods=sleeping_methods) == report["manual_routine"]
            if work_overrides:
                assert await http('GET', preference_path) == report['work_preferences']
                stale = preference_request | {'requestId': 'stale-work'}
                await http('POST', '/api/player/work-preferences/replace', body=stale, expected=409)
                assert await http('GET', preference_path) == report['work_preferences']
                report['work_preference_restart'] = True
        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            await capture(bridge, "restart", baseline, restart=True)
            after = outcome(await wire(bridge, "final", "lifecycle_read_identity", {}), "loaded")
            assert after["paused"] and after["context"]["tick"] == final["context"]["tick"]
        if sleeping_methods:
            report["sleeping_audit"] = audit_sleeping(report, database)
        report["passed"] = True
    except BaseException as error:
        report.update(error=repr(error), traceback=traceback.format_exc())
    finally:
        if launched and all(p.get("joined") for p in report.get("service_phases", [])):
            try:
                async with bridge_session(gabs, configuration) as bridge:
                    if not report['passed'] and shelter_methods and 'shelter_plan' in report:
                        try:
                            await bridge.connect()
                            report['failure_colony'] = await wire(bridge, 'failure-colony', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': True})
                            buildings = [a['building'] for a in report['shelter_plan']['actions']]
                            report['failure_roof_cells'] = payload(await evidence.call(bridge, 'failure-roof-cells', 'home/get_cells_plus', {
                                'x': min(b['x'] for b in buildings), 'z': min(b['z'] for b in buildings),
                                'width': 9, 'height': 9, 'fields': 'roof,areas,things,designations'}))
                        except BaseException as diagnostic_error:
                            report['diagnostic_error'] = repr(diagnostic_error)
                    report["stop"] = (await bridge.core("games_stop", gameId=bridge.game_id)).model_dump(mode="json")
            except BaseException as error:
                report.update(passed=False, cleanup_error=repr(error))
        elif launched:
            report.update(passed=False, resources_retained=True)
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf8")
    return report["passed"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--go-binary", type=Path, required=True)
    add_handoff_arguments(parser)
    parser.add_argument("--sleeping-methods", action="store_true")
    parser.add_argument("--cooking-methods", action="store_true")
    parser.add_argument("--shelter-methods", action="store_true")
    parser.add_argument("--work-project", action="store_true")
    parser.add_argument("--work-overrides", action="store_true")
    parser.add_argument("--power-fixture", action="store_true")
    parser.add_argument("--resource-rule", action="append", default=[])
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-go-routine-acceptance", args.go_binary,
        go_source=args.go_source, go_sha256=args.go_sha256, sleeping_methods=args.sleeping_methods or args.cooking_methods or args.shelter_methods, shelter_methods=args.shelter_methods, cooking_methods=args.cooking_methods, work_project=args.work_project, work_overrides=args.work_overrides, power_fixture=args.power_fixture, resource_rules=args.resource_rule)) else 1)
