from copy import deepcopy
from unittest.mock import AsyncMock
import pytest
from rimbot.player_commands import apply_command
from test_construction_cancellation import fixture


async def setup(tmp_path):
    rt, rows = await fixture(tmp_path)
    rows[1].update(thingId='Blueprint_Wall2', buildDefName='Wall', isBlueprint=True)
    async def preview(name, arguments, **kwargs):
        if name=='home/spatial_access':return dict(success=True,accepted=True,pawnCount=1)
        if name == 'home/cancel_construction':
            return {'success':True, 'applied':False}
        return {'success':True, 'canPlace':True, 'passability':'Impassable','isDoor':False,'rotations':[{'accepted':True,'blockingThings':[],
                'rotation':arguments['rotation'],'occupiedCells':[{'x':arguments['x'],'z':arguments['z']}]}], 'costList':[{'defName':'WoodLog','count':5}],
                'materials':{'rows':[{'defName':'WoodLog','available':1000}]}}
    rt.game.invoke = AsyncMock(side_effect=preview)
    return rt, rows


async def relocate(rt):
    return await apply_command(rt, {'kind':'RelocateConstruction', 'intent_id':'room',
        'replacement':{'kind':'place_buildings','placements':[
            {'def_name':'Wall','x':30,'z':30,'materials':['WoodLog']},
            {'def_name':'Wall','x':31,'z':30,'materials':['WoodLog']}]}},
        token=rt.context_token, revision=rt.chat_revision)


@pytest.mark.asyncio
async def test_relocation_commits_validated_dependency_without_native_mutation(tmp_path):
    rt, rows = await setup(tmp_path)
    result = await relocate(rt)
    plan = rt.current_plan
    replacement = next(s for s in plan.spec.steps if s.id == result['step'])
    assert replacement.after[0].step == result['cancellation_step']
    assert replacement.after[0].when == 'complete'
    assert plan.progress['player-room'].state == 'cancelled'
    assert replacement not in plan.ready()
    assert plan.control['player_intents']['room']['step'] == replacement.id
    assert not plan.colony_goals['intent-room'].cancelled
    assert len(rows) == 2
    rt.native.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
@pytest.mark.parametrize('failure', ['placement', 'replacement', 'policy', 'completed', 'missing', 'direction', 'preserve'])
async def test_relocation_refusal_preserves_original_plan_and_orders(tmp_path, failure):
    rt, rows = await setup(tmp_path)
    if failure == 'completed': rows[1]['isBlueprint'] = False
    if failure == 'missing': rows.pop()
    if failure == 'policy':
        rt.current_plan.control['resource_policy'] = {'WoodLog':{'spending':'stop','reserve':0}}
    if failure == 'preserve':
        rt.chat.append({'kind':'human','revision':rt.chat_revision,'text':'Move the plan but keep the existing blueprints.'})
    if failure in ('placement', 'replacement', 'direction'):
        original = rt.game.invoke.side_effect
        async def changed(name, arguments, **kwargs):
            result = await original(name, arguments, **kwargs)
            if name == 'home/place_building':
                if failure == 'placement': result['canPlace'] = False
                elif failure == 'replacement': result['rotations'][0]['blockingThings'] = [{'category':'Building'}]
                else: rt.chat_revision += 1
            return result
        rt.game.invoke.side_effect = changed
    before = deepcopy(rt.current_plan.model_dump())
    native_before = deepcopy(rows)
    with pytest.raises(ValueError): await relocate(rt)
    assert rt.current_plan.model_dump() == before
    assert rows == native_before
    rt.native.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
async def test_interrupted_removal_keeps_replacement_blocked(tmp_path):
    rt, rows = await setup(tmp_path)
    result = await relocate(rt)
    rt.current_plan.progress[result['cancellation_step']].issued['0'] = {'confirmed':False}
    await rt.execute_manual_requests()
    plan = rt.current_plan
    assert plan.progress[result['cancellation_step']].state == 'blocked'
    assert plan.progress[result['step']].state == 'pending'
    assert len(rows) == 2
    rt.native.assert_not_awaited()
    rt.store.close()


@pytest.mark.asyncio
async def test_manual_dispatch_advances_only_newly_ready_accepted_dependencies(tmp_path):
    rt, rows = await setup(tmp_path)
    result = await relocate(rt)
    calls=[]
    async def advance(runtime, max_operations, only_ids):
        calls.append((set(only_ids), max_operations))
        for identity in only_ids:
            runtime.current_plan.progress[identity].state='complete'
            runtime.current_plan.progress[identity].issued['0']={'confirmed':True}
    rt.hands.advance=AsyncMock(side_effect=advance)
    await rt.execute_manual_requests()
    assert calls == [({result['cancellation_step']},512), ({result['step']},511)]
    assert rt.mode=='manual' and rt.manual_execution is None
    rt.store.close()


@pytest.mark.asyncio
async def test_manual_dependency_stops_on_direction_change(tmp_path):
    rt, rows = await setup(tmp_path)
    result = await relocate(rt)
    async def advance(runtime, max_operations, only_ids):
        runtime.current_plan.progress[result['cancellation_step']].state='complete'
        runtime.chat_revision+=1
    rt.hands.advance=AsyncMock(side_effect=advance)
    await rt.execute_manual_requests()
    rt.hands.advance.assert_awaited_once()
    assert rt.current_plan.progress[result['step']].state=='pending'
    rt.store.close()


@pytest.mark.asyncio
async def test_unissued_room_relocation_uses_shared_refinement_without_removal(tmp_path,monkeypatch):
    from rimbot.colony_plan import RoomShell,StepProgress
    from rimbot.player_commands import RelocateConstruction
    from rimbot.construction_relocation import relocate as apply_relocation
    rt,rows=await setup(tmp_path)
    shell=RoomShell(bounds={'x':20,'z':20,'width':4,'height':4},wall_def='Wall',door_def='Door',materials=['WoodLog'],entrance='south')
    rt.current_plan.spec.steps[0].action=shell
    rt.current_plan.progress['player-room']=StepProgress()
    request=RelocateConstruction(kind='RelocateConstruction',intent_id='player-room',replacement=shell.model_copy(update={'entrance':'north'}))
    refine=AsyncMock(return_value={'step':'refined'})
    monkeypatch.setattr('rimbot.player_commands.apply_command',refine)
    result=await apply_relocation(rt,request,token=rt.context_token,revision=rt.chat_revision)
    assert result=={'step':'refined'}
    payload=refine.await_args.args[1]
    assert payload['kind']=='BuildRoom' and payload['intent_id']=='room'
    assert payload['room']['entrance']=='north' and payload['room']['bounds']==shell.bounds.model_dump()
    rt.game.invoke.assert_not_awaited()
    assert len(rows)==2
    rt.store.close()


@pytest.mark.asyncio
async def test_issued_relocation_allows_unrelated_admission_but_uncertain_slots_stay_strict(tmp_path):
    from rimbot.colony_plan import PlanStep,StepProgress
    from rimbot.construction_preflight import preflight_construction,ConstructionRefusal
    rt,rows=await setup(tmp_path)
    result=await relocate(rt)
    plan=rt.current_plan
    plan.progress[result['step']]=StepProgress(state='waiting',issued={'0':{'confirmed':True},'1':{'confirmed':True}})
    plan.progress[result['cancellation_step']].state='complete'
    original=rt.game.invoke.side_effect
    async def observed(name,args,**kwargs):
        reply=await original(name,args,**kwargs)
        if name=='home/place_building' and args['x'] in (30,31):
            reply['canPlace']=False
            reply['rotations'][0].update(accepted=False,blockingThings=[{'thingId':'OwnedBlueprint','isBlueprint':True}])
        return reply
    rt.game.invoke=AsyncMock(side_effect=observed)
    spec=plan.spec.model_copy(deep=True)
    spec.steps.append(PlanStep(id='unrelated',title='Unrelated wall',completion_criteria='Built',action={
        'kind':'place_buildings','placements':[{'def_name':'Wall','x':40,'z':30,'materials':['WoodLog']}]}))
    await preflight_construction(spec,plan,rt.game)
    plan.progress[result['step']].issued['0']['confirmed']=False
    with pytest.raises(ConstructionRefusal):await preflight_construction(spec,plan,rt.game)
    rt.native.assert_not_awaited()
    rt.store.close()
