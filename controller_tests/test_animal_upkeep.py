from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.animal_upkeep import containment_evidence, containment_method
from rimbot.colony_plan import ColonyPlan, ColonyGoal
from rimbot.colony_skills import SkillBlocked
from rimbot.colony_upkeep import upkeep_nodes
from rimbot.production_policy import required_resource_work


def facts(**changes):
    animal = dict(dict(id='Thing_Alpaca1', requiresPen=True, contained=False, suitablePen='Thing_Pen1',
                      release=False, slaughter=False), **changes)
    return dict(tick=10, upkeep=dict(version=1, tick=10, animals=[animal], errors={}))


def test_containment_uses_native_pen_membership_and_preserves_player_removal_policy():
    assert containment_evidence(facts())
    assert containment_evidence(facts(contained=True)) == []
    assert containment_evidence(facts(requiresPen=False, contained=None)) == []
    assert containment_evidence(facts(release=True)) == []
    assert containment_evidence(facts(slaughter=True)) == []
    assert containment_evidence(facts(contained=None)) is None
    f = facts()
    f['upkeep']['tick'] = 9
    assert containment_evidence(f) is None


def test_unknown_animal_census_does_not_block_essential_work_with_invented_handling_need():
    plan = ColonyPlan(colony_goals={'MaintainAnimalContainment': ColonyGoal(priority_class=3)})
    upkeep_nodes({}, plan.control)
    assert 'Handling' not in required_resource_work(plan)
    upkeep_nodes(facts(), plan.control)
    assert required_resource_work(plan)['Handling'] == 'Animals'


@pytest.mark.asyncio
async def test_existing_suitable_pen_waits_for_normal_handling_without_animal_setting_writes():
    f = facts()
    plan = ColonyPlan(colony_goals={'MaintainAnimalContainment': ColonyGoal(priority_class=3)})
    upkeep_nodes(f, plan.control)
    rt = SimpleNamespace(current_plan=plan, inspect_native=AsyncMock())
    people = [dict(work=dict(types=[dict(name='Handling', priority=1, disabled=False)]))]
    assert await containment_method(rt, f, people) is None
    assert plan.colony_goals['MaintainAnimalContainment'].evidence['waiting_for_native_pen']
    rt.inspect_native.assert_not_awaited()
    people[0]['work']['types'][0]['priority'] = 0
    with pytest.raises(SkillBlocked, match='preserve player work overrides'):
        await containment_method(rt, f, people)


@pytest.mark.asyncio
async def test_missing_pen_uses_shared_bounded_enclosure_without_releasing_or_slaughtering(monkeypatch):
    f = facts(suitablePen=None)
    plan = ColonyPlan(colony_goals={'MaintainAnimalContainment': ColonyGoal(priority_class=3)})
    upkeep_nodes(f, plan.control)
    rt = SimpleNamespace(current_plan=plan)
    helper = AsyncMock(return_value=dict(kind='build_room_shell'))
    monkeypatch.setattr('rimbot.upkeep_sites.enclosure_site', helper)
    _, actions = await containment_method(rt, f, [])
    assert actions == [dict(kind='build_room_shell')]
    assert helper.await_args.kwargs == dict(wall='Fence', door='FenceGate', empty_interior=False)
