import pytest
from rimbot.routine import execute_routine
from rimbot.contracts import Action

async def test_routine_executes_and_tracks_without_submit(colony):
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    project={'project_id':'supplies','kind':'supply_access','work_ids':[]}
    rt.memory['projects']=[project]
    action=Action(title='Allow wood',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})
    result=await execute_routine(rt,{'project':{'project_id':'supplies','kind':'supply_access'}},action,'Executor:supply_access')
    assert result['verified'] and not game.forbidden
    assert len(project['work_ids'])==1
    assert rt.memory['work'][-1]['project_id']=='supplies'
    rt.mode='manual'
    with pytest.raises(ValueError,match='Automation is off'):await execute_routine(rt,{'project':project},action,'Executor:supply_access')

async def test_routine_rejects_missing_project_before_game_write(colony):
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    action=Action(title='Allow wood',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})
    with pytest.raises(ValueError,match='no longer active'):
        await execute_routine(rt,{'project':{'project_id':'missing'}},action,'Executor:supply_access')
    assert game.forbidden and not game.writes
