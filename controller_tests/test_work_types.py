import pytest
from rimbot.contracts import Action,Proposal

@pytest.mark.parametrize('name',['Wall','LayDown','ConstructTrapSpike'])
async def test_invalid_work_type_never_writes_or_false_completes(colony,name):
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    action=Action(title='Disable invalid category',endpoint='post_colonist_work_priority',arguments={'id':11,'work':name,'priority':0})
    with pytest.raises(ValueError,match='Unknown work type'):
        await rt.execute(action,'Executor:work_assignment')
    assert not game.writes and not rt.memory['work']
    with pytest.raises(ValueError,match='Construction'):
        await rt.planner.validate_observation(Proposal(summary='Invalid',actions=[action]))

async def test_entire_batch_validated_before_any_priority_changes(colony):
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    action=Action(title='Set work',endpoint='post_colonists_work_priority',arguments={'priorities':[{'id':11,'work':'Construction','priority':1},{'id':12,'work':'Wall','priority':1}]})
    with pytest.raises(ValueError,match='Unknown work type'):await rt.execute(action,'Executor:work_assignment')
    assert not game.writes

@pytest.mark.parametrize('assignment,hour',[('UnforbidAll',0),('Work',24)])
async def test_invalid_timetable_cannot_reach_game(colony,assignment,hour):
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    action=Action(title='Invalid timetable',endpoint='post_colonist_time_assignment',arguments={'pawn_id':11,'hour':hour,'assignment':assignment})
    with pytest.raises(ValueError):await rt.execute(action,'Executor:work_assignment')
    assert not game.writes and not rt.memory['work']
