"""Paused native upkeep parity and the reference needs for Go replay."""
import math

from rimgovernor.colony_upkeep import evidence, progress_metric

SECTIONS = {'fires': 'MaintainFireSafety', 'items': 'SecureSupplies',
            'structures': 'MaintainEssentialRepairs', 'filth': 'MaintainCleanFacilities'}
FIELDS = {'fires': 'fires', 'items': 'vulnerable', 'structures': 'damaged', 'filth': 'filth'}


def audit_upkeep(colony, legacy):
    assert legacy['success'] and int(legacy['tick']) == int(colony['context']['tick'])
    raw, typed = legacy['upkeep'], colony['upkeep']['observed']
    assert raw['version'] == 1 and raw['tick'] == legacy['tick']
    issues = {r['field']: r for r in typed.get('issues', [])}
    result = {}
    reference = evidence(legacy)
    for section, goal in SECTIONS.items():
        rows = raw.get(section)
        projected = typed.get(section, [])
        if section in issues:
            assert not projected
            assert rows is None or section in raw.get('errors', {}) or len(rows) > 256, 'Unexpected unavailable upkeep census'
            result[goal] = {'need': 'unknown', 'priority': 4, 'targets': None, 'metric': None}
            continue
        assert isinstance(rows, list) and section not in raw.get('errors', {})
        assert len(rows) == len(projected) <= 256
        key = {'fires': 'fire', 'items': 'item', 'structures': 'building', 'filth': 'filth'}[section]
        ref = {r['id']: r for r in rows}
        seen = set()
        for row in projected:
            entity = row[key]['building'] if section == 'structures' else row[key]
            identity = entity['id']
            assert identity in ref and identity not in seen
            seen.add(identity)
            old = ref[identity]
            assert entity['mapId'] == colony['context']['identity']['mapId']
            assert entity['position'] == {'x': old['x'], 'z': old['z']}
            bools = ('roofed', 'inStorage', 'forbidden', 'medicine', 'perishable') if section == 'items' else ('home',)
            for field in bools:
                assert type(row[field]) is bool and row[field] == old[field]
            if section == 'items':
                assert entity['defName'] == old['defName'] and int(row['count']) == old['count']
                for field in ('deteriorationRate', 'baseDeteriorationRate'):
                    assert math.isclose(row[field], old[field], rel_tol=1e-6, abs_tol=1e-7)
                assert (int(row['rotTicks']) if 'rotTicks' in row else None) == old['rotTicks']
            elif section == 'structures':
                assert entity['defName'] == old['defName']
                assert row['building']['hitPoints'] == old['hitPoints'] and row['building']['maxHitPoints'] == old['maxHitPoints']
                assert 0 <= old['hitPoints'] <= old['maxHitPoints'], 'Repair census contains a non-damageable marker or invalid hit points'
                assert row['repairPriority'] == old['repairPriority']
            elif section == 'fires':
                assert math.isclose(row['size'], old['size'], rel_tol=1e-6, abs_tol=1e-7)
            else:
                assert row['thickness'] == old['thickness'] and row.get('roomRole') == old['room']
        targets = reference[FIELDS[section]]
        assert targets is not None
        result[goal] = {'need': 'deficit' if targets else 'recovered', 'priority': 1 if section == 'fires' else 3,
                        'targets': [r['id'] for r in targets], 'metric': progress_metric(goal, targets)}
    return result


def audit_upkeep_review(active, expected):
    for goal, reference in expected.items():
        actual = active['goals'][goal]
        assert actual['Need'] == reference['need'] and actual['Priority'] == reference['priority'], (goal, actual, reference)


def medical_reserve_reference(colony, legacy):
    from rimgovernor.medical_reserves import reserve_evidence
    assert int(colony['context']['tick']) == legacy['tick']
    assert colony['colonistCount'] == legacy['colonists']
    assert {r['defName']: int(r['units']) for r in colony.get('resources', [])} == legacy['resources']
    result = {}
    for phase, active in [('initial', False), ('retained', True)]:
        control = {'medical_reserve_active': active}
        targets = reserve_evidence(legacy, control)
        result[phase] = {'known': targets is not None, 'active': control['medical_reserve_active'],
            **control.get('medical_reserve', {}),
            'replenish': None if targets is None else targets[0]['count'] if targets else 0}
    return result


def animal_upkeep_reference(colony, legacy):
    from rimgovernor.animal_upkeep import containment_evidence
    from rimgovernor.animal_feed import feed_evidence
    assert int(colony['context']['tick']) == legacy['tick']
    raw, typed = legacy['upkeep'], colony['upkeep']['observed']
    assert raw['version'] == 1 and raw['tick'] == legacy['tick']
    assert 'animals' not in raw.get('errors', {})
    assert not any(r['field'] == 'animals' for r in typed.get('issues', []))
    reference = {r['id']: r for r in raw['animals']}
    projected = typed.get('animals', [])
    assert len(projected) == len(reference) == len(raw['animals']) <= 256
    seen = set()
    for row in projected:
        entity, state = row['pawn']['pawn'], row['pawn']['animalState']
        identity = entity['id']
        assert identity in reference and identity not in seen
        seen.add(identity)
        old = reference[identity]
        assert entity['defName'] == old['defName']
        assert entity['mapId'] == colony['context']['identity']['mapId']
        assert entity['position'] == {'x': old['x'], 'z': old['z']}
        assert type(row['requiresPen']) is bool and row['requiresPen'] == old['requiresPen']
        assert row['diet'] == old['diet']
        for field in ('release', 'slaughter'):
            assert type(state[field]) is bool and state[field] == old[field]
        assert state.get('contained') == old['contained']
        if old['requiresPen']:
            assert type(state['contained']) is bool
        else:
            assert 'contained' not in state
        assert state.get('penId') == old['pen'] and row.get('suitablePenId') == old['suitablePen']
        stocks = {r['id']: r for r in old['reachableStoredFeed']}
        feed = row.get('reachableStoredFeed', [])
        assert len(feed) == len(stocks) == len(old['reachableStoredFeed'])
        stock_seen = set()
        for stock in feed:
            key = stock['item']['id']
            assert key in stocks and key not in stock_seen
            stock_seen.add(key)
            assert int(stock['count']) == stocks[key]['count']
            assert math.isclose(stock['nutrition'], stocks[key]['nutrition'], rel_tol=1e-6, abs_tol=1e-7)
            assert stock['eaterIds'] == [identity] and stock['holderId'] == ''
    containment = containment_evidence(legacy)
    assert containment is not None
    result = {'containment': [r['id'] for r in containment]}
    for phase, active in [('initial', False), ('retained', True)]:
        control = {'animal_feed_active': {identity: active for identity in reference}}
        targets = feed_evidence(legacy, control)
        assert targets is not None, 'Native animal feed forecast unavailable'
        result[phase] = [{'id': r['id'], 'runwayDays': r['runwayDays'], 'nutrition': r['count'], 'targetDays': r['targetDays']} for r in targets]
    return result
