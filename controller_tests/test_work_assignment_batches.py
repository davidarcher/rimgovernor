import pytest
from test_colony_controller import Replay
from rimbot.colony_plan import ColonyGoal


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
