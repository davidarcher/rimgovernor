import json
import httpx
import pytest
from rimbot.catalog import Catalog
from rimbot.config import Settings
from rimbot.rimapi import RimAPI
from rimbot.runtime import Runtime
from rimbot.store import Store
from rimbot.http_contract import DOCUMENT, OPERATIONS


def dto_fixture(schema, value):
    """Fill required scalar fields for protocol fixtures, never production data."""
    if '$ref' in schema:schema=DOCUMENT['components']['schemas'][schema['$ref'].split('/')[-1]]
    if 'anyOf' in schema:return value
    if schema.get('type')=='array':
        return [dto_fixture(schema['items'],v) for v in (value if isinstance(value,list) else [])]
    if schema.get('type')=='object':
        value=dict(value) if isinstance(value,dict) else {}
        for key,prop in schema.get('properties',{}).items():
            if key in value:value[key]=dto_fixture(prop,value[key])
            elif key in schema.get('required',[]):value[key]=dto_fixture(prop,None)
        return value
    if value is not None:return value
    return {'integer':0,'number':0.0,'boolean':False,'string':''}.get(schema.get('type'))


class GameFixture:
    """Deterministic HTTP fixture, not a claim about RimWorld gameplay."""
    def __init__(self):
        self.tick=1000
        self.session='test-session'
        self.map_id=7
        self.forbidden=True
        self.writes=[]
        self.fail_after_write=False
        self.calls=[]
        self.buildings=[]
        self.catalog=Catalog()
        self.use_work_priorities=True

    def handle(self,r):
        self.calls.append(r)
        path=r.url.path.removeprefix('/api/v1/')
        data={}
        if path=='work/settings':
            if r.method=='POST':
                self.use_work_priorities=json.loads(r.content)['use_work_priorities']
                self.writes.append(path)
            return httpx.Response(200,json={'use_work_priorities':self.use_work_priorities})
        if path=='docs':
            data={'sections':[{'endpoints':[{'method':e['method'],'path':e['path']} for e in self.catalog.entries.values()]}]}
        elif path=='game/state':
            data={'session_id':self.session,'game_tick':self.tick,'program_state':'Playing','is_paused':False,'colonist_count':3,'colony_wealth':24000}
        elif path=='maps':
            data=[{'id':self.map_id,'seed':42,'is_player_home':True,'faction_id':'Player','size':'(250,1,250)'}]
        elif path=='colonists/detailed':
            data=[{'colonist':{'id':i,'name':n,'health':1,'mood':.6},'colonist_work_info':{'current_job':'Standing','work_priorities':[]}} for i,n in [(11,'Madam'),(12,'James'),(13,'Mal')]]
        elif path=='map/things':
            assert int(r.url.params['map_id'])==self.map_id
            data=[{'thing_id':101,'def_name':'WoodLog','is_forbidden':self.forbidden,'stack_count':75,'position':{'x':100,'z':100}}, {'thing_id':102,'def_name':'InsectJelly','is_forbidden':True,'stack_count':20,'position':{'x':5,'z':5}}]
        elif path=='map/buildings':
            data=self.buildings
        elif path=='map/zones':
            data={'zones':[],'areas':[]}
        elif path=='map/rooms':
            data={'rooms':[]}
        elif path=='resources/stored':
            data=[]
        elif path=='things/set-forbidden':
            body=json.loads(r.content)
            assert body['map_id']==self.map_id
            assert body['thing_ids']==[101]
            self.forbidden=body['forbidden']
            self.writes.append(path)
            if self.fail_after_write:
                raise httpx.ReadTimeout('Response lost after game applied order')
        elif r.method!='GET':
            self.writes.append(path)
        operation=next((o for o in OPERATIONS.values() if o['path']==r.url.path and o['method']==r.method),None)
        envelope={'success':True,'data':data,'errors':[],'warnings':[],'timestamp':'2026-09-05T18:00:00Z'}
        if operation and path!='docs':
            if path=='resources/stored':envelope['data']={}
            envelope=dto_fixture(operation['response_schema'],envelope)
            if operation['response_schema'].get('$ref','').endswith('/ApiResult'):envelope.pop('data',None)
        return httpx.Response(200,json=envelope)


@pytest.fixture
async def colony(tmp_path):
    game=GameFixture()
    store=Store(tmp_path/'test.sqlite')
    rt=Runtime(store,Settings(),api_factory=lambda url,c:RimAPI(url,c,transport=httpx.MockTransport(game.handle)))
    await rt.poll()
    yield rt,game
    await rt.stop()
    store.close()
