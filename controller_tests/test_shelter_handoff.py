from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyPlan,ColonyGoal
from rimbot.colony_skills import ColonySkills,SkillBlocked


def fixture(count=8):
    shell={'bounds':{'x':10,'z':10,'width':9,'height':9},'entrance':'south'}
    plan=ColonyPlan(colony_goals={
        'intent-home':ColonyGoal(priority_class=2,status='complete',source='PLAYER',evidence={
            'request':{'kind':'BuildRoom','purpose':'shelter','room':shell}}),
        'EnsureInitialShelter':ColonyGoal(priority_class=2)})
    room={'id':3,'properRoom':True,'psychologicallyOutdoors':False,'openRoofCount':0,'cellsComplete':True,
          'cells':[{'x':x,'z':z} for x in range(11,18) for z in range(11,18)]}
    async def preview(name,args):
        assert name=='home/place_building' and args['defName']=='SleepingSpot' and args['dryRun']
        x,z=args['x'],args['z']
        cells=[{'x':x,'z':z},{'x':x+(args['rotation']=='east'),'z':z+(args['rotation']=='north')}]
        return {'canPlace':True,'rotations':[{'accepted':True,'blockingThings':[],'occupiedCells':cells}]}
    rt=SimpleNamespace(current_plan=plan,game=SimpleNamespace(query=AsyncMock(return_value={'success':True,'rooms':[room]})),
                       inspect_native=AsyncMock(side_effect=preview))
    return rt,room,{'colonists':count,'indoorSleepingCapacity':0}


@pytest.mark.asyncio
async def test_completed_player_shell_is_furnished_without_another_shell_or_layout():
    rt,room,facts=fixture()
    skill=ColonySkills(rt);skill.layout=AsyncMock(side_effect=AssertionError('Duplicate shelter layout'))
    method,actions=await skill.compile('EnsureInitialShelter',facts,[])
    assert method=='player-sleep-8' and len(actions)==1 and actions[0]['kind']=='place_buildings'
    assert len(actions[0]['placements'])==8
    occupied=set()
    for p in actions[0]['placements']:
        footprint={(p['x'],p['z']),(p['x']+(p['rotation']=='east'),p['z']+(p['rotation']=='north'))}
        assert not footprint&occupied and all(x!=14 and 11<=x<=17 and 11<=z<=17 for x,z in footprint)
        occupied|=footprint
    assert rt.current_plan.control['adopted_shelter']['intent']=='intent-home'


@pytest.mark.asyncio
async def test_handoff_only_adds_missing_sleeping_capacity_and_waits_for_roof():
    rt,room,facts=fixture();facts['indoorSleepingCapacity']=6
    result=await ColonySkills(rt).compile('EnsureInitialShelter',facts,[])
    assert len(result[1][0]['placements'])==2
    rt.current_plan.colony_goals['intent-home'].evidence['request']['room']['bounds'].update(width=22,height=22)
    room['cells']=[{'x':x,'z':z} for x in range(11,31) for z in range(11,31)]
    room['openRoofCount']=300;room['psychologicallyOutdoors']=True;rt.inspect_native.reset_mock()
    assert await ColonySkills(rt).compile('EnsureInitialShelter',facts,[]) is None
    assert rt.current_plan.control['simulation_needed']
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
@pytest.mark.parametrize('change',[{'cellsComplete':False},{'properRoom':False},{'openRoofCount':None}])
async def test_unknown_or_changed_player_room_blocks_duplicate_shelter(change):
    rt,room,facts=fixture();room.update(change)
    with pytest.raises(SkillBlocked):await ColonySkills(rt).compile('EnsureInitialShelter',facts,[])
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_native_obstructions_or_insufficient_space_never_claim_handoff():
    rt,room,facts=fixture()
    rt.inspect_native=AsyncMock(return_value={'canPlace':True,'rotations':[{
        'accepted':True,'blockingThings':[{'thingId':'Thing_Bed1'}],'occupiedCells':[{'x':11,'z':11}]}]})
    with pytest.raises(SkillBlocked,match='insufficient'):await ColonySkills(rt).compile('EnsureInitialShelter',facts,[])
    assert 'adopted_shelter' not in rt.current_plan.control
