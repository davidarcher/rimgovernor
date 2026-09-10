import json
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from pydantic import ValidationError
from rimbot.visual_review import review, Region
from rimbot.bridge_runtime import BridgeRuntime
from rimbot.store import Store
import io
from PIL import Image


def png():
    stream=io.BytesIO()
    Image.new("RGB", (320, 240), "green").save(stream, format="PNG")
    return stream.getvalue()


def bridge_for(path):
    async def call(name, **kwargs):
        value = {"mapId":1,"mapPosition":{"x":10,"z":20},"rootSize":12} if name == "rimworld/get_camera_state" else {"path":str(path)}
        return SimpleNamespace(structuredContent=value)
    return SimpleNamespace(call=AsyncMock(side_effect=call))


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
    path=tmp_path/'native.png';path.write_bytes(png())
    rt.bridge=bridge_for(path)
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
    assert [c.args[0] for c in rt.bridge.call.call_args_list]==['home/render_demand','rimworld/get_camera_state','rimworld/take_screenshot','rimworld/get_camera_state']
    rt.store.close()


@pytest.mark.asyncio
async def test_camera_change_during_inference_keeps_original_source_without_restoration(tmp_path):
    rt=BridgeRuntime(Store(tmp_path/'camera.sqlite'),tmp_path)
    rt.context_token='load';rt.sync_identity=AsyncMock(return_value=False)
    path=tmp_path/'native.png';path.write_bytes(png());rt.bridge=bridge_for(path)
    rt.game=SimpleNamespace(query=AsyncMock(return_value={'time':{'ticksGame':4}}))
    player_camera={'x':10,'z':20}
    async def complete(*args):
        player_camera['x']=99
        return response(),{}
    rt.router=SimpleNamespace(enabled=lambda _:True,complete=AsyncMock(side_effect=complete))
    result=await rt.visual_review('Check',expected_token='load',expected_revision=0)
    assert result['source']['camera']['mapPosition']=={'x':10,'z':20}
    assert player_camera['x']==99
    assert [c.args[0] for c in rt.bridge.call.call_args_list]==[
        'home/render_demand','rimworld/get_camera_state','rimworld/take_screenshot','rimworld/get_camera_state']
    rt.store.close()


@pytest.mark.asyncio
async def test_headless_never_uses_old_camera_image(tmp_path):
    rt=BridgeRuntime(Store(tmp_path/'headless.sqlite'),tmp_path,headless=True)
    rt.bridge=SimpleNamespace(call=AsyncMock())
    with pytest.raises(ValueError,match='headless'):
        await rt.visual_review('Check',expected_token='load',expected_revision=0)
    rt.bridge.call.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
async def test_image_consultation_captures_fresh_source_preserving_advice_schema(tmp_path):
    import base64
    import hashlib
    from rimbot.consultation import Consultations
    rt=BridgeRuntime(Store(tmp_path/'consult.sqlite'),tmp_path)
    rt.context_token='load';rt.sync_identity=AsyncMock(return_value=False)
    old=tmp_path/'old.png';old.write_bytes(b'old cached image');rt.camera_path=old
    fresh=tmp_path/'fresh.png';data=png();fresh.write_bytes(data)
    rt.bridge=bridge_for(fresh)
    rt.game=SimpleNamespace(query=AsyncMock(return_value={'time':{'ticksGame':14}}))
    advice={'answer':'Inspect the entrance.','confidence':'low','recommendations':['Read the door state.']}
    router=SimpleNamespace(complete=AsyncMock(return_value=({'tool_calls':[{'function':{'name':'report','arguments':json.dumps(advice)}}]},{})))
    rt.consultations=Consultations(router)
    result=await rt.consult('architect','Check entrances',['construction'],True,expected_token='load',expected_revision=0)
    messages=router.complete.call_args.args[1]
    content=messages[1]['content']
    assert content[1]['image_url']['url']=='data:image/png;base64,'+base64.b64encode(data).decode()
    assert result['image_source']['image_sha256']==hashlib.sha256(data).hexdigest()
    assert result['image_source']['tick']==14
    assert result['report']['recommendations']==['Read the door state.']
    assert result['requires_native_verification']
    assert [c.args[0] for c in rt.bridge.call.call_args_list]==['home/render_demand','rimworld/get_camera_state','rimworld/take_screenshot','rimworld/get_camera_state']
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('reason',['headless','role','changed_capture','changed_inference'])
async def test_image_consultation_rejects_unusable_or_stale_context(tmp_path,reason):
    rt=BridgeRuntime(Store(tmp_path/'reject.sqlite'),tmp_path,headless=reason=='headless')
    rt.context_token='load';rt.sync_identity=AsyncMock(return_value=False)
    path=tmp_path/'cached.png';path.write_bytes(png());rt.camera_path=path
    rt.bridge=bridge_for(path)
    async def status(*args,**kwargs):
        if reason=='changed_capture':rt.context_token='other-load'
        return {'time':{'ticksGame':15}}
    rt.game=SimpleNamespace(query=AsyncMock(side_effect=status))
    async def ask(*args):
        if reason=='changed_inference':rt.chat_revision+=1
        return {'id':'report'}
    rt.consultations=SimpleNamespace(ask=AsyncMock(side_effect=ask))
    with pytest.raises(ValueError):
        await rt.consult('analyst' if reason=='role' else 'architect','Check',['construction'],True,
                         expected_token='load',expected_revision=0)
    assert not rt.advice
    if reason!='changed_inference':rt.consultations.ask.assert_not_awaited()
    if reason in ('headless','role'):rt.bridge.call.assert_not_awaited()
    rt.store.close()
