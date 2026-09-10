from copy import deepcopy
from dataclasses import replace
import pytest

from rimbot.colony_policy import ColonyPolicy, criteria, priority_nodes
from rimbot.food_capacity import field_target
from rimbot.food_capacity import choose_crop
from rimbot.capacity_growth import growth_fields
from test_capacity_growth import fixture


def test_player_target_expands_capacity_even_with_abundant_stock():
    plan, facts = fixture()
    facts.update(foodRunwayDays=40, farms=[{'edible': True, 'crop':'Plant_Rice', 'usableCells': 625,
                                         'growingCells': 625}])
    policy = ColonyPolicy()
    assert field_target(facts, 7) == 625
    assert ('EnsureFoodSupply', 2) not in priority_nodes(facts, {}, policy)
    policy = replace(policy, food_target_days=14)
    assert field_target(facts, 14) == 1167
    assert ('EnsureFoodSupply', 2) in priority_nodes(facts, {}, policy)
    assert growth_fields(plan, facts, 7) == []
    assert growth_fields(plan, facts, 14)
    assert facts['foodRunwayDays'] == 40


def test_split_fields_count_together_but_unsown_cells_do_not():
    _, facts = fixture()
    facts['farms'] = [dict(edible=True, growingCells=80, usableCells=400)] * 2
    assert criteria(facts, ColonyPolicy())['production']
    facts['farms'][1] = dict(edible=False, growingCells=400, usableCells=400)
    assert not criteria(facts, ColonyPolicy())['production']


def test_unknown_native_capacity_cannot_complete_food_goal():
    _, facts = fixture()
    facts.update(foodRunwayDays=40, farms=[dict(edible=True, growingCells=2000)])
    for value in (None, 0, -1, float('nan'), float('inf'), True):
        observed = deepcopy(facts)
        observed['definitions']['Plant_Rice']['harvestNutrition'] = value
        assert field_target(observed) is None
        assert ('EnsureFoodSupply', 2) in priority_nodes(observed, {}, ColonyPolicy())


def test_crop_selection_uses_soil_and_native_climate_without_crediting_food():
    _, facts=fixture()
    facts['definitions']={
        'Plant_Rice':dict(growDays=3,harvestNutrition=.3,fertilityMin=.7,fertilitySensitivity=1),
        'Plant_Potato':dict(growDays=6,harvestNutrition=.5,fertilityMin=.7,fertilitySensitivity=.4),
        'Plant_Corn':dict(growDays=10,harvestNutrition=.9,fertilityMin=.7,fertilitySensitivity=1)}
    facts['foodClimate']={'sowingNow':True,'growingDays':60}
    assert choose_crop(facts)=='Plant_Rice'
    for cell in facts['cells']:cell['fertility']=.7
    assert choose_crop(facts)=='Plant_Potato'
    facts['foodClimate']['growingDays']=10
    assert choose_crop(facts)=='Plant_Rice'
    facts['foodClimate']['growingDaysRemaining']=5
    assert choose_crop(facts) is None
    facts['foodClimate']['growingDaysRemaining']=10
    facts['foodClimate']['sowingNow']=False
    assert choose_crop(facts) is None
    facts['foodClimate']['sowingNow']=True
    for cell in facts['cells']:cell['roofed']=True
    assert choose_crop(facts) is None


def test_mixed_crops_use_their_own_native_yields():
    from rimbot.food_capacity import field_coverage
    _,facts=fixture()
    facts['definitions']['Plant_Potato']=dict(growDays=3,harvestNutrition=.6,fertilityMin=.7)
    facts['farms']=[dict(edible=True,crop='Plant_Rice',growingCells=625),
                    dict(edible=True,crop='Plant_Potato',growingCells=313)]
    assert field_coverage(facts)==pytest.approx(2)
    facts['farms'][1]['crop']=None
    assert field_coverage(facts) is None


@pytest.mark.asyncio
async def test_crop_and_storage_goals_do_not_claim_the_same_zone_label():
    from test_colony_controller import Replay
    from rimbot.colony_plan import ColonyGoal
    rt=Replay(8)
    rt.facts['foodRunwayDays']=10
    for name in ('EnsureFoodSupply','EnsureFoodStorage'):
        rt.current_plan.colony_goals[name]=ColonyGoal(priority_class=2)
    _,farms=await rt.controller.skills.compile('EnsureFoodSupply',rt.facts,rt.people)
    _,stores=await rt.controller.skills.compile('EnsureFoodStorage',rt.facts,rt.people)
    assert {a['label'] for a in farms}.isdisjoint(a['label'] for a in stores)
