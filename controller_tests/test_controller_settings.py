from dataclasses import asdict
import pytest
import httpx
from pydantic import ValidationError
from rimbot.colony_plan import ColonyGoal
from rimbot.controller_settings import PolicyUpdate, PolicyChanges, settings_state, update_policy
from rimbot.bridge_server import create_app
from test_strategic_architecture import runtime


def request(rt,**changes):
    return PolicyUpdate(session_id=rt.context_token,expected_version=settings_state(rt.current_plan)['version'],changes=changes)


@pytest.mark.asyncio
async def test_settings_persist_shared_food_target_and_invalidate_orders_without_inference(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.connected=True
    rt.current_plan.colony_goals['EnsureFoodSupply']=ColonyGoal(priority_class=2,cancelled=True)
    rt.current_plan.control['latches']={'food':True}
    direction=rt.chat_revision
    result=await update_policy(rt,request(rt,food_target_days=20,execution_speed='Fast'))
    assert result['values']['food_target_days']==20 and result['source']=='PLAYER'
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].target=={'food_days':20}
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].cancelled
    assert rt.chat_revision==direction+1 and rt.wake.is_set()
    assert rt.current_plan.control['latches']=={}
    assert not any(m['kind']=='human' for m in rt.chat) and rt.counters['model_calls']==0
    saved=rt.store.get('bridge:'+rt.colony)
    assert saved['current_plan']['control']['policy']['food_target_days']==20
    rt.store.close()


@pytest.mark.asyncio
async def test_stale_settings_invalid_cross_fields_and_wrong_colony_are_atomic(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.connected=True
    stale=request(rt,wood_target=400)
    rt.current_plan.control['policy']={'food_target_days':20}
    before=rt.current_plan.model_dump()
    with pytest.raises(ValueError,match='another view'):await update_policy(rt,stale)
    with pytest.raises(ValueError,match='Temperatures'):await update_policy(rt,request(rt,temperature_exit_low=35))
    with pytest.raises(ValueError,match='thresholds'):await update_policy(rt,request(rt,wood_target=1))
    wrong=request(rt,wood_target=400);wrong.session_id='other-colony'
    with pytest.raises(ValueError,match='loaded colony'):await update_policy(rt,wrong)
    assert rt.current_plan.model_dump()==before
    rt.store.close()


@pytest.mark.parametrize('change',[{}, {'wood_target':None},{'wood_target':2.5},{'food_target_days':float('nan')},{'foothold_food_days':0},{'unknown':1}])
def test_invalid_or_readonly_policy_fields_are_rejected(change):
    with pytest.raises(ValidationError):PolicyChanges.model_validate(change)


@pytest.mark.asyncio
async def test_settings_route_requires_local_mutation_header_and_version(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.connected=True
    app=create_app(rt);app.state.rt=rt
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app),base_url='http://testserver') as client:
        body=request(rt,execution_speed='Fast').model_dump(exclude_unset=True)
        assert (await client.post('/api/autopilot/settings',json=body)).status_code==403
        response=await client.post('/api/autopilot/settings',json=body,headers={'X-RimBot':'1'})
        assert response.status_code==200 and response.json()['values']['execution_speed']=='Fast'
        assert (await client.post('/api/autopilot/settings',json=body,headers={'X-RimBot':'1'})).status_code==400
    rt.store.close()
