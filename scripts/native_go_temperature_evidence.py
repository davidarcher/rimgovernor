"""Correlate thermal construction with actual sleeping-room temperature recovery."""
import asyncio
import sqlite3

from native_building_service_acceptance import outcome
from native_go_facility_evidence import completed_claims


async def wait_temperature_recovery(http, database, identity, mode):
    from native_go_clock_acceptance import routine_evidence
    from native_go_routine_acceptance import assert_routine_running
    definition = 'Campfire' if mode == 'cold' else 'PassiveCooler'
    async with asyncio.timeout(600):
        while True:
            await assert_routine_running(http)
            with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
                ids = [r[0] for r in db.execute("SELECT m.plan_id FROM goal_methods m JOIN goals g ON g.id=m.goal_id WHERE json_extract(g.payload,'$.ID') LIKE '%-EnsureTemperatureSafety' ORDER BY m.plan_id")]
            assert len(ids) <= 1, 'Thermal recovery duplicated room construction'
            plans = [await http('GET', '/api/plan?id=' + id) for id in ids]
            actions = [a for plan in plans for a in plan['actions']]
            assert all(len(plan['actions']) == 1 for plan in plans)
            assert all(a['building']['defName'] == definition for a in actions)
            review = routine_evidence(database, identity, enabled=True, allow_methods=True)
            goal = review['goals']['EnsureTemperatureSafety']
            if actions and all(a['progress']['stage'] == 'completed' for a in actions) and goal['Need'] == 'recovered':
                assert goal['Status'] == 'satisfied'
                return {'plans': plans, 'review': review, 'claims': completed_claims(database, plans)}
            await asyncio.sleep(.2)


def sleeping_room(reply, setup):
    snapshot = outcome(reply, 'observed')
    rooms = snapshot.get('rooms', [])
    count = snapshot['completeness']
    assert count['page']['complete'] and not count['page'].get('nextCursor')
    assert int(count['matched']) == int(count['returned']) == len(rooms) and int(count['unreadable']) == 0
    beds = set(setup['beds'])
    selected = [r for r in rooms if beds & {b['building']['id'] for b in r.get('beds', [])}]
    assert len(selected) == 1
    room = selected[0]
    assert {b['building']['id'] for b in room['beds']} == beds
    assert room['properRoom'] and not room['outdoors'] and not room['psychologicallyOutdoors']
    assert not room['touchesMapEdge'] and int(room['openRoofCount']) == 0
    assert int(room['cellCount']) == len(room['cells']) == 25
    assert {(c['x'], c['z']) for c in room['cells']} == {(c['x'], c['z']) for c in setup['cells']}
    return room


def audit_temperature_outcome(reply, colony_reply, buildings_reply, setup, recovery, mode):
    room = sleeping_room(reply, setup)
    snapshot = outcome(reply, 'observed')
    colony, buildings = outcome(colony_reply, 'observed'), outcome(buildings_reply, 'observed')
    assert snapshot['context'] == colony['context'] == buildings['context']
    temperature, outside = room['temperatureC'], colony['outdoorTemperatureC']
    assert 16 <= temperature <= 28, 'Construction did not restore safe room temperature'
    assert outside > 32 if mode == 'hot' else outside < 12, 'Outdoor weather recovered without thermal work'
    claims = recovery['claims']
    assert len(claims) == len(buildings['buildings']) == 1
    built = buildings['buildings'][0]
    claim = claims[0]
    assert built['building']['id'] == claim['current'] and built['status'] == 'built'
    assert built['building']['defName'] == setup['definition'] == claim['building']['defName']
    assert built['building']['position'] == {'x': claim['building']['x'], 'z': claim['building']['z']}
    assert (claim['building']['x'], claim['building']['z']) in {(c['x'], c['z']) for c in room['cells']}
    assert sum(int(q['units']) for q in room.get('contents', []) if q['defName'] == setup['definition']) == 1
    return {'room': room['id'], 'temperature': temperature, 'outdoor_temperature': outside,
            'completed': claim['current'], 'tick': snapshot['context']['tick']}
