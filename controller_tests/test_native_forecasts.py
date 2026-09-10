import pytest

from rimgovernor.native_forecasts import forecasts
from rimgovernor.strategic_state import StrategicState
from test_power_forecast import batch, power_events
from types import SimpleNamespace


def facts():
    return {'nativeForecastInputs': {'readable': True, 'tick': 123, 'animalIds': ['animal'],
        'combinedFoodSupply': {'readable': True,
            'consumers': [{'id': 'person', 'nutritionPerDay': 2}, {'id': 'animal', 'nutritionPerDay': 1}],
            'stocks': [{'id': 'shared', 'nutrition': 9, 'holder': None, 'perishable': False,
                        'eaters': ['person', 'animal']}]},
        'crops': [{'sowWork': 100, 'harvestWork': 20, 'standingYield': 50}],
        'patients': [{'id': 'person', 'mood': .3, 'moodTarget': .15,
                      'minorBreakThreshold': .4, 'majorBreakThreshold': .2, 'extremeBreakThreshold': .1,
                      'hoursUntilDeathFromBloodLoss': 8}]}}


def test_animal_feed_shares_stock_with_human_demand_and_does_not_credit_harvest():
    result = forecasts(facts())
    assert result['animalFeed']['runwayDays'] == 3
    assert result['animalFeed']['nutritionPerDay'] == 1
    assert result['harvest']['guaranteedNutrition'] is None
    assert result['food']['runwayDays'] is None
    assert result['labor'] == {'sowWork': 100, 'harvestWork': 20, 'constructionWork': None, 'completionDays': None}
    assert result['people'][0]['breakRisk'] == 'minor'
    assert result['people'][0]['moodPressure'] == -.15
    assert result['people'][0]['hoursUntilDeathFromBloodLoss'] == 8


def test_absence_and_unreadability_remain_distinct():
    unknown = forecasts({})
    assert unknown['animalFeed']['nutritionPerDay'] is None
    assert unknown['people'] is None
    assert unknown['labor']['sowWork'] is None
    value = facts()
    value['nativeForecastInputs'].update(animalIds=[], crops=[], patients=[])
    observed = forecasts(value)
    assert observed['animalFeed']['nutritionPerDay'] == 0
    assert observed['animalFeed']['runwayDays'] is None
    assert observed['people'] == [] and observed['labor']['sowWork'] == 0


def test_unknown_labor_and_mood_threshold_do_not_imply_safety():
    value = facts()
    value['nativeForecastInputs']['crops'][0]['harvestWork'] = None
    value['nativeForecastInputs']['patients'][0]['minorBreakThreshold'] = None
    result = forecasts(value)
    assert result['labor']['harvestWork'] is None
    assert result['people'][0]['breakRisk'] is None


def test_colonist_runway_reserves_animal_share_without_acquiring_food_for_animals():
    value = facts()
    value['foodSupply'] = {'readable': True, 'consumers': [{'id': 'person', 'nutritionPerDay': 2}],
                          'stocks': [{'id': 'shared', 'nutrition': 9, 'holder': None, 'perishable': False}]}
    result = forecasts(value)
    assert result['food']['runwayDays'] == 3
    assert [r['id'] for r in result['food']['consumers']] == ['person']
    assert result['food']['usableNutrition'] + result['animalFeed']['consumers'][0]['usableNutrition'] == 9


def test_construction_work_uses_native_remainder_and_refuses_filtered_census():
    buildings = {'success': True, 'buildings': [{'status': 'frame', 'workLeft': 30},
                 {'status': 'blueprint', 'workLeft': 50}, {'status': 'built'}]}
    assert forecasts({}, buildings)['labor']['constructionWork'] == 80
    buildings['skipped'] = {'byRadius': 1}
    assert forecasts({}, buildings)['labor']['constructionWork'] is None
    buildings['skipped'] = {}
    buildings['buildings'][0]['workLeft'] = None
    assert forecasts({}, buildings)['labor']['constructionWork'] is None


def test_mood_risk_uses_native_pawn_threshold_and_retains_unknown_risk():
    observation = batch({})
    observation.summary.pawns = [SimpleNamespace(thing_id='p', downed=False, dead=False, bleeding=False,
        needs_tend=False, job='Wait', armed=False, mood=.38, food=.8)]
    observation.native['pawns'] = {'pawns': [{'thingId': 'p', 'needs': {'breakThresholdMinor': .4}}]}
    state = StrategicState()
    state.update(observation)
    assert state.latches['mood:p'] is True
    state.decided()
    observation.native['pawns']['pawns'][0]['needs']['breakThresholdMinor'] = None
    observation.summary.pawns[0].mood = .9
    state.update(observation)
    assert state.latches['mood:p'] is True
    assert not any(e['kind'] == 'mood.recovered' for e in state.pending)
    observation.native['pawns']['pawns'][0]['needs']['breakThresholdMinor'] = .4
    state.update(observation)
    assert state.latches['mood:p'] is False


@pytest.mark.parametrize('watts,reserve', [(float('nan'), 10), (float('inf'), 10), ('100', 10),
                                          (True, 10), (-100, -1), (-100, float('inf'))])
def test_invalid_power_cannot_clear_persisted_risk(watts, reserve):
    state = StrategicState({'latches': {'low_power': True}})
    state.update(batch({'powerNets': [{'netW': watts, 'storedWd': reserve}]}))
    assert state.latches['low_power'] is True
    assert power_events(state) == []
