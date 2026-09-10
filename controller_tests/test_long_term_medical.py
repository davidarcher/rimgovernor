from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyGoal, NativeOperation
from rimbot.medical_management import care_state, manage_care
from rimbot.colony_skills import SkillBlocked
from rimbot.surgery import surgery_outcome, effect_from_preview
from rimbot.medical_replacement import replace_doctor
from test_medical_triage import fixture


def action():
    return NativeOperation(tool='home/medical_operations', completion='surgery_health',
        arguments=dict(patient='Thing_Patient', recipe='InstallLeg', part=7,
            expectedHealth='Hediff_1:MissingBodyPart:7', expectedCare='NormalOrWorse', dryRun=False,
            colonyId='colony', loadToken='load', mapId=1),
        medical_effect=dict(recipe='InstallLeg', part=7, addsHediff='PegLeg', removesHediff=None))


def patient():
    return dict(thingId='Thing_Patient', dead=False, health=dict(careObservationVersion=1,
        hediffs=[], surgeryBills=[dict(id='Bill_1', suspended=False)]))


def test_surgical_bill_receipt_or_removal_never_certifies_health_success():
    pawn = patient()
    assert surgery_outcome(action(), {'bill_id':'Bill_1'}, [pawn]) == 'waiting'
    pawn['health']['surgeryBills'] = []
    assert surgery_outcome(action(), {'bill_id':'Bill_1'}, [pawn]).code == 'surgery_failed_or_cancelled'
    pawn['health']['hediffs'] = [dict(id='Hediff_2', defName='PegLeg', partIndex=8)]
    assert surgery_outcome(action(), {'bill_id':'Bill_1'}, [pawn]).code == 'surgery_failed_or_cancelled'
    pawn['health']['hediffs'][0]['partIndex'] = 7
    assert surgery_outcome(action(), {'bill_id':'Bill_1'}, [pawn]) == 'complete'
    pawn['dead'] = True
    assert surgery_outcome(action(), {'bill_id':'Bill_1'}, [pawn]).code == 'patient_dead'


def test_unknown_surgery_and_player_suspension_do_not_retry():
    pawn = patient()
    assert surgery_outcome(action(), {}, [pawn]).code == 'surgery_uncertain'
    pawn['health']['surgeryBills'][0]['suspended'] = True
    assert surgery_outcome(action(), {'bill_id':'Bill_1'}, [pawn]).code == 'surgery_suspended'
    pawn['health'].pop('hediffs')
    assert surgery_outcome(action(), {'bill_id':'Bill_1'}, [pawn]).code == 'medical_state_unknown'


def test_native_recipe_refusal_and_effect_mismatch_are_rejected():
    with pytest.raises(ValueError, match='Native surgery refused'):
        effect_from_preview(dict(success=False, error='No medicine'))
    payload = action().model_dump()
    payload['medical_effect']['part'] = 8
    with pytest.raises(ValueError, match='exact requested'):
        NativeOperation.model_validate(payload)


def test_existing_native_operations_keep_their_persisted_signature_shape():
    existing = dict(kind='native_operation', tool='home/order', arguments={'action':'draft','pawn':'Thing_Patient'}, completion='native_receipt')
    assert NativeOperation.model_validate(existing).model_dump() == existing
    assert NativeOperation.model_validate_json(action().model_dump_json()).medical_effect == action().medical_effect


@pytest.mark.asyncio
async def test_disease_monitor_preserves_policy_and_observes_recovery_separately():
    rt = fixture()
    rt.current_plan.colony_goals['MaintainMedicalCare'] = ColonyGoal(priority_class=2)
    pawn = rt.people[0]
    pawn['health'].update(shouldSeekMedicalRest=True, inBed=False, medicalCare='NoCare',
        hediffs=[dict(id='Hediff_2', defName='Flu', isBad=True, immunity=.4, severity=.3, immunizable=True)])
    state = care_state(rt.people)
    assert state['patients'][0]['conditions'][0]['immunity'] == .4
    with pytest.raises(SkillBlocked, match='policy disables care'):
        await manage_care(rt, {'longTermMedical': state}, rt.people)
    assert pawn['health']['medicalCare'] == 'NoCare'
    pawn['health'].update(shouldSeekMedicalRest=False, needsTend=False, hediffs=[])
    assert care_state(rt.people)['patients'] == []


@pytest.mark.asyncio
async def test_player_bed_rest_override_and_missing_observations_are_explicit():
    rt = fixture()
    rt.current_plan.colony_goals['MaintainMedicalCare'] = ColonyGoal(priority_class=2)
    pawn = rt.people[0]
    pawn['health'].update(shouldSeekMedicalRest=True, medicalCare='NormalOrWorse')
    pawn['work']['types'].append(dict(name='PatientBedRest', disabled=False, priority=0))
    rt.current_plan.control['work_overrides'] = {pawn['thingId']: {'PatientBedRest':0}}
    with pytest.raises(SkillBlocked, match='player disabled PatientBedRest'):
        await manage_care(rt, {'longTermMedical': care_state(rt.people)}, rt.people)
    pawn['health'].pop('careObservationVersion')
    with pytest.raises(SkillBlocked, match='observations unavailable'):
        await manage_care(rt, {'longTermMedical': care_state(rt.people)}, rt.people)


@pytest.mark.asyncio
@pytest.mark.parametrize('changed', ['none', 'direction', 'native_order', 'unknown_history', 'player_action'])
async def test_doctor_replacement_keeps_receipts_and_honors_player_ownership(changed):
    from test_medical_recovery import fixture as recovery_fixture
    plan, people = recovery_fixture()
    people[1]['downed'] = True
    alternate = deepcopy(people[1])
    alternate.update(thingId='Thing_Alternate', downed=False, bio={'skills':[{'name':'Medicine','level':9}]})
    people.append(alternate)
    rt = SimpleNamespace(current_plan=plan, mode='automate', context_token='load', chat_revision=0,
        game=SimpleNamespace(describe=AsyncMock(return_value={'type':'object'})),
        sync_identity=AsyncMock(), refresh_clock_events=AsyncMock())
    async def preview(*args, **kwargs):
        if changed == 'direction': rt.chat_revision += 1
        return {'success':True}
    rt.game.invoke = preview
    if changed == 'native_order': people[0]['orderGeneration'] = 1
    if changed == 'unknown_history': people[0].pop('orderGeneration')
    if changed == 'player_action': plan.spec.steps[0].source = 'PLAYER'
    original = deepcopy(plan.progress['tend'].issued)
    await replace_doctor(rt, plan.spec.steps[0], people, tick=200, token='load', direction=0, revision=0, limit=3)
    assert plan.progress['tend'].issued == original
    if changed == 'none':
        assert plan.progress['tend'].state == 'cancelled'
        assert plan.spec.steps[-1].action.arguments['pawn'] == 'Thing_Alternate'
        assert plan.spec.steps[0].signature() not in plan.cancelled_actions
    else:
        assert len(plan.spec.steps) == 1
        assert plan.progress['tend'].state == 'blocked'

def surgical_runtime():
    from unittest.mock import Mock
    from rimbot.colony_plan import ColonyPlan, PlanStep, StepProgress
    plan = ColonyPlan()
    plan.spec.steps.append(PlanStep(id='surgery', title='Install leg', action=action(),
        completion_criteria='Native peg leg observed'))
    plan.progress['surgery'] = StepProgress(state='waiting', issued={'0': dict(
        confirmed=True, bill_id='Bill_1', issued_at=10, issued_tick=100, player_direction=0)})
    return SimpleNamespace(current_plan=plan, identity=dict(colonyId='colony', loadToken='load', mapId=1),
        batch=SimpleNamespace(started_at=11, summary=SimpleNamespace(end_tick=101),
            native={'pawns': {'pawns': [patient()]}}),
        controller=SimpleNamespace(policy=SimpleNamespace(blocked_after_ticks=60000)), note=Mock(), signal=Mock())


@pytest.mark.parametrize('change', ['none', 'cancelled', 'direction', 'load', 'map', 'colony',
    'unknown_receipt', 'future_tick', 'expired', 'autopilot'])
def test_anesthesia_clock_permission_is_bounded_and_player_scoped(change):
    from rimbot.surgery import recovery_patients
    rt = surgical_runtime()
    progress = rt.current_plan.progress['surgery']
    if change == 'cancelled': progress.state = 'cancelled'
    if change == 'direction': rt.current_plan.control['player_direction'] = 1
    if change in ('load', 'map', 'colony'): rt.identity[{'load':'loadToken','map':'mapId','colony':'colonyId'}[change]] = 'other'
    if change == 'unknown_receipt': progress.issued['0']['confirmed'] = False
    if change == 'future_tick': rt.batch.summary.end_tick = 99
    if change == 'expired': rt.batch.summary.end_tick = 60100
    if change == 'autopilot': rt.current_plan.spec.steps[0].source = 'AUTOPILOT'
    assert recovery_patients(rt) == ('Thing_Patient' if change == 'none' else '')


def test_surgery_timeout_observes_late_native_success_without_reissuing():
    from rimbot.bridge_runtime import BridgeRuntime
    rt = surgical_runtime()
    rt.batch.summary.end_tick = 60100
    receipt = deepcopy(rt.current_plan.progress['surgery'].issued)
    BridgeRuntime.reconcile_plan(rt)
    progress = rt.current_plan.progress['surgery']
    assert progress.state == 'blocked' and progress.failure.code == 'surgery_no_progress'
    rt.batch.native['pawns']['pawns'][0]['health']['hediffs'] = [dict(id='Hediff_2', defName='PegLeg', partIndex=7)]
    BridgeRuntime.reconcile_plan(rt)
    assert progress.state == 'complete' and progress.failure is None
    assert progress.issued == receipt


def test_surgical_outcome_requires_fresh_batch_and_same_native_context():
    from rimbot.bridge_runtime import BridgeRuntime
    rt = surgical_runtime()
    rt.batch.native['pawns']['pawns'][0]['health']['hediffs'] = [dict(id='Hediff_2', defName='PegLeg', partIndex=7)]
    rt.batch.started_at = 10
    BridgeRuntime.reconcile_plan(rt)
    assert rt.current_plan.progress['surgery'].state == 'waiting'
    rt.batch.started_at = 11
    rt.identity['loadToken'] = 'reloaded'
    BridgeRuntime.reconcile_plan(rt)
    assert rt.current_plan.progress['surgery'].failure.code == 'surgery_context_changed'
