import copy
from types import SimpleNamespace as NS
from unittest.mock import AsyncMock
import pytest
from rimbot.spatial import cells,boundary,validate_layout,validate_orders,render_map,survey_runs
from rimbot.contracts import Action


def area():
    return {'cells':[{'position':{'x':x,'z':z},'terrain_def':'Soil','fertility':1.,'roofed':False,'walkable':True,'zone_id':None,'zone_type':'','zone_label':'','plan_id':'','plantable':True,'encloses':False,'thing_ids':[]} for z in range(8) for x in range(8)]}

def region():
    return {'id':'shelter','label':'Shelter','purpose':'room','project_ids':['a'],'patches':[{'z1':z,'z2':z,'x1':1,'x2':5} for z in range(1,6)],'reuse_zone_ids':[],'fertility_floor':0,'rationale':'Near camp'}

def layout(r=None):return {'summary':'Shelter','regions':[r or region()],'deferred':{}}

def test_reject_room_in_existing_farm():
    a=area();next(c for c in a['cells'] if c['position']=={'x':2,'z':2}).update(zone_id=7,zone_type='Zone_Growing')
    with pytest.raises(ValueError,match='overlaps existing'):validate_layout(layout(),a,[{'project_id':'a'}])

def test_room_requires_connected_filled_interior():
    r=region();r['patches']=[{'z1':z,'z2':z,'x1':x,'x2':x} for x,z in [(1,1),(1,5),(5,1),(5,5)]]
    with pytest.raises(ValueError,match='connected'):validate_layout(layout(r),area(),[{'project_id':'a'}])
    r['patches']=[{'z1':1,'z2':1,'x1':1,'x2':5}]
    with pytest.raises(ValueError,match='interior'):validate_layout(layout(r),area(),[{'project_id':'a'}])

def test_irregular_farm_excludes_unsuitable_hole():
    a=area();next(c for c in a['cells'] if c['position']=={'x':2,'z':2}).update(fertility=0.,plantable=False)
    r=region();r.update(purpose='farm',fertility_floor=1,patches=[{'z1':1,'z2':1,'x1':1,'x2':3},{'z1':2,'z2':2,'x1':1,'x2':1},{'z1':2,'z2':2,'x1':3,'x2':3},{'z1':3,'z2':3,'x1':1,'x2':3}])
    validate_layout(layout(r),a,[{'project_id':'a'}]);assert (2,2) not in cells(r)
    r['patches'][1]['x2']=2
    with pytest.raises(ValueError,match='unsuitable'):validate_layout(layout(r),a,[{'project_id':'a'}])

def test_shared_site_and_conflicting_reservations():
    r=region();r['project_ids']=['a','b'];validate_layout(layout(r),area(),[{'project_id':'a'},{'project_id':'b'}])
    other=copy.deepcopy(r);other['id']='other';value=layout(r);value['regions'].append(other)
    with pytest.raises(ValueError,match='overlap'):validate_layout(value,area(),[{'project_id':'a'},{'project_id':'b'}])

async def validate(points,*,bed=False,door=False,complete=True):
    a=area();r=region()
    async def call(name,args):
        if name=='construction_footprints':return NS(items=[NS(cells=[NS(x=x,z=z) for x,z in points],encloses=not bed,is_door=door,is_bed=bed)])
        return NS(model_dump=lambda:a)
    rt=NS(memory={'spatial_layout':layout(r),'colony_focus':{'x':3,'z':3}},observation={'map':{'id':0}},api=NS(call=call))
    action=Action(endpoint='construction_place',arguments={'map_id':0,'buildings':[]},title='Shelter')
    await validate_orders(rt,{'project_id':'a'},[action],complete=complete)

async def test_four_corners_rejected_but_full_boundary_passes():
    with pytest.raises(ValueError,match='Incomplete'):await validate({(1,1),(1,5),(5,1),(5,5)})
    await validate(boundary(cells(region())),door=True)

async def test_bed_full_footprint_must_fit_inside():
    with pytest.raises(ValueError,match='perimeter'):await validate({(2,2),(2,1)},bed=True)
    with pytest.raises(ValueError,match='leaves'):await validate({(5,3),(6,3)},bed=True)
    await validate({(2,2),(2,3)},bed=True)

def test_survey_image_and_lossless_run_encoding():
    a=area();png=render_map(a,[region()]);assert png.startswith(b'\x89PNG')
    runs=survey_runs(a);assert len(runs)==8
    assert sum(r['x2']-r['x1']+1 for r in runs)==len(a['cells'])


def test_storage_can_be_nested_inside_room_but_crops_cannot():
    room=region();storage=region();storage.update(id='store',purpose='storage',parent_id='shelter',project_ids=['b'],patches=[{'z1':2,'z2':2,'x1':2,'x2':3}])
    value=layout();value['regions'].append(storage)
    validate_layout(value,area(),[{'project_id':'a'},{'project_id':'b'}])
    storage['purpose']='farm'
    with pytest.raises(ValueError,match='Nested'):validate_layout(value,area(),[{'project_id':'a'},{'project_id':'b'}])

async def test_closed_perimeter_without_door_is_rejected():
    with pytest.raises(ValueError,match='door'):await validate(boundary(cells(region())))

async def test_stockpile_can_use_interior_of_shared_room():
    a=area();r=region();r['project_ids'].append('b')
    rt=NS(memory={'spatial_layout':layout(r),'colony_focus':{'x':3,'z':3}},observation={'map':{'id':0}},api=NS(call=AsyncMock(return_value=NS(model_dump=lambda:a))))
    order=Action(endpoint='post_map_zone_stockpile',arguments={'map_id':0,'point_a':{'x':2,'z':2},'point_b':{'x':3,'z':3}},title='Interior storage')
    await validate_orders(rt,{'project_id':'b'},[order])
    order.arguments['point_a']['x']=1
    with pytest.raises(ValueError,match='land use'):await validate_orders(rt,{'project_id':'b'},[order])
