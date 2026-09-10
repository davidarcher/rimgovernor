from types import SimpleNamespace
from unittest.mock import Mock
import pytest
from pydantic import ValidationError
from rimbot.colony_plan import NativeOperation, ColonyPlan, Decision, PlanSpec
from rimbot.config import ModelRole
from rimbot.medical_outcome import patient_outcome, rescue_outcome
from rimbot.bridge_runtime import BridgeRuntime


ARGS={'action':'tend','pawn':'Thing_Doctor','target':'Thing_Patient'}


@pytest.mark.parametrize('pawns,code', [
    ([], 'patient_unobserved'),
    ([{'thingId':'Thing_Patient','dead':True}], 'patient_dead'),
    ([{'thingId':'Thing_Patient','dead':False}], 'medical_state_unknown'),
    ([{'thingId':'Thing_Patient','dead':False,'health':{'needsTend':True}}], 'doctor_unavailable'),
    ([{'thingId':'Thing_Patient','dead':False,'health':{'needsTend':True}},
      {'thingId':'Thing_Doctor','job':'Wait_Combat'}], 'tending_interrupted'),
])
def test_medical_failure_is_not_completion(pawns,code):
    assert patient_outcome(ARGS,pawns).code==code


def test_native_health_controls_completion():
    patient={'thingId':'Thing_Patient','dead':False,'health':{'needsTend':True}}
    rows=[patient,{'thingId':'Thing_Doctor','job':'TendPatient','jobTargetA':'Thing_Patient'}]
    assert patient_outcome(ARGS,rows)=='waiting'
    patient['health']['needsTend']=False
    assert patient_outcome(ARGS,rows)=='complete'


def test_contract_rejects_nonmedical_and_ambiguous_targets():
    for args in (dict(ARGS,action='attack'),dict(ARGS,target='Sam')):
        with pytest.raises(ValidationError):
            NativeOperation(tool='home/order',arguments=args,completion='patient_tended')


def test_waiting_survives_reload_and_gates_cleanup():
    spec=PlanSpec(steps=[
        {'id':'tend','title':'Treat patient','completion_criteria':'No tending needed',
         'action':{'kind':'native_operation','tool':'home/order','arguments':ARGS,'completion':'patient_tended'}},
        {'id':'cleanup','title':'Release doctor','completion_criteria':'Undrafted','after':[{'step':'tend'}],
         'action':{'kind':'stand_down','pawn_ids':['Thing_Doctor']}}])
    plan=ColonyPlan()
    plan.commit(Decision(expected_revision=0,disposition='revise',assessment='Treat',rationale='Wounded',reply='Treating',plan=spec),actor=ModelRole.STRATEGIST,tick=1)
    plan.progress['tend'].state='waiting'
    restored=ColonyPlan.model_validate_json(plan.model_dump_json())
    assert list(restored.ready())==[]
    restored.progress['tend'].state='complete'
    assert [s.id for s in restored.ready()]==['cleanup']


@pytest.mark.parametrize('completion,order', [('patient_tended','tend'),('patient_in_bed','rescue')])
def test_runtime_requires_fresh_health_and_emits_completion_once(completion,order):
    action=NativeOperation(tool='home/order',arguments=dict(ARGS,action=order),completion=completion)
    progress=SimpleNamespace(state='waiting',issued={'0':{'confirmed':True,'issued_at':20}},failure=None,project_id=None)
    rt=SimpleNamespace(current_plan=SimpleNamespace(spec=SimpleNamespace(steps=[SimpleNamespace(id='tend',title='Treat',action=action)]),progress={'tend':progress}),
        batch=SimpleNamespace(started_at=10,native={'pawns':{'pawns':[{'thingId':'Thing_Patient','dead':False,'health':{'needsTend':False,'inBed':True,'bedThingId':'Thing_Bed'}}]}}),
        signal=Mock(),note=Mock())
    BridgeRuntime.reconcile_plan(rt)
    assert progress.state=='waiting'
    rt.batch.started_at=21
    BridgeRuntime.reconcile_plan(rt)
    assert progress.state=='complete'
    BridgeRuntime.reconcile_plan(rt)
    rt.signal.assert_called_once()


@pytest.mark.parametrize('rows,expected', [
    ([{'thingId':'Thing_Doctor','job':'Rescue','carriedThingId':'Thing_Patient'}], 'waiting'),
    ([{'thingId':'Thing_Doctor','job':'Rescue','carriedThingId':'Thing_Other'}], 'patient_unobserved'),
    ([{'thingId':'Thing_Patient','dead':False,'health':{'inBed':True,'bedThingId':'Thing_Bed'}}], 'complete'),
    ([{'thingId':'Thing_Patient','dead':True,'health':{'inBed':True,'bedThingId':'Thing_Bed'}}], 'patient_dead'),
    ([{'thingId':'Thing_Patient','dead':False,'health':{'inBed':True}},
      {'thingId':'Thing_Doctor','job':'Wait'}], 'rescue_interrupted'),
    ([{'thingId':'Thing_Patient','dead':False,'health':{'inBed':False}},
      {'thingId':'Thing_Doctor','job':'Rescue'}], 'waiting'),
    ([{'thingId':'Thing_Doctor','downed':True,'job':'Rescue','carriedThingId':'Thing_Patient'}], 'rescuer_unavailable'),
])
def test_rescue_requires_observed_delivery(rows,expected):
    result=rescue_outcome(dict(ARGS,action='rescue'),rows)
    assert (result if isinstance(result,str) else result.code)==expected


def test_rescue_completion_contract():
    NativeOperation(tool='home/order',arguments=dict(ARGS,action='rescue'),completion='patient_in_bed')
    with pytest.raises(ValidationError):
        NativeOperation(tool='home/order',arguments=ARGS,completion='patient_in_bed')
