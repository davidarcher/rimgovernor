from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.capacity_growth import growth_fields,grow_shelter,protected_cells
from rimbot.colony_plan import ColonyPlan,ColonyGoal
from rimbot.colony_skills import ColonySkills


def fixture():
    plan=ColonyPlan(colony_goals={'EnsureInitialShelter':ColonyGoal(priority_class=2)},
        control={'layout':{'room':{'x':5,'z':5,'width':9,'height':9}}})
    facts={'colonists':16,'indoorSleepingCapacity':12,'nutritionPerDay':25,'center':{'x':15,'z':15},
        'farms':[{'edible':True,'usableCells':100}],
        'definitions':{'Plant_Rice':{'harvestNutrition':.3,'growDays':3,'fertilityMin':.7}},
        'cells':[{'x':x,'z':z,'walkable':True,'supportsLight':True,'fertility':1,'occupied':False,'zone':False}
            for x in range(40) for z in range(40)]}
    return plan,facts


def test_growth_fields_preserve_existing_zones_and_room_access_and_bound_batch():
    plan,facts=fixture()
    for c in facts['cells']:
        if c['x']>=30:c['zone']=True
    patches=growth_fields(plan,facts)
    assert 0<len(patches)<=32
    selected=set()
    for p in patches:
        cells={(x,z) for x in range(p['x'],p['x']+p['width']) for z in range(p['z'],p['z']+p['height'])}
        assert not cells&selected and not cells&protected_cells(plan) and all(x<30 for x,z in cells)
        selected|=cells
    assert len(selected)==512  # The remaining thirteen cells require another bounded batch.
    facts['farms'][0]['usableCells']=625
    assert growth_fields(plan,facts)==[]


@pytest.mark.asyncio
async def test_expansion_selects_new_native_validated_room_and_reuses_pending_proposal():
    plan,facts=fixture()
    rt=SimpleNamespace(current_plan=plan,controller=SimpleNamespace(policy=SimpleNamespace(max_method_attempts=3)),
        inspect_native=AsyncMock(return_value={'canPlace':True}))
    skill=ColonySkills(rt)
    method,actions=await grow_shelter(skill,facts)
    assert method.startswith('expand-shell-') and len(actions)==1
    bounds=actions[0]['bounds']
    assert not {(x,z) for x in range(bounds['x'],bounds['x']+9) for z in range(bounds['z'],bounds['z']+9)}&{
        (x,z) for x in range(4,15) for z in range(3,15)}
    before=rt.inspect_native.await_count
    assert await grow_shelter(skill,facts)==(method,actions)
    assert rt.inspect_native.await_count==before and len(plan.control['shelter_expansions'])==1


@pytest.mark.asyncio
async def test_additional_fields_wait_for_missing_sleeping_capacity():
    from test_colony_controller import Replay
    rt=Replay(13);rt.facts.update(foodRunwayDays=10,armed=0)
    rt.current_plan.colony_goals['EnsureFoodSupply']=ColonyGoal(priority_class=2,evidence={'methods':{'rice':['field']}})
    assert await rt.controller.skills.compile('EnsureFoodSupply',rt.facts,rt.people) is None
    rt.facts['indoorSleepingCapacity']=13
    method,actions=await rt.controller.skills.compile('EnsureFoodSupply',rt.facts,rt.people)
    assert method.startswith('rice-expand-') and actions[0]['kind']=='create_zone'


@pytest.mark.asyncio
async def test_expansion_skips_free_footprint_with_blocked_outside_entrance(monkeypatch):
    plan,facts=fixture()
    bad={'x':20,'z':5,'width':9,'height':9}
    good={'x':20,'z':16,'width':9,'height':9}
    for cell in facts['cells']:
        if (cell['x'],cell['z'])==(24,4):cell['walkable']=False
    monkeypatch.setattr('rimbot.capacity_growth.starter_layouts',lambda _: [{'room':bad},{'room':good}])
    rt=SimpleNamespace(current_plan=plan,controller=SimpleNamespace(policy=SimpleNamespace(max_method_attempts=3)),
        inspect_native=AsyncMock(return_value={'canPlace':True}))
    _,actions=await grow_shelter(ColonySkills(rt),facts)
    assert actions[0]['bounds']==good
    assert plan.control['shelter_expansions']==[good]
