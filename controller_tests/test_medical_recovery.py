import asyncio
from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock
import pytest
from rimbot.colony_plan import ColonyPlan, ColonyGoal, PlanSpec, StepProgress, Failure
from rimbot.medical_recovery import recover_treatment
from rimbot.bridge_runtime import BridgeRuntime


def fixture():
    plan = ColonyPlan(spec=PlanSpec(steps=[dict(id='tend', title='Treat patient', goal_id='CriticalMedical',
        source='AUTOPILOT', completion_criteria='Patient no longer needs tending',
        action=dict(kind='native_operation', tool='home/order', completion='patient_tended',
                    arguments={'action':'tend','pawn':'Thing_Doctor','target':'Thing_Patient'}))]),
        colony_goals={'CriticalMedical': ColonyGoal(priority_class=1, status='blocked', steps=['tend'])},
        progress={'tend': StepProgress(state='blocked', issued={'0':dict(confirmed=True,load_token='load',issued_tick=100,player_direction=0,order_generation=0,patient_order_generation=0)},
            failure=Failure(code='tending_interrupted',detail='Interrupted',retryable=True))})
    people = [dict(thingId='Thing_Patient',dead=False,orderGeneration=0,health={'needsTend':True}),
        dict(thingId='Thing_Doctor',dead=False,downed=False,drafted=False,orderGeneration=0,job='Wait',jobTargetA='Thing_Patient',
             work={'types':[dict(name='Doctor',disabled=False)]})]
    return plan, people


def recover(plan, people, **overrides):
    return recover_treatment(plan,plan.spec.steps[0],people,**dict(token='load',tick=200,owners={},limit=3,**overrides))


def test_recovery_preserves_identity_and_receipts_and_survives_reload_with_a_bound():
    plan, people = fixture()
    original = deepcopy(plan.progress['tend'].issued)
    for attempt in range(3):
        assert 'Retrying' in recover(plan,people)
        progress = plan.progress['tend']
        assert progress.state == 'pending' and progress.issued == {}
        assert len(progress.recovery_history) == attempt + 1
        assert progress.recovery_history[-1]['issued'] == original
        assert [s.id for s in plan.spec.steps] == ['tend']
        progress.issued = deepcopy(original)
        progress.state = 'blocked'
        progress.failure = Failure(code='tending_interrupted',detail='Interrupted again',retryable=True)
        plan = ColonyPlan.model_validate_json(plan.model_dump_json())
    assert 'limit reached' in recover(plan,people)
    assert plan.progress['tend'].state == 'blocked'
    assert len(plan.progress['tend'].recovery_history) == 3


@pytest.mark.parametrize('mode,expected', [('healed','complete'),('resumed','waiting')])
def test_observed_recovery_does_not_reissue(mode,expected):
    plan, people = fixture()
    if mode == 'healed': people[0]['health']['needsTend'] = False
    else: people[1]['job'] = 'TendPatient'
    recover(plan,people)
    assert plan.progress['tend'].state == expected
    assert plan.progress['tend'].issued['0']['confirmed']
    assert not plan.progress['tend'].recovery_history


@pytest.mark.parametrize('condition', ['foreign_load','rewind','unconfirmed','unknown_health','dead','doctor_downed',
    'player_draft','player_work','foreign_draft','other_treatment','cancelled','player_action','nonretryable',
    'unknown_tick','unknown_job','player_direction','legacy_direction'])
def test_unsafe_or_player_owned_work_never_reissues(condition):
    plan, people = fixture()
    receipt = plan.progress['tend'].issued['0']
    if condition == 'foreign_load': receipt['load_token'] = 'old'
    elif condition == 'rewind': receipt['issued_tick'] = 500
    elif condition == 'unconfirmed': receipt['confirmed'] = False
    elif condition == 'unknown_health': people[0]['health'] = {}
    elif condition == 'dead': people[0]['dead'] = True
    elif condition == 'doctor_downed': people[1]['downed'] = True
    elif condition == 'player_draft': plan.control['player_draft_overrides'] = {'Thing_Doctor':False}
    elif condition == 'player_work': plan.control['work_overrides'] = {'Thing_Doctor':{'Doctor':0}}
    elif condition == 'foreign_draft': people[1]['drafted'] = True
    elif condition == 'other_treatment': people.append({'thingId':'Thing_Other','job':'TendPatient'})
    elif condition == 'cancelled': plan.colony_goals['CriticalMedical'].cancelled = True
    elif condition == 'player_action': plan.spec.steps[0].source = 'PLAYER'
    elif condition == 'nonretryable': plan.progress['tend'].failure.retryable = False
    elif condition == 'unknown_tick': receipt['issued_tick'] = None
    elif condition == 'unknown_job': people[1]['job'] = None
    elif condition == 'player_direction': plan.control['player_direction'] = 1
    elif condition == 'legacy_direction': receipt.pop('player_direction')
    recover(plan,people)
    assert plan.progress['tend'].state == 'blocked'
    assert not plan.progress['tend'].recovery_history
    assert plan.progress['tend'].issued


@pytest.mark.asyncio
@pytest.mark.parametrize('change', [None,'direction','load','mode','plan'])
async def test_runtime_revalidates_after_fresh_observation_and_records_recovery(change):
    plan, people = fixture()
    rt = SimpleNamespace(lock=asyncio.Lock(),mode='automate',context_token='load',chat_revision=4,
        current_plan=plan,draft_owners={},sync_identity=AsyncMock(),note=Mock(),persist=Mock())
    async def query(name,**args):
        if name == 'home/list_pawns': return {'pawns':people}
        if change == 'direction': rt.chat_revision += 1
        elif change == 'load': rt.context_token = 'new-load'
        elif change == 'mode': rt.mode = 'manual'
        elif change == 'plan': plan.revision += 1
        return {'time':{'ticksGame':200}}
    rt.game = SimpleNamespace(query=query)
    await BridgeRuntime.recover_medical(rt,'tend',expected_token='load',expected_revision=4,limit=3)
    assert plan.progress['tend'].state == ('pending' if change is None else 'blocked')
    if change is None:
        assert plan.revision == 1
        assert plan.colony_goals['CriticalMedical'].evidence['recovery']['attempts'] == 1
        rt.persist.assert_called_once()


@pytest.mark.asyncio
async def test_controller_recovers_same_action_and_keeps_lower_priority_work_suspended():
    from test_colony_controller import Replay
    rt = Replay()
    plan, _ = fixture()
    rt.current_plan = plan
    rt.context_token = 'load'
    rt.lock = asyncio.Lock()
    rt.sync_identity = AsyncMock()
    rt.draft_owners = {}
    plan.spec.steps[0].action.arguments.update(pawn=rt.people[0]['thingId'],target=rt.people[1]['thingId'])
    rt.people[0].update(job='Wait',health={'needsTend':False})
    rt.people[1]['health'] = {'needsTend':True}
    rt.batch.summary.pawns[1].needs_tend = True
    query = rt.query
    async def fresh_query(name,**args):
        return {'time':{'ticksGame':200}} if name == 'home/status' else await query(name,**args)
    rt.game.query = fresh_query
    rt.recover_medical = lambda step_id,**kwargs: BridgeRuntime.recover_medical(rt,step_id,**kwargs)
    await rt.controller.cycle()
    assert plan.progress['tend'].state == 'pending'
    assert [s.id for s in plan.ready()] == ['tend']
    assert plan.control['criteria']['medical'] is False
    assert plan.colony_goals['EnsureInitialShelter'].status == 'suspended'
    assert not plan.control.get('execution_hold')
    await rt.controller.cycle()
    assert len(plan.progress['tend'].recovery_history) == 1
    assert [s.id for s in plan.spec.steps] == ['tend']
