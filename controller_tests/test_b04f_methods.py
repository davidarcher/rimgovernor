from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from test_colony_controller import Replay
from test_medical_recovery import fixture, recover
from rimgovernor.colony_plan import ColonyGoal
from rimgovernor.development import development_nodes, power_method, development_method
from rimgovernor.medical_replacement import replace_doctor
from rimgovernor.colony_policy import derive, work_assignment


@pytest.mark.asyncio
async def test_native_building_skill_requirement_blocks_before_placement():
    from rimgovernor.development import placement
    from rimgovernor.colony_skills import SkillBlocked
    rt=Replay();rt.facts['definitions']['Heater']={'available':True,'constructionSkill':5}
    rt.game.query=AsyncMock(return_value={'pawns':rt.people})
    for p in rt.people:
        for s in p['bio']['skills']:
            if s['name']=='Construction':s['level']=4
    with pytest.raises(SkillBlocked,match='level 5 builder'):
        await placement(rt,rt.facts,'Heater')


def test_construction_preserves_the_highest_native_skill_under_load():
    rt=Replay()
    for i,p in enumerate(rt.people):
        for s in p['bio']['skills']:
            s['level']=8 if i==0 else 1
        p['work']['manualPriorities']=False
    assignments,_=work_assignment(rt.people)
    assert assignments[rt.people[0]['thingId']]['Construction']==1
    for i,p in enumerate(rt.people):
        for s in p['bio']['skills']:
            if s['name']=='Construction':s.update(level=5 if i==0 else 4,passion='None' if i==0 else 'Major')
    assignments,_=work_assignment(rt.people)
    assert assignments[rt.people[0]['thingId']]['Construction']==1


def test_downed_permanent_manhunter_does_not_keep_active_combat_open():
    rt=Replay()
    rt.batch.summary.hostile_count=2
    rt.batch.native['status_after']={'threats':{'hostiles':[{'thingId':'Hare1','downed':True},{'thingId':'Hare2','downed':False}]}}
    assert derive(rt.batch,rt.facts,rt.controller.policy)['hostiles']==1
    rt.batch.native['status_after']['threats']['hostiles'][0]['downed']=False
    assert derive(rt.batch,rt.facts,rt.controller.policy)['hostiles']==2


def test_checkbox_researcher_can_work_before_endless_cleaning_and_respects_override():
    rt=Replay(8)
    for i,p in enumerate(rt.people):
        p['work']['manualPriorities']=False
        p['work']['types'].append(dict(name='Research',disabled=False,priority=0,priorityStored=0))
        p['bio']['skills'].append(dict(name='Intellectual',level=20 if i==3 else 1,disabled=False))
    assignments,covered=work_assignment(rt.people,{'Research':'Intellectual'})
    assert covered
    researcher=assignments[rt.people[3]['thingId']]
    assert researcher['Research']==1 and researcher['Hauling']==researcher['Cleaning']==0
    assignments,covered=work_assignment(rt.people,{'Research':'Intellectual'},{rt.people[3]['thingId']:{'Research':0}})
    assert assignments[rt.people[3]['thingId']]['Research']==0


def test_native_player_order_history_prevents_even_completed_external_job_recovery():
    plan, people = fixture()
    people[1]['orderGeneration'] = 1
    assert 'Native player order' in recover(plan, people)
    assert plan.progress['tend'].state == 'blocked'
    assert plan.progress['tend'].issued


@pytest.mark.asyncio
async def test_group_defense_preserves_every_exact_opponent():
    rt = Replay(6)
    rt.draft_owners = {}
    for p in rt.people:
        p['bio'].update(incapableOfRead=True,incapableOfTags=[])
    goal = rt.current_plan.colony_goals['ActiveCombat'] = ColonyGoal(priority_class=0)
    enemies = [dict(thingId=f'animal{i}',hostile=True,animal=True,mentalState='Manhunter',
                   animals={'bodySize':1.5},nearestColonistDistance=10) for i in range(3)]
    rt.game.query = AsyncMock(return_value={'pawns':enemies})
    _, actions = await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)
    assert len(actions)==6
    assert {a['arguments']['target'] for a in actions} == {e['thingId'] for e in enemies}
    assert goal.evidence['combat_targets'] == [e['thingId'] for e in enemies]


@pytest.mark.asyncio
async def test_power_route_uses_native_previews_and_existing_conduit():
    rt=Replay()
    rt.facts['development']={'power':[
        dict(id='load',x=30,z=30,baseW=-100,powered=False,outputW=0,net=None),
        dict(id='generator',x=40,z=30,baseW=1000,powered=True,outputW=1000,net=2)],
        'furniture':[dict(defName='PowerConduit',x=40,z=30)]}
    _, actions=await power_method(rt,rt.facts)
    assert len(actions[0]['placements'])==8
    assert all(p['def_name']=='PowerConduit' for p in actions[0]['placements'])
    assert not any((p['x'],p['z'])==(40,30) for p in actions[0]['placements'])
    rt.facts['development']['furniture']=[]
    rt.facts['development']['power'][1]['occupiedCells']=[{'x':x,'z':30} for x in (39,40,41)]
    _,actions=await power_method(rt,rt.facts)
    assert all(p['x']<39 for p in actions[0]['placements'])


@pytest.mark.asyncio
@pytest.mark.parametrize('stale', [False,True])
async def test_unavailable_doctor_gets_new_action_preserving_old_receipt(stale):
    plan, people=fixture()
    people[1]['downed']=True
    alternate=deepcopy(people[1])
    alternate.update(thingId='Thing_Alternate',downed=False,bio={'skills':[{'name':'Medicine','level':9}]})
    people.append(alternate)
    rt=SimpleNamespace(current_plan=plan,mode='automate',context_token='load',chat_revision=0,
        game=SimpleNamespace(describe=AsyncMock(return_value={'type':'object'})),
        sync_identity=AsyncMock(),refresh_clock_events=AsyncMock())
    async def preview(*args,**kwargs):
        if stale:rt.chat_revision+=1
        return {'success':True}
    rt.game.invoke=preview
    old=deepcopy(plan.progress['tend'].issued)
    await replace_doctor(rt,plan.spec.steps[0],people,tick=200,token='load',direction=0,revision=0,limit=3)
    assert plan.progress['tend'].issued==old
    if stale:
        assert len(plan.spec.steps)==1 and plan.progress['tend'].state=='blocked'
    else:
        assert len(plan.spec.steps)==2 and plan.progress['tend'].state=='cancelled'
        assert plan.spec.steps[0].action.arguments['pawn']=='Thing_Doctor'
        assert plan.spec.steps[1].action.arguments['pawn']=='Thing_Alternate'
        assert plan.progress[plan.spec.steps[1].id].state=='pending'
        assert plan.spec.steps[0].signature() not in plan.cancelled_actions


@pytest.mark.parametrize('body_size,reason,fallback', [(.2, 'Verb.CanHitTarget is false', True),
    (1.5, 'Verb.CanHitTarget is false', False), (.2, 'Player order changed', False)])
async def test_small_animal_defense_retains_native_defensive_fire_after_range_refusal(body_size, reason, fallback):
    from mcp.types import CallToolResult
    from rimgovernor.bridge import BridgeError
    rt = Replay(2)
    rt.draft_owners = {}
    for pawn in rt.people:
        pawn['bio'].update(incapableOfRead=True, incapableOfTags=[])
        pawn['equipment'] = {'primary': {'ranged': True}}
    rt.current_plan.colony_goals['ActiveCombat'] = ColonyGoal(priority_class=0)
    rt.game.query = AsyncMock(return_value={'pawns': [dict(thingId='animal', hostile=True, animal=True,
        mentalState='Manhunter', animals={'bodySize': body_size}, nearestColonistDistance=35)]})
    async def preview(tool, args):
        if args.get('mode') == 'ranged':
            raise BridgeError(tool, CallToolResult(content=[], isError=True, structuredContent={'message': reason}))
        return {'success': True}
    rt.inspect_native = AsyncMock(side_effect=preview)
    if fallback:
        _, actions = await rt.controller.skills.compile('ActiveCombat', rt.facts, rt.people)
        assert len(actions) == 2 and all(a['arguments']['action'] == 'draft' for a in actions)
    else:
        with pytest.raises(BridgeError):
            await rt.controller.skills.compile('ActiveCombat', rt.facts, rt.people)
        assert rt.inspect_native.await_count == 1
