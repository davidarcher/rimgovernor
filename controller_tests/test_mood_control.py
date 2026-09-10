from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimgovernor.colony_plan import ColonyGoal, NativeOperation, Failure
from rimgovernor.colony_skills import SkillBlocked
from rimgovernor.mood_control import assess, method, outcome, priority_nodes


def pawn(**changes):
    return dict(thingId='Thing_Human1', dead=False, downed=False, drafted=False,
        jobLoadId=17, jobPlayerForced=False, schedule={'current': 'Anything'},
        needs={'mood': .2, 'breakThresholdMinor': .35, 'rest': .1, 'food': .7, 'joy': .2}, **changes)


def test_native_threshold_and_need_hysteresis():
    p = pawn()
    first = assess([p], [], {})
    assert first[p['thingId']]['active']
    assert [c['need'] for c in first[p['thingId']]['causes']] == ['rest', 'joy']
    p['needs'].update(mood=.38, rest=.4, joy=.6)
    second = assess([p], [], first)
    assert second[p['thingId']]['active']
    assert [c['need'] for c in second[p['thingId']]['causes']] == ['rest']
    p['needs'].update(mood=.6, rest=.6)
    assert not assess([p], [], second)[p['thingId']]['active']


@pytest.mark.parametrize('missing', ['pawn', 'needs', 'mood', 'threshold'])
def test_unknown_never_proves_recovery(missing):
    p = pawn()
    old = assess([p], [], {})
    rows = [p]
    if missing == 'pawn': rows = []
    elif missing == 'needs': p['needs'] = None
    else: p['needs'].pop('mood' if missing == 'mood' else 'breakThresholdMinor')
    now = assess(rows, [], old)[p['thingId']]
    assert now['active'] and not now['known']


def test_traits_use_native_threshold_not_global_constant():
    p = pawn()
    p['needs'].update(mood=.45, breakThresholdMinor=.5)
    assert assess([p], [], {})[p['thingId']]['active']
    p['needs']['breakThresholdMinor'] = .1
    assert not assess([p], [], {})[p['thingId']]['active']


def test_downward_native_thought_pressure_admits_prevention_without_a_prediction():
    p = pawn()
    p['needs']['mood'] = .5
    state = assess([p], [{'id':p['thingId'], 'moodTarget':.2}], {})[p['thingId']]
    assert state['active'] and state['pressure'] == pytest.approx(-.3)
    assert all(c['expectedMoodBenefit'] is None for c in state['causes'])


def test_greatest_native_mood_deficit_is_served_first_with_stable_identity_ties():
    people = [pawn() for _ in range(3)]
    for p, name, mood in zip(people, ['Thing_Human3', 'Thing_Human2', 'Thing_Human1'], [.3, .1, .1]):
        p['thingId'] = name
        p['needs']['mood'] = mood
    assert [p for p, _ in priority_nodes(assess(people, [], {}))] == [
        'EnsureMood-Thing_Human1', 'EnsureMood-Thing_Human2', 'EnsureMood-Thing_Human3']


def action():
    return NativeOperation(kind='native_operation', tool='home/relieve_need',
        arguments={'pawn':'Thing_Human1', 'need':'rest', 'dryRun':False}, completion='need_recovered')


def test_only_need_readback_completes():
    p = pawn()
    assert outcome(action(), [p]) == 'waiting'
    p['needs']['rest'] = None
    assert outcome(action(), [p]) == 'waiting'
    assert outcome(action(), []) == 'waiting'
    p['needs']['rest'] = .5
    assert outcome(action(), [p]) == 'complete'
    p['mentalState'] = 'Wander_Sad'
    assert isinstance(outcome(action(), [p]), Failure)


def test_receipts_cannot_be_selected_as_need_completion():
    with pytest.raises(ValueError):
        NativeOperation(kind='native_operation', tool='home/relieve_need', arguments={})
    with pytest.raises(ValueError):
        NativeOperation(kind='native_operation', tool='home/order', arguments={}, completion='need_recovered')


def runtime():
    return SimpleNamespace(current_plan=SimpleNamespace(colony_goals={'EnsureMood-Thing_Human1':ColonyGoal(priority_class=2)}),
        inspect_native=AsyncMock(return_value={'success':True}))


@pytest.mark.asyncio
async def test_compiles_ranked_cause_with_native_identity_and_completion():
    p, rt = pawn(), runtime()
    name, actions = await method(rt, 'EnsureMood-Thing_Human1', {'mood':assess([p], [], {})}, [p])
    assert name == 'rest' and actions[0]['completion'] == 'need_recovered'
    assert actions[0]['arguments']['expectedJob'] == 17
    assert actions[0]['arguments']['expectedSchedule'] == 'Anything'
    assert rt.inspect_native.call_args.args[1]['dryRun'] is True


@pytest.mark.asyncio
@pytest.mark.parametrize('field,value', [('mentalState','Wander_Sad'), ('jobPlayerForced',True), ('drafted',True), ('downed',True)])
async def test_protected_pawn_never_produces_orders(field, value):
    p, rt = pawn(), runtime()
    p[field] = value
    with pytest.raises(SkillBlocked):
        await method(rt, 'EnsureMood-Thing_Human1', {'mood':assess([p], [], {})}, [p])
    rt.inspect_native.assert_not_called()


@pytest.mark.asyncio
async def test_ineligible_first_cause_can_use_eligible_alternative():
    p, rt = pawn(), runtime()
    rt.inspect_native.side_effect = [{'success':False,'error':'Cannot sleep now'}, {'success':True}]
    name, _ = await method(rt, 'EnsureMood-Thing_Human1', {'mood':assess([p], [], {})}, [p])
    assert name == 'joy'


@pytest.mark.asyncio
async def test_native_preview_exception_retains_refusal_and_tries_other_need():
    from mcp.types import CallToolResult
    from rimgovernor.bridge import BridgeError
    p, rt = pawn(), runtime()
    payload = dict(success=False,
                   error='Current job, carried cargo or fire prevents safe interruption.')
    rt.inspect_native.side_effect = [BridgeError('games_call_tool',
        CallToolResult(content=[], structuredContent=payload, isError=True), native_tool='home/relieve_need'), {'success': True}]
    name, actions = await method(rt, 'EnsureMood-Thing_Human1', {'mood':assess([p], [], {})}, [p])
    assert name == 'joy' and len(actions) == 1
    assert rt.current_plan.colony_goals['EnsureMood-Thing_Human1'].evidence['need_preview_refusals']['rest'] == payload
    assert all(call.args[1]['dryRun'] is True for call in rt.inspect_native.await_args_list)


@pytest.mark.asyncio
@pytest.mark.parametrize('payload', [{'success':False}, {'tool':'home/relieve_need'},
    {'tool':'other/tool', 'success':False}])
async def test_unidentified_need_preview_errors_propagate(payload):
    from mcp.types import CallToolResult
    from rimgovernor.bridge import BridgeError
    p, rt = pawn(), runtime()
    rt.inspect_native.side_effect = BridgeError('games_call_tool',
        CallToolResult(content=[], structuredContent=payload, isError=True))
    with pytest.raises(BridgeError):
        await method(rt, 'EnsureMood-Thing_Human1', {'mood':assess([p], [], {})}, [p])


@pytest.mark.parametrize('mismatch', [None, 'load', 'direction', 'age'])
def test_uncertain_write_reconciles_only_fresh_owned_observations(mismatch):
    from rimgovernor.bridge_runtime import BridgeRuntime
    from rimgovernor.colony_plan import ColonyPlan, PlanSpec, PlanStep, StepProgress
    plan = ColonyPlan(spec=PlanSpec(steps=[PlanStep(id='need-test', title='Rest',
        action=action(), completion_criteria='Observed rest recovery')]))
    progress = plan.progress['need-test'] = StepProgress()
    progress.state = 'blocked'
    progress.failure = Failure(code='uncertain_write', detail='Receipt lost')
    progress.issued['0'] = dict(confirmed=False, issued_at=10, load_token='current', player_direction=0)
    p = pawn()
    p['needs']['rest'] = .6
    rt = SimpleNamespace(current_plan=plan, context_token='current', signal=lambda *args: None,
        batch=SimpleNamespace(started_at=11, native={'pawns':{'pawns':[p]}}))
    if mismatch == 'load': rt.context_token = 'new-load'
    if mismatch == 'direction': plan.control['player_direction'] = 1
    if mismatch == 'age': rt.batch.started_at = 9
    BridgeRuntime.reconcile_plan(rt)
    assert progress.state == ('blocked' if mismatch else 'complete')
    assert progress.issued['0']['confirmed'] is False  # Observation does not fabricate a receipt.


@pytest.mark.asyncio
async def test_active_break_suspends_routine_goals_without_orders_and_recovers_by_observation():
    from test_colony_controller import Replay
    rt = Replay()
    p = rt.people[0]
    p.update(needs=pawn()['needs'], mentalState='Wander_Sad')
    await rt.controller.cycle()
    goal = rt.current_plan.colony_goals['EnsureMood-'+p['thingId']]
    assert goal.status == 'blocked'
    assert 'Active mental break' in rt.current_plan.control['execution_hold']
    assert not rt.current_plan.spec.steps
    p.update(mentalState=None, needs=dict(mood=.8, breakThresholdMinor=.35, rest=.8, food=.8, joy=.8))
    await rt.controller.cycle()
    assert goal.status == 'complete'
    assert 'execution_hold' not in rt.current_plan.control


@pytest.mark.asyncio
async def test_unchanged_blocker_rechecks_only_on_bounded_native_tick_window():
    from test_colony_controller import Replay
    rt = Replay()
    p = rt.people[0]
    p['needs'] = pawn()['needs']
    rt.controller.skills.compile = AsyncMock(side_effect=SkillBlocked('Fixture has no eligible resource'))
    identity = 'EnsureMood-'+p['thingId']
    def calls():
        return sum(call.args[0] == identity for call in rt.controller.skills.compile.await_args_list)
    await rt.controller.cycle()
    assert calls() == 1
    await rt.controller.cycle()
    assert calls() == 1
    rt.facts['tick'] += 2500
    await rt.controller.cycle()
    assert calls() == 2
    assert not rt.current_plan.spec.steps
