import copy
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
try:
    from native_go_upkeep_evidence import audit_upkeep, audit_upkeep_review
finally:
    sys.path.pop(0)


def fixture():
    entity = {'id': 'item', 'defName': 'MedicineHerbal', 'mapId': 0, 'position': {'x': 1, 'z': 2}}
    item = dict(id='item', defName='MedicineHerbal', x=1, z=2, count=5, roofed=False, inStorage=False,
                forbidden=False, medicine=True, perishable=True, rotTicks=100, deteriorationRate=1.0, baseDeteriorationRate=1.0)
    wire = {'item': entity, **{k: v for k, v in item.items() if k not in ('id', 'defName', 'x', 'z')}}
    wire['count'], wire['rotTicks'] = '5', '100'
    legacy = {'success': True, 'tick': 7, 'upkeep': {'version': 1, 'tick': 7, 'items': [item], 'structures': [], 'fires': [], 'filth': []}}
    typed = {'context': {'identity': {'mapId': 0}, 'tick': '7'}, 'upkeep': {'observed': {'items': [wire]}}}
    return typed, legacy


def test_upkeep_reference_preserves_native_counts_and_false_presence():
    typed, legacy = fixture()
    result = audit_upkeep(typed, legacy)
    assert result['SecureSupplies'] == {'need': 'deficit', 'priority': 3, 'targets': ['item'], 'metric': 5}
    assert result['MaintainFireSafety']['need'] == 'recovered'
    active = {'goals': {goal: {'Need': row['need'], 'Priority': row['priority']} for goal, row in result.items()}}
    audit_upkeep_review(active, result)
    active['goals']['SecureSupplies']['Need'] = 'recovered'
    with pytest.raises(AssertionError):
        audit_upkeep_review(active, result)


def test_upkeep_reference_rejects_matching_native_sleeping_spot_sentinels():
    typed, legacy = fixture()
    legacy['upkeep']['structures'] = [dict(id='spot', defName='SleepingSpot', x=1, z=2,
        hitPoints=-1, maxHitPoints=100, home=True, repairPriority=1)]
    typed['upkeep']['observed']['structures'] = [{'building': {
        'building': {'id': 'spot', 'defName': 'SleepingSpot', 'mapId': 0, 'position': {'x': 1, 'z': 2}},
        'hitPoints': -1, 'maxHitPoints': 100}, 'home': True, 'repairPriority': 1}]
    with pytest.raises(AssertionError, match='non-damageable'):
        audit_upkeep(typed, legacy)


@pytest.mark.parametrize('mutation', ['missing', 'false', 'count', 'id', 'duplicate', 'tick', 'position', 'unavailable'])
def test_upkeep_reference_rejects_incomplete_or_changed_native_facts(mutation):
    typed, legacy = fixture()
    row = typed['upkeep']['observed']['items'][0]
    if mutation == 'missing': del row['roofed']
    if mutation == 'false': row['forbidden'] = True
    if mutation == 'count': row['count'] = '4'
    if mutation == 'id': row['item']['id'] = 'other'
    if mutation == 'duplicate': typed['upkeep']['observed']['items'].append(copy.deepcopy(row))
    if mutation == 'tick': typed['context']['tick'] = '8'
    if mutation == 'position': row['item']['position']['x'] = 2
    if mutation == 'unavailable': typed['upkeep']['observed']['issues'] = [{'field': 'items'}]
    with pytest.raises((AssertionError, KeyError)):
        audit_upkeep(typed, legacy)
