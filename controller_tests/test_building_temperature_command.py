from unittest.mock import AsyncMock
from copy import deepcopy
import pytest
from rimbot.player_commands import apply_command
from test_strategic_architecture import runtime, batch


async def fixture(tmp_path):
    rt=runtime(tmp_path);await rt.sync_identity();rt.batch=batch();rt.mode='manual'
    original=rt.game.query
    async def query(name, **args):
        if name=='home/list_buildings':
            return {'success':True,'buildings':[{'thingId':'Cooler1','isBlueprint':False,'isFrame':False}]}
        return await original(name, **args)
    rt.game.query=AsyncMock(side_effect=query)
    rt.game.describe=AsyncMock(return_value={'type':'object','properties':{
        'thing':{'type':'string'},'temperature':{'type':'number'},'dryRun':{'type':'boolean'},'watch':{'type':'boolean'}},
        'additionalProperties':False})
    rt.inspect_native=AsyncMock(return_value={'success':True,'refused':[],
        'fields':[{'field':'temperature','refused':False,'after':-10}]})
    return rt


@pytest.mark.asyncio
async def test_exact_temperature_request_uses_shared_hands(tmp_path):
    rt=await fixture(tmp_path)
    result=await apply_command(rt,{'kind':'SetBuildingTemperature','thing':'Cooler1','celsius':-10},
        token=rt.context_token,revision=rt.chat_revision)
    step=next(s for s in rt.current_plan.spec.steps if s.id==result['step'])
    assert step.action.tool=='home/building_config'
    assert step.action.arguments=={'thing':'Cooler1','temperature':-10,'watch':False,'dryRun':False}
    assert step.source=='PLAYER' and rt.mode=='manual'
    assert rt.manual_requests==[(step.id,rt.context_token,rt.chat_revision)]
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('failure',['label','native_refusal','truncated','blueprint'])
async def test_temperature_refusal_does_not_accept_an_unverified_target(tmp_path,failure):
    rt=await fixture(tmp_path)
    if failure=='native_refusal':rt.inspect_native.return_value['refused']=[{'reason':'No temperature control'}]
    if failure in ('truncated','blueprint'):
        original=rt.game.query.side_effect
        async def changed(name, **args):
            result=await original(name, **args)
            if name=='home/list_buildings':
                if failure=='truncated':result['skipped']={'byMaxDetailed':1}
                else:result['buildings'][0]['isBlueprint']=True
            return result
        rt.game.query.side_effect=changed
    before=deepcopy(rt.current_plan.model_dump())
    with pytest.raises(ValueError):
        await apply_command(rt,{'kind':'SetBuildingTemperature','thing':'Cooler' if failure=='label' else 'Cooler1','celsius':-10},
            token=rt.context_token,revision=rt.chat_revision)
    assert rt.current_plan.model_dump()==before and not rt.manual_requests
    rt.store.close()
