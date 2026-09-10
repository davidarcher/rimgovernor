from copy import deepcopy
import pytest
from test_colony_controller import Replay
from rimgovernor.colony_plan import ColonyGoal, CommitSteps, StepProgress


async def fixture(source='AUTOPILOT'):
    rt = Replay()
    goal = rt.current_plan.colony_goals['EnsureFoodSupply'] = ColonyGoal(priority_class=2, source=source)
    rt.facts.update(foodNutrition=0, nutritionPerDay=3, acquisition=[dict(
        id='Thing_Plant_Berry1', resource='RawBerries', food=True, tree=False,
        designated=False, x=20, z=20, yield_=7, nutritionYield=.35)])
    method, actions = await rt.controller.skills.compile('EnsureFoodSupply', rt.facts, rt.people)
    steps, _ = rt.controller.skills.steps('EnsureFoodSupply', method, actions, rt.facts)
    plan = rt.current_plan
    plan.commit(CommitSteps(expected_revision=plan.revision, reason='Harvest', steps=steps).decision(plan), tick=rt.facts['tick'], actor='strategist')
    goal.steps = [s.id for s in steps]
    goal.evidence['methods'][method] = goal.steps[:]
    return rt, steps[0]


@pytest.mark.parametrize('source', ['AUTOPILOT', 'PLAYER'])
async def test_regrown_plant_renews_confirmed_order_and_retains_receipt(source):
    rt, first = await fixture(source)
    plan = rt.current_plan
    plan.progress[first.id] = StepProgress(state='complete', issued={'0': {'confirmed': True, 'receipt': 'first'}})
    retained = deepcopy(plan.progress[first.id])
    rt.facts['tick'] += 60000
    method, actions = await rt.controller.skills.compile('EnsureFoodSupply', rt.facts, rt.people)
    steps, _ = rt.controller.skills.steps('EnsureFoodSupply', method, actions, rt.facts)
    assert first.action.tool == 'home/acquire_resource'
    assert steps[0].signature() == first.signature() and steps[0].id != first.id
    plan.commit(CommitSteps(expected_revision=plan.revision, reason='Regrown harvest', steps=steps).decision(plan), tick=rt.facts['tick'], actor='strategist')
    assert first.id not in {s.id for s in plan.spec.steps}
    assert plan.control['retired_steps'][first.id] == first.model_dump()
    assert plan.progress[first.id] == retained
    assert plan.progress[steps[0].id].state == 'pending'


@pytest.mark.parametrize('state,confirmed', [('pending',False), ('waiting',True), ('blocked',False), ('complete',False), ('cancelled',True)])
async def test_repeated_acquisition_never_replays_uncertain_or_cancelled_work(state, confirmed):
    rt, first = await fixture()
    plan = rt.current_plan
    plan.progress[first.id] = StepProgress(state=state, issued={'0': {'confirmed': confirmed}})
    if state == 'cancelled': plan.cancel(first.id)
    rt.facts['tick'] += 60000
    compiled = await rt.controller.skills.compile('EnsureFoodSupply', rt.facts, rt.people)
    assert compiled is None or not compiled[0].startswith('acquire-')
    repeated = first.model_copy(update={'id':'second'})
    with pytest.raises(ValueError):
        plan.commit(CommitSteps(expected_revision=plan.revision, reason='Invalid repeat', steps=[repeated]).decision(plan), tick=rt.facts['tick'], actor='strategist')


async def test_designated_plant_does_not_generate_another_harvest():
    rt, first = await fixture()
    rt.current_plan.progress[first.id] = StepProgress(state='complete', issued={'0': {'confirmed': True}})
    rt.facts['acquisition'][0]['designated'] = True
    rt.facts['tick'] += 60000
    compiled = await rt.controller.skills.compile('EnsureFoodSupply', rt.facts, rt.people)
    assert compiled is None or not compiled[0].startswith('acquire-')
