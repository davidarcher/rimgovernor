from copy import deepcopy
from dataclasses import replace

from rimbot.colony_policy import ColonyPolicy, criteria, priority_nodes
from rimbot.food_capacity import field_target
from rimbot.capacity_growth import growth_fields
from test_capacity_growth import fixture


def test_player_target_expands_capacity_even_with_abundant_stock():
    plan, facts = fixture()
    facts.update(foodRunwayDays=40, farms=[{'edible': True, 'usableCells': 625,
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
