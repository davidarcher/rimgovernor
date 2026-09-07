import asyncio
import json
import struct
import httpx
import pytest
from rimbot.catalog import Catalog
from rimbot.contracts import Action,Check,Query,select,satisfies,Proposal,Decision,Plans
from rimbot.model import LocalModel,ModelError
from rimbot.rimapi import APIError,unwrap,compact
from rimbot.server import create_app
from rimbot.video import JPEGReceiver,Video,abandoned_local_stream
import socket
from types import SimpleNamespace
from rimbot.planner import tool


def test_local_tool_schema_contains_complete_nested_action_contract():
    schema=tool('submit','Proposal',Proposal.model_json_schema())['function']['parameters']
    assert '$ref' not in json.dumps(schema)
    action=schema['properties']['actions']['items']
    assert 'arguments' in action['required']
    assert 'query' in action['properties']['done']['anyOf'][0]['properties']


async def test_administrator_can_correct_unknown_proposal_ids(colony):
    rt,_=colony
    rt.cycle_generation=rt.generation
    calls=[]
    class Model:
        async def complete(self,messages,*args):
            assert [t['function']['name'] for t in args[0]]==['submit']
            calls.append(1)
            if len(calls)>1:
                assert 'Workforce' in json.loads(messages[-1]['content'])['error']
            data={'response':'Set the priority.','accepted':['wrong' if len(calls)==1 else 'Workforce'],'deferred':{}}
            return {'role':'assistant','tool_calls':[{'id':str(len(calls)),'type':'function','function':{'name':'submit','arguments':json.dumps(data)}}]},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    result=await rt.planner.arbitrate({}, {'Workforce':Proposal(summary='No action').model_dump()})
    assert result.accepted==['Workforce'] and len(calls)==2


async def test_allow_discovery_and_exact_cell_contract(colony):
    rt,_=colony
    assert 'post_things_set_forbidden' in [e['name'] for e in rt.catalog.listing('unforbid')]
    with pytest.raises(ValueError,match='position'):
        rt.catalog.validate('get_map_things_at',{'map_id':0},False)
    with pytest.raises(ValueError,match='native tool named post_things_set_forbidden'):
        rt.catalog.get('post_things_set_forbidden',False)


async def test_submission_rejects_nonexistent_completion_field(colony):
    rt,game=colony
    action=allow()
    action.done.field='items.total'
    with pytest.raises(ValueError,match='does not exist'):
        await rt.planner.validate_observation(Proposal(summary='Allow timber',actions=[action]))
    assert not game.writes


async def test_definition_groups_share_one_unfiltered_snapshot(colony):
    rt,_=colony
    requests=[]
    async def request(entry,params,body):
        requests.append({'params':params,'body':body})
        return {'things_defs':[{'def_name':'ModdedBed'}],'terrain_defs':[{'def_name':'Soil'}]}
    rt.api.typed_data=request
    rt.api.invalidate(definitions=True)
    things=await rt.api.call('get_def_all',{'filters':['ThingsDefs']})
    terrain=await rt.api.call('get_def_all',{'filters':['TerrainDefs']})
    assert things['things_defs'][0]['def_name']=='ModdedBed'
    assert terrain['terrain_defs'][0]['def_name']=='Soil'
    assert requests==[{'params':{},'body':{}}]
    with pytest.raises(ValueError,match='groups, not item names'):
        await rt.api.call('get_def_all',{'filters':['ModdedBed']})


async def test_video_recovers_abandoned_receiver_and_shares_stream():
    with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as receiver:
        receiver.bind(('127.0.0.1',0))
        config={'address':'127.0.0.1','port':receiver.getsockname()[1]}
        assert not abandoned_local_stream(config)
    assert abandoned_local_stream(config)
    assert not abandoned_local_stream({**config,'address':'192.168.1.2'})
    calls=[]
    class API:
        async def request(self,method,path,**kwargs):
            calls.append((method,path))
            if method=='GET':return {'is_streaming':True,'config':config}
            return {}
    video=Video(SimpleNamespace(api=API()))
    first=await video.subscribe()
    second=await video.subscribe()
    assert [path.rsplit('/',1)[-1] for _,path in calls]==['status','stop','setup','start']
    await video.unsubscribe(first)
    assert video.transport is not None
    await video.unsubscribe(second)
    assert video.transport is None
    assert [path.rsplit('/',1)[-1] for _,path in calls][-2:]==['stop','setup']


def allow():
    return Action(title='Allow nearby building timber',endpoint='post_things_set_forbidden',arguments={'thing_ids':[101],'map_id':7,'forbidden':False},done=Check(query=Query(endpoint='get_map_things',arguments={'map_id':7},where={'thing_id':101,'is_forbidden':False}),field='total',op='eq',value=1))


def test_definition_query_errors_explain_actual_paths_and_filters():
    data={'things_defs':[{'label':'bed'}]}
    with pytest.raises(ValueError,match='things_defs'):
        select(data,Query(endpoint='get_def_all',path='stuff_defs',search='steel'))
    with pytest.raises(ValueError,match='use search'):
        select(data,Query(endpoint='get_def_all',path='things_defs',where={'label':{'like':'*bed*'}}))
    with pytest.raises(ValueError,match='def_name'):
        select([{'def_name':'Bed','label':'bed'}],Query(endpoint='get_def_all',fields=['defName']))


async def test_four_identical_queries_handoff_without_a_total_call_limit(colony):
    rt,_=colony;rt.cycle_generation=rt.generation
    class Model:
        count=0
        async def complete(self,messages,*args):
            self.count+=1
            if self.count==5:
                assert 'same result 4 times' in json.loads(messages[-1]['content'])['repeat_notice']
            name='query' if self.count<=4 else 'submit'
            data={'endpoint':'get_map_things','arguments':{'map_id':7}} if self.count<=4 else {'summary':'Inspection complete'}
            return {'role':'assistant','tool_calls':[{'id':str(self.count),'type':'function','function':{'name':name,'arguments':json.dumps(data)}}]},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    result=await rt.planner.ask('Infrastructure',{},Proposal)
    assert result.blockers and not result.actions and rt.model.count==4


async def test_rejected_draft_cannot_silently_become_empty_success(colony):
    rt,_=colony;rt.cycle_generation=rt.generation
    class Model:
        count=0
        async def complete(self,messages,*args):
            self.count+=1
            name='submit';data={'summary':'Order drafted'}
            if self.count==1:
                name='post_things_set_forbidden';data=allow().model_dump(exclude={'endpoint'})
                data['done']['query']['arguments']['made_up']=True
            elif self.count==2:
                result=json.loads(messages[-1]['content'])
                assert result['draft_retained'] is False
                assert result['completion_query_contract']['name']=='get_map_things'
            elif self.count==3:
                assert 'NOT retained' in json.loads(messages[-1]['content'])['error']
                name='post_things_set_forbidden';data=allow().model_dump(exclude={'endpoint'})
            return {'role':'assistant','tool_calls':[{'id':str(self.count),'type':'function','function':{'name':name,'arguments':json.dumps(data)}}]},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    result=await rt.planner.ask('Survival',{},Proposal)
    assert not result.actions and rt.model.count==2
    assert any('Not issued:' in b for b in result.blockers)


async def test_discovery_real_map_and_nested_colonists(colony):
    rt,game=colony
    assert rt.connected and rt.observation['map']['id']==7
    assert rt.observation['pawns'][0]['colonist']['name']=='Madam'
    assert len(rt.catalog.listing())>80
    assert all('map_id=0' not in str(r.url) for r in game.calls)


@pytest.mark.parametrize('name,args',[
    ('post_item_spawn',{}),('post_map_destroy_rect',{}),('post_map_repair_rect',{}),
    ('post_pawn_edit_position',{}),('post_builder_paste',{}),
    ('post_pawn_edit_status',{'pawn_id':11,'is_drafted':True,'kill':True}),
    ('post_research_target',{'name':'Electricity','force':True}),
])
async def test_editor_operations_not_model_tools(colony,name,args):
    rt,_=colony
    with pytest.raises(ValueError):rt.catalog.validate(name,args,True)


async def test_equip_query_transport_and_bill_mixed_transport(colony):
    rt,game=colony
    await rt.api.call('post_jobs_make_equip',{'map_id':7,'pawn_id':11,'item_id':501},write=True)
    request=game.calls[-1]
    assert request.url.params['item_id']=='501' and not request.content
    await rt.api.call('post_buildings_bills_add',{'building_id':31,'recipe_def_name':'CookMealSimple','repeat_count':10},write=True)
    request=game.calls[-1]
    assert request.url.params['building_id']=='31'
    assert json.loads(request.content)=={'recipe_def_name':'CookMealSimple','repeat_count':10}


async def test_immediate_allow_is_complete_and_jelly_stays_forbidden(colony):
    rt,game=colony
    rt.mode='automate'
    await rt.execute(allow(),'Infrastructure')
    assert not game.forbidden
    assert rt.memory['work'][0]['status']=='complete'
    assert game.writes==['things/set-forbidden']
    await rt.execute(allow(),'Infrastructure')
    assert len(game.writes)==1 # already done, rather than duplicate work lock


async def test_uncertain_write_is_reconciled_not_retried(colony):
    rt,game=colony
    rt.mode='automate';game.fail_after_write=True
    with pytest.raises(APIError):await rt.execute(allow(),'Infrastructure')
    assert rt.memory['work'][0]['status']=='unknown'
    await rt.reconcile()
    assert rt.memory['work'][0]['status']=='complete'
    assert len(game.writes)==1


async def test_player_change_not_blocked_by_completed_work(colony):
    rt,game=colony
    rt.mode='automate'
    await rt.execute(allow(),'Infrastructure')
    game.forbidden=True
    await rt.execute(allow(),'Infrastructure')
    assert len(game.writes)==2


async def test_failed_prerequisite_never_sends_order(colony):
    rt,game=colony
    rt.mode='automate'
    a=allow();a.requires=[a.done]
    with pytest.raises(ValueError,match='Prerequisite'):await rt.execute(a,'Infrastructure')
    assert game.writes==[] and rt.memory['work']==[]


async def test_manual_and_generation_cancel_stop_execution(colony):
    rt,game=colony
    await rt.execute(allow(),'Infrastructure')
    assert game.writes==[]
    rt.mode='automate';rt.generation+=1
    with pytest.raises(asyncio.CancelledError):await rt.execute(allow(),'Infrastructure')
    assert game.writes==[]


async def test_load_rollback_and_map_switch_reset_work_not_objectives(colony):
    rt,game=colony
    rt.memory['goals']=[{'id':'1','text':'Self sufficient','status':'active'}]
    rt.persist();rt.mode='automate'
    await rt.execute(allow(),'Infrastructure')
    game.tick=100
    await rt.poll()
    assert rt.mode=='manual' and rt.memory['work']==[] and rt.memory['goals']
    game.map_id=8
    await rt.poll()
    assert rt.memory['goals']==[]


async def test_fresh_quicktest_with_identical_map_metadata_resets_all_state(colony):
    rt,game=colony
    old=rt.colony
    rt.memory.update(goals=[{'text':'Old goal'}], plans={'old':True}, chat=[{'text':'Old chat'}])
    rt.persist()
    rt.events_pending=[{'type':'old'}]
    rt.steering_pending=True
    rt.counters['actions']=12
    rt.mode='automate'
    game.session='new-game'
    # A new game can even have the same or a later tick than the previous game.
    await rt.poll()
    assert rt.colony != old
    assert rt.memory == rt.empty_memory()
    assert rt.mode == 'manual' and not rt.events_pending and not rt.steering_pending
    assert rt.counters['actions']==0
    assert rt.store.get('colony:'+old)['goals']


async def test_reconnecting_same_game_keeps_direction(colony):
    rt,game=colony
    old=rt.colony
    rt.memory['direction']=['Grow food']
    rt.persist()
    rt.connected=False
    game.tick+=100
    await rt.poll()
    assert rt.colony==old and rt.memory['direction']==['Grow food']


async def test_game_changes_before_write_never_uses_old_ids(colony):
    rt,game=colony
    rt.mode='automate'
    rt.cycle_generation=rt.generation
    game.session='different-game'
    await rt.execute(allow(),'Infrastructure')
    assert not game.writes
    assert rt.mode=='manual' and rt.memory['work'][-1]['status']=='cancelled'


async def test_build_material_validation_uses_native_modded_definitions(colony):
    rt,_=colony
    async def call(*args,**kwargs):
        return {'things_defs':[{'def_name':'ModdedCot','made_from_stuff':True,'allowed_stuff_defs':['ModdedPlank']},
                              {'def_name':'FreeSpot','made_from_stuff':False}]}
    rt.api.call=call
    building={'def_name':'ModdedCot'}
    action=SimpleNamespace(endpoint='post_builder_blueprint',arguments={'blueprint':{'buildings':[building]}})
    with pytest.raises(ValueError,match='ModdedPlank'):await rt.validate_build_materials(action)
    building['stuff_def_name']='WoodLog'
    with pytest.raises(ValueError,match='ModdedPlank'):await rt.validate_build_materials(action)
    building['stuff_def_name']='ModdedPlank'
    await rt.validate_build_materials(action)
    building.clear();building['def_name']='FreeSpot'
    await rt.validate_build_materials(action)


async def test_misnested_read_filters_are_normalized_without_guessing(colony):
    rt,_=colony
    result=await rt.query(Query(endpoint='get_map_things',arguments={'map_id':7,'where':{'thing_id':101},'limit':1}))
    assert result['total']==1 and result['items'][0]['thing_id']==101
    with pytest.raises(ValueError,match='Conflicting'):
        await rt.query(Query(endpoint='get_map_things',arguments={'map_id':7,'where':{'thing_id':101}},where={'thing_id':102}))
    rt.catalog.entries['get_map_things']['schema']['properties']['limit']={'type':'integer'}
    q=rt.normalize_query(Query(endpoint='get_map_things',arguments={'map_id':7,'limit':5}))
    assert q.arguments['limit']==5 and q.limit==30


def test_paging_distance_and_false_zero_values():
    data=[{'id':1,'position':{'x':100,'z':100},'forbidden':False},{'id':2,'position':{'x':3,'z':4},'forbidden':False}]
    q=Query(endpoint='x',where={'forbidden':False},near={'x':0,'z':0},fields=['id'],limit=1)
    result=select(data,q)
    assert result=={'items':[{'id':2}],'total':2,'offset':0,'next_offset':1}
    assert unwrap({'success':True,'data':0})==0
    with pytest.raises(APIError):unwrap({'success':False,'data':{},'errors':['missing item']})
    json.loads(json.dumps(compact([{'long':'x'*2000}]*10,1000)))


def test_compact_query_pages_never_skip_rows_or_change_item_shape():
    data=[{'id':i,'description':'x'*250} for i in range(20)]
    seen=[];offset=0
    while True:
        page=compact(select(data,Query(endpoint='test',offset=offset,limit=10)),900)
        assert isinstance(page['items'],list)
        seen.extend(row['id'] for row in page['items'])
        if page['next_offset'] is None:break
        assert page['next_offset']==offset+len(page['items'])
        offset=page['next_offset']
    assert seen==list(range(20))


def test_query_text_search_finds_partial_names_without_changing_exact_filters():
    data=[{'def_name':'SolarGenerator','label':'solar generator'},{'def_name':'WoodLog','label':'wood'}]
    assert select(data,Query(endpoint='test',search='SOLAR'))['items']==data[:1]
    assert select(data,Query(endpoint='test',where={'label':'Solar Generator'}))['total']==0


def test_udp_chunking_and_bad_frames():
    frames=[];receiver=JPEGReceiver(frames.append)
    def packet(data,i=0,total=1):return b'CAM'+struct.pack('<I',len(data))+bytes([i,total])+data
    receiver.datagram_received(packet(b'\xff\xd8first',0,2),('127.0.0.1',5007))
    receiver.datagram_received(packet(b'second\xff\xd9',1,2),('127.0.0.1',5007))
    assert frames==[b'\xff\xd8firstsecond\xff\xd9']
    receiver.datagram_received(packet(b'\xff\xd8bad\xff\xd9',1,2),('127.0.0.1',5007))
    receiver.datagram_received(packet(b'\xff\xd8remote\xff\xd9'),('192.168.1.1',5007))
    assert len(frames)==1


async def test_local_qwen_stream_on_off_and_output_limit(colony):
    rt,_=colony
    bodies=[]
    def respond(r):
        bodies.append(json.loads(r.content))
        return httpx.Response(200,text='data: '+json.dumps({'choices':[{'delta':{'reasoning_content':'plan'},'finish_reason':'length'}]})+'\n\ndata: [DONE]\n\n')
    model=LocalModel(rt.settings,transport=httpx.MockTransport(respond))
    async def progress(_):pass
    for thinking in (True,False):
        with pytest.raises(ModelError,match='output limit'):await model.complete([],[],thinking,progress)
    assert [b['reasoning_effort'] for b in bodies]==['medium','none']
    assert [b['chat_template_kwargs']['enable_thinking'] for b in bodies]==[True,False]
    await model.close()


async def test_reasoning_legacy_rejection_negotiates_once(colony):
    rt,_=colony
    bodies=[]
    def respond(r):
        body=json.loads(r.content);bodies.append(body)
        if body['reasoning_effort']=='medium':
            return httpx.Response(400,json={'error':{'message':"Invalid reasoning_effort. Supported values: on, off."}})
        return httpx.Response(200,text='data: '+json.dumps({'choices':[{'delta':{'content':'Ready'},'finish_reason':'stop'}]})+'\n\ndata: [DONE]\n\n')
    model=LocalModel(rt.settings,transport=httpx.MockTransport(respond))
    async def progress(_):pass
    for thinking in (True,False):
        response,_=await model.complete([],[],thinking,progress)
        assert response['content']=='Ready'
    assert [b['reasoning_effort'] for b in bodies]==['medium','on','off']
    await model.close()


async def test_api_mutations_require_local_dashboard_header(colony):
    rt,_=colony
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),base_url='http://testserver') as client:
        r=await client.post('/api/control',json={'mode':'manual'})
        assert r.status_code==403
        r=await client.post('/api/control',json={'mode':'manual'},headers={'X-RimBot':'1','Origin':'http://evil.example'})
        assert r.status_code==403
        r=await client.post('/api/control',json={'mode':'manual'},headers={'X-RimBot':'1'})
        assert r.status_code==200
        r=await client.get('/api/state')
        assert r.json()['connected']
        r=await client.get('/rimapi/api/v1/map/things?map_id=0')
        assert r.status_code==200 and r.json()['data'][0]['thing_id']==101


async def test_complete_hierarchy_http_fixture(colony):
    from rimbot.semantic_models import ObjectiveProposal, WorkObjective
    rt,game=colony
    roles=[]
    class ScriptedModel:
        async def complete(self,messages,tools,thinking,progress):
            roles.append(messages[0]['content'])
            system=messages[0]['content']
            if 'Strategy:' in system:
                result=Plans(today=['Use the nearby supplies'],week=['Sustainable food'],season=['Develop production'],year=['Reliable settlement'],horizon='Room to grow',response='Start with nearby supplies.').model_dump()
            elif 'Administrator:' in system:
                result=Decision(response='Allow the nearby timber; leave cave supplies alone.',accepted=['Infrastructure:0']).model_dump()
            elif 'Your role is Executor:supply_access' in system:
                result=Proposal(summary='Release the nearby timber.',actions=[allow()]).model_dump()
            elif 'Construction, rooms, storage filters, farms, production bills and power.' in system:
                result=ObjectiveProposal(summary='Make nearby timber usable',objectives=[WorkObjective(kind='supply_access',outcome='Make nearby timber available',success_signals=['Selected timber is allowed'])]).model_dump()
            else:
                result=ObjectiveProposal(summary='No immediate objective.').model_dump()
            return {'role':'assistant','content':None,'tool_calls':[{'id':str(len(roles)),'type':'function','function':{'name':'submit','arguments':json.dumps(result)}}]}, {'prompt_tokens':100,'completion_tokens':50}
        async def close(self):pass
    await rt.model.close();rt.model=ScriptedModel();rt.mode='automate'
    await rt.review()
    assert len(roles)==6
    assert not any('Administrator:' in role for role in roles)
    assert rt.memory['work'][0]['status']=='complete'
    assert not rt.memory['chat']  # Routine-only setup does not need an administrator message.
    assert game.writes==['things/set-forbidden']


async def test_strategy_delegates_without_discovery_tools(colony):
    rt,_=colony
    rt.cycle_generation=rt.generation
    class Model:
        async def complete(self,messages,tools,*args):
            assert [t['function']['name'] for t in tools]==['submit']
            assert 'capabilities' not in json.loads(messages[1]['content'])
            plan=Plans(today=['Sleeping arrangements'],week=[],season=[],year=[],horizon='Stable colony',response='Prepare sleeping places.',assignments={'Infrastructure':'Provide sleeping arrangements'})
            return {'role':'assistant','content':plan.model_dump_json()},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    plan=await rt.planner.ask('Strategy: plan',{'capabilities':['unused'],'colony':{}},Plans)
    assert list(plan.assignments)==['Infrastructure']


async def test_focused_routing_retains_daily_coverage_and_threat_response(colony):
    rt,_=colony
    rt.last_review=1000
    rt.memory['plans']={'assignments':{'Infrastructure':'Sleeping arrangements'}}
    assert rt.review_roles([])==['Infrastructure']
    assert rt.review_roles([{'type':'raid'}])==['Infrastructure','Survival','Security']
    rt.last_review+=60000
    assert set(rt.review_roles([]))=={'Infrastructure','Survival','Security','Development'}


async def test_focused_coordinator_does_not_wake_unassigned_workforce(colony):
    rt,_=colony
    calls=[]
    async def proposals(context,roles):
        calls.extend(roles)
        return {r:Proposal(summary='No labor change needed').model_dump() for r in roles}
    async def arbitrate(context,proposals):return Decision(response='Ready',accepted=list(proposals))
    rt.planner.proposals=proposals;rt.planner.arbitrate=arbitrate
    rt.cycle_generation=rt.generation
    await rt.coordinate({},['Infrastructure'])
    assert calls==['Infrastructure']


async def test_specialist_tool_schema_separates_queries_and_owned_commands(colony):
    rt,game=colony
    rt.cycle_generation=rt.generation
    calls=[]
    class Model:
        async def complete(self,messages,tools,*args):
            calls.append(1)
            schemas={t['function']['name']:t['function']['parameters'] for t in tools}
            reads=schemas['query']['properties']['endpoint']['enum']
            assert 'get_map_things' in reads and 'post_things_set_forbidden' not in reads
            assert 'actions' not in schemas['submit']['properties']
            context=json.loads(messages[1]['content'])
            assert set(context['capabilities'])=={'read','propose'}
            assert 'post_builder_blueprint' not in context['capabilities']['propose']
            if len(calls)==1:
                name='describe';args={'endpoint':'post_things_set_forbidden'}
            elif len(calls)==2:
                name='post_things_set_forbidden';args=allow().model_dump(exclude={'endpoint'})
                native=schemas[name]['properties']['arguments']
                assert native['properties']['thing_ids']['type']=='array'
                assert 'map_id' in native['required']
            else:
                assert not game.writes
                name='submit';args={'summary':'Release nearby timber.'}
            return {'role':'assistant','tool_calls':[{'id':str(len(calls)),'type':'function','function':{'name':name,'arguments':json.dumps(args)}}]},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    proposal=await rt.planner.ask('Survival',{'capabilities':rt.catalog.listing()},Proposal)
    assert len(proposal.actions)==1 and not game.writes


async def test_prose_proposal_repaired_and_diagnostics_exported(colony):
    rt,game=colony
    rt.cycle_generation=rt.generation
    replies=iter([
        {'role':'assistant','content':'I will inspect supplies.'},
        {'role':'assistant','content':json.dumps({'summary':'Inspect supplies first.','blockers':['Need supply locations']})},
    ])
    class Model:
        async def complete(self,messages,tools,thinking,progress):
            assert all(m['role']!='system' for m in messages[1:])
            return next(replies),{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    proposal=await rt.planner.ask('Survival',{},Proposal)
    assert proposal.blockers==['Need supply locations']
    assert not game.writes
    assert not any(e['kind']=='model_diagnostic' for e in rt.store.history(rt.colony))
    diagnostics=[e for e in rt.store.history(rt.colony,include_diagnostics=True) if e['kind']=='model_diagnostic']
    assert diagnostics[0]['response']['content']=='I will inspect supplies.'


async def test_bad_proposal_format_correction_is_bounded(colony):
    rt,_=colony
    rt.cycle_generation=rt.generation
    calls=[]
    class Model:
        async def complete(self,*args):
            calls.append(1)
            return {'role':'assistant','content':'No submission'},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    with pytest.raises(ModelError,match='Survival returned no valid proposal after two format corrections'):
        await rt.planner.ask('Survival',{},Proposal)
    assert len(calls)==3


async def test_compaction_keeps_system_first_and_tool_reply_paired(colony):
    rt,_=colony
    rt.cycle_generation=rt.generation
    rt.settings.context_chars=1
    calls=[]
    class Model:
        async def complete(self,messages,*args):
            calls.append(1)
            if len(calls)==1:
                return {'role':'assistant','content':None,'tool_calls':[{'id':'query1','type':'function','function':{'name':'discover','arguments':'{"search":"beds"}'}}]},{}
            assert [m['role'] for m in messages]==['system','user','user','assistant','tool']
            assert messages[-1]['tool_call_id']==messages[-2]['tool_calls'][0]['id']
            return {'role':'assistant','content':'{"summary":"No orders."}'},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    assert (await rt.planner.ask('Survival',{},Proposal)).summary=='No orders.'


async def test_submit_missing_action_arguments_can_be_corrected(colony):
    rt,game=colony
    rt.cycle_generation=rt.generation
    calls=[]
    bad=allow().model_dump();bad['arguments']={}
    class Model:
        async def complete(self,messages,*args):
            calls.append(1)
            if len(calls)==1:
                proposal={'summary':'Long but valid prose. '*30,'actions':[bad]}
            else:
                feedback=json.loads(messages[-1]['content'])
                assert 'actions[0]' in feedback['error'] and 'required' in feedback['error']
                assert '350' not in feedback['error']
                proposal=Proposal(summary='Allow nearby timber.',actions=[allow()]).model_dump()
            return {'role':'assistant','tool_calls':[{'id':str(len(calls)),'type':'function','function':{'name':'submit','arguments':json.dumps(proposal)}}]},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    p=await rt.planner.ask('Infrastructure',{},Proposal)
    assert p.actions[0].arguments['thing_ids']==[101]
    assert len(calls)==2 and not game.writes

async def test_compaction_retains_observed_building_materials(colony):
    from pathlib import Path
    from types import SimpleNamespace
    rt,_=colony;rt.cycle_generation=rt.generation;rt.settings.context_chars=1
    rt.catalog.install_contracts(json.loads((Path(__file__).parents[1]/'controller/rimbot/data/construction_contracts.json').read_text()))
    definition={'def_name':'Wall','label':'wall','allowed_materials':[{'def_name':'WoodLog','label':'wood'}]}
    original=rt.api.call
    async def call(name,args,**kwargs):
        if name=='construction_definitions':return SimpleNamespace(model_dump=lambda:{'items':[definition],'total':1,'next_offset':None})
        return await original(name,args,**kwargs)
    rt.api.call=call
    calls=[]
    async def complete(messages,*args):
        calls.append(1)
        if len(calls)==1:
            return {'role':'assistant','tool_calls':[{'id':'wall','type':'function','function':{'name':'construction_definitions','arguments':'{"map_id":0,"search":"Wall","offset":0,"limit":10}'}}]},{}
        retained=json.loads(messages[2]['content'])
        assert retained['observed_building_definitions']==[definition]
        return {'role':'assistant','content':'{"summary":"Use the observed material."}'},{}
    rt.model.complete=complete
    await rt.planner.ask('Executor:construction',{},Proposal,True)

async def test_work_settings_execute_and_verify_while_game_remains_paused(colony):
    rt,game=colony
    game.use_work_priorities=False
    original=rt.api.call
    async def call(name,args,**kwargs):
        result=await original(name,args,**kwargs)
        if name=='get_game_state':result['is_paused']=True
        return result
    rt.api.call=call;rt.mode='automate';rt.cycle_generation=rt.generation
    tick=game.tick
    await rt.execute(Action(title='Enable manual priorities',endpoint='post_work_settings',arguments={'use_work_priorities':True}),'Executor:work_assignment')
    assert game.use_work_priorities and rt.memory['work'][-1]['status']=='complete'
    assert game.writes==['work/settings'] and game.tick==tick
    assert (await rt.api.call('get_game_state',{}))['is_paused']

async def test_satisfied_supply_flag_is_not_staged_again(colony):
    rt,game=colony;rt.cycle_generation=rt.generation;game.forbidden=False
    class Model:
        count=0
        async def complete(self,messages,*args):
            self.count+=1
            if self.count==1:
                name='post_things_set_forbidden';data={'title':'Allow wood','arguments':{'map_id':7,'thing_ids':[101],'forbidden':False}}
            else:
                receipt=json.loads(messages[-1]['content'])
                assert receipt['already_satisfied'] and not receipt['draft_retained']
                name='submit';data={'summary':'Supplies already accessible; no duplicate order.'}
            return {'role':'assistant','tool_calls':[{'id':str(self.count),'type':'function','function':{'name':name,'arguments':json.dumps(data)}}]},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    result=await rt.planner.ask('Infrastructure',{},Proposal)
    assert not result.actions
