"""Join actual completed Go construction to same-tick native upkeep evidence."""
import json
import math
import sqlite3

from native_building_service_acceptance import payload, outcome


def completed_claims(database, plans):
    claims = []
    with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
        for plan in plans:
            goal = json.loads(db.execute('SELECT g.payload FROM goal_methods m JOIN goals g ON g.id=m.goal_id WHERE m.plan_id=?', (plan['id'],)).fetchone()[0])
            assert goal['Source'] == 'autopilot' and goal['Status'] != 'cancelled'
            for action in plan['actions']:
                progress = action['progress']
                assert progress['stage'] == progress['effect'] == 'completed' and progress['attempt'] == '1' and not progress['unresolved']
                events = [json.loads(r[0]) for r in db.execute('SELECT payload FROM transitions WHERE action_id=? ORDER BY sequence', (action['id'],))]
                assert sum(r['Kind'] == 'dispatch' for r in events) == 1
                completed = [r['Observation'] for r in events if r['Kind'] == 'observe' and r['Observation']['Effect'] == 'completed']
                assert len(completed) == 1
                proof = completed[0]
                assert proof['Causality'] == 'after_dispatch' and proof['Attempt'] == 1
                identity = proof['Construction']
                assert identity['Origin'] and identity['Current'], 'Native completion identity was discarded'
                claims.append({'plan': plan['id'], 'action': action['id'], 'origin': identity['Origin'], 'current': identity['Current'], 'building': action['building']})
    assert 1 <= len(claims) <= 256
    assert len({r['current'] for r in claims}) == len(claims)
    return sorted(claims, key=lambda r: r['current'])


def audit_facility_facts(colony, legacy, buildings, claims):
    assert legacy['success'] and int(legacy['tick']) == int(colony['context']['tick'])
    assert buildings['context'] == colony['context'], 'Ownership query escaped the native observation tick'
    current = {r['building']['id']: r for r in buildings.get('buildings', [])}
    assert len(current) == len(claims) == len(buildings.get('buildings', []))
    counts = buildings['completeness']
    assert counts['page']['complete'] and not counts['page'].get('nextCursor')
    assert int(counts['matched']) == int(counts['returned']) == len(current) and int(counts['unreadable']) == 0
    for claim in claims:
        row, expected = current[claim['current']], claim['building']
        assert row['status'] == 'built' and row['building']['defName'] == expected['defName']
        assert row['building']['position'] == {'x': expected['x'], 'z': expected['z']}
        assert row['rotation'] in ('North', 'East', 'South', 'West')
        assert row['rotation'].lower() == expected['rotation'] and row.get('stuff', '') == expected.get('stuff', '')
        assert row['building']['mapId'] == colony['context']['identity']['mapId']
    raw, typed = legacy['upkeep'], colony['upkeep']['observed']
    assert raw['version'] == 1 and raw['tick'] == legacy['tick']
    assert not ({'homeCoverage', 'structures'} & set(raw.get('errors', {})))
    assert not ({'home_coverage', 'structures'} & {r['field'] for r in typed.get('issues', [])})
    old, home = raw['homeCoverage'], typed['homeCoverage']['observed']
    assert int(home['revision']) == old['revision']
    rows = {r['id']: r for r in home.get('targets', [])}
    assert len(rows) == len(old['targets']) == len(home.get('targets', []))
    for row in old['targets']:
        projected = rows[row['id']]
        assert projected.get('shapeToken') == row['shape']
        assert projected.get('missingCells') == row['missing'] and projected.get('excludedCells') == row['excluded']
        assert projected.get('cells', []) == (row['cells'] or [])
    structures = {r['building']['building']['id']: r for r in typed.get('structures', [])}
    assert len(structures) == len(raw['structures'])
    for row in raw['structures']:
        assert math.isclose(structures[row['id']]['flammability'], row['flammability'], rel_tol=1e-6, abs_tol=1e-7)
    owned = {r['current'] for r in claims}
    home_targets = sorted(r['id'] for r in old['targets'] if r['id'] in owned and r['missing'])
    stone_targets = sorted(r['id'] for r in raw['structures'] if r['id'] in owned and r['defName'] == 'Wall' and r['flammability'] > 0)
    return {'home': home_targets, 'stone': stone_targets, 'owned': sorted(owned)}


async def capture_facility_upkeep(bridge, wire, evidence, database, output, identity, plans):
    claims = completed_claims(database, plans)
    walls = [r for r in claims if r['building']['defName'] == 'Wall']
    assert len(walls) == 31, 'Facility acceptance needs the actual completed autonomous shell'
    target = walls[0]['current']
    before = payload(await evidence.call(bridge, 'facility-home-before', 'test/home_coverage_read', {'target': target}))
    assert before['success'] and before['covered'] > 0
    removed = payload(await evidence.call(bridge, 'facility-home-player-remove', 'test/home_player_remove', {'target': target}))
    assert removed['success']
    after = payload(await evidence.call(bridge, 'facility-home-after', 'test/home_coverage_read', {'target': target}))
    assert after['covered'] == before['covered'] - 1 and after['outsideHome'] == before['outsideHome'] and after['excluded'] >= 1
    colony = await wire(bridge, 'facility-colony', 'observations_read_colony_facts', {'scope': {'expectedIdentity': identity}, 'planning': False})
    legacy = payload(await evidence.call(bridge, 'facility-legacy', 'home/colony_facts', {'planning': False}))
    ids = [r['current'] for r in claims]
    buildings = await wire(bridge, 'facility-buildings', 'observations_list_buildings', {'scope': {'expectedIdentity': identity}, 'ids': ids, 'statuses': ['built'], 'playerOnly': True, 'category': 'artificial', 'page': {'limit': len(ids)}})
    expected = audit_facility_facts(outcome(colony, 'observed'), legacy, outcome(buildings, 'observed'), claims)
    assert target in expected['home'] and len(expected['stone']) == 31
    row = next(r for r in outcome(colony, 'observed')['upkeep']['observed']['homeCoverage']['observed']['targets'] if r['id'] == target)
    assert row['excludedCells'] >= 1
    # Backup the joined service journal: replay must derive real ownership from
    # retained native transitions, never from invented fixture receipts.
    with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as source:
        with sqlite3.connect(output / 'facility-journal.sqlite') as destination:
            source.backup(destination)
    (output / 'facility-replay.json').write_text(json.dumps({'colony': colony, 'buildings': buildings, 'ids': ids, 'expected': expected}), encoding='utf8')
    return {'claims': claims, 'expected': expected, 'home_before': before, 'player_removal': removed, 'home_after': after}
