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
@pytest.mark.parametrize('count',[9,12])
async def test_larger_starter_fits_native_footprints_without_using_service_rows(count):
    rt,room,facts=fixture(count)
    del rt.current_plan.colony_goals['intent-home']
    goal=rt.current_plan.colony_goals['EnsureInitialShelter']
    goal.evidence['methods']={'shell':['completed-shell']}
    skill=ColonySkills(rt)
    skill.layout=AsyncMock(return_value={'room':{'x':10,'z':10,'width':9,'height':9}})
    method,actions=await skill.compile('EnsureInitialShelter',facts,[])
    assert method==f'starter-sleep-{count}'
    assert len(actions[0]['placements'])==count
    occupied=set()
    for p in actions[0]['placements']:
        footprint={(p['x'],p['z']),(p['x']+(p['rotation']=='east'),p['z']+(p['rotation']=='north'))}
        assert not occupied&footprint
        assert all(x!=14 and 11<=x<=17 and 11<=z<15 for x,z in footprint)
        occupied|=footprint
    assert 'adopted_shelter' not in rt.current_plan.control
    goal.evidence['methods'][method]=['sleeping-action']
    facts['indoorSleepingCapacity']=count
    rt.inspect_native.reset_mock()
    assert await skill.compile('EnsureInitialShelter',facts,[]) is None
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_larger_starter_waits_for_roof_then_uses_available_capacity_before_expansion():
    rt,room,facts=fixture(13)
    del rt.current_plan.colony_goals['intent-home']
    rt.current_plan.colony_goals['EnsureInitialShelter'].evidence['methods']={'shell':['completed-shell']}
    skill=ColonySkills(rt)
    skill.layout=AsyncMock(return_value={'room':{'x':10,'z':10,'width':9,'height':9}})
    room['openRoofCount']=2
    assert await skill.compile('EnsureInitialShelter',facts,[]) is None
    rt.inspect_native.assert_not_awaited()
    room['openRoofCount']=0
    method,actions=await skill.compile('EnsureInitialShelter',facts,[])
    assert method=='starter-sleep-13' and len(actions[0]['placements'])==12


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


def services():
    rt,room,facts=fixture()
    facts.update(indoorSleepingCapacity=8,sleepingTemperatureMin=30,cooking=[])
    rt.controller=SimpleNamespace(policy=SimpleNamespace(temperature_enter_low=10))
    buildings=[{'defName':'SleepingSpot','position':{'x':x,'z':z},
        'occupies':{'minX':x,'maxX':x,'minZ':z,'maxZ':z+1}} for x in (11,13,15,17) for z in (11,13)]
    async def query(name,**args):
        if name=='home/list_rooms':return {'success':True,'rooms':[room]}
        assert name=='home/list_buildings' and args['aggregate'] is False
        return {'success':True,'buildings':buildings,'skipped':{}}
    async def preview(name,args):
        if name=='home/place_building':
            return {'canPlace':True,'rotations':[{'accepted':True,'blockingThings':[],
                'occupiedCells':[{'x':args['x'],'z':args['z']}]}]}
        assert name=='home/zone_cells' and args['dryRun'] and args['op']=='create'
        return {'cellsAccepted':9,'cells':[{'takenFrom':None} for _ in range(9)]}
    rt.game.query=AsyncMock(side_effect=query);rt.inspect_native=AsyncMock(side_effect=preview)
    return rt,room,facts,buildings


@pytest.mark.asyncio
@pytest.mark.parametrize('goal,expected',[('EnsureCooking','Campfire'),('EnsureTemperatureSafety','PassiveCooler'),('EnsureFoodStorage',None)])
async def test_service_furniture_uses_player_room_without_starter_layout(goal,expected):
    rt,room,facts,buildings=services()
    rt.current_plan.colony_goals[goal]=ColonyGoal(priority_class=2)
    skill=ColonySkills(rt);skill.layout=AsyncMock(side_effect=AssertionError('Unrelated starter coordinates'))
    method,actions=await skill.compile(goal,facts,[])
    if expected:
        p=actions[0]['placements'][0]
        assert p['def_name']==expected and 11<=p['x']<=17 and 15<=p['z']<=17 and p['x']!=14
    else:
        patch=actions[0]['patches'][0]
        assert patch=={'x':11,'z':15,'width':3,'height':3}


@pytest.mark.asyncio
async def test_service_work_waits_for_player_shell_and_preserves_foreign_zones():
    rt,room,facts,buildings=services()
    rt.current_plan.colony_goals['EnsureFoodStorage']=ColonyGoal(priority_class=3)
    rt.current_plan.colony_goals['intent-home'].status='active'
    assert await ColonySkills(rt).compile('EnsureFoodStorage',facts,[]) is None
    rt.inspect_native.assert_not_awaited()
    assert rt.current_plan.control['simulation_needed']
    rt.current_plan.colony_goals['intent-home'].status='complete'
    rt.inspect_native=AsyncMock(return_value={'cellsAccepted':9,'cells':[{'takenFrom':'Player stockpile'}]*9})
    with pytest.raises(SkillBlocked,match='No verified space'):await ColonySkills(rt).compile('EnsureFoodStorage',facts,[])


@pytest.mark.asyncio
async def test_existing_campfire_in_adopted_room_is_not_duplicated_for_heating():
    rt,room,facts,buildings=services()
    buildings.append({'defName':'Campfire','position':{'x':17,'z':17},'isBlueprint':False,'isFrame':False})
    facts['sleepingTemperatureMin']=0
    rt.current_plan.colony_goals['EnsureTemperatureSafety']=ColonyGoal(priority_class=2)
    assert await ColonySkills(rt).compile('EnsureTemperatureSafety',facts,[]) is None
    rt.inspect_native.assert_not_awaited()


@pytest.mark.asyncio
async def test_unissued_cached_farms_are_replanned_around_accepted_player_shell():
    from test_colony_controller import Replay
    from rimbot.colony_plan import PlanSpec,PlanStep,StepProgress
    rt=Replay()
    initial=await rt.controller.skills.layout(rt.facts)
    patch=initial['farms'][0]
    shell=rt.controller.skills.shell({'room':dict(patch)})
    step=PlanStep(id='player-room',title='Player shelter',source='PLAYER',action=shell,completion_criteria='Room built')
    rt.current_plan.spec=PlanSpec(steps=[step]);rt.current_plan.progress[step.id]=StepProgress(state='waiting')
    revised=await rt.controller.skills.layout(rt.facts)
    protected={(x,z) for x in range(patch['x'],patch['x']+patch['width']) for z in range(patch['z'],patch['z']+patch['height'])}
    farmland={(x,z) for p in revised['farms'] for x in range(p['x'],p['x']+p['width']) for z in range(p['z'],p['z']+p['height'])}
    assert not protected&farmland
    assert revised['room']==initial['room']


def test_footprint_census_allows_harmless_objects_but_not_replacements():
    from rimbot.shelter_handoff import safe_rotation
    harmless={'category':'Pawn','isBlueprint':False,'isFrame':False,'frameWouldBeCancelled':False,'wouldBeWiped':False}
    assert safe_rotation({'accepted':True,'blockingThings':[harmless]})
    for change in ({'category':'Building'},{'wouldBeWiped':True},{'frameWouldBeCancelled':True},{'isBlueprint':True}):
        assert not safe_rotation({'accepted':True,'blockingThings':[dict(harmless,**change)]})
