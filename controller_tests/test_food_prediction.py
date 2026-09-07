import pytest
from rimbot.food_access import runway
from rimbot.harvest_forecast import forecast


def access(pools, demand=(1., 1.)):
    return {'observed_tick': 10, 'consumers': [{'pawn_id': i+1, 'nutrition_per_day': n} for i, n in enumerate(demand)],
            'pools': [{'pawn_ids': ids, 'nutrition': n} for ids, n in pools]}


def test_shared_meals_are_not_counted_once_per_pawn():
    assert runway(access([([1, 2], 10)]))['food_runway_days'] == 5


def test_restricted_diet_limits_whole_colony_coverage():
    # Plenty of meat for one pawn cannot feed the other pawn.
    assert runway(access([([1], 100), ([1, 2], 2)]))['food_runway_days'] == 2
    assert runway(access([([1], 100)]))['food_runway_days'] == 0


def test_allocation_can_redirect_shared_food_to_restricted_consumer():
    assert runway(access([([1, 2], 4), ([1], 4)]))['food_runway_days'] == 4
    assert runway(access([([1, 2], 9)], demand=(1., 2.)))['food_runway_days'] == 3


def test_no_food_is_zero_but_missing_observation_is_unknown():
    assert runway(access([]))['food_runway_days'] == 0
    assert runway(None)['food_runway_days'] is None
    assert runway(access([], demand=(0.,)))['food_runway_days'] is None


@pytest.mark.parametrize('bad', [float('nan'), float('inf'), -1, True])
def test_invalid_native_amounts_do_not_produce_forecasts(bad):
    assert runway(access([([1, 2], bad)]))['food_runway_days'] is None


def crops(tick, remaining, count=10, infected=0):
    return {'game': {'game_tick': tick}, 'farm': {'forecast_basis': 'ideal_growth_days_v1', 'crop_types': [
        {'plant_def_name': 'Crop', 'total_plants': count, 'days_until_harvest': remaining,
         'harvestable_plants': 0, 'expected_yield': 0, 'infected_count': infected}]}}


def test_harvest_projection_includes_full_observed_day():
    memory = {}
    for tick in range(0, 60001, 2500):
        result = forecast(memory, crops(tick, 3-tick/60000*.5))
        if tick < 60000:
            assert result['crops'][0]['mean_calendar_days_remaining'] is None
    assert result['crops'][0]['mean_calendar_days_remaining'] == 5


@pytest.mark.parametrize('change', ['population', 'replant', 'rewind', 'gap', 'blight'])
def test_changed_crops_invalidate_harvest_prediction(change):
    memory = {}
    for tick in range(0, 60001, 2500):
        forecast(memory, crops(tick, 3-tick/60000*.5))
    obs = crops(62500, 2.4)
    if change == 'population': obs = crops(62500, 2.4, count=11)
    if change == 'replant': obs = crops(62500, 3.)
    if change == 'rewind': obs = crops(0, 2.4)
    if change == 'gap': obs = crops(90000, 2.4)
    if change == 'blight': obs = crops(62500, 2.4, infected=1)
    assert forecast(memory, obs)['crops'][0]['mean_calendar_days_remaining'] is None


def test_empty_zone_and_old_native_summary_do_not_promise_harvest():
    assert forecast({}, crops(0, 0, count=0))['crops'][0]['mean_calendar_days_remaining'] is None
    assert 'unavailable' in forecast({}, {'farm': {'crop_types': []}})


def test_access_shortfall_wakes_survival_without_waiting_for_stock_trend():
    from types import SimpleNamespace
    from unittest.mock import Mock
    from rimbot.world_model import update
    obs = {'game': {'game_tick': 10}, 'resources': {'critical_resources': {
        'food_summary': {'total_nutrition': 100, 'access': access([])}}}}
    rt = SimpleNamespace(memory={}, observation=obs, note=Mock())
    events = update(rt)
    risk = next(e for e in events if e['data']['risk'] == 'food_access_shortfall')
    assert risk['roles'] == ['Survival']
    assert rt.memory['world_facts']['food']['food_runway_days'] == 0
    assert update(rt) == []
    obs['resources']['critical_resources']['food_summary'].pop('access')
    obs['game']['game_tick'] = 1000
    assert update(rt) == []
    assert rt.memory['risk_state']['food_access_shortfall']['active']
