from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimgovernor.colony_plan import ColonyGoal, PlanStep, StepProgress
from rimgovernor.home_coverage import targets, method, guard
from test_construction_ownership import scenario


def fixture():
    plan, facts, _ = scenario()
    plan.colony_goals['MaintainHomeCoverage'] = ColonyGoal(priority_class=3)
    row = dict(id='built-wall', shape='native-shape', missing=3, excluded=0, blocker=None,
               cells=[dict(x=10, z=z) for z in (20, 21, 22)])
    facts['upkeep']['homeCoverage'] = dict(revision=12, targets=[row])
    return SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=AsyncMock(return_value=facts))), facts, row


@pytest.mark.asyncio
async def test_native_home_method_requires_exact_confirmed_construction():
    rt, facts, row = fixture()
    key, actions = await method(rt, facts)
    step = PlanStep(id='home', title='Home coverage', goal_id='MaintainHomeCoverage', source='AUTOPILOT',
        completion_criteria='Observed bounded Home cells', action=actions[0])
    assert step.action.arguments == dict(target='built-wall', shape='native-shape', revision=12)
    await guard(rt, step)
    rt.current_plan.progress['wall'].issued['0']['confirmed'] = False
    assert targets(rt.current_plan, facts) == []
    with pytest.raises(ValueError, match='ownership'):
        await guard(rt, step)


@pytest.mark.asyncio
@pytest.mark.parametrize('change', ['revision', 'shape', 'player_exclusion', 'source', 'unknown'])
async def test_home_write_rechecks_player_edits_and_native_observation(change):
    rt, facts, row = fixture()
    _, actions = await method(rt, facts)
    step = PlanStep(id='home', title='Home', source='AUTOPILOT', goal_id='MaintainHomeCoverage',
        completion_criteria='Native cells', action=actions[0])
    if change == 'revision': facts['upkeep']['homeCoverage']['revision'] += 1
    elif change == 'shape': row['shape'] = 'changed'
    elif change == 'player_exclusion': row['excluded'] = 1
    elif change == 'source': step.source = 'PLAYER'
    else: facts['upkeep']['errors']['homeCoverage'] = 'unavailable'
    with pytest.raises(ValueError): await guard(rt, step)


@pytest.mark.asyncio
async def test_player_exclusions_remain_a_visible_blocker_without_repainting():
    from rimgovernor.colony_skills import SkillBlocked
    rt, facts, row = fixture()
    row['excluded'] = 1
    assert targets(rt.current_plan, facts)[0]['count'] == 3
    with pytest.raises(SkillBlocked, match='exclusions'):
        await method(rt, facts)
    row['excluded'] = 0
    facts['upkeep']['homeCoverage']['targets'] = []
    assert targets(rt.current_plan, facts) == []
    assert await method(rt, facts) is None


def test_owned_stockpile_requires_native_id_and_unchanged_footprint():
    rt, facts, row = fixture()
    step = PlanStep(id='store', title='Store', source='AUTOPILOT', goal_id='shelter',
        completion_criteria='Native zone', action=dict(kind='create_zone', label='Protected', zone_type='stockpile',
            patches=[dict(x=10, z=20, width=1, height=3)]))
    rt.current_plan.spec.steps.append(step)
    progress = rt.current_plan.progress[step.id] = StepProgress(state='complete', issued={'0':
        dict(confirmed=True, zone_id='7')})
    row['id'] = 'stockpile:7'
    assert len(targets(rt.current_plan, facts)) == 1
    row['cells'].append(dict(x=11, z=20))
    assert 'geometry changed' in targets(rt.current_plan, facts)[0]['blocker']
    progress.issued['0'].pop('zone_id')
    assert targets(rt.current_plan, facts) == []


@pytest.mark.parametrize('change', ['stale', 'duplicate', 'partial', 'cells', 'counts'])
def test_incomplete_home_observation_cannot_prove_recovery(change):
    rt, facts, row = fixture()
    if change == 'stale': facts['upkeep']['tick'] -= 1
    elif change == 'duplicate': facts['upkeep']['homeCoverage']['targets'].append(deepcopy(row))
    elif change == 'cells': row['cells'][0].pop('x')
    elif change == 'counts': row['excluded'] = row['missing'] + 1
    else: row['missing'] = None
    assert targets(rt.current_plan, facts) is None


def test_home_tool_is_registered_as_a_guarded_write():
    from rimgovernor.bridge_game import WRITES
    assert 'home/upkeep_home' in WRITES
