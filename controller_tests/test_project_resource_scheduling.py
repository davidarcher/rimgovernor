from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyGoal, ColonyPlan, PlanSpec, StepProgress
from rimbot.resource_accounting import (
    ResourceShortage, execution_reservations, validate_allocations,
    validate_execution_costs,
)


def project(identity, priority, x, **kwargs):
    return dict(id=identity, title=identity, priority=priority,
        completion_criteria='Native building observed',
        action=dict(kind='place_buildings', placements=[
            dict(def_name='Wall', x=x, z=10, materials=['WoodLog'])]), **kwargs)


def plan_for(*steps):
    return ColonyPlan(spec=PlanSpec(steps=list(steps)),
        progress={step['id']: StepProgress() for step in steps},
        control={'costs': {step['id']: {'0': {'WoodLog': 5}} for step in steps}})


def preview(stock):
    return dict(canPlace=True, costList=[dict(defName='WoodLog', count=5)],
        materials={'rows': [dict(defName='WoodLog', available=stock)]})


def validate(plan, identity, stock):
    validate_execution_costs(plan, plan.progress[identity], '0', preview(stock))


def test_consumption_yields_lower_priority_budget_without_changing_intent():
    plan = plan_for(project('later', 10, 10), project('urgent', 90, 11))
    before = deepcopy(plan.model_dump())
    # Both commitments fitted at admission; production has consumed half the stock.
    validate(plan, 'urgent', 5)
    with pytest.raises(ResourceShortage) as error:
        validate(plan, 'later', 5)
    assert error.value.evidence == dict(resource='WoodLog', required=10, available=5)
    assert plan.model_dump() == before
    restored = ColonyPlan.model_validate_json(plan.model_dump_json())
    validate(restored, 'urgent', 5)
    assert [step.id for step in restored.ready()] == ['urgent', 'later']


def test_same_priority_uses_durable_plan_order():
    plan = plan_for(project('first', 50, 10), project('second', 50, 11))
    validate(plan, 'first', 5)
    with pytest.raises(ResourceShortage):
        validate(plan, 'second', 5)


@pytest.mark.parametrize('when,state', [('complete', 'complete'), ('issued', 'waiting')])
def test_dependent_budget_cannot_starve_prerequisite(when, state):
    plan = plan_for(project('furniture', 90, 11, after=[dict(step='shell', when=when)]),
                    project('shell', 10, 10))
    validate(plan, 'shell', 5)
    plan.progress['shell'].issued['0'] = dict(confirmed=True, receipt={'id': 'blueprint'})
    plan.progress['shell'].state = state
    restored = ColonyPlan.model_validate_json(plan.model_dump_json())
    validate(restored, 'furniture', 5)
    assert [step.id for step in restored.ready()] == ['furniture']
    assert restored.progress['shell'].issued == plan.progress['shell'].issued


@pytest.mark.parametrize('status', ['blocked', 'suspended', 'complete'])
def test_inactive_goal_does_not_hoard_unissued_budget(status):
    plan = plan_for(project('held', 90, 10, goal_id='goal'), project('ready', 10, 11))
    plan.colony_goals['goal'] = ColonyGoal(priority_class=2, status=status)
    validate(plan, 'ready', 5)


@pytest.mark.parametrize('state', ['pending', 'blocked', 'cancelled'])
def test_uncertain_lower_priority_write_keeps_budget(state):
    plan = plan_for(project('urgent', 90, 10), project('uncertain', 10, 11))
    plan.progress['uncertain'].state = state
    plan.progress['uncertain'].issued['0'] = dict(confirmed=False)
    with pytest.raises(ResourceShortage):
        validate(plan, 'urgent', 5)
    assert execution_reservations(plan, 'urgent') == {'WoodLog': 10}


def test_revision_cannot_release_unresolved_write_reservation():
    plan = plan_for(project('urgent', 90, 10), project('uncertain', 10, 11))
    plan.progress['uncertain'].issued['0'] = dict(confirmed=False)
    plan.spec = PlanSpec(steps=[plan.spec.steps[0]])
    restored = ColonyPlan.model_validate_json(plan.model_dump_json())
    with pytest.raises(ResourceShortage):
        validate(restored, 'urgent', 5)


def test_partial_batch_retains_remaining_budget_and_player_reserve():
    plan = plan_for(project('urgent', 90, 10), project('later', 10, 11))
    plan.control['costs']['urgent'].update({'1': {'WoodLog': 5}, '2': {'WoodLog': 5}})
    plan.progress['urgent'].issued['1'] = dict(confirmed=True)
    plan.control['resource_policy'] = {'WoodLog': {'reserve': 3, 'spending': 'normal'}}
    with pytest.raises(ResourceShortage) as error:
        validate(plan, 'urgent', 12)
    assert error.value.evidence['required'] == 13
    validate(plan, 'urgent', 13)
    plan.control['resource_policy']['WoodLog']['spending'] = 'stop'
    with pytest.raises(ValueError, match='policy'):
        validate(plan, 'urgent', 100)


async def test_new_admission_still_reserves_all_accepted_projects():
    plan = plan_for(project('accepted', 10, 10))
    spec = PlanSpec(steps=[*plan.spec.steps, project('new', 90, 11)])
    game = SimpleNamespace(invoke=AsyncMock(return_value=preview(5)))
    with pytest.raises(ValueError, match='reservation'):
        await validate_allocations(spec, plan, game)
    game.invoke.return_value = preview(10)
    assert await validate_allocations(spec, plan, game) == {'new': {'0': {'WoodLog': 5}}}


async def test_hands_dispatches_affordable_project_then_resumes_competitor_without_duplicates():
    from test_construction_recovery import fixture
    from rimbot.construction_recovery import recover_construction

    rt, stock = fixture()
    plan = rt.current_plan
    later = project('later', 10, 20, goal_id='EnsureInitialShelter', source='AUTOPILOT')
    plan.spec = PlanSpec(steps=[*plan.spec.steps, later])
    plan.progress['later'] = StepProgress()
    plan.control['costs']['later'] = {'0': {'WoodLog': 5}}

    async def native(*args, **kwargs):
        stock['wood'] -= 5
        return {'receipt': {'outcome': 'placed', 'stuff': 'WoodLog'}}

    rt.native.side_effect = native
    # Admission originally covered 20; only the first project's 15 remain.
    await rt.hands.advance(rt)
    assert plan.progress['build'].state == 'complete'
    assert plan.progress['later'].failure.code == 'construction_resources'
    assert [call.args[1]['x'] for call in rt.native.await_args_list] == [10, 11, 12]
    receipts = deepcopy(plan.progress['build'].issued)
    rt.current_plan = ColonyPlan.model_validate_json(plan.model_dump_json())
    stock['wood'] = 5
    assert await recover_construction(rt, 'later', token='load', direction=0, limit=3)
    await rt.hands.advance(rt)
    assert rt.current_plan.progress['later'].state == 'complete'
    assert rt.current_plan.progress['build'].issued == receipts
    assert [call.args[1]['x'] for call in rt.native.await_args_list] == [10, 11, 12, 20]
