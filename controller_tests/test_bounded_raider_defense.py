from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyGoal
from rimbot.colony_skills import SkillBlocked
from test_colony_controller import Replay


def fixture():
    rt=Replay();rt.draft_owners={}
    rt.current_plan.colony_goals['ActiveCombat']=ColonyGoal(priority_class=0)
    for pawn in rt.people:
        pawn['bio'].update(incapableOfRead=True,incapableOfTags=[])
        pawn['health']={'summaryPct':1,'needsTend':False}
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
