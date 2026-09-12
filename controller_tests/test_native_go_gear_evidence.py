import copy
from pathlib import Path
import sys

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
try:
    from native_go_gear_evidence import audit_gear
finally:
    sys.path.pop(0)


def fixture():
    context = {'identity': {'mapId': 0}, 'tick': '10'}
    row = {'pawn': {'id': 'pawn'}, 'snapshot': {'context': context, 'entityId': 'pawn', 'token': 'loadout'},
           'deficit': False, 'completeness': {'page': {'complete': True}, 'returned': '1'},
           'candidates': [{'item': {'thing': {'id': 'item', 'defName': 'Shirt'}, 'apparel': True, 'weapon': False}, 'gain': 1.2}]}
    colony = {'context': context, 'colonistCount': 1, 'planning': {'observed': {'gear': {
        'context': context, 'pawns': [row], 'completeness': {'page': {'complete': True}, 'returned': '1'}}}}}
    legacy = {'success': True, 'tick': 10, 'mapId': 0, 'pawns': [{'pawn': 'pawn', 'loadout': 'loadout', 'deficit': False,
        'candidates': [{'target': 'item', 'gear': {'defName': 'Shirt'}, 'kind': 'apparel', 'gain': 1.2}]}]}
    return colony, legacy


def test_native_gear_parity_preserves_false_and_exact_candidate_census():
    colony, legacy = fixture()
    assert audit_gear(colony, legacy) == {'pawns': 1, 'deficits': 0, 'blocked': 0, 'candidates': 1, 'same_tick_native_parity': True}
    for mutation in ('missing', 'false', 'candidate', 'gain', 'token', 'tick', 'needs'):
        changed = copy.deepcopy(colony)
        row = changed['planning']['observed']['gear']['pawns'][0]
        if mutation == 'missing': del row['deficit']
        if mutation == 'false': row['deficit'] = True
        if mutation == 'candidate': row['candidates'].clear()
        if mutation == 'gain': row['candidates'][0]['gain'] = 2
        if mutation == 'token': row['snapshot']['token'] = 'changed'
        if mutation == 'tick': changed['context']['tick'] = '11'
        if mutation == 'needs': row['replacementNeeds'] = [{'defName': 'Shirt', 'reason': 'worn'}]
        with pytest.raises((AssertionError, KeyError)):
            audit_gear(changed, legacy)
