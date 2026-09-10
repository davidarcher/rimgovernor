from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimgovernor.colony_plan import ColonyGoal, ColonyPlan, PlanStep, StepProgress
from rimgovernor.colony_skills import SkillBlocked
from rimgovernor.colony_upkeep import upkeep_nodes
from rimgovernor.sleeping_upkeep import sleeping_evidence, sleeping_method


def facts():
    return dict(tick=100, colonists=1, upkeep_context='load1', center=dict(x=10, z=10),
        definitions={'Bed': dict(available=True, stuff='WoodLog', restEffectiveness=1, costs={'WoodLog': 45})},
        cells=[dict(x=x, z=z, temperature=21, roofed=True, walkable=True, storageEmpty=True, zone=False)
               for x in (12, 13) for z in (10, 11)],
        upkeep=dict(version=1, tick=100, errors={}, items=[], fires=[], filth=[], structures=[],
            people=[dict(id='Thing_Human1', ownedBed=None, comfortableMin=10, comfortableMax=30)], beds=[]))


def bed(**kwargs):
    return dict(dict(id='Thing_Bed1', defName='Bed', x=10, z=10, humanlike=True, medical=False,
        prisoners=False, roofed=True, restEffectiveness=1, temperature=21, owners=[], users=[],
        accessibleTo=['Thing_Human1']), **kwargs)


def runtime(f):
    plan = ColonyPlan(colony_goals={'MaintainSleeping': ColonyGoal(priority_class=3)})
    upkeep_nodes(f, plan.control)
    return SimpleNamespace(current_plan=plan, inspect_native=AsyncMock(return_value={'success': True}))


def test_ownership_and_capacity_do_not_certify_use_and_changed_load_requires_new_evidence():
    f, control = facts(), {}
    f['upkeep']['people'][0]['ownedBed'] = 'Thing_Bed1'
    f['upkeep']['beds'] = [bed(owners=['Thing_Human1'])]
    assert sleeping_evidence(f, control)[0]['kind'] == 'use'
    f['upkeep']['beds'][0]['users'] = ['Thing_Human1']
    assert sleeping_evidence(f, control) == []
    f['upkeep']['beds'][0]['users'] = []
    assert sleeping_evidence(f, control) == []
    f['upkeep_context'] = 'load2'
    assert sleeping_evidence(f, control)[0]['kind'] == 'use'
    f['upkeep']['errors']['beds'] = 'unavailable'
    assert sleeping_evidence(f, control) is None


@pytest.mark.asyncio
async def test_reuse_empty_native_bed_with_expected_previous_assignment():
    f = facts()
    f['upkeep']['beds'] = [bed()]
    rt = runtime(f)
    _, actions = await sleeping_method(rt, f)
    assert actions[0]['tool'] == 'home/upkeep_bed'
    assert actions[0]['arguments'] == dict(pawn='Thing_Human1', bed='Thing_Bed1', previousBed='none', dryRun=False)


@pytest.mark.asyncio
@pytest.mark.parametrize('change', ['player_assignment', 'temperature', 'medical'])
async def test_preserves_player_assignment_and_unsafe_bed(change):
    f = facts()
    f['upkeep']['people'][0]['ownedBed'] = 'Thing_Floor1'
    f['upkeep']['beds'] = [bed(id='Thing_Floor1', defName='SleepingSpot', owners=['Thing_Human1'], restEffectiveness=.8), bed()]
    if change == 'temperature': f['upkeep']['beds'][0]['temperature'] = -20
    if change == 'medical': f['upkeep']['beds'][0]['medical'] = True
    rt = runtime(f)
    with pytest.raises(SkillBlocked):
        await sleeping_method(rt, f)
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize('recorded', ['Thing_Floor1', 'Thing_ReplacedFloor1', None])
async def test_controller_spot_upgrade_builds_beside_it_and_preserves_capacity(recorded):
    f = facts()
    f['upkeep']['people'][0]['ownedBed'] = 'Thing_Floor1'
    f['upkeep']['beds'] = [bed(id='Thing_Floor1', defName='SleepingSpot', owners=['Thing_Human1'], restEffectiveness=.8)]
    rt = runtime(f)
    step = PlanStep(id='floor', title='Temporary sleeping', source='AUTOPILOT', completion_criteria='Spot available',
        action=dict(kind='place_buildings', placements=[dict(def_name='SleepingSpot', x=10, z=10)]))
    rt.current_plan.spec.steps = [step]
    rt.current_plan.progress[step.id] = StepProgress(state='complete', issued={'0': dict(
        confirmed=True, outcome='placed', placed_thing_id=recorded)})
    if recorded != 'Thing_Floor1':
        with pytest.raises(SkillBlocked):
            await sleeping_method(rt, f)
        rt.inspect_native.assert_not_awaited()
        return
    async def inspect(tool, args):
        if tool == 'home/place_building':
            return dict(canPlace=True, rotations=[dict(accepted=True, blockingThings=[], occupiedCells=[
                dict(x=args['x'], z=args['z']), dict(x=args['x'], z=args['z']+1)])])
        return dict(success=True, pawns=[dict(pawn='Human1', targets=[dict(nativeReachable=True, projectedReachable=True)])])
    rt.inspect_native.side_effect = inspect
    _, actions = await sleeping_method(rt, f)
    assert len(actions) == 1 and actions[0]['kind'] == 'place_buildings'
    assert actions[0]['placements'][0]['def_name'] == 'Bed'
    assert rt.current_plan.progress['floor'].state == 'complete'
    assert rt.current_plan.spec.steps[0].action.placements[0].def_name == 'SleepingSpot'


@pytest.mark.asyncio
async def test_available_owned_bed_waits_for_natural_use_without_schedule_or_order_changes():
    f = facts()
    f['upkeep']['people'][0]['ownedBed'] = 'Thing_Bed1'
    f['upkeep']['beds'] = [bed(owners=['Thing_Human1'])]
    rt = runtime(f)
    assert await sleeping_method(rt, f) is None
    assert rt.current_plan.colony_goals['MaintainSleeping'].evidence['waiting_for_native_sleep']
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_missing_bed_research_is_an_explicit_blocker_without_placement():
    f = facts()
    f['definitions']['Bed']['available'] = False
    rt = runtime(f)
    with pytest.raises(SkillBlocked, match='required research is unavailable'):
        await sleeping_method(rt, f)
    rt.inspect_native.assert_not_awaited()
    assert rt.current_plan.colony_goals['MaintainSleeping'].evidence['sleeping_blockers']
    assert rt.current_plan.colony_goals['MaintainSleeping'].evidence['required_capabilities'] == ['Bed']
