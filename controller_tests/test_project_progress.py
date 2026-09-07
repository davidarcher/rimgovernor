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

def test_site_recovery_distinguishes_permanent_and_temporary_failures():
    from rimbot.project_progress import site_recovery
    site={'thing_id':7,'def_name':'RestrictedBed','stage':'blueprint','targeted_by':[],
          'workers':[{'can_construct':False,'reason':'Only members can build'}],
          'materials':[{'needed':30,'accessible_quantity':0,'forbidden_quantity':30}]}
    result=site_recovery(site,{'restrictions':['Ideology restriction']})
    assert result['state']=='needs_alternative' and 'Priorities cannot' in result['next_action']
    site['targeted_by']=[3]
    assert site_recovery(site,{'restrictions':[]})['state']=='targeted'
    site['targeted_by']=[];site['workers'][0]['reason']='Downed'
    assert site_recovery(site,{})['state']=='worker_blocked'
    site['workers'][0]['can_construct']=True
    assert site_recovery(site,{})['state']=='awaiting_materials'
    site['materials'][0]['needed']=0
    assert site_recovery(site,{})['state']=='ready_for_work'

async def test_recovery_uses_current_native_eligibility_and_owned_site():
    rt=runtime();p={'project_id':'bed','kind':'construction','feedback':[],'work_ids':['w']}
    rt.memory.update(projects=[p],work=[{'id':'w','status':'complete','action':{'arguments':{'buildings':[{'position':{'x':3,'z':4}}]}}}])
    async def call(name,args,**kwargs):
        assert name=='construction_definitions' and args['map_id']==0
        return NS(items=[NS(def_name='SlabBed',eligibility=NS(model_dump=lambda:{'restrictions':['Ideology restriction']}))])
    rt.api.call=call
    context={'construction_work':{'sites':[{'thing_id':8,'def_name':'SlabBed','stage':'blueprint','position':{'x':3,'z':4}}],'next_offset':None}}
    await refresh_progress(rt,context)
    assert p['progress']['sites'][0]['state']=='needs_alternative'
    assert p['progress']['sites_complete']
