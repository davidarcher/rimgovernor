from rimbot.hunting import screen_prey
import pytest
from unittest.mock import AsyncMock
from rimbot.colony_plan import ColonyGoal
from test_colony_controller import Replay


def pawn(identity,x=0,z=0,**fields):
    return dict(thingId=identity,position={'x':x,'z':z},hostile=False,predator=False,
                manhunterOnDamageChance=0,dead=False,downed=False,
                animals={'designations':{'hunt':False},'bodySize':1},**fields)


def test_predator_boundary_blocks_prey_and_explains_identity():
    prey=pawn('prey');predator=pawn('predator',25,25);predator['predator']=True
    choices,evidence=screen_prey([prey,predator],{'x':0,'z':0})
    assert not choices and evidence['rejected'][0]['predators']==['predator']
    predator['position']['x']=26
    assert screen_prey([prey,predator],{'x':0,'z':0})[0]==[prey]


def test_unknown_predator_data_blocks_instead_of_assuming_safe():
    prey=pawn('prey');unknown=pawn('unknown',100,100);unknown['predator']=None
    assert not screen_prey([prey,unknown],{'x':0,'z':0})[0]
    unknown['predator']=True;unknown['position']=None
    assert not screen_prey([prey,unknown],{'x':0,'z':0})[0]
    unknown['dead']=True
    assert screen_prey([prey,unknown],{'x':0,'z':0})[0]==[prey]


def test_rejects_unsafe_unknown_and_already_designated_prey():
    for key,value in [('hostile',True),('dead',None),('downed',None),('manhunterOnDamageChance',.1),('position',None)]:
        prey=pawn('prey');prey[key]=value
        assert not screen_prey([prey],{'x':0,'z':0})[0]
    prey=pawn('prey');prey['animals']['designations']['hunt']=True
    assert not screen_prey([prey],{'x':0,'z':0})[0]


def test_ranking_is_stable_and_out_of_range_prey_is_excluded():
    a=pawn('a',1);b=pawn('b',1);far=pawn('far',51)
    assert screen_prey([b,far,a],{'x':0,'z':0})[0]==[a,b]


@pytest.mark.asyncio
async def test_controller_does_not_compile_hunt_near_predator_and_retains_evidence():
    rt=Replay();rt.current_plan.colony_goals['EnsureFoodSupply']=ColonyGoal(priority_class=2)
    rt.facts.update(center={'x':0,'z':0},armed=1,foodRunwayDays=0,
        farms=[{'edible':True,'usableCells':100}],butchering=[{'bills':[{'recipe':'ButcherCorpseFlesh','suspended':False}]}])
    prey=pawn('prey');predator=pawn('predator',25);predator['predator']=True
    rt.game.query=AsyncMock(return_value={'pawns':[prey,predator]})
    rt.controller.skills.designator=AsyncMock(return_value='hunt')
    assert await rt.controller.skills.compile('EnsureFoodSupply',rt.facts,rt.people) is None
    assert rt.current_plan.colony_goals['EnsureFoodSupply'].evidence['hunting_screen']['rejected'][0]['prey']=='prey'
    rt.controller.skills.designator.assert_not_awaited()
    predator['position']['x']=26
    method,actions=await rt.controller.skills.compile('EnsureFoodSupply',rt.facts,rt.people)
    assert method=='hunt-prey' and actions[0]['arguments']['x']==0
