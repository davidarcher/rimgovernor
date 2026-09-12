"""Native census and deterministic disaster planning evidence, without relief orders."""
from __future__ import annotations

from math import isclose

from rimgovernor.service_recovery import pending


def disaster_reference(colony: dict, legacy: dict) -> dict:
    recovery = colony['recovery']['observed']
    assert recovery['context'] == colony['context']
    native = {b['building']['id']: b for b in recovery['buildings']}
    source = {b['thingId']: b for b in legacy['recovery']['buildings']}
    assert native.keys() == source.keys(), 'Native recovery census differs from legacy'
    for identity, row in native.items():
        old, service = source[identity], row['service']
        assert row['usesHitPoints'] == old['usesHitPoints']
        assert row['burning'] == old['burning']
        assert row['settings']['forbidden'] == old['forbidden']
        assert service['brokenDown'] == old['broken']
        if old['usesHitPoints']:
            assert row['hitPoints'] == old['hitPoints'] and row['maxHitPoints'] == old['maxHitPoints']
        if old['fuel'] is not None:
            assert isclose(service['fuel'], old['fuel'], abs_tol=1e-6)
            assert isclose(service['targetFuel'], old['fuelTarget'], abs_tol=1e-6)
            assert set(service.get('allowedFuelDefs', [])) == set(old['fuelDefs'])
    assert recovery['roofHazard'] == legacy['recovery']['roofHazard']
    assert {a['id'] for a in recovery.get('areas', [])} == {str(a['id']) for a in legacy['recovery']['areas']}
    work = pending(legacy['recovery'])
    assert work is not None
    return {'conditions': sorted((c['id'], c['defName']) for c in colony['environment']),
            'work': [{'Building': b['thingId'], 'Method': method} for b, method in work],
            'damaged': sorted({b['thingId'] for b, method in work if method != 'refuel'})}


def audit_disaster_review(active: dict, reference: dict, setup: dict) -> dict:
    history = active['review']['Disaster']
    assert history['Phase'] == 'disrupted'
    assert history['Started'] == history['Observed'] == active['review']['Tick']
    assert [(c['ID'], c['Definition']) for c in history['Conditions']] == reference['conditions']
    assert history['WorkKnown'] and history['Work'] == reference['work']
    assert history['Damaged'] == reference['damaged']
    assert {setup['wall'], setup['generator']} <= set(history['Damaged'])
    assert {'Building': setup['campfire'], 'Method': 'refuel'} in history['Work']
    goal = active['goals']['RecoverDisasterServices']
    assert goal['Need'] == 'deficit' and goal['Priority'] == 2
    assert active['goals']['MaintainWood']['Priority'] == 2
    return history
