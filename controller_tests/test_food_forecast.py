from copy import deepcopy
import pytest
from rimbot.food_forecast import food_forecast, acquisition_targets
from rimbot.colony_policy import derive, ColonyPolicy, criteria
from test_colony_controller import Replay


def supply():
    return {'readable': True, 'consumers': [{'id': 'a', 'nutritionPerDay': 2},
                                           {'id': 'b', 'nutritionPerDay': 1}],
            'stocks': [{'id': 'rice', 'nutrition': 9, 'holder': None,
                        'perishable': True, 'rotTicks': 60000}]}


def test_spoilage_limits_runway_and_tracks_waste():
    result = food_forecast(supply())
    assert result['readable'] and result['runwayDays'] == 1
    assert result['usableNutrition'] == 3 and result['atRiskNutrition'] == 6


def test_eat_earliest_expiring_food_before_durable_stock():
    value = supply()
    value['stocks'].append({'id': 'pemmican', 'nutrition': 6, 'holder': None,
                            'perishable': False, 'rotTicks': None})
    result = food_forecast(value)
    assert result['runwayDays'] == 3 and result['usableNutrition'] == 9
    assert result == food_forecast(dict(value, stocks=list(reversed(value['stocks']))))


def test_private_inventory_does_not_feed_other_colonists_or_mask_deficit():
    value = supply()
    value['stocks'] = [{'id': 'pack', 'nutrition': 30, 'holder': 'a', 'perishable': False}]
    result = food_forecast(value)
    assert result['runwayDays'] == 0 and result['inventoryNutrition'] == 30
    facts = {'foodForecast': result, 'nutritionPerDay': 3, 'acquisition': []}
    _, budget = acquisition_targets(facts, 7)
    assert budget['neededNutrition'] == 7


def test_private_inventory_and_carried_food_extend_holders_runway_only():
    value = supply()
    value['stocks'] += [{'id': 'pack-a', 'nutrition': 4, 'holder': 'a', 'perishable': False},
                        {'id': 'carry-b', 'nutrition': 2, 'holder': 'b', 'perishable': False}]
    result = food_forecast(value)
    assert result['runwayDays'] == 3 and result['inventoryNutrition'] == 6


@pytest.mark.parametrize('damage', ['duplicate', 'unknown_rot', 'missing_consumer', 'negative', 'nan', 'truncated'])
def test_bad_native_data_never_certifies_runway(damage):
    value = supply()
    if damage == 'duplicate': value['stocks'].append(deepcopy(value['stocks'][0]))
    elif damage == 'unknown_rot': value['stocks'][0]['rotTicks'] = None
    elif damage == 'missing_consumer': value['stocks'][0]['holder'] = 'missing'
    elif damage == 'negative': value['consumers'][0]['nutritionPerDay'] = -1
    elif damage == 'nan': value['stocks'][0]['nutrition'] = float('nan')
    elif damage == 'truncated': value['truncated'] = True
    result = food_forecast(value)
    assert not result['readable'] and result['runwayDays'] is None
    with pytest.raises(ValueError, match='unavailable'):
        acquisition_targets({'nutritionPerDay': 3, 'foodForecast': result, 'foodNutrition': 100}, 7)


def test_derived_food_gate_uses_spoilage_runway_and_preserves_raw_reading():
    rt = Replay()
    rt.facts.update(foodSupply=supply(), foodRunwayDays=10)
    result = derive(rt.batch, rt.facts, ColonyPolicy())
    assert result['rawFoodRunwayDays'] == 10 and result['foodRunwayDays'] == 1
    assert criteria(result, ColonyPolicy())['food'] is False
    rt.facts['foodSupply'] = None
    result = derive(rt.batch, rt.facts, ColonyPolicy())
    assert result['foodRunwayDays'] is None and not criteria(result, ColonyPolicy())['food']


def test_acquisition_counts_native_nutrition_and_pending_work_without_crediting_stock():
    facts = {'nutritionPerDay': 3, 'foodNutrition': 15, 'pendingFoodNutrition': 3,
             'acquisition': [{'id': str(i), 'food': True, 'nutritionYield': 2, 'designated': False}
                             for i in range(9)]}
    selected, evidence = acquisition_targets(facts, 7)
    assert len(selected) == 2 and evidence['neededNutrition'] == 3
    assert evidence['selectedNutrition'] == 4  # One indivisible plant of overshoot.
    assert facts['foodNutrition'] == 15
    facts['pendingFoodNutrition'] = 6
    assert acquisition_targets(facts, 7)[0] == []
    facts['pendingFoodNutrition'] = 0
    assert len(acquisition_targets(facts, 7)[0]) == 3


def test_acquisition_unknown_yield_refuses_instead_of_ordering_fixed_batch():
    with pytest.raises(ValueError, match='unavailable'):
        acquisition_targets({'nutritionPerDay': 3, 'foodNutrition': 0,
                             'acquisition': [{'id': 'berry', 'food': True, 'designated': False}]}, 7)
