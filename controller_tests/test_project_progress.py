from types import SimpleNamespace as NS
from unittest.mock import Mock
import pytest
from rimbot.project_progress import refresh_progress,validate_farm_expansion,routing_error
from rimbot.contracts import Action


def runtime():
    async def call(name,args,**kwargs):
        if name=='get_map_zones':return {'zones':[{'type':'Zone_Growing','id':3,'cells_count':50}]}
        if name=='get_map_zone_growing':return {'plant_def_name':'Crop','plant_count':0,'is_sowing':True,'growth_progress':0}
        return NS(model_dump=lambda:{'cells':[{'position':{'x':1,'z':1},'roofed':True}]})
    project={'project_id':'food','kind':'growing','crop_def':'Crop','target_cells':50,'feedback':['No plants; make another zone'],'work_ids':[]}
    return NS(observation={'map':{'id':0}},memory={'projects':[project]},last_tick=42,api=NS(call=call),note=Mock())

async def test_unsown_zone_counts_as_existing_capacity_and_replaces_old_feedback():
    rt=runtime();await refresh_progress(rt,{})
    p=rt.memory['projects'][0]
    assert p['progress']['matching_cells']==50 and p['progress']['remaining_cells']==0
    assert p['progress']['zones'][0]['plants_present']==0
    assert not p['feedback'] and rt.note.called
    action=Action(title='More crops',endpoint='zone_growing_cells',arguments={'plant_def':'Crop','cells':[{'x':8,'z':8}]})
    with pytest.raises(ValueError,match='remaining 0'):await validate_farm_expansion(rt,p,[action])
    p['target_cells']=51
    await validate_farm_expansion(rt,p,[action])
    p['target_cells']=None
    with pytest.raises(ValueError,match='target_cells'):await validate_farm_expansion(rt,p,[action])

def test_stockpile_routing_without_matching_incidental_mentions():
    assert routing_error({'kind':'construction','outcome':'Place a stockpile near camp'})
    assert not routing_error({'kind':'storage','outcome':'Place a stockpile near camp'})
    assert not routing_error({'kind':'construction','outcome':'Build beds beside the stockpile'})

async def test_room_progress_uses_built_state_and_roof_independently():
    rt=runtime();p={'project_id':'room','kind':'construction','feedback':['wall unfinished'],'work_ids':[]};rt.memory.update(projects=[p],colony_focus={'x':1,'z':1},spatial_layout={'regions':[{'purpose':'room','project_ids':['room'],'patches':[{'x1':0,'x2':2,'z1':0,'z2':2}]}]})
    context={'construction_state':{'buildings':[{'def_name':'Wall','state':'built','position':{'x':0,'z':0}}]}}
    await refresh_progress(rt,context)
    assert p['progress']['buildings']==[{'def_name':'Wall','state':'built','count':1}]
    assert p['progress']['roof']['roofed_cells']==1 and not p['feedback']
