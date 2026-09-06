import json
from pathlib import Path
import httpx
import pytest
from rimbot.contracts import Action,Proposal

@pytest.mark.parametrize('lost_response',[False,True])
async def test_bulk_allow_is_immediate_and_lost_response_reconciles(colony,lost_response):
    rt,game=colony
    manifest=json.loads((Path(__file__).parents[1]/'controller/rimbot/data/construction_contracts.json').read_text())
    rt.catalog.install_contracts(manifest)
    previous=rt.api.http._transport
    state={'remaining':4,'writes':0}
    def handle(request):
        if request.url.path.startswith('/api/v2/orders/'):
            changed=0
            if request.url.path.endswith('/unforbid-all'):
                changed=state['remaining'];state['remaining']=0;state['writes']+=1
                if lost_response:raise httpx.ReadTimeout('Lost receipt after applying order')
            return httpx.Response(200,json={'map_id':7,'changed_count':changed,'excluded_jelly_count':2,'remaining_eligible_count':state['remaining']})
        return game.handle(request)
    rt.api.http._transport=httpx.MockTransport(handle)
    rt.mode='automate';rt.cycle_generation=rt.generation
    action=Action(title='Allow supplies except jelly',endpoint='orders_unforbid_all',arguments={'map_id':7})
    rt.planner.validate_submission('Executor:supply_access',Proposal(summary='Release supplies',actions=[action]))
    await rt.planner.validate_observation(Proposal(summary='Release supplies',actions=[action]))
    if lost_response:
        with pytest.raises(RuntimeError):await rt.execute(action,'Executor:supply_access')
        await rt.reconcile()
    else:await rt.execute(action,'Executor:supply_access')
    assert state['writes']==1
    assert rt.memory['work'][-1]['status']=='complete'
    await rt.execute(action,'Executor:supply_access')
    assert state['writes']==1
    rt.api.http._transport=previous
