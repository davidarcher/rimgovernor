"""Observed native consumer recovery, correlated with completed shared methods."""
import asyncio
import sqlite3

from native_building_service_acceptance import outcome
from native_go_facility_evidence import completed_claims


async def wait_power_recovery(http, database, identity, mode):
    from native_go_clock_acceptance import routine_evidence
    from native_go_routine_acceptance import assert_routine_running
    async with asyncio.timeout(600):
        while True:
            await assert_routine_running(http)
            with sqlite3.connect(database.as_uri() + '?mode=ro', uri=True) as db:
                ids = [r[0] for r in db.execute("SELECT m.plan_id FROM goal_methods m JOIN goals g ON g.id=m.goal_id WHERE json_extract(g.payload,'$.ID') LIKE '%-EnsureBasicPower' ORDER BY m.plan_id")]
            assert len(ids) <= 4, 'Power recovery exceeded bounded method count'
            plans = [await http('GET', '/api/plan?id=' + id) for id in ids]
            actions = [a for p in plans for a in p['actions']]
            assert all(1 <= len(p['actions']) <= 8 for p in plans)
            assert all(a['building']['defName'] in ('WoodFiredGenerator', 'PowerConduit') for a in actions)
            generators = [a for a in actions if a['building']['defName'] == 'WoodFiredGenerator']
            assert len(generators) <= (1 if mode == 'generation' else 0), 'Duplicated or unnecessary generation'
            review = routine_evidence(database, identity, enabled=True, allow_methods=True)
            if actions and all(a['progress']['stage'] == 'completed' for a in actions) and review['goals']['EnsureBasicPower']['Need'] == 'recovered':
                assert review['goals']['EnsureBasicPower']['Status'] == 'satisfied'
                assert len(generators) == (1 if mode == 'generation' else 0)
                return {'plans': plans, 'review': review, 'claims': completed_claims(database, plans)}
            await asyncio.sleep(.2)


def audit_power_outcome(reply, setup, recovery, mode):
    colony = outcome(reply, 'observed')
    development = colony['development']['observed']
    rows = development.get('power', [])
    counts = development['completeness']
    assert counts['page']['complete'] and not counts['page'].get('nextCursor')
    assert int(counts['matched']) == int(counts['returned']) == len(rows) + len(development.get('furniture', []))
    assert int(counts['unreadable']) == 0
    consumers = [r for r in rows if r['baseW'] < 0]
    producers = [r for r in rows if r['baseW'] > 0]
    assert len(consumers) == len(producers) == 1
    consumer, producer = consumers[0]['building'], producers[0]['building']
    assert consumer['building']['id'] == setup['consumer']
    assert all(b['service']['connected'] and b['service']['powerOn'] for b in (consumer, producer))
    assert producer['service']['powerOutputW'] > 0
    assert consumer['service']['powerNetId'] == producer['service']['powerNetId']
    assert sum(r['building']['service']['powerOutputW'] for r in rows) >= 0
    claims = {c['current']: c for c in recovery['claims']}
    if mode == 'generation':
        assert producer['building']['id'] in claims
    else:
        assert producer['building']['id'] == setup['generator']
    native = {r['building']['id']: r['building'] for r in development.get('furniture', [])}
    native.update({r['building']['building']['id']: r['building']['building'] for r in rows})
    for id, claim in claims.items():
        b = claim['building']
        assert native[id]['defName'] == b['defName']
        assert native[id]['position'] == {'x': b['x'], 'z': b['z']}
    return {'consumer': setup['consumer'], 'producer': producer['building']['id'],
            'completed': sorted(claims), 'network': consumer['service']['powerNetId'],
            'tick': colony['context']['tick']}
