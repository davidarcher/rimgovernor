from types import SimpleNamespace as NS
from unittest.mock import AsyncMock, Mock
import pytest
from rimbot.contracts import Action, Proposal
from rimbot.spatial import validate_orders, prepare_layout
from test_spatial import area, region, layout

def setup(regions=()):
    survey=area()
    async def call(name,args,**kwargs):
        if name=='get_map_zones':return {'zones':[]}
        if name=='get_def_all':return {'plant_defs':[{'def_name':'Crop','fertility_min':.7}]}
        return NS(model_dump=lambda:survey)
    return NS(memory={'spatial_layout':{'regions':list(regions)},'colony_focus':{'x':3,'z':3}},observation={'map':{'id':0}},api=NS(call=call)),survey

def zone(x=6,z=6,kind='growing'):
    args={'map_id':0,'point_a':{'x':x,'z':z},'point_b':{'x':x,'z':z}}
    if kind=='growing':args['plant_def']='Crop'
    return Action(title='Zone',endpoint='post_map_zone_stockpile' if kind=='storage' else 'post_map_zone_growing',arguments=args)

async def test_specialist_chooses_unreserved_site():
    rt,_=setup([region()])
    await validate_orders(rt,{'kind':'growing','project_id':'food','crop_def':'Crop','target_cells':20},[zone()])
    assert len(rt.memory['spatial_layout']['regions'])==1  # validation does not claim land

@pytest.mark.parametrize('purpose',['room','path','farm','storage'])
async def test_shared_reservations_protected(purpose):
    r=region();r['purpose']=purpose
    rt,_=setup([r])
    with pytest.raises(ValueError,match='conflicts'):
        await validate_orders(rt,{'kind':'growing','project_id':'food','crop_def':'Crop','target_cells':20},[zone(2,2)])

async def test_storage_room_interior_allowed_perimeter_rejected():
    rt,_=setup([region()]);project={'kind':'storage','project_id':'store'}
    await validate_orders(rt,project,[zone(2,2,'storage')])
    with pytest.raises(ValueError,match='conflicts'):await validate_orders(rt,project,[zone(1,2,'storage')])

async def test_fresh_native_zones_and_bad_soil_rejected():
    rt,survey=setup();cell=next(c for c in survey['cells'] if c['position']=={'x':6,'z':6})
    project={'kind':'growing','project_id':'food','crop_def':'Crop','target_cells':20}
    cell['fertility']=.1
    with pytest.raises(ValueError,match='fertility'):await validate_orders(rt,project,[zone()])
    cell.update(fertility=1.,zone_id=99,zone_type='Zone_Stockpile')
    with pytest.raises(ValueError,match='Existing'):await validate_orders(rt,project,[zone()])

async def test_zones_reuse_existing_master_plan_without_architect():
    rt,_=setup();rt.memory['projects']=[{'project_id':'food','kind':'growing'}]
    from rimbot.base_plan import BasePlan,land_state
    rt.memory['spatial_layout']=BasePlan(population_at_review=0,observed_land=land_state(area())).model_dump()
    rt.model_for_role=Mock(side_effect=AssertionError('No architect needed'))
    await prepare_layout(rt,{},rt.memory['projects'])
    rt.model_for_role.assert_not_called()

async def test_spatial_work_waits_for_valid_initial_master_plan(monkeypatch):
    from rimbot.semantic import execute_projects
    projects=[{'project_id':'room','kind':'construction'},{'project_id':'food','kind':'growing','owner':'Survival','outcome':'Grow food'}]
    order=[]
    async def prepare(*args):order.append('architect');raise ValueError('bad room')
    async def ask(*args):order.append('growing');return Proposal(summary='Need a suitable crop',blockers=['No crop selected'])
    monkeypatch.setattr('rimbot.spatial.prepare_layout',prepare)
    async def refreshed(rt,context):return context
    monkeypatch.setattr('rimbot.project_progress.refresh_progress',refreshed)
    rt=NS(last_tick=0,memory={'projects':projects,'work':[]},check_generation=Mock(),mode='automate',resume_initial_planning=AsyncMock(),note=Mock(),persist=Mock(),manager_context=AsyncMock(return_value={}),observe_resources=AsyncMock(return_value={}),planner=NS(ask=ask),settings=NS(reasoning=False))
    await execute_projects(rt,{},projects)
    assert order==['architect']
    assert projects[0]['status']=='needs_review'


async def test_farm_cannot_designate_deconstruction_in_another_project():
    farm=region();farm.update(purpose='farm',project_ids=['farm'])
    rt,_=setup([farm])
    project={'kind':'growing','project_id':'farm'}
    action=Action(title='Clear land',endpoint='post_order_designate_area',arguments={'map_id':0,'type':'deconstruct','point_a':{'x':20,'z':20},'point_b':{'x':25,'z':25}})
    with pytest.raises(ValueError,match='leaves this project site'):
        await validate_orders(rt,project,[action])
    action.arguments.update(point_a={'x':2,'z':2},point_b={'x':2,'z':2})
    await validate_orders(rt,project,[action])
