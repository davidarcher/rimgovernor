from copy import deepcopy
from unittest.mock import AsyncMock

import pytest

from rimgovernor.colony_plan import ColonyGoal
from rimgovernor.colony_policy import ColonyPolicy
from rimgovernor.disaster_recovery import prioritize, reconcile
from test_colony_controller import Replay, facts


def stable():
    return dict(facts(), environment={'conditions': []},
                bedCapacity=3, indoorSleepingCapacity=3,
                sleepingTemperatureMin=22, sleepingTemperatureMax=22,
                farms=[dict(edible=True, growingCells=30)], foodStorage=True,
                cooking=[dict(id='stove', defName='ElectricStove', usable=True,
                              recipes=['meal'], bills=[dict(recipe='meal', suspended=False)])],
                powerRequired=True, powerHeadroom=100)


def update(control, value, context='colony:map:load'):
    return reconcile(control, value, ColonyPolicy(), context=context, direction=1)


def test_compound_failure_expiry_does_not_prove_recovery():
    control, value = {}, stable()
    assert update(control, value) is None
    value['environment']['conditions'] = [dict(defName='SolarFlare'), dict(defName='ColdSnap')]
    value.update(powerHeadroom=-100, sleepingTemperatureMin=0, foodRunwayDays=0)
    state = update(control, value)
    assert state['phase'] == 'disrupted'
    assert set(state['deficits']) == {'power', 'temperature', 'food'}
    nodes = [('ActiveCombat', 0), ('EnsureFoodStorage', 3), ('MaintainWood', 3)]
    state['deficits'].append('storage')
    assert prioritize(nodes, state) == [('ActiveCombat', 0), ('EnsureFoodStorage', 2), ('MaintainWood', 2)]
    value['environment']['conditions'] = []
    assert update(control, value)['phase'] == 'recovering'
    recovered = update(control, stable())
    assert recovered['phase'] == 'restored'
    assert set(recovered['affected_services']) == {'power', 'temperature', 'food'}
    later = stable()
    later['foodRunwayDays'] = 0
    assert update(control, later)['phase'] == 'restored'
    later['tick'] += 600
    later['environment']['conditions'] = [dict(defName='ColdSnap')]
    assert update(control, later)['started_tick'] == later['tick']


def test_temporary_survival_missing_reads_and_load_changes():
    control, value = {}, stable()
    value['environment']['conditions'] = [dict(defName='ColdSnap', permanent=True, ticksLeft=None)]
    assert update(control, value)['phase'] == 'temporary_survival'
    del value['environment']
    assert update(control, value)['phase'] == 'unknown'
    assert update(control, value, 'new-load') is None
    assert 'disaster_recovery' not in control


def test_inaccessible_stock_and_crop_loss_remain_deficits():
    control, value = {}, stable()
    value['environment']['conditions'] = [dict(defName='ColdSnap')]
    value.update(foodRunwayDays=0, farms=[dict(edible=True, growingCells=0)])
    value['resources']['WoodLog'] = 0
    state = update(control, value)
    assert {'food', 'production'} <= set(state['deficits'])
    value['environment']['conditions'] = []
    state = update(control, value)
    assert state['phase'] == 'recovering'
    assert state['stock']['WoodLog'] == 0


@pytest.mark.asyncio
async def test_unpowered_stove_uses_bounded_campfire_fallback():
    rt = Replay()
    rt.current_plan.colony_goals['EnsureCooking'] = ColonyGoal(priority_class=2)
    value = stable()
    value['cooking'][0]['usable'] = False
    value['cells'] = [dict(x=30, z=30, walkable=True, supportsLight=True,
                           occupied=False, zone=False)]
    method, actions = await rt.controller.skills.compile('EnsureCooking', value, rt.people)
    assert method == 'campfire'
    assert actions[0]['placements'][0]['def_name'] == 'Campfire'
    assert actions[0]['placements'][0]['x'] == 30
    rt.current_plan.colony_goals['EnsureCooking'].evidence['methods'] = {'campfire': ['pending']}
    assert await rt.controller.skills.compile('EnsureCooking', value, rt.people) is None


@pytest.mark.asyncio
async def test_cooking_fallback_refuses_occupied_or_zoned_cells():
    from rimgovernor.colony_skills import SkillBlocked
    rt = Replay()
    rt.current_plan.colony_goals['EnsureCooking'] = ColonyGoal(priority_class=2)
    value = stable()
    value['cooking'][0]['usable'] = False
    value['cells'] = [dict(x=30, z=30, walkable=True, supportsLight=True,
                           occupied=False, zone=True)]
    with pytest.raises(SkillBlocked, match='No legal nearby cooking fallback'):
        await rt.controller.skills.compile('EnsureCooking', value, rt.people)


@pytest.mark.asyncio
async def test_existing_unfueled_campfire_is_not_duplicated_or_given_a_bill():
    rt = Replay()
    rt.current_plan.colony_goals['EnsureCooking'] = ColonyGoal(priority_class=2)
    value = stable()
    value['cooking'][0].update(defName='Campfire', usable=False)
    assert await rt.controller.skills.compile('EnsureCooking', value, rt.people) is None


@pytest.mark.asyncio
async def test_nonheating_stove_does_not_suppress_cold_recovery():
    rt = Replay()
    rt.current_plan.colony_goals['EnsureTemperatureSafety'] = ColonyGoal(priority_class=2)
    value = stable()
    value['sleepingTemperatureMin'] = 0
    method, actions = await rt.controller.skills.compile('EnsureTemperatureSafety', value, rt.people)
    assert method == 'thermal'
    assert actions[0]['placements'][0]['def_name'] == 'Campfire'
    value['cooking'][0].update(defName='Campfire', usable=True)
    assert await rt.controller.skills.compile('EnsureTemperatureSafety', value, rt.people) is not None
    placement = actions[0]['placements'][0]
    value['cooking'][0]['position'] = dict(x=placement['x'], z=placement['z'])
    assert await rt.controller.skills.compile('EnsureTemperatureSafety', value, rt.people) is None


@pytest.mark.asyncio
async def test_manual_direction_does_not_create_disaster_work():
    rt = Replay()
    rt.facts['environment'] = {'conditions': [dict(defName='ColdSnap')]}
    rt.mode = 'manual'
    before = deepcopy(rt.current_plan.model_dump())
    await rt.controller.cycle()
    assert rt.current_plan.model_dump() == before


@pytest.mark.asyncio
async def test_recovery_releases_temporary_fuel_priority():
    rt = Replay()
    rt.facts = stable()
    rt.facts['resources']['WoodLog'] = 0
    rt.facts['sleepingTemperatureMin'] = 0
    rt.facts['environment']['conditions'] = [dict(defName='ColdSnap')]
    rt.controller.skills.compile = AsyncMock(return_value=None)
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['MaintainWood'].priority_class == 2
    rt.facts['environment']['conditions'] = []
    rt.facts['sleepingTemperatureMin'] = 22
    await rt.controller.cycle()
    assert rt.current_plan.control['disaster_recovery']['phase'] == 'restored'
    assert rt.current_plan.colony_goals['MaintainWood'].priority_class == 3
