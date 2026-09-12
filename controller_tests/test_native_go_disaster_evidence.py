import pytest

from native_go_disaster_evidence import disaster_reference


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
