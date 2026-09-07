import copy,json
from types import SimpleNamespace as NS
from unittest.mock import Mock,AsyncMock
import pytest
from rimbot.base_plan import BasePlan,PlanChange,apply_change,extent,land_state,reserve_site,SiteRequest,prepare_master_plan,validate_reserved_space,request_review
from rimbot.spatial import cells
from rimbot.store import Store

def survey():
 return {'cells':[{'position':{'x':x,'z':z},'terrain_def':'Soil','fertility':1.,'roofed':False,'walkable':True,'zone_id':None,'zone_type':'','zone_label':'','plantable':True,'encloses':False,'thing_ids':[]} for x in range(32) for z in range(32)]}

def test_terrain_rectangles_are_lossless_including_holes_and_changed_facts():
 from rimbot.base_plan import terrain_rectangles
 from rimbot.spatial import survey_runs
 area=survey()
 assert len(terrain_rectangles(area)[1])==1
 area['cells']=[c for c in area['cells'] if c['position']!={'x':4,'z':5}]
 area['cells'][0]['fertility']=0.5
 classes,rectangles=terrain_rectangles(area)
 decoded={}
 for x1,z1,x2,z2,kind in rectangles:
  for z in range(z1,z2+1):
   for x in range(x1,x2+1):
    assert (x,z) not in decoded
    decoded[x,z]=classes[kind]
 expected={(x,row['z']):row['facts'] for row in survey_runs(area) for x in range(row['x1'],row['x2']+1)}
 assert decoded==expected

def test_budget_compaction_preserves_indexed_terrain_as_one_observation():
 from rimbot.request_budget import shorten,fit_request
 classes=[{'terrain_def':f'Terrain{i}'} for i in range(25)]
 terrain=';'.join(f'{i},1,{i},2,{i%25}' for i in range(2000))
 facts={'terrain_classes':classes,'terrain_rectangles_x1_z1_x2_z2_class':terrain,
        'survey_bounds':{'x_min':0,'x_max':1999,'z_min':1,'z_max':2},'irrelevant_history':['long'*500]*30}
 compact=shorten(facts,200)
 assert compact['terrain_classes']==classes
 assert compact['terrain_rectangles_x1_z1_x2_z2_class']==terrain
 # Oversized geometry must fail explicitly, never silently lose cells/classes.
 with pytest.raises(ValueError,match='cannot fit safely'):
  fit_request([{'role':'user','content':json.dumps(facts)}],[],10000,2048)
def zone(id='food',x=2):
 return {'id':id,'label':id,'purpose':'food' if id=='food' else 'residential','anchor':{'x':x,'z':2},'reserved_size':{'width':10,'height':14},'expansion_direction':'north','phase':1 if id=='food' else 2,'adjacent_to':[],'rationale':'Expandable'}
def change(zones=None,mode='FULL_REPLAN'):
 return PlanChange(mode=mode,summary='A compact camp with expansion room',zones=zones or [zone(),zone('homes',18)],corridors=[{'id':'main','label':'Main walk','start':{'x':15,'z':1},'end':{'x':15,'z':29},'width':1}] if mode=='FULL_REPLAN' else [],build_phases=[{'number':1,'label':'Survival','goals':['Shelter and food']},{'number':2,'label':'Stability','goals':['Housing growth']}]).model_dump()
def plan():
 p,_=apply_change({},change(),survey());p.update(population_at_review=8,observed_land=land_state(survey()));return p

def runtime(p=None):
 a=survey();rt=NS(memory={'spatial_layout':p or plan(),'colony_focus':{'x':16,'z':16}},observation={'map':{'id':0},'game':{'colonist_count':8}},last_tick=1,persist=Mock(),note=Mock(),api=NS(call=AsyncMock(return_value=NS(model_dump=lambda:a))),model_for_role=Mock(),mode='manual')
 return rt,a

def test_partial_replan_preserves_other_zones_and_deviations():
 p=plan();p['deviations']=[{'id':'prior','reason':'Player changed the approach'}];modified=zone();modified['reserved_size']['height']=16
 result,affected=apply_change(p,change([modified],'MODIFY_ZONE'),survey())
 assert affected==['food'] and result['zones'][1]==p['zones'][1]
 assert result['deviations']==p['deviations']
 assert result['version']==2

def test_overlapping_or_unknown_geometry_is_rejected():
 z=zone();z['anchor']={'x':100,'z':100}
 with pytest.raises(ValueError,match='surveyed'):apply_change({},change([z]),survey())
 z=zone('homes',2)
 # Existing unrelated reservations cannot be silently moved to make room.
 with pytest.raises(ValueError,match='does not fit'):apply_change(plan(),change([z],'MODIFY_ZONE'),survey())

async def test_increment_preserves_expansion_and_reuses_without_architect():
 rt,a=runtime();before=copy.deepcopy(rt.memory['spatial_layout']['reserved_regions']);project={'project_id':'kitchen','kind':'construction'}
 result=await reserve_site(rt,project,SiteRequest(zone_id='food',label='Kitchen',purpose='room',width=6,height=6))
 r=result['region'];assert cells(r)<cells(before[0])
 assert rt.memory['spatial_layout']['reserved_regions']==before
 again=await reserve_site(rt,project,SiteRequest(zone_id='food',label='Kitchen',purpose='room',width=6,height=6))
 assert again['region']['id']==r['id'] and len(rt.memory['spatial_layout']['regions'])==1
 expanded=await reserve_site(rt,project,SiteRequest(zone_id='food',label='Kitchen',purpose='room',width=8,height=9,reuse_region_id=r['id']))
 assert cells(r)<=cells(expanded['region'])<cells(before[0])
 await prepare_master_plan(rt,{},[project])
 rt.model_for_role.assert_not_called()

def test_future_reserve_cannot_be_consumed_without_explicit_intent():
 rt,_=runtime();project={'project_id':'rogue'}
 with pytest.raises(ValueError,match='Reserved space'):validate_reserved_space(rt,project,{(3,3)})
 validate_reserved_space(rt,project,{(30,30)})

async def test_terrain_invalidation_triggers_local_review_and_persists_deviation():
 rt,a=runtime();next(c for c in a['cells'] if c['position']=={'x':3,'z':12})['zone_id']=99
 rt.strategies=NS(search=lambda *args:[]);rt.progress=AsyncMock();rt.model_progress=AsyncMock();rt.usage=Mock();rt.check_generation=Mock();rt.settings=NS(architect_reasoning=False)
 payload=PlanChange(mode='NO_CHANGE',summary='Keep the reservation; resolve the conflicting zone before building').model_dump()
 rt.model_for_role.return_value=NS(complete=AsyncMock(return_value=({'role':'assistant','tool_calls':[{'id':'one','function':{'name':'submit','arguments':json.dumps(payload)}}]},{})))
 await prepare_master_plan(rt,{},[])
 assert rt.model_for_role.call_count==1 and rt.memory['spatial_layout']['deviations']
 saved=copy.deepcopy(rt.memory['spatial_layout']['deviations']);await prepare_master_plan(rt,{},[])
 assert rt.model_for_role.call_count==1 and rt.memory['spatial_layout']['deviations']==saved

async def test_initial_plan_persists_then_routine_need_does_not_replan(tmp_path):
 rt,a=runtime();rt.memory['spatial_layout']={};rt.strategies=NS(search=lambda *args:[]);rt.progress=AsyncMock();rt.model_progress=AsyncMock();rt.usage=Mock();rt.check_generation=Mock();rt.settings=NS(architect_reasoning=False)
 rt.model_for_role.return_value=NS(complete=AsyncMock(return_value=({'role':'assistant','tool_calls':[{'id':'one','function':{'name':'submit','arguments':json.dumps(change())}}]},{})))
 store=Store(tmp_path/'state.sqlite');rt.persist=lambda:store.set('colony:test',rt.memory)
 await prepare_master_plan(rt,{},[])
 rt.memory=store.get('colony:test');await prepare_master_plan(rt,{},[{'project_id':'new-bedroom','kind':'construction'}])
 assert rt.model_for_role.call_count==1 and rt.memory['spatial_layout']['zones']
 store.close()

async def test_rejected_plan_retry_keeps_map_without_repeating_large_reply():
 rt,a=runtime();rt.memory['spatial_layout']={};rt.strategies=NS(search=lambda *args:[])
 rt.progress=AsyncMock();rt.model_progress=AsyncMock();rt.usage=Mock();rt.check_generation=Mock();rt.settings=NS(architect_reasoning=False)
 rejected=change();rejected['zones'][0]['reserved_size']={'width':32,'height':32}
 rejected['zones'][0]['rationale']='A long invalid proposal '*500
 inputs=[]
 async def complete(messages,*args):
  inputs.append(copy.deepcopy(messages))
  payload=rejected if len(inputs)==1 else change()
  return {'role':'assistant','tool_calls':[{'id':'one','function':{'name':'submit','arguments':json.dumps(payload)}}]},{}
 rt.model_for_role.return_value=NS(complete=complete)
 await prepare_master_plan(rt,{},[])
 assert len(inputs)==2
 assert inputs[1][:2]==inputs[0]
 assert len(inputs[1])==3 and inputs[1][-1]['role']=='user'
 assert 'rejected' in inputs[1][-1]['content']
 assert 'A long invalid proposal' not in json.dumps(inputs[1])
 assert len([c for c in rt.note.call_args_list if c.args[0]=='model_call'])==2

def test_replan_cannot_move_committed_site():
 p=plan();p['regions']=[{'id':'k','zone_id':'food','patches':[{'x1':2,'x2':7,'z1':2,'z2':7}]}]
 z=zone();z['anchor']['z']=20
 with pytest.raises(ValueError):apply_change(p,change([z],'MODIFY_ZONE'),survey())

def test_architect_has_one_footprint_and_direction_never_moves_reservation():
 schema=PlanChange.model_json_schema()['$defs']['PlannedZone']['properties']
 assert 'reserved_size' in schema and 'initial_size' not in schema and 'max_size' not in schema
 z=zone();expected=cells(extent(z))
 for direction in ('north','south','east','west'):
  z['expansion_direction']=direction
  assert cells(extent(z))==expected

@pytest.mark.parametrize('direction',['north','south','east','west'])
async def test_current_sites_fit_single_reservation_in_each_growth_direction(direction):
 p=plan();p['zones'][0]['expansion_direction']=direction
 rt,_=runtime(p)
 result=await reserve_site(rt,{'project_id':'room','kind':'construction'},SiteRequest(zone_id='food',label='Room',purpose='room',width=6,height=6))
 assert cells(result['region'])<cells(p['reserved_regions'][0])
