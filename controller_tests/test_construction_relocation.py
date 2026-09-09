from copy import deepcopy
from unittest.mock import AsyncMock
import pytest
from rimbot.player_commands import apply_command
from test_construction_cancellation import fixture


async def setup(tmp_path):
    rt, rows = await fixture(tmp_path)
    rows[1].update(thingId='Blueprint_Wall2', buildDefName='Wall', isBlueprint=True)
    async def preview(name, arguments, **kwargs):
        if name == 'home/cancel_construction':
            return {'success':True, 'applied':False}
        return {'success':True, 'canPlace':True, 'rotations':[{'accepted':True,'blockingThings':[]}], 'costList':[{'defName':'WoodLog','count':5}],
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
