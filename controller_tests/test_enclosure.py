from types import SimpleNamespace as NS
import pytest
from rimbot.enclosure import Enclosure,compile_enclosure
from test_spatial import area,region,layout
from rimbot.spatial import boundary,cells

async def test_compile_full_perimeter_with_door_and_reused_wall():
    a=area()
    next(c for c in a['cells'] if c['position']=={'x':1,'z':1}).update(encloses=True,is_door=False)
    async def call(name,args):
        if name=='construction_area':return NS(model_dump=lambda:a)
        if name=='construction_footprints':return NS(items=[NS(cells=[NS(**b['position'])],encloses=True,is_door=b['def_name']=='Door') for b in args['buildings']])
        if name=='construction_inspect':return NS(accepted=True)
        if name=='construction_state':return NS(revision='test')
    rt=NS(memory={'spatial_layout':layout(),'colony_focus':{'x':3,'z':3}},observation={'map':{'id':0}},api=NS(call=call))
    request=Enclosure(region_id='shelter',wall_def='Wall',wall_material='WoodLog',door_def='Door',door_material='WoodLog',entrances=[{'x':3,'z':1}])
    action=await compile_enclosure(rt,{'project_id':'a'},request)
    bs=action.arguments['buildings']
    assert {(b['position']['x'],b['position']['z']) for b in bs}==boundary(cells(region()))-{(1,1)}
    assert len([b for b in bs if b['def_name']=='Door'])==1
    request.entrances[0].x=1
    with pytest.raises(ValueError,match='not a corner'):await compile_enclosure(rt,{'project_id':'a'},request)
