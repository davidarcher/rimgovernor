from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyGoal
from rimbot.colony_skills import SkillBlocked
from test_colony_controller import Replay
from rimbot.bridge import BridgeError
from mcp.types import CallToolResult
from rimbot.colony_policy import derive,ColonyPolicy


def fixture():
    rt=Replay();rt.draft_owners={}
    rt.current_plan.colony_goals['ActiveCombat']=ColonyGoal(priority_class=0)
    for pawn in rt.people:
        pawn['bio'].update(incapableOfRead=True,incapableOfTags=[])
        pawn['health']={'summaryPct':1,'needsTend':False}
        pawn['equipment']={'armed':True,'primary':{'ranged':True,'melee':False}}
    enemy=dict(thingId='Thing_Human99',hostile=True,humanlike=True,mechanoid=False,animal=False,
        nearestColonistDistance=20,equipment={'armed':True,'primary':{'melee':True,'ranged':False}})
    rt.game.query=AsyncMock(return_value={'pawns':[enemy]})
    rt.inspect_native=AsyncMock(return_value={'success':True})
    return rt,enemy


@pytest.mark.asyncio
async def test_single_melee_raider_requires_three_healthy_defenders_and_native_hostility():
    rt,enemy=fixture()
    _,actions=await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)
    assert len(actions)==3 and all(a['arguments']['requireHostile'] is True for a in actions)
    assert all(a['arguments']['target']==enemy['thingId'] for a in actions)
    assert all(a['arguments']['requireStandingTarget'] is True for a in actions)
    assert all(a['arguments']['mode']=='auto' for a in actions)
    assert rt.inspect_native.await_count==3


@pytest.mark.asyncio
async def test_missing_native_firing_solution_waits_without_admitting_attack():
    rt,_=fixture()
    rt.inspect_native.return_value={'success':False,'reason':'Target out of range'}
    assert await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people) is None
    assert rt.current_plan.colony_goals['ActiveCombat'].evidence['firing_solution_refusal']['success'] is False
    assert not rt.current_plan.spec.steps


@pytest.mark.asyncio
async def test_obstructed_shooter_does_not_delay_legal_defenders():
    rt,_=fixture()
    refusal=BridgeError('home/order',CallToolResult(content=[],isError=True,
        structuredContent={'message':'Verb.CanHitTarget is false'}))
    rt.inspect_native.side_effect=[{'success':True},refusal,{'success':True}]
    _,actions=await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)
    assert [a['arguments']['action'] for a in actions]==['attack','draft','attack']
    assert len({a['arguments']['pawn'] for a in actions})==3


@pytest.mark.asyncio
async def test_unavailable_native_preview_cannot_be_treated_as_an_obstruction():
    rt,_=fixture()
    rt.inspect_native.side_effect=BridgeError('home/order',CallToolResult(content=[],isError=True,
        structuredContent={'message':'Bridge unavailable'}))
    with pytest.raises(BridgeError):await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)


@pytest.mark.asyncio
async def test_muster_then_partial_engagement_preserves_all_cleanup_participants():
    rt,enemy=fixture();enemy['nearestColonistDistance']=90
    rt.batch.summary.hostile_count=1
    async def query(name,**args):
        if name=='home/colony_facts':return rt.facts
        return {'pawns':rt.people if args.get('colonistsOnly') else [enemy]}
    rt.game.query=query
    await rt.controller.cycle()
    muster=[s.id for s in rt.current_plan.spec.steps]
    assert len(muster)==3
    rt.facts['tick']+=600
    for step in muster:rt.current_plan.progress[step].state='complete'
    for pawn in rt.people:
        pawn['drafted']=True;rt.draft_owners[pawn['thingId']]=rt.context_token
    enemy['nearestColonistDistance']=20
    rt.inspect_native.side_effect=[{'success':True},BridgeError('home/order',CallToolResult(
        content=[],isError=True,structuredContent={'message':'Verb.CanHitTarget is false'})),{'success':True}]
    await rt.controller.cycle()
    combat=rt.current_plan.control['combat']
    assert set(combat['pawns'])=={p['thingId'] for p in rt.people}
    assert len(combat['steps'])==2
    assert len(rt.current_plan.spec.steps)==5
    assert all(rt.current_plan.progress[s].state=='complete' for s in muster)


@pytest.mark.asyncio
async def test_unarmed_defender_equips_observed_nearby_weapon_before_muster():
    rt,enemy=fixture();enemy['nearestColonistDistance']=90
    pawn=rt.people[0];pawn['equipment']={'armed':False,'primary':None};pawn['position']={'x':20,'z':20}
    async def query(name,**args):
        if name=='home/list_pawns':return {'pawns':[enemy]}
        assert args['includeHeld'] is False and args['ownership']=='ours' and args['radius']==12
        return {'things':[{'weapon':{'ranged':True},'positions':[{'thingId':'Thing_Gun1','spawned':True}]}]}
    rt.game.query=query
    method,actions=await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)
    assert method.startswith('equip-') and len(actions)==1
    assert actions[0]['completion']=='pawn_equipped'
    assert actions[0]['arguments']['target']=='Thing_Gun1'


@pytest.mark.asyncio
async def test_unarmed_defender_cannot_be_sent_into_nearby_melee_raider():
    rt,_=fixture();rt.people[0]['equipment']={'armed':False,'primary':None}
    with pytest.raises(SkillBlocked,match='equipped defenders'):
        await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)


@pytest.mark.parametrize('count,rows,remaining',[
    (1,[{'thingId':'Enemy','downed':True}],0),
    (2,[{'thingId':'Enemy','downed':True}],1),
    (2,[{'thingId':'Enemy','downed':True}]*2,1),
    (1,[{'thingId':'Enemy'}],1),
    (1,[{'thingId':'Enemy','downed':False}],1),
])
def test_incapacitated_threats_do_not_hide_unseen_or_standing_enemies(count,rows,remaining):
    rt,_=fixture();rt.batch.summary.hostile_count=count
    rt.batch.native['status_after']={'threats':{'hostiles':rows}}
    assert derive(rt.batch,rt.facts,ColonyPolicy())['hostiles']==remaining


@pytest.mark.asyncio
async def test_downed_raider_selects_owned_stand_down():
    rt,enemy=fixture();enemy['nearestColonistDistance']=90;rt.batch.summary.hostile_count=1
    async def query(name,**args):
        if name=='home/colony_facts':return rt.facts
        return {'pawns':rt.people if args.get('colonistsOnly') else [enemy]}
    rt.game.query=query
    await rt.controller.cycle()
    for step in rt.current_plan.spec.steps:rt.current_plan.progress[step.id].state='complete'
    for pawn in rt.people:
        pawn['drafted']=True;rt.draft_owners[pawn['thingId']]=rt.context_token
    enemy['downed']=True
    rt.batch.native['status_after']={'threats':{'hostiles':[{'thingId':enemy['thingId'],'downed':True}]}}
    await rt.controller.cycle()
    assert rt.current_plan.colony_goals['ActiveCombat'].status=='complete'
    cleanup=[s for s in rt.current_plan.spec.steps if s.action.kind=='stand_down']
    assert len(cleanup)==1 and set(cleanup[0].action.pawn_ids)==set(rt.draft_owners)


@pytest.mark.asyncio
@pytest.mark.parametrize('change', ['ranged','unknown_gear','many','incapable','injured'])
async def test_unsupported_raids_retain_hold(change):
    rt,enemy=fixture()
    if change=='ranged':enemy['equipment']['primary']['ranged']=True
    elif change=='unknown_gear':enemy['equipment']={}
    elif change=='many':rt.game.query.return_value['pawns'].append(dict(enemy,thingId='Thing_Human100'))
    elif change=='incapable':
        for p in rt.people:p['bio']['incapableOfTags']=['Violent']
    elif change=='injured':
        for p in rt.people:p['health']['summaryPct']=.7
    with pytest.raises(SkillBlocked):await rt.controller.skills.compile('ActiveCombat',rt.facts,rt.people)
