import pytest

from native_go_disaster_evidence import disaster_reference, recovery_candidates


def disaster_pair():
    old = dict(thingId='wall', usesHitPoints=True, hitPoints=50, maxHitPoints=100,
               broken=False, burning=False, forbidden=False, fuel=None, fuelTarget=None, fuelDefs=None)
    legacy = {'recovery': {'success': True, 'buildings': [old], 'roofHazard': True, 'areas': [{'id': 1}]}}
    row = {'building': {'id': 'wall'}, 'usesHitPoints': True, 'hitPoints': 50, 'maxHitPoints': 100,
           'burning': False, 'settings': {'forbidden': False}, 'service': {'brokenDown': False}}
    colony = {'context': {'tick': '10'}, 'environment': [{'id': '1', 'defName': 'ToxicFallout'}],
              'recovery': {'observed': {'context': {'tick': '10'}, 'buildings': [row], 'roofHazard': True, 'areas': [{'id': '1'}]}}}
    return colony, legacy


def test_disaster_reference_uses_actual_damage_and_containment():
    colony, legacy = disaster_pair()
    assert disaster_reference(colony, legacy) == {
        'conditions': [('1', 'ToxicFallout')], 'work': [{'Building': 'wall', 'Method': 'repair'}], 'damaged': ['wall']}


@pytest.mark.parametrize('field', ['identity', 'hit_points', 'roof', 'area', 'tick'])
def test_disaster_reference_rejects_mismatched_native_census(field):
    colony, legacy = disaster_pair()
    observed = colony['recovery']['observed']
    if field == 'identity':
        observed['buildings'][0]['building']['id'] = 'another'
    elif field == 'hit_points':
        observed['buildings'][0]['hitPoints'] = 100
    elif field == 'roof':
        observed['roofHazard'] = False
    elif field == 'area':
        observed['areas'] = []
    else:
        observed['context'] = {'tick': '11'}
    with pytest.raises(AssertionError):
        disaster_reference(colony, legacy)


def recovery_pawn(identity, *, forced=False):
    return {'pawn': {'id': identity}, 'dead': False, 'downed': False, 'drafted': False,
            'job': {'playerForced': forced}, 'issues': [{'field': 'mental_state',
             'unavailable': {'reason': 'UNAVAILABLE_REASON_NOT_APPLICABLE'}}]}


def test_recovery_candidates_protect_existing_area_before_work():
    colony, _ = disaster_pair()
    colony['recovery']['observed']['restrictions'] = [{'pawn': {'id': 'pawn'}, 'areaId': 'player'}]
    work = [{'Building': 'wall', 'Method': 'repair'}]
    assert recovery_candidates(colony, [recovery_pawn('pawn')], work) == [
        {'Kind': 'roofed_area', 'Pawn': 'pawn', 'Area': '1', 'PriorArea': 'player', 'Window': 0}]
    colony['recovery']['observed']['restrictions'][0]['areaId'] = '1'
    assert recovery_candidates(colony, [recovery_pawn('pawn')], work) == [
        {'Kind': 'service_work', 'Pawn': 'pawn', 'Building': 'wall', 'Method': 'repair', 'PriorArea': '1'}]


def test_recovery_candidates_refuse_no_refuge_and_player_work():
    colony, _ = disaster_pair()
    colony['recovery']['observed']['restrictions'] = [{'pawn': {'id': 'pawn'}}]
    work = [{'Building': 'wall', 'Method': 'repair'}]
    assert recovery_candidates(colony, [recovery_pawn('pawn', forced=True)], work) == []
    colony['recovery']['observed']['areas'] = []
    assert recovery_candidates(colony, [recovery_pawn('pawn')], work) == []


def test_recovery_candidates_bound_worker_pairs():
    colony, _ = disaster_pair()
    colony['recovery']['observed']['roofHazard'] = False
    pawns = [recovery_pawn(f'pawn{i:02}') for i in reversed(range(16))]
    colony['recovery']['observed']['restrictions'] = [{'pawn': p['pawn']} for p in pawns]
    candidates = recovery_candidates(colony, pawns, [{'Building': 'wall', 'Method': 'repair'}])
    assert len(candidates) == 8 and candidates[0]['Pawn'] == 'pawn00' and candidates[-1]['Pawn'] == 'pawn07'
