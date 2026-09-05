import json
import subprocess
import sys
from pathlib import Path

import pytest

from rimbot.contracts import Action, Proposal, Decision
from rimbot.model import ModelError


async def test_completion_is_owned_by_allow_integration(colony):
    rt,game=colony;rt.mode='automate';rt.cycle_generation=rt.generation
    action=Action(title='Allow timber',endpoint='post_things_set_forbidden',arguments={'map_id':7,'thing_ids':[101],'forbidden':False})
    rt.planner.validate_submission('Survival',Proposal(summary='Timber',actions=[action]))
    await rt.execute(action,'Survival')
    assert rt.memory['work'][-1]['status']=='complete'
    await rt.execute(action,'Survival')
    assert game.writes==['things/set-forbidden']


async def test_validated_draft_survives_output_limit(colony):
    rt,_=colony;rt.cycle_generation=rt.generation
    class Model:
        calls=0
        async def complete(self,messages,tools,*args):
            self.calls+=1
            if self.calls>1:raise ModelError('Model output limit reached')
            schema=next(t['function']['parameters'] for t in tools if t['function']['name']=='post_things_set_forbidden')
            assert not {'done','requires','observation_basis'} & schema['properties'].keys()
            return {'role':'assistant','tool_calls':[{'id':'1','type':'function','function':{'name':'post_things_set_forbidden','arguments':json.dumps({'title':'Allow timber','arguments':{'map_id':7,'thing_ids':[101],'forbidden':False}})}}]},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    proposal=await rt.planner.ask('Survival',{},Proposal)
    assert len(proposal.actions)==1 and proposal.actions[0].done is None
    assert 'output limit' in proposal.blockers[0]


async def test_identical_failed_call_stops_without_a_total_call_quota(colony):
    rt,_=colony;rt.cycle_generation=rt.generation
    class Model:
        calls=0
        async def complete(self,*args):
            self.calls+=1
            return {'role':'assistant','tool_calls':[{'id':str(self.calls),'type':'function','function':{'name':'query','arguments':json.dumps({'endpoint':'invented_pawn_endpoint'})}}]},{}
        async def close(self):pass
    await rt.model.close();rt.model=Model()
    proposal=await rt.planner.ask('Workforce',{},Proposal)
    assert rt.model.calls==3 and not proposal.actions and proposal.blockers


async def test_each_manager_observes_player_replacements(colony):
    rt,game=colony;seen=[]
    async def ask(role,context,*args):
        seen.append(context['colony']['buildings'])
        if len(seen)==1:
            game.buildings=[{'id':900,'def':'SleepingSpot','position':{'x':130,'y':0,'z':110}}]
        return Proposal(summary='Observed')
    rt.planner.ask=ask
    await rt.planner.proposals({},['Infrastructure','Security'])
    assert seen[0]==[] and seen[1][0]['id']==900


async def test_infrastructure_self_label_does_not_spawn_workforce(colony):
    rt,_=colony;rt.cycle_generation=rt.generation;roles=[]
    async def proposals(context,requested):
        roles.extend(requested)
        return {'Infrastructure':Proposal(summary='Ready',labor=['Infrastructure']).model_dump()}
    async def arbitrate(context,proposed):return Decision(response='Ready',accepted=['Infrastructure'])
    rt.planner.proposals=proposals;rt.planner.arbitrate=arbitrate
    await rt.coordinate({},['Infrastructure'])
    assert roles==['Infrastructure']


def test_manager_catalog_is_generated_from_openapi():
    root=Path(__file__).parents[1]
    subprocess.run([sys.executable,'scripts/generate_manager_catalog.py','--check'],cwd=root,check=True,capture_output=True)


async def test_administrator_receives_native_construction_costs(colony):
    from types import SimpleNamespace
    rt,_=colony
    original=rt.api.call
    class Definition:
        def_name='Bedroll'
        def model_dump(self, **kwargs):
            return {'def_name':'Bedroll','work_to_build':600,'stuff_count':40,'costs':[]}
    async def call(name,args,**kwargs):
        if name=='construction_definitions':
            assert args['limit']<=32
            return SimpleNamespace(items=[Definition()])
        return await original(name,args,**kwargs)
    async def ask(role,context,*args):
        assert context['construction_definitions'][0]['stuff_count']==40
        return Decision(response='Needs cloth.',deferred={'Infrastructure':'Not free.'})
    rt.api.call=call;rt.planner.ask=ask
    proposal=Proposal(summary='Free bedroll',actions=[Action(title='Bedroll',endpoint='construction_place',arguments={'map_id':7,'buildings':[{'def_name':'Bedroll'}]})])
    decision=await rt.planner.arbitrate({}, {'Infrastructure':proposal.model_dump()})
    assert not decision.accepted
