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
from rimbot.video import JPEGReceiver


def allow():
    return Action(title='Allow nearby building timber',endpoint='post_things_set_forbidden',arguments={'thing_ids':[101],'map_id':7,'forbidden':False},done=Check(query=Query(endpoint='get_map_things',arguments={'map_id':7},where={'thing_id':101,'is_forbidden':False}),field='total',op='eq',value=1))


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


def test_paging_distance_and_false_zero_values():
    data=[{'id':1,'position':{'x':100,'z':100},'forbidden':False},{'id':2,'position':{'x':3,'z':4},'forbidden':False}]
    q=Query(endpoint='x',where={'forbidden':False},near={'x':0,'z':0},fields=['id'],limit=1)
    result=select(data,q)
    assert result=={'items':[{'id':2}],'total':2,'offset':0,'next_offset':1}
    assert unwrap({'success':True,'data':0})==0
    with pytest.raises(APIError):unwrap({'success':False,'data':{},'errors':['missing item']})
    json.loads(json.dumps(compact([{'long':'x'*2000}]*10,1000)))


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
    rt,game=colony
    roles=[]
    class ScriptedModel:
        async def complete(self,messages,tools,thinking,progress):
            roles.append(messages[0]['content'])
            system=messages[0]['content']
            if 'Strategy:' in system:
                result=Plans(today=['Use the nearby supplies'],week=['Sustainable food'],season=['Develop production'],year=['Reliable settlement'],horizon='Room to grow',response='Start with nearby supplies.').model_dump()
            elif 'Administrator:' in system:
                result=Decision(response='Allow the nearby timber; leave cave supplies alone.',accepted=['Infrastructure'],deferred={k:'No immediate order' for k in ['Survival','Security','Development','Workforce']}).model_dump()
            elif system.endswith('Construction, rooms, storage filters, farms, production bills and power. Coordinate sites and materials using current map observations. Reuse existing structures when suitable.'):
                result=Proposal(summary='Release the nearby timber.',actions=[allow()]).model_dump()
            else:
                result=Proposal(summary='No immediate order.').model_dump()
            return {'role':'assistant','content':None,'tool_calls':[{'id':str(len(roles)),'type':'function','function':{'name':'submit','arguments':json.dumps(result)}}]}, {'prompt_tokens':100,'completion_tokens':50}
        async def close(self):pass
    await rt.model.close();rt.model=ScriptedModel();rt.mode='automate'
    await rt.review()
    assert len(roles)==7
    assert rt.memory['work'][0]['status']=='complete'
    assert rt.memory['chat'][-1]['role']=='manager'
    assert game.writes==['things/set-forbidden']
