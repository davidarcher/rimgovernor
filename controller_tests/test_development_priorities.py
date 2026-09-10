from copy import deepcopy
from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyPlan, ColonyGoal, PlanStep, StepProgress
from rimbot.colony_policy import ColonyPolicy
from rimbot.development_priorities import arbitrate, committed_projects, release_admission
from test_colony_controller import Replay, roster


def scenario():
    nodes = [('EnsureFoodStorage', 3), ('EnsureBasicDefense', 3), ('MaintainWood', 3)]
    plan = ColonyPlan(colony_goals={identity: ColonyGoal(priority_class=priority)
                                 for identity, priority in nodes})
    facts = dict(tick=100, colonists=3, armed=1, foodStorage=False, resources={'WoodLog':300})
    return plan, facts, nodes


def review(plan, facts, nodes, *, workers=3, context='colony:map:load', direction=0, limit=1):
    return arbitrate(plan, facts, roster(workers), nodes,
                     ColonyPolicy(max_development_projects=limit), context=context, direction=direction)


def commitment(plan, goal_id, state='pending', *, source='AUTOPILOT', issued=None):
    step = PlanStep(id='project-'+str(len(plan.spec.steps)), title='Development', goal_id=goal_id,
                    source=source, completion_criteria='Native outcome observed',
                    action=dict(kind='native_operation', tool='home/research', arguments={}))
    plan.spec.steps.append(step)
    plan.progress[step.id] = StepProgress(state=state, issued=issued or {})
    if goal_id in plan.colony_goals:
        plan.colony_goals[goal_id].steps.append(step.id)
    return step.id


def test_deficit_order_capacity_and_explicit_reasons():
    plan, facts, nodes = scenario()
    ordered, admitted = review(plan, facts, nodes)
    assert ordered[0][0] == 'EnsureFoodStorage'
    assert admitted == {'EnsureFoodStorage'}
    assert 'capacity' in plan.colony_goals['MaintainWood'].evidence['development']['reason']
    assert plan.control['development']['completion_days'] is None
    _, admitted = review(plan, facts, nodes, workers=1, limit=8)
    assert len(admitted) == 1
    _, admitted = review(plan, facts, nodes, workers=0)
    assert not admitted
    assert 'workers' in plan.control['development']['goals']['EnsureFoodStorage']['reason']


def test_unavailable_method_yields_slot_to_next_ranked_goal_without_new_review():
    plan, facts, nodes = scenario()
    _, admitted = review(plan, facts, nodes)
    release_admission(plan, 'EnsureFoodStorage', admitted, 'Waiting for shelter')
    assert admitted == {'EnsureBasicDefense'}
    release_admission(plan, 'EnsureBasicDefense', admitted, 'No accessible weapons')
    assert admitted == {'MaintainWood'}
    assert plan.colony_goals['EnsureFoodStorage'].evidence['development']['reason'] == 'Waiting for shelter'


@pytest.mark.parametrize('state', ['pending', 'executing', 'waiting', 'blocked', 'cancelled'])
def test_capacity_retains_issued_identity_until_observed_completion(state):
    plan, facts, nodes = scenario()
    step_id = commitment(plan, 'MaintainWood', state, issued={'0':{'confirmed':False}})
    before = deepcopy(plan.progress[step_id])
    _, admitted = review(plan, facts, nodes)
    assert admitted == {'MaintainWood'}
    assert plan.progress[step_id] == before
    plan.progress[step_id].state = 'complete'
    _, admitted = review(plan, facts, nodes)
    assert admitted == {'EnsureFoodStorage'}


def test_player_commitments_reserve_capacity_without_rewriting_or_suspending_them():
    plan, facts, nodes = scenario()
    plan.colony_goals['intent-room'] = ColonyGoal(priority_class=2, source='PLAYER')
    step_id = commitment(plan, 'intent-room', source='PLAYER')
    before = deepcopy(plan.spec)
    _, admitted = review(plan, facts, nodes)
    assert admitted == {'intent-room'}
    assert [step.id for step in plan.ready()] == [step_id]
    assert plan.spec == before
    plan.progress[step_id].state = 'cancelled'
    assert not committed_projects(plan)


def test_emergencies_advisers_and_unknown_stock_cannot_admit_development():
    plan, facts, nodes = scenario()
    plan.colony_goals['ActiveCombat'] = ColonyGoal(priority_class=0)
    ordered, admitted = review(plan, facts, nodes+[('ActiveCombat',0)])
    assert ordered[0] == ('ActiveCombat',0) and not admitted
    plan.colony_goals['EnsureFoodStorage'].source = 'LLM_ADVISOR'
    plan.colony_goals['EnsureBasicDefense'].cancelled = True
    facts['resources'] = {}
    _, admitted = review(plan, facts, nodes)
    assert not admitted
    assert 'unavailable' in plan.control['development']['goals']['MaintainWood']['reason']


def test_player_goal_preference_and_age_survive_restart_without_paused_review_inflation():
    plan, facts, nodes = scenario()
    plan.colony_goals['MaintainWood'].source = 'PLAYER'
    assert review(plan, facts, nodes)[1] == {'MaintainWood'}
    snapshot = deepcopy(plan.control['development'])
    for _ in range(20):
        review(plan, facts, nodes)
    assert plan.control['development']['goals']['EnsureFoodStorage']['waiting_since'] == 100
    # Successful native work resets only the served project's waiting age.
    step_id = commitment(plan, 'MaintainWood', 'waiting')
    facts['tick'] = 600000
    review(plan, facts, nodes)
    plan.progress[step_id].state = 'complete'
    plan = ColonyPlan.model_validate_json(plan.model_dump_json())
    assert review(plan, facts, nodes)[1] == {'EnsureFoodStorage'}
    assert snapshot['tick'] == 100


@pytest.mark.parametrize('change', ['direction', 'context', 'rewind'])
def test_changed_context_discards_selection_and_wait_age(change):
    plan, facts, nodes = scenario()
    review(plan, facts, nodes)
    facts['tick'] = 10000
    review(plan, facts, nodes)
    kwargs = {}
    if change == 'rewind': facts['tick'] = 50
    else: kwargs[change] = 1 if change == 'direction' else 'other-load'
    review(plan, facts, nodes, **kwargs)
    assert all(row['waiting_since'] == facts['tick'] for row in plan.control['development']['goals'].values())


@pytest.mark.asyncio
async def test_player_promoted_goal_still_obeys_optional_node_capacity():
    rt = Replay()
    rt.current_plan.colony_goals['EnsureBasicDefense'] = ColonyGoal(priority_class=2, source='PLAYER')
    rt.batch.summary.pawns[0].armed = False
    for pawn in rt.people:
        pawn['drafted'] = True
    rt.controller.skills.compile = AsyncMock(return_value=None)
    await rt.controller.cycle()
    assert rt.current_plan.control['development']['capacity'] == 0
    assert 'EnsureBasicDefense' not in [call.args[0] for call in rt.controller.skills.compile.call_args_list]


@pytest.mark.asyncio
async def test_controller_replay_progress_and_manual_guard():
    rt = Replay()
    rt.current_plan.control['policy'] = {'max_development_projects':1}
    for _ in range(40):
        await rt.controller.cycle()
        if rt.current_plan.control.get('status') == 'FOOTHOLD_STABLE': break
        rt.labor()
    assert rt.current_plan.control['status'] == 'FOOTHOLD_STABLE'
    assert rt.current_plan.control['development']['capacity'] == 1
    rt.mode = 'manual'
    before = rt.current_plan.model_dump()
    await rt.controller.cycle()
    assert rt.current_plan.model_dump() == before


@pytest.mark.parametrize('limit', [0,9,True,1.5])
def test_invalid_capacity_is_rejected(limit):
    with pytest.raises(ValueError, match='Development'):
        ColonyPolicy(max_development_projects=limit)
