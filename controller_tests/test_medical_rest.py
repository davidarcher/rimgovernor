from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from test_colony_controller import Replay
from test_clock_control import NativeClock
from rimbot.clock_control import PlayClock
from rimbot.colony_plan import ColonyGoal
from rimbot.colony_policy import derive,priority_nodes,criteria
from rimbot.medical_management import resting_patients


def resting_fixture():
    rt=Replay()
    summary=rt.batch.summary.pawns[0]
    summary.downed=True
    pawn=rt.people[0]
    pawn.update(downed=True)
    pawn['health'].update(stableRestEligible=True,shouldSeekMedicalRest=True,inBed=True,bedThingId='Bed1')
    rt.batch.native['pawns']={'pawns':rt.people}
    rt.batch.summary.end_tick=rt.facts['tick']
    return rt,pawn


@pytest.mark.parametrize('eligible',[True,False,None])
def test_rest_monitoring_shares_survival_priority_without_claiming_medical_recovery(eligible):
    rt,pawn=resting_fixture()
    pawn['health']['stableRestEligible']=eligible
    facts=derive(rt.batch,rt.facts,rt.controller.policy)
    nodes=priority_nodes(facts,{},rt.controller.policy)
    assert ('CriticalMedical',2 if eligible else 1) in nodes
    assert criteria(facts,rt.controller.policy)['medical'] is False


@pytest.mark.asyncio
async def test_rest_releases_emergency_preemption_and_worsening_restores_it():
    rt,pawn=resting_fixture()
    rt.current_plan.colony_goals['CriticalMedical']=ColonyGoal(priority_class=1)
    rt.controller.skills.compile=AsyncMock(return_value=None)
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['CriticalMedical'].priority_class==2
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].status=='active'
    pawn['health']['stableRestEligible']=False
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['CriticalMedical'].priority_class==1
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].status=='suspended'


@pytest.mark.parametrize('change',['none','cancelled','blocked','stale','unknown','drafted'])
def test_medical_rest_clock_uses_current_observed_patient_and_care_goal(change):
    rt,pawn=resting_fixture()
    facts=derive(rt.batch,rt.facts,rt.controller.policy)
    rt.current_plan.control['facts']=facts
    goal=rt.current_plan.colony_goals['MaintainMedicalCare']=ColonyGoal(priority_class=2)
    if change=='cancelled':goal.cancelled=True
    if change=='blocked':goal.status='blocked'
    if change=='stale':facts['tick']-=1
    if change=='unknown':pawn['health']['stableRestEligible']=None
    if change=='drafted':pawn['drafted']=True
    assert resting_patients(rt)==(pawn['thingId'] if change=='none' else '')


@pytest.mark.asyncio
async def test_medical_rest_clock_does_not_use_unconditional_downed_exemptions():
    bridge=NativeClock()
    clock=PlayClock(bridge)
    for ticks in (None,601):
        with pytest.raises(ValueError,match='1..600'):
            await clock.change('Superfast',max_ticks=ticks,medical_rest='Thing_Human1')
    assert not bridge.calls
    await clock.change('Superfast',max_ticks=600,medical_rest='Thing_Human1')
    start=next(args for _,args in bridge.calls if args.get('op')=='start')
    assert start['medicalRestIds']=='Thing_Human1'
    assert start['ignoredDownedColonistIds']=='' and start['injuryStopCooldownMs']==0
