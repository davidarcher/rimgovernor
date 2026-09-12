"""Native census and deterministic disaster planning evidence, without relief orders."""
from __future__ import annotations

from math import isclose

from rimgovernor.service_recovery import pending


def disaster_reference(colony: dict, legacy: dict, pawns: list[dict] | None = None) -> dict:
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
    result = {'conditions': sorted((c['id'], c['defName']) for c in colony['environment']),
            'work': [{'Building': b['thingId'], 'Method': method} for b, method in work],
            'damaged': sorted({b['thingId'] for b, method in work if method != 'refuel'})}
    if pawns is not None:
        result['candidates'] = recovery_candidates(colony, pawns, result['work'])
    return result


def recovery_candidates(colony: dict, pawns: list[dict], work: list[dict]) -> list[dict]:
    recovery = colony['recovery']['observed']
    restrictions = {r['pawn']['id']: r.get('areaId', '') for r in recovery['restrictions']}
    assert set(restrictions) == {p['pawn']['id'] for p in pawns}
    areas = sorted(a['id'] for a in recovery.get('areas', []))
    available = []
    unknown_worker = False
    for pawn in sorted(pawns, key=lambda p: p['pawn']['id']):
        mental_clear = any(i.get('field') == 'mental_state'
                           and i.get('unavailable', {}).get('reason') == 'UNAVAILABLE_REASON_NOT_APPLICABLE'
                           for i in pawn.get('issues', [])) and not pawn.get('mentalState')
        forced = pawn.get('job', {}).get('playerForced')
        blocked = any(pawn.get(k) is True for k in ('dead', 'downed', 'drafted')) or bool(pawn.get('mentalState')) or forced is True
        known = all(type(pawn.get(k)) is bool for k in ('dead', 'downed', 'drafted')) and (mental_clear or bool(pawn.get('mentalState'))) and type(forced) is bool
        unknown_worker |= not blocked and not known
        if known and not blocked:
            available.append(pawn['pawn']['id'])
    candidates = []
    if recovery['roofHazard']:
        if not areas:
            return []
        for pawn in available:
            prior = restrictions[pawn]
            if prior not in areas:
                for area in areas[:2]:
                    candidates.append({'Kind': 'roofed_area', 'Pawn': pawn, 'Area': area, 'PriorArea': prior,
                                       'Window': int(colony['context']['tick']) // 600})
        if candidates:
            return candidates[:8]
    if unknown_worker:
        return []
    for pawn in available:
        for row in work:
            candidates.append({'Kind': 'service_work', 'Pawn': pawn, **row, 'PriorArea': restrictions[pawn]})
    return candidates[:8]


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
    if 'candidates' in reference:
        recovery = active['review']['Recovery']
        selection = recovery['Selection']
        assert selection['Tick'] == active['review']['Tick']
        assert selection['Reason'] == 'native_admission_required'
        assert not recovery['Used']
        assert reference['candidates'], 'Fixture did not produce eligible recovery candidates'
        assert len(selection['Candidates']) == len(reference['candidates'])
        for actual, expected in zip(selection['Candidates'], reference['candidates'], strict=True):
            assert {key: actual[key] for key in expected} == expected
    return history
