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
from rimgovernor.medical_management import care_state


def medical_care_reference(people):
    care = care_state(people)
    patients = sorted(p['patient'] for p in care['patients'])
    unknown = care['unknown']
    return {'patients': patients, 'unknown': unknown,
            'need': 'deficit' if patients else 'unknown' if unknown else 'recovered'}


def audit_medical_care(active, expected):
    history = active['review']['MedicalCare']
    assert history['CensusKnown'] is True
    assert (history['Patients'] or []) == expected['patients']
    assert (history['Unknown'] or []) == expected['unknown']
    goal = active['goals']['MaintainMedicalCare']
    assert goal['Need'] == expected['need'] and goal['Priority'] == 2


def audit_starting_supplies(active, cells):
    history = active['review']['StartingSupplies']
    expected = sorted([{'X': c['x'], 'Z': c['z']} for c in cells], key=lambda c: (c['Z'], c['X']))
    assert history['Initialized'] is True and (history['Pending'] or []) == expected
    assert active['goals']['AllowStartingSupplies']['Need'] == ('deficit' if cells else 'recovered')


def audit_comfort_use(active, native):
    goal = active['goals']['EnsureComfort']
    assert goal['Need'] == 'recovered' and goal['Status'] == 'satisfied'
    history = active['review']['Comfort']
    people = set(native['people'])
    assert people, 'Comfort acceptance requires actual eligible colonists'
    for kind in ('Dining', 'Recreation'):
        proof = history[kind]
        assert proof['Facility'] and 0 < proof['Tick'] <= active['review']['Tick']
        facilities = native[kind.lower()]
        assert people <= {p for f in facilities for p in f.get('accessibleTo', [])}, 'Current comfort capacity lost'
        assert any(f['id'] == proof['Facility'] and people & set(f.get('accessibleTo', [])) for f in facilities), 'Use proof no longer identifies an accessible native facility'
    return history


def audit_comfort_methods(report, database):
    plans = report['comfort_plans']
    assert set(plans) == {'Table1x2c', 'DiningChair', 'HorseshoesPin'}
    root = report['active_routine']['review']['Snapshot']
    footprints = {}
    with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
        for definition, plan in plans.items():
            assert len(plan['actions']) == 1
            action = plan['actions'][0]
            progress = action['progress']
            assert action['building']['defName'] == definition
            assert progress['stage'] == progress['effect'] == 'completed' and progress['attempt'] == '1' and not progress['unresolved']
            transitions = [json.loads(r[0]) for r in db.execute('SELECT payload FROM transitions WHERE action_id=? ORDER BY sequence', (action['id'],))]
            dispatched = [r for r in transitions if r['Kind'] == 'dispatch']
            assert len(dispatched) == 1
            scope = dict(dispatched[0]['Snapshot'])
            assert scope['Plan'] == plan['id']
            scope['Plan'], scope['Revision'] = root['Plan'], root['Revision']
            assert scope == root, 'Comfort method changed shared player authority'
            admission = json.loads(db.execute('SELECT payload FROM admissions WHERE action_id=?', (action['id'],)).fetchone()[0])
            footprints[definition] = {(c['X'], c['Z']) for c in admission['Footprint']}
            assert admission['Costs'], 'Furniture construction bypassed material accounting'
        assert db.execute('SELECT count(*) FROM goal_methods').fetchone()[0] == 3
    interior = {(c['x'], c['z']) for c in report['sleeping_setup']['interior']}
    assert footprints['Table1x2c'] <= interior and footprints['DiningChair'] <= interior
    assert any(abs(x-a) + abs(z-b) == 1 for x,z in footprints['Table1x2c'] for a,b in footprints['DiningChair'])
    assert report['traces']['operate'].count(EXECUTE) == 4
    return {'completed_facilities': 3, 'shared_authority': True, 'single_attempts': True, 'native_use': True}


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


def assert_construction_start(reply):
    assert medical_need(reply) == 'recovered', 'Construction fixture requires healthy starting colonists'
    threats = outcome(reply, 'observed')['threats']
    census = threats['completeness']
    groups = ('hostiles', 'huntingPredators', 'ignoredHunters', 'wildPredatorsNear', 'downedNear')
    count = sum(len(threats.get(k, [])) for k in groups)
    assert census['page']['complete'] and all(int(census[k]) == 0 for k in ('filtered', 'unreadable')) and all(int(census[k]) == count for k in ('matched', 'returned')), 'Construction fixture requires a complete threat census'
    assert not threats.get('hostiles', []) and not threats.get('huntingPredators', []), 'Construction fixture requires no starting hostiles or hunting predators'
    assert all(r.get('pawn', {}).get('hostile') is False for key in ('ignoredHunters', 'wildPredatorsNear', 'downedNear') for r in threats.get(key, [])), 'Construction fixture requires explicit non-hostile incidental observations'


def audit_routine(events, baseline, capabilities, *, restart):
    rows = sorted(events, key=lambda row: row["Sequence"])
    assert rows and [r["Sequence"] for r in rows] == list(range(baseline + 1, rows[-1]["Sequence"] + 1))
    allowed = READS | DIAGNOSTICS | {"rimgovernor/observations_list_pawns"} | {"rimgovernor/clock_" + n for n in ("read_status", "read_events", "read_attempt", "pause")}
    if not restart:
        allowed |= {CONTROL, EXECUTE, "rimgovernor/operations_preview", "rimgovernor/placement_preview",
                    "rimgovernor/clock_start", "rimgovernor/clock_renew", "rimgovernor/observations_read_colony_facts", "rimgovernor/observations_list_rooms"}
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


def audit_development(review, workers, project_limit=2):
    development = review['Development']
    assert development['Snapshot'] == review['Snapshot'] and development['Tick'] == review['Tick']
    assert development['Workers'] == workers and development['Capacity'] == min(project_limit, workers)
    assert len(development['Committed']) == 1, 'Accepted player project must consume optional capacity'
    rows = development['Rows'] or []
    assert len({r['Goal'] for r in rows}) == len(rows)
    for row in rows:
        assert row['Goal'] in {'MaintainWood', 'EnsureBasicDefense', 'EnsureComfort', 'EnsureExpansion', 'MaintainEquipment',
                              'MaintainFireSafety', 'SecureSupplies', 'MaintainEssentialRepairs', 'MaintainCleanFacilities', 'MaintainMedicalReserves', 'MaintainAnimalContainment', 'MaintainAnimalFeed', 'MaintainSleeping', 'MaintainHomeCoverage', 'MaintainStoneShell'}
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


async def assert_routine_running(http):
    state = await http("GET", "/api/state")
    if state.get("mode") != "automate":
        clock = await http("GET", "/api/player/clock")
        raise AssertionError(f"Routine acceptance interrupted: state={state}, clock={clock}")


async def wait_building_method(http, database, definition, count, *, shell=False, timeout=180):
    async with asyncio.timeout(600 if shell else timeout):
        while True:
            await assert_routine_running(http)
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


def audit_expansion(report, database):
    native = report['expansion_outcome']
    assert native['indoorSleepingCapacity'] >= native['colonistCount'] + 1
    assert report['expansion_recovered']['goals']['EnsureExpansion']['Need'] == 'recovered'
    # Reuse the same per-action proof: native completion, exact indoor footprint,
    # one dispatch, and unchanged player direction for the additional place.
    return audit_sleeping(report | {'sleeping_plan': report['expansion_plan'],
        'sleeping_setup': report['sleeping_setup'] | {'colonists': 1}}, database)


async def run(root, output, binary, *, go_source, go_sha256, sleeping_methods=False, cooking_methods=False, work_project=False, work_overrides=False, power_fixture=False, resource_rules=(), shelter_methods=False, start_save=None, supply_history=False, comfort_methods=False, expansion_methods=False, facility_upkeep=False, power_methods=None, temperature_methods=None):
    if facility_upkeep:
        assert shelter_methods, 'Facility upkeep requires actual autonomous shell completion'
    assert Path("/.dockerenv").is_file(), "Use the isolated scenario launcher"
    output.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "source": go_source,
              "scope": "Native core/emergency facts reach twenty-three durable Go needs; Manual invalidates them; disabled restart neither acquires authority nor reads routine facts. No routine method execution claim."}
    if temperature_methods:
        assert temperature_methods in ('cold', 'hot')
        assert not (sleeping_methods or cooking_methods or shelter_methods or comfort_methods or expansion_methods or power_fixture or power_methods or resource_rules or supply_history)
        report['scope'] = 'Go constructs one native thermal facility in the actual sleeping room, observes ordinary temperature recovery under persistently unsafe outdoor weather, and verifies Manual and disabled restart.'
    if power_methods:
        assert power_methods in ("generation", "conduit")
        assert not (sleeping_methods or cooking_methods or shelter_methods or comfort_methods or expansion_methods or power_fixture or resource_rules or supply_history)
        report["scope"] = "Go selects and completes native generation or conduit work through shared Hands, observes the consumer powered, then verifies Manual and disabled restart."
    if expansion_methods:
        assert not (sleeping_methods or cooking_methods or shelter_methods or comfort_methods or resource_rules or supply_history)
        report["scope"] = "Go expands an established foothold by one indoor sleeping place through shared Hands, observes capacity recovery, then verifies Manual and disabled restart. A separate paused fixture supplies populated bounded upkeep replay."
    if comfort_methods:
        assert not (sleeping_methods or cooking_methods or shelter_methods or work_project or work_overrides or power_fixture or resource_rules or supply_history)
        report['scope'] = 'From an established disposable foothold without comfort furniture, Go constructs a dining table, chair and recreation facility through shared Hands and observes ordinary pawn use. Manual preserves use history and disabled restart grants no authority or time.'
    if sleeping_methods:
        report["scope"] = "Reviewed indoor sleeping deficit compiles and executes through shared Hands, with native completion, Manual invalidation and disabled restart. Private fixture supplies only an empty room and healthy starting colonists."
    if shelter_methods:
        report["scope"] = "From an empty outdoor site, shared Go Hands constructs a starter shell, normal pawn work roofs it, and the same maintained goal furnishes indoor sleeping capacity. Door completion gates walls; all native outcomes, Manual and disabled restart are verified."
    if cooking_methods:
        report["scope"] = "Reviewed sleeping and cooking deficits execute ordinary building methods through shared Hands; native completion, shared authority, Manual and disabled restart. Campfire construction does not certify cooking bills or food production."
    if supply_history:
        assert not (sleeping_methods or cooking_methods or work_project or work_overrides or power_fixture or resource_rules)
        report['scope'] = 'Go retains the initial native startup-supply cohort across Manual and restart. Ordinary player Allow clears it; later Forbid on those cells does not reopen the need or cause a routine write.'
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
            if start_save:
                assert shelter_methods and Path(start_save).name == start_save
                await evidence.call(bridge, 'load-start', 'rimworld/load_game_ready', {'saveName': start_save, 'readiness': 'visual', 'timeoutMs': 120000})
            else:
                if comfort_methods or expansion_methods or power_methods or temperature_methods:
                    report['start_configuration'] = payload(await evidence.call(bridge, 'configure-start', 'test/configure_start', {
                        'scenario': 'Crashlanded', 'count': 3, 'seed': 'g01-05-comfort',
                        'minTemperature': 33 if temperature_methods == 'hot' else -100 if temperature_methods == 'cold' else 15, 'maxTemperature': 10 if temperature_methods == 'cold' else 100 if temperature_methods == 'hot' else 27}))
                    assert report['start_configuration']['success']
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
            if sleeping_methods or resource_rules or comfort_methods or expansion_methods or power_methods or temperature_methods:
                report["sleeping_setup"] = payload(await evidence.call(bridge, "sleeping-setup", "test/routine_sleeping_prepare", {"outdoorSite": shelter_methods, "captureHistory": bool(comfort_methods or expansion_methods or power_methods or temperature_methods)}))
                assert report["sleeping_setup"]["success"] and report["sleeping_setup"]["sleepingSpotsCreated"] == 0
            if comfort_methods or expansion_methods:
                report['comfort_setup'] = payload(await evidence.call(bridge, 'comfort-setup', 'test/comfort_foothold', report['sleeping_setup']['center']))
                assert report['comfort_setup']['success']
            if temperature_methods:
                report['temperature_setup'] = payload(await evidence.call(bridge, 'temperature-setup', 'test/routine_temperature_prepare', {**report['sleeping_setup']['center'], 'hot': temperature_methods == 'hot'}))
                assert report['temperature_setup']['success']
            if power_methods:
                report['power_setup'] = payload(await evidence.call(bridge, 'power-method-setup', 'test/routine_power_methods', {'connectExisting': power_methods == 'conduit'}))
                assert report['power_setup']['success']
            if comfort_methods or expansion_methods or power_methods or temperature_methods:
                from native_work_readback import work_reference
                workers = payload(await evidence.call(bridge, 'comfort-workers-before', 'home/list_pawns', {'colonistsOnly': True, 'work': True, 'bio': True, 'equipment': True, 'health': True}))
                assignments = work_reference(workers['pawns'])['assignments']
                for pawn, work in assignments.items():
                    configured = payload(await evidence.call(bridge, 'comfort-work-' + pawn, 'home/pawn_config', {'pawn': pawn, 'work': ','.join(f'{name}={priority}' for name, priority in work.items()), 'dryRun': False}))
                    assert configured['success']
            report["initial_colony"] = await wire(bridge, "initial-colony", "observations_read_status", {
                "scope": {"expectedIdentity": identity}, "colonists": True, "threats": True, "colonistDetail": False, "page": {"limit": 256}})
            if sleeping_methods or resource_rules or comfort_methods or expansion_methods or power_methods or temperature_methods:
                assert_construction_start(report['initial_colony'])
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
            legacy_work = payload(await evidence.call(bridge, "initial-work", "home/list_pawns", {"colonistsOnly": True, "work": True, "bio": True, "equipment": True, "health": True}))
            assert legacy_work['success'] and {p['thingId'] for p in legacy_work['pawns']} == set(pawn_ids)
            report['medical_care_reference'] = medical_care_reference(legacy_work['pawns'])
            minimum_construction = report['temperature_setup']['requiredConstruction'] if temperature_methods else report['power_setup']['requiredConstruction'] if power_methods else report['comfort_setup']['requiredConstruction'] if comfort_methods or expansion_methods else 0
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
            from native_go_gear_evidence import audit_gear
            gear_reference = payload(await evidence.call(bridge, 'initial-gear-reference', 'home/gear_upkeep', {'dryRun': True}))
            report['gear_reference'] = audit_gear(outcome(work_colony, 'observed'), gear_reference)
            report['initial_supply_cells'] = outcome(work_colony, 'observed').get('forbiddenSupplies', [])
            if supply_history:
                assert report['initial_supply_cells'], 'Supply-history acceptance requires original forbidden stock'
            for name, value in {'work-pawns': {'observed': detailed}, 'work-colony': work_colony,
                                'work-status': report['initial_colony'], 'work-reference': report['initial_work']}.items():
                (output / (name + '.json')).write_text(json.dumps(value), encoding='utf8')
            facts = payload(await evidence.call(bridge, "initial-food", "home/colony_facts", {"planning": False}))
            from native_go_upkeep_evidence import audit_upkeep, audit_upkeep_review, medical_reserve_reference, animal_upkeep_reference, sleeping_upkeep_reference
            report['upkeep_reference'] = audit_upkeep(outcome(work_colony, 'observed'), facts)
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
            if comfort_methods or expansion_methods:
                expected_food = 'recovered'
                assert report['initial_work']['matches'] and food['readable'] and food['runwayDays'] >= 7
            if shelter_methods:
                saved = payload(await evidence.call(bridge, 'save-start', 'rimworld/save_game', {'saveName': 'RimGovernor-shelter-start'}))
                assert saved['exists'] and Path(saved['path']).is_relative_to(profile)
                shutil.copyfile(saved['path'], output / 'initial-save.rws')
                report['initial_save_sha256'] = hashlib.sha256((output / 'initial-save.rws').read_bytes()).hexdigest()
            if power_methods:
                initial_power = await wire(bridge, "power-initial", "observations_read_colony_facts", {"scope": {"expectedIdentity": identity}, "planning": True})
                (output / "power-initial.json").write_text(json.dumps(initial_power), encoding="utf8")
            if temperature_methods:
                from native_go_temperature_evidence import sleeping_room
                rooms = await wire(bridge, 'temperature-initial-rooms', 'observations_list_rooms', {'scope': {'expectedIdentity': identity}, 'includeCells': True, 'includeBoundary': False, 'includeOutdoors': False, 'page': {'limit': 256}})
                room = sleeping_room(rooms, report['temperature_setup'])
                assert room['temperatureC'] > 32 if temperature_methods == 'hot' else room['temperatureC'] < 12
                (output / 'temperature-initial-rooms.json').write_text(json.dumps(rooms), encoding='utf8')
                (output / 'temperature-initial-colony.json').write_text(json.dumps(work_colony), encoding='utf8')
            baseline = await capture(bridge, "setup")

        async with service(private, gabs, configuration, profile, database, output / "operate", report, clock_control=True, routine_reviews=True, routine_methods=bool(sleeping_methods or comfort_methods or expansion_methods or power_methods or temperature_methods), routine_power=bool(power_methods), routine_temperature=bool(temperature_methods), routine_comfort=comfort_methods, routine_expansion=expansion_methods, routine_cooking=cooking_methods or bool(resource_rules), routine_shelter=shelter_methods, resource_rules=resource_rules) as http:
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
            active = routine_evidence(database, identity, enabled=True, expected_food_need=expected_food, allow_methods=bool(sleeping_methods or comfort_methods or expansion_methods or power_methods or temperature_methods))
            assert active["review"]["Tick"] == tick, "Review did not use the initial paused boundary"
            assert active["goals"]["CriticalMedical"]["Need"] == medical_need(report["initial_colony"])
            audit_medical_care(active, report['medical_care_reference'])
            audit_starting_supplies(active, report['initial_supply_cells'])
            if report["initial_armed"] < min(2, len(pawn_ids)):
                assert active["goals"]["EnsureBasicDefense"]["Need"] == "deficit", "Native equipment shortage did not reach routine defense need"
            report["defense_need"] = active["goals"]["EnsureBasicDefense"]["Need"]
            report['work_need'] = active['goals']['EnsureWorkAssignments']['Need']
            assert report['work_need'] == ('recovered' if report['initial_work']['matches'] else 'deficit'), 'Native work readback did not reach routine need'
            report["active_routine"] = active
            audit_upkeep_review(active, report['upkeep_reference'])
            report['development'] = audit_development(active['review'], len(report['initial_work']['assignments']))
            if comfort_methods:
                unmet = {name: goal['Need'] for name, goal in active['goals'].items() if goal['Priority'] < 3 and goal['Need'] != 'recovered'}
                assert not unmet, f'Comfort foothold prerequisites are unmet: {unmet}'
                assert any(r['Goal'] == 'EnsureComfort' and r['Selected'] for r in active['review']['Development']['Rows'])
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
            if temperature_methods:
                from native_go_temperature_evidence import wait_temperature_recovery
                assert active['goals']['EnsureTemperatureSafety']['Need'] == 'deficit'
                report['temperature_recovery'] = await wait_temperature_recovery(http, database, identity, temperature_methods)
            if power_methods:
                from native_go_power_evidence import wait_power_recovery
                report["power_recovery"] = await wait_power_recovery(http, database, identity, power_methods)
            if expansion_methods:
                rows = active['review']['Development']['Rows']
                assert [r['Goal'] for r in rows if r['Selected']] == ['EnsureExpansion']
                assert any(r['Goal'] == 'EnsureComfort' and r['Reason'] == 'method_unavailable' for r in rows)
                report['expansion_plan'] = await wait_building_method(http, database, 'SleepingSpot', 1)
                async with asyncio.timeout(120):
                    while True:
                        recovered = routine_evidence(database, identity, enabled=True, expected_food_need=expected_food, allow_methods=True)
                        if recovered['goals']['EnsureExpansion']['Need'] == 'recovered':
                            assert recovered['goals']['EnsureExpansion']['Status'] == 'satisfied'
                            report['expansion_recovered'] = recovered
                            break
                        await asyncio.sleep(.2)
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
            if comfort_methods:
                report['comfort_plans'] = {}
                for definition in ('Table1x2c', 'DiningChair', 'HorseshoesPin'):
                    # Native dining chairs require 8,000 work, unlike starter spots.
                    report['comfort_plans'][definition] = await wait_building_method(http, database, definition, 1, timeout=600)
                async with asyncio.timeout(900):
                    while True:
                        await assert_routine_running(http)
                        recovered = routine_evidence(database, identity, enabled=True, allow_methods=True)
                        if recovered['goals']['EnsureComfort']['Need'] == 'recovered':
                            assert recovered['goals']['EnsureComfort']['Status'] == 'satisfied'
                            history = recovered['review']['Comfort']
                            assert all(history[k]['Facility'] and 0 < history[k]['Tick'] <= recovered['review']['Tick'] for k in ('Dining', 'Recreation'))
                            report['comfort_recovered'] = recovered
                            break
                        await asyncio.sleep(.2)
            manual = await http("POST", "/api/player/control/manual", body={"requestId": "routine-manual", "expected": identity})
            assert manual["record"]["phase"] == "disabled" and not manual["state"]["enabled"]
            report["manual_routine"] = routine_evidence(database, identity, enabled=False, expected_food_need=expected_food, allow_methods=bool(sleeping_methods or comfort_methods or expansion_methods or power_methods or temperature_methods))
            if comfort_methods:
                assert report['manual_routine']['review']['Comfort'] == report['comfort_recovered']['review']['Comfort']
            assert not report['manual_routine']['review']['Development']['Rows']
            assert report['manual_routine']['review']['Development']['Workers'] is None

        async with bridge_session(gabs, configuration) as bridge:
            await bridge.connect()
            retained = []
            if shelter_methods or comfort_methods or expansion_methods or power_methods or temperature_methods:
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
            if temperature_methods:
                from native_go_temperature_evidence import audit_temperature_outcome
                rooms = await wire(bridge, 'temperature-outcome-rooms', 'observations_list_rooms', {'scope': {'expectedIdentity': identity}, 'includeCells': True, 'includeBoundary': False, 'includeOutdoors': False, 'page': {'limit': 256}})
                colony = await wire(bridge, 'temperature-outcome-colony', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': True})
                buildings = await wire(bridge, 'temperature-outcome-buildings', 'observations_list_buildings', {'scope': {'expectedIdentity': identity}, 'ids': [c['current'] for c in report['temperature_recovery']['claims']], 'statuses': ['built'], 'playerOnly': True, 'category': 'artificial', 'page': {'limit': 256}})
                report['temperature_outcome'] = audit_temperature_outcome(rooms, colony, buildings, report['temperature_setup'], report['temperature_recovery'], temperature_methods)
                for name, data in (('rooms', rooms), ('colony', colony), ('buildings', buildings)):
                    (output / ('temperature-outcome-' + name + '.json')).write_text(json.dumps(data), encoding='utf8')
                (output / 'temperature-methods.json').write_text(json.dumps({'mode': temperature_methods, 'setup': report['temperature_setup'], 'recovery': report['temperature_recovery'], 'outcome': report['temperature_outcome']}), encoding='utf8')
            if power_methods:
                from native_go_power_evidence import audit_power_outcome
                power_outcome = await wire(bridge, "power-outcome", "observations_read_colony_facts", {"scope": {"expectedIdentity": identity}, "planning": True})
                report["power_outcome"] = audit_power_outcome(power_outcome, report["power_setup"], report["power_recovery"], power_methods)
                (output / "power-outcome.json").write_text(json.dumps(power_outcome), encoding="utf8")
                (output / "power-methods.json").write_text(json.dumps({"mode": power_methods, "setup": report["power_setup"], "recovery": report["power_recovery"], "outcome": report["power_outcome"]}), encoding="utf8")
            if shelter_methods:
                native = outcome(await wire(bridge, 'shelter-outcome', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': True}), 'observed')
                assert int(native['indoorSleepingCapacity']) >= report['sleeping_setup']['colonists']
                report['shelter_outcome'] = native
                if facility_upkeep:
                    from native_go_facility_evidence import capture_facility_upkeep
                    report['facility_upkeep'] = await capture_facility_upkeep(bridge, wire, evidence, database, output, identity, [report['shelter_plan'], report['sleeping_plan']])
                    assert report['manual_routine']['review']['Latches']['StoneShell'], 'Completed native wooden shell did not establish upkeep history'
            if comfort_methods:
                report['comfort_fixture'] = payload(await evidence.call(bridge, 'comfort-inspect', 'test/comfort_inspect', {}))
                assert report['comfort_fixture']['TriggerCount'] == 1 and not report['comfort_fixture']['Armed']
                native = outcome(await wire(bridge, 'comfort-outcome', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': False}), 'observed')
                report['comfort_outcome'] = native
                audit_comfort_use(report['comfort_recovered'], outcome(outcome(native['upkeep'], 'observed')['comfort'], 'observed'))
            if comfort_methods or expansion_methods:
                report['upkeep_fixture'] = payload(await evidence.call(bridge, 'upkeep-setup', 'test/upkeep_setup', {'fireSize': .5, 'repairCompetition': True, 'boundedCensus': True, 'animals': True}))
                assert report['upkeep_fixture']['success']
                upkeep_colony = await wire(bridge, 'populated-upkeep', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': False})
                upkeep_legacy = payload(await evidence.call(bridge, 'populated-upkeep-reference', 'home/colony_facts', {'planning': False}))
                report['populated_upkeep_reference'] = audit_upkeep(outcome(upkeep_colony, 'observed'), upkeep_legacy)
                assert all(v['need'] == 'deficit' for v in report['populated_upkeep_reference'].values())
                (output / 'upkeep-replay.json').write_text(json.dumps({'colony': upkeep_colony, 'expected': report['populated_upkeep_reference'], 'medical': medical_reserve_reference(outcome(upkeep_colony, 'observed'), upkeep_legacy), 'animals': animal_upkeep_reference(outcome(upkeep_colony, 'observed'), upkeep_legacy), 'sleeping': sleeping_upkeep_reference(outcome(upkeep_colony, 'observed'), upkeep_legacy)}), encoding='utf8')
            if expansion_methods:
                native = outcome(await wire(bridge, 'expansion-outcome', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': False}), 'observed')
                assert native['indoorSleepingCapacity'] >= native['colonistCount'] + 1
                report['expansion_outcome'] = native
                plan = report['expansion_plan']
                assert len(plan['actions']) == 1 and plan['actions'][0]['progress']['attempt'] == '1'
                assert report['traces']['operate'].count(EXECUTE) == 2
            baseline = await capture(bridge, "restart-baseline")
        if supply_history:
            for phase, class_name in [('supplies-cleared', 'RimWorld.Designator_Unforbid'), ('supplies-reforbidden', 'RimWorld.Designator_Forbid')]:
                async with bridge_session(gabs, configuration) as bridge:
                    await bridge.connect()
                    catalog = payload(await evidence.call(bridge, phase + '-designators', 'rimworld/list_architect_designators', {'categoryId': 'Orders'}))
                    choices = [r for r in catalog.get('designators', []) if r.get('className') == class_name]
                    assert len(choices) == 1, 'Native supply designator unavailable'
                    for index, cell in enumerate(report['initial_supply_cells']):
                        await evidence.call(bridge, phase + '-player-' + str(index), 'rimworld/apply_architect_designator',
                                            {'designatorId': choices[0]['id'], **cell, 'keepSelected': False})
                    native = outcome(await wire(bridge, phase + '-native', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': False}), 'observed')
                    observed = {(c['x'], c['z']) for c in native.get('forbiddenSupplies', [])}
                    original = {(c['x'], c['z']) for c in report['initial_supply_cells']}
                    if phase == 'supplies-cleared':
                        assert not (observed & original), 'Original supplies remain forbidden after player Allow'
                    else:
                        assert original <= observed, 'Player Forbid did not restore the observed forbidden stock'
                    assert native['context']['tick'] == final['context']['tick'], 'Paused supply changes advanced simulation'
                    report[phase + '-native'] = native
                    baseline = await capture(bridge, phase + '-baseline')
                # Food availability changes with Allow/Forbid. This phase tests
                # startup ownership against the freshly read native supplies.
                expected_food = None
                async with service(private, gabs, configuration, profile, database, output / phase, report, clock_control=True, routine_reviews=True) as http:
                    await poll(http, '/api/state', lambda v: v.get('connected') and not v.get('game', {}).get('stale', True))
                    control = await http('GET', '/api/player/control')
                    assert not control['state']['enabled']
                    granted = await http('POST', '/api/player/control/acquire', body={
                        'requestId': phase + '-acquire', 'expected': identity, 'planId': submission['planId'],
                        'revision': submission['revision'], 'expectedDirection': control['record']['direction']})
                    assert granted['record']['phase'] == 'granted'
                    await wait_review(database, report['manual_routine']['review']['Revision'])
                    active = routine_evidence(database, identity, enabled=True)
                    audit_starting_supplies(active, [])
                    assert active['goals']['AllowStartingSupplies']['Status'] == 'satisfied'
                    report[phase] = active
                    await http('POST', '/api/player/control/manual', body={'requestId': phase + '-manual', 'expected': identity})
                    report['manual_routine'] = routine_evidence(database, identity, enabled=False)
                async with bridge_session(gabs, configuration) as bridge:
                    await bridge.connect()
                    await capture(bridge, phase, baseline)
                    assert EXECUTE not in report['traces'][phase], 'Supply review unexpectedly issued an operation'
                    final = outcome(await wire(bridge, phase + '-identity', 'lifecycle_read_identity', {}), 'loaded')
                    assert final['paused'] and final['context']['tick'] == native['context']['tick']
                    baseline = await capture(bridge, phase + '-restart-baseline')
        async with service(private, gabs, configuration, profile, database, output / "restart", report, clock_control=True, routine_reviews=True, routine_methods=bool(sleeping_methods or comfort_methods or expansion_methods or power_methods or temperature_methods), routine_power=bool(power_methods), routine_temperature=bool(temperature_methods), routine_comfort=comfort_methods, routine_expansion=expansion_methods, routine_cooking=cooking_methods or bool(resource_rules), routine_shelter=shelter_methods, resource_rules=resource_rules) as http:
            await poll(http, "/api/state", lambda v: v.get("connected") and not v.get("game", {}).get("stale", True))
            await asyncio.sleep(2)
            assert not (await http("GET", "/api/player/control"))["state"]["enabled"]
            assert routine_evidence(database, identity, enabled=False, expected_food_need=expected_food, allow_methods=bool(sleeping_methods or comfort_methods or expansion_methods or power_methods or temperature_methods)) == report["manual_routine"]
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
        if expansion_methods:
            report["expansion_audit"] = audit_expansion(report, database)
        if comfort_methods:
            report['comfort_audit'] = audit_comfort_methods(report, database)
        report["passed"] = True
    except BaseException as error:
        report.update(error=repr(error), traceback=traceback.format_exc())
    finally:
        if launched and all(p.get("joined") for p in report.get("service_phases", [])):
            try:
                async with bridge_session(gabs, configuration) as bridge:
                    if not report['passed'] and comfort_methods:
                        try:
                            await bridge.connect()
                            report['failure_comfort_fixture'] = payload(await evidence.call(bridge, 'failure-comfort-inspect', 'test/comfort_inspect', {}))
                            report['failure_colony'] = await wire(bridge, 'failure-comfort-colony', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': True})
                            report['failure_comfort_legacy'] = payload(await evidence.call(bridge, 'failure-comfort-legacy', 'home/colony_facts', {'planning': True}))
                            report['failure_comfort_pawns'] = payload(await evidence.call(bridge, 'failure-comfort-pawns', 'home/list_pawns', {'colonistsOnly': True, 'work': True, 'bio': True, 'health': True, 'equipment': True, 'needs': True}))
                        except BaseException as diagnostic_error:
                            report['diagnostic_error'] = repr(diagnostic_error)
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
                    if not report['passed'] and temperature_methods and 'temperature_setup' in report:
                        try:
                            await bridge.connect()
                            report['failure_temperature_rooms'] = await wire(bridge, 'failure-temperature-rooms', 'observations_list_rooms', {'scope': {'expectedIdentity': identity}, 'includeCells': True, 'includeBoundary': False, 'includeOutdoors': False, 'page': {'limit': 256}})
                            report['failure_temperature_colony'] = await wire(bridge, 'failure-temperature-colony', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': True})
                            report['failure_temperature_clock'] = await wire(bridge, 'failure-temperature-clock', 'clock_read_status', {'identity': identity})
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
    parser.add_argument("--expansion-methods", action="store_true")
    parser.add_argument("--comfort-methods", action="store_true")
    parser.add_argument("--sleeping-methods", action="store_true")
    parser.add_argument("--cooking-methods", action="store_true")
    parser.add_argument("--shelter-methods", action="store_true")
    parser.add_argument("--facility-upkeep", action="store_true", help="After autonomous shelter completion, verify owned Home/stone evidence and player Home exclusions; requires UpkeepFixture")
    parser.add_argument('--start-save', help='Repeat shelter acceptance from a retained initial save staged in the private profile')
    parser.add_argument("--work-project", action="store_true")
    parser.add_argument("--work-overrides", action="store_true")
    parser.add_argument("--power-fixture", action="store_true")
    parser.add_argument("--temperature-methods", choices=("cold", "hot"))
    parser.add_argument("--power-methods", choices=("generation", "conduit"))
    parser.add_argument("--resource-rule", action="append", default=[])
    parser.add_argument('--supply-history', action='store_true')
    args = parser.parse_args()
    raise SystemExit(0 if asyncio.run(run(args.root, args.output or args.root / "native-go-routine-acceptance", args.go_binary,
        go_source=args.go_source, go_sha256=args.go_sha256, sleeping_methods=args.sleeping_methods or args.cooking_methods or args.shelter_methods, shelter_methods=args.shelter_methods, start_save=args.start_save, cooking_methods=args.cooking_methods, work_project=args.work_project, work_overrides=args.work_overrides, power_fixture=args.power_fixture, resource_rules=args.resource_rule, supply_history=args.supply_history, comfort_methods=args.comfort_methods, expansion_methods=args.expansion_methods, facility_upkeep=args.facility_upkeep, power_methods=args.power_methods, temperature_methods=args.temperature_methods)) else 1)
