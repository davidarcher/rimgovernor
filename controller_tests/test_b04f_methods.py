from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from test_colony_controller import Replay
from test_medical_recovery import fixture, recover
from rimbot.colony_plan import ColonyGoal
from rimbot.development import development_nodes, power_method, development_method
from rimbot.medical_replacement import replace_doctor


def test_native_player_order_history_prevents_even_completed_external_job_recovery():
    plan, people = fixture()
    people[1]['orderGeneration'] = 1
    assert 'native ordered job' in recover(plan, people)
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


@pytest.mark.asyncio
async def test_existing_research_is_preserved_and_receipt_is_not_completion():
    rt=Replay()
    rt.current_plan.colony_goals['EnsureResearch']=ColonyGoal(priority_class=4)
    rt.facts['development']={'furniture':[{'defName':'SimpleResearchBench'}],
        'research':{'current':'Electricity','progress':20,'available':[],'finished':[]}}
    before=deepcopy(rt.facts)
    assert await development_method(rt.controller.skills,'EnsureResearch',rt.facts,rt.people) is None
    assert rt.facts==before


@pytest.mark.asyncio
async def test_requested_research_selects_available_ancestor_before_unrelated_project():
    rt=Replay()
    rt.current_plan.colony_goals['EnsureResearch']=ColonyGoal(priority_class=4,target={'project':'Requested'})
    rt.facts['development']={'furniture':[{'defName':'SimpleResearchBench'}], 'research':{
        'current':None,'finished':[], 'available':[{'defName':'Unrelated','cost':1},{'defName':'Foundation','cost':100}],
        'projects':[{'defName':'Requested','prerequisites':['Intermediate']},
                    {'defName':'Intermediate','prerequisites':['Foundation']}]}}
    _,actions=await development_method(rt.controller.skills,'EnsureResearch',rt.facts,rt.people)
    assert actions[0]['arguments']['set']=='Foundation'


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
