from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.colony_plan import ColonyPlan
from rimbot.construction_preflight import preflight_construction
from rimbot.hands import Hands
from rimbot.shell_site import ShellSiteRefusal
from test_construction_preflight import plan, native_reply
from test_shell_site import runtime


@pytest.mark.asyncio
async def test_cancelled_unbuilt_shell_does_not_project_walls_or_own_space():
    from rimbot.colony_plan import StepProgress,PlanStep
    current=ColonyPlan(spec=plan())
    identity=current.spec.steps[0].id
    current.progress[identity]=StepProgress(state='cancelled')
    spec=current.spec.model_copy(deep=True)
    spec.steps.append(PlanStep(id='replacement',title='New use of cancelled site',completion_criteria='Observed',
        action=dict(kind='place_buildings',placements=[dict(def_name='SleepingSpot',x=10,z=10)])))
    requests=[]
    async def invoke(name,args,**kwargs):
        if name=='home/spatial_access':requests.append(args)
        reply=native_reply(name,args,canPlace=True)
        if name=='home/place_building':reply.update(passability='Standable',isDoor=False)
        return reply
    await preflight_construction(spec,current,SimpleNamespace(invoke=AsyncMock(side_effect=invoke)))
    assert requests==[dict(blockedCells='',targetCells='')]


@pytest.mark.asyncio
@pytest.mark.parametrize('evidence,code',[
    ({'success':False,'error':'No paused map'},'incomplete_pawn_access'),
    ({'success':True,'accepted':True},'incomplete_pawn_access'),
    ({'success':True,'accepted':True,'pawnCount':0},'incomplete_pawn_access'),
    ({'success':True,'accepted':False,'pawnCount':1,'pawns':[{'lostCellCount':70}]},'projected_pawn_access'),
])
async def test_native_route_refusal_preserves_uncommitted_plan(evidence,code):
    current=ColonyPlan();before=current.model_dump()
    async def invoke(name,args,**kwargs):
        return evidence if name=='home/spatial_access' else native_reply(name,args,canPlace=True)
    with pytest.raises(ShellSiteRefusal) as error:
        await preflight_construction(plan(),current,SimpleNamespace(invoke=AsyncMock(side_effect=invoke)))
    assert error.value.code==code and error.value.evidence['native']==evidence
    assert current.model_dump()==before


@pytest.mark.asyncio
async def test_native_projection_uses_wall_footprints_and_excludes_door():
    requests=[]
    async def invoke(name,args,**kwargs):
        if name=='home/spatial_access':requests.append(args)
        return native_reply(name,args,canPlace=True)
    await preflight_construction(plan(),ColonyPlan(),SimpleNamespace(invoke=AsyncMock(side_effect=invoke)))
    assert len(requests)==1
    assert '12,10' not in requests[0]['blockedCells'].split(';')
    assert len(requests[0]['blockedCells'].split(';'))==11
    assert set(requests[0]['targetCells'].split(';'))=={'12,9','12,11'}


@pytest.mark.asyncio
async def test_changed_native_routes_between_batches_preserve_prior_receipts():
    rt,_=runtime();await Hands().advance(rt,max_operations=1)
    issued=deepcopy(rt.current_plan.progress['room'].issued)
    original=rt.game.query.side_effect
    async def query(name,**args):
        if name=='home/spatial_access':return dict(success=True,accepted=False,pawnCount=1,pawns=[dict(lostCellCount=400)])
        return await original(name,**args)
    rt.game.query.side_effect=query
    await Hands().advance(rt)
    progress=rt.current_plan.progress['room']
    assert rt.native.await_count==1 and progress.issued==issued
    assert progress.failure.code=='projected_pawn_access'


@pytest.mark.asyncio
async def test_manual_input_during_route_audit_prevents_dispatch():
    rt,_=runtime();original=rt.game.query.side_effect
    async def query(name,**args):
        result=await original(name,**args)
        if name=='home/spatial_access':rt.mode='manual'
        return result
    rt.game.query.side_effect=query
    await Hands().advance(rt)
    rt.native.assert_not_awaited()
    assert not rt.current_plan.progress['room'].issued
