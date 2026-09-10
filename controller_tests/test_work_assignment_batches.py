import pytest
from test_colony_controller import Replay
from rimbot.colony_plan import ColonyGoal
from rimbot.colony_policy import work_assignment


@pytest.mark.asyncio
async def test_work_assignments_continue_after_eight_changed_pawns():
    rt=Replay(count=12)
    goal=rt.current_plan.colony_goals['EnsureWorkAssignments']=ColonyGoal(priority_class=2)
    applied=set()
    for _ in range(4):
        result=await rt.controller.skills.compile('EnsureWorkAssignments',rt.facts,rt.people)
        if result is None:break
        method,actions=result
        assert 1<=len(actions)<=8
        goal.evidence.setdefault('methods',{})[method]=[]
        for action in actions:
            args=action['arguments'];applied.add(args['pawn'])
            pawn=next(p for p in rt.people if p['thingId']==args['pawn'])
            for entry in args['work'].split(','):
                name,priority=entry.split('=')
                work=next(w for w in pawn['work']['types'] if w['name']==name)
                work['priorityStored']=work['priority']=int(priority)
    assert applied=={p['thingId'] for p in rt.people}


@pytest.mark.asyncio
async def test_mental_state_worker_is_unavailable_for_allocation_and_treatment():
    rt=Replay(count=4)
    before,_=work_assignment(rt.people)
    doctor=next(identity for identity,work in before.items() if work.get('Doctor')==1)
    pawn=next(p for p in rt.people if p['thingId']==doctor)
    pawn['mentalState']='SocialFighting'
    assignments,covered=work_assignment(rt.people)
    assert doctor not in assignments and covered
    rt.current_plan.colony_goals['CriticalMedical']=ColonyGoal(priority_class=1)
    rt.facts.update(medicalKnown=True,criticalPatients=['Thing_Patient'])
    rt.people.append({'thingId':'Thing_Patient','dead':False,'downed':True,'health':{'needsTend':True}})
    from unittest.mock import AsyncMock
    rt.inspect_native = AsyncMock(return_value={'success':True})
    _,actions=await rt.controller.skills.compile('CriticalMedical',rt.facts,rt.people)
    assert actions[0]['arguments']['pawn']!=doctor
    pawn['mentalState']=None
    assert work_assignment(rt.people)[0]==before
