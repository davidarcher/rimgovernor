from types import SimpleNamespace
from unittest.mock import AsyncMock,Mock

import pytest

from rimbot.colony_plan import ColonyPlan
from rimbot.colony_skills import ColonySkills
from test_construction_preflight import native_reply


def fixture(monkeypatch):
    candidates=[dict(room=dict(x=x,z=10,width=9,height=9),farm=None,farms=[]) for x in (10,30)]
    monkeypatch.setattr('rimbot.colony_skills.starter_layouts',lambda _:candidates)
    async def invoke(name,args,**kwargs):
        result=native_reply(name,args,canPlace=True)
        if name=='home/place_building':
            result.update(costList=[dict(defName='WoodLog',count=3 if args['x']<20 else 1)],
                materials=dict(rows=[dict(defName='WoodLog',available=64)]))
        if name=='home/spatial_access':
            x,z=map(int,args['targetCells'].split(';')[0].split(','))
            result['pawns']=[dict(targets=[dict(x=x,z=z,nativeReachable=True,projectedSteps=10 if x==14 else 30),
                dict(x=999,z=999,nativeReachable=True,projectedSteps=0)])]
        return result
    rt=SimpleNamespace(current_plan=ColonyPlan(),context_token='load',chat_revision=0,
        controller=SimpleNamespace(policy=SimpleNamespace(max_method_attempts=3)),
        game=SimpleNamespace(invoke=AsyncMock(side_effect=invoke)),
        ensure_context=AsyncMock(),persist=Mock(),note=Mock())
    return rt,candidates


@pytest.mark.asyncio
async def test_layout_compares_native_supplies_before_travel_without_issuing_work(monkeypatch):
    rt,candidates=fixture(monkeypatch)
    selected=await ColonySkills(rt).layout({})
    assert selected==candidates[1]
    evidence=rt.current_plan.control['layout_comparison']
    assert evidence[0]['shortage']>0 and evidence[1]['shortage']==0
    assert evidence[0]['travel_steps']<evidence[1]['travel_steps']
    assert not rt.current_plan.spec.steps
    assert all(call.kwargs==dict(allow_write=False) for call in rt.game.invoke.await_args_list)


@pytest.mark.asyncio
async def test_refused_site_is_skipped_and_retains_native_reason(monkeypatch):
    rt,candidates=fixture(monkeypatch);original=rt.game.invoke.side_effect
    async def invoke(name,args,**kwargs):
        result=await original(name,args,**kwargs)
        if name=='home/spatial_access' and args['targetCells'].startswith('14,'):result['accepted']=False
        return result
    rt.game.invoke.side_effect=invoke
    assert await ColonySkills(rt).layout({})==candidates[1]
    assert rt.current_plan.control['layout_comparison'][0]['accepted'] is False


@pytest.mark.asyncio
async def test_player_direction_during_comparison_cannot_publish_layout(monkeypatch):
    rt,_=fixture(monkeypatch);original=rt.game.invoke.side_effect
    async def invoke(name,args,**kwargs):
        result=await original(name,args,**kwargs)
        if name=='home/spatial_access':rt.chat_revision+=1
        return result
    rt.game.invoke.side_effect=invoke
    with pytest.raises(InterruptedError):await ColonySkills(rt).layout({})
    assert not rt.current_plan.control and not rt.current_plan.spec.steps
