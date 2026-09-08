import json
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from pydantic import ValidationError
from rimbot.visual_review import review, Region
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store


def response():
    return {'tool_calls':[{'function':{'name':'report','arguments':json.dumps({
        'answer':'Possible missing entrance.','concerns':[{'observation':'No visible doorway on this side.',
        'region':{'left':.1,'top':.2,'right':.5,'bottom':.6},'confidence':'low',
        'verify':'Inspect the perimeter for a door.'}],'missing_facts':['Other walls are outside the view.']})}}]}


@pytest.mark.asyncio
async def test_blind_report_has_only_image_question_and_one_report_tool():
    router=SimpleNamespace(complete=AsyncMock(return_value=(response(),{})))
    result=await review(router,'Check entrances','data:image/png;base64,abc',{'load_token':'load','tick':1},AsyncMock())
    args=router.complete.call_args.args
    assert len(args[1])==2
    assert [t['function']['name'] for t in args[2]]==['report']
    assert result['requires_native_verification'] and result['report']['concerns'][0]['confidence']=='low'


@pytest.mark.parametrize('right,bottom',[(.1,.6),(.5,.2),(1.1,.6)])
def test_region_must_be_valid(right,bottom):
    with pytest.raises(ValidationError):Region(left=.1,top=.2,right=right,bottom=bottom)


@pytest.mark.asyncio
async def test_no_report_or_extra_calls_rejected():
    router=SimpleNamespace(complete=AsyncMock(return_value=({'tool_calls':[]},{})))
    with pytest.raises(ValueError,match='one report'):
        await review(router,'Check','data:image/png;base64,abc',{'load_token':'load','tick':1},AsyncMock())


@pytest.mark.asyncio
@pytest.mark.parametrize('stale',[False,True])
async def test_fresh_capture_and_stale_discard(tmp_path,stale):
    rt=BridgeRuntime(Store(tmp_path/'state.sqlite'),tmp_path)
    rt.context_token='load';rt.sync_identity=AsyncMock(return_value=False)
    path=tmp_path/'native.png';path.write_bytes(b'\x89PNG\r\n\x1a\n'+b'test')
    rt.bridge=SimpleNamespace(call=AsyncMock(return_value=SimpleNamespace(structuredContent={'path':str(path)})))
    rt.game=SimpleNamespace(query=AsyncMock(return_value={'time':{'ticksGame':4}}))
    async def complete(*args):
        if stale:rt.chat_revision+=1
        return response(),{}
    rt.router=SimpleNamespace(enabled=lambda _:True,complete=AsyncMock(side_effect=complete))
    if stale:
        with pytest.raises(ValueError,match='discarded'):
            await rt.visual_review('Check',expected_token='load',expected_revision=0)
        assert not rt.advice
    else:
        result=await rt.visual_review('Check',expected_token='load',expected_revision=0)
        assert result['source']['tick']==4 and len(result['source']['image_sha256'])==64
        assert result['id'] in rt.advice
    assert [c.args[0] for c in rt.bridge.call.call_args_list]==['rimworld/take_screenshot']
    rt.store.close()


@pytest.mark.asyncio
async def test_headless_never_uses_old_camera_image(tmp_path):
    rt=BridgeRuntime(Store(tmp_path/'headless.sqlite'),tmp_path,headless=True)
    rt.bridge=SimpleNamespace(call=AsyncMock())
    with pytest.raises(ValueError,match='headless'):
        await rt.visual_review('Check',expected_token='load',expected_revision=0)
    rt.bridge.call.assert_not_awaited()
    rt.store.close()
