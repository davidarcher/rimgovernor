from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyPlan, ColonyGoal
from rimbot.comfort_upkeep import comfort_evidence, comfort_method
from rimbot.development_priorities import arbitrate
from rimbot.colony_policy import ColonyPolicy
from rimbot.colony_policy import priority_nodes
from test_colony_controller import roster


def facts():
    return dict(tick=10, upkeep_context='load1', upkeep=dict(version=1, tick=10, errors={}, comfort=dict(
        people=['p'], surfaces=[dict(id='table', adjacent=[dict(x=1, z=2)])],
        dining=[dict(id='seat', accessibleTo=['p'], users=[])],
        recreation=[dict(id='pin', accessibleTo=['p'], users=[])])))


def test_native_furniture_requires_use_and_reopens_after_access_or_load_changes():
    f, control = facts(), {}
    assert all(r['kind'] == 'use' for r in comfort_evidence(f, control))
    for kind in ('dining', 'recreation'):
        f['upkeep']['comfort'][kind][0]['users'] = ['p']
    assert comfort_evidence(f, control) == []
    for kind in ('dining', 'recreation'):
        f['upkeep']['comfort'][kind][0]['users'] = []
    assert comfort_evidence(f, control) == []
    f['upkeep_context'] = 'load2'
    assert len(comfort_evidence(f, control)) == 2
    f['upkeep']['comfort']['dining'][0]['accessibleTo'] = []
    assert comfort_evidence(f, control)[0]['kind'] == 'capacity'
    f['upkeep']['errors']['comfort'] = 'read failed'
    assert comfort_evidence(f, control) is None


@pytest.mark.asyncio
async def test_seat_placement_is_limited_to_native_eating_surface_adjacency(monkeypatch):
    f = facts()
    f['upkeep']['comfort']['dining'] = []
    f['cells'] = [dict(x=1, z=2), dict(x=50, z=60)]
    plan = ColonyPlan(colony_goals={'EnsureComfort': ColonyGoal(priority_class=4)})
    f['comfortUpkeep'] = comfort_evidence(f, plan.control)
    helper = AsyncMock(return_value=dict(kind='place_buildings'))
    monkeypatch.setattr('rimbot.development.placement', helper)
    await comfort_method(SimpleNamespace(current_plan=plan), f)
    assert helper.await_args.args[1]['cells'] == [dict(x=1, z=2)]
    assert helper.await_args.args[2] == 'DiningChair'


def test_comfort_admission_waits_for_survival_and_uses_observed_deficit():
    plan = ColonyPlan(colony_goals={'EnsureComfort': ColonyGoal(priority_class=4),
                                   'EnsureFoodSupply': ColonyGoal(priority_class=2)})
    f = dict(tick=10, comfortUpkeep=[dict(id='dining')])
    args = dict(context='load', direction=0)
    nodes = [('EnsureComfort', 4), ('EnsureFoodSupply', 2)]
    assert not arbitrate(plan, f, roster(), nodes, ColonyPolicy(), **args)[1]
    assert arbitrate(plan, f, roster(), nodes[:1], ColonyPolicy(), **args)[1] == {'EnsureComfort'}


def test_survival_deficit_does_not_falsely_complete_maintained_comfort():
    from test_colony_controller import facts as colony_facts
    f = colony_facts()
    f.update(development=dict(furniture=[]), comfortUpkeep=[dict(id='dining')], hostiles=1)
    assert ('EnsureComfort', 4) in priority_nodes(f, {}, ColonyPolicy())
