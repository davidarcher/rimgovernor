from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimbot.colony_plan import ColonyPlan, ColonyGoal, StepProgress
from rimbot.production_policy import production_budgets, sync_production_policy, resource_method, ingredient_deficits
from rimbot.colony_skills import SkillBlocked, ColonySkills
from rimbot.player_commands import CreateGoal


def target(resource='Steel', quantity=100):
    identity = 'MaintainResource-' + resource
    plan = ColonyPlan()
    plan.colony_goals[identity] = ColonyGoal(source='PLAYER', priority_class=3, target={'resource':resource, 'quantity':quantity})
    rt = SimpleNamespace(current_plan=plan, identity={'colonyId':'colony','loadToken':'load','mapId':1})
    return rt, identity, {'resources':{}, 'policyResources':{resource:resource}}


def test_reservations_roundtrip_and_transfer_to_native_construction_without_double_hold():
    rt, _, _ = target()
    plan = rt.current_plan
    plan.control = {'resource_policy': {'Steel':{'reserve':20,'spending':'stop'}, 'MedicineIndustrial':{'reserve':5,'spending':'normal'}},
                    'costs': {'build':{'0':{'Steel':30},'1':{'Steel':40}}, 'failed':{'0':{'Steel':60}}}}
    plan.progress = {'build':StepProgress(state='waiting', issued={'0':{'confirmed':True}}),
                     'failed':StepProgress(state='blocked')}
    restored = ColonyPlan.model_validate(plan.model_dump())
    assert production_budgets(restored) == ({'Steel':60,'MedicineIndustrial':5}, ['Steel'])
    restored.progress['build'].issued['1'] = {'confirmed':True}
    assert production_budgets(restored)[0]['Steel'] == 20


@pytest.mark.asyncio
@pytest.mark.parametrize('resource', ['Steel','ComponentIndustrial','MedicineIndustrial','MedicineHerbal','WoodLog','Chemfuel'])
async def test_native_resource_targets_use_observed_sources_and_never_credit_pending_yield_as_stock(resource):
    rt, identity, facts = target(resource, 10)
    async def invoke(name, args):
        assert name == 'home/resource_sources'
        return {'success':True,'sources':[{'thingId':'source','resource':resource,'yield':12,'designated':False,'x':10,'z':11}]}
    rt.game = SimpleNamespace(invoke=invoke)
    method, actions = await resource_method(rt, identity, facts)
    steps, _ = ColonySkills(rt).steps(identity, method, actions, facts)
    assert len(steps) == 1 and steps[0].action.tool == 'home/acquire_resource'
    assert steps[0].action.arguments['resource'] == resource
    assert rt.current_plan.colony_goals[identity].evidence['deficit'] == 10
    restored = ColonyPlan.model_validate(rt.current_plan.model_dump())
    assert restored.colony_goals[identity].target == {'resource':resource,'quantity':10}


@pytest.mark.asyncio
async def test_pending_acquisition_bounds_new_designations_without_satisfying_target():
    rt, identity, facts = target(quantity=25)
    rows = [{'thingId':'a','resource':'Steel','yield':20,'designated':True,'x':1,'z':1},
            {'thingId':'b','resource':'Steel','yield':20,'designated':False,'x':2,'z':1},
            {'thingId':'c','resource':'Steel','yield':20,'designated':False,'x':3,'z':1}]
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value={'success':True,'sources':rows}))
    _, actions = await resource_method(rt, identity, facts)
    assert [a['arguments']['thingId'] for a in actions] == ['b']
    assert rt.current_plan.colony_goals[identity].evidence['deficit'] == 25


@pytest.mark.asyncio
@pytest.mark.parametrize('mode, existing_target, count', [('Forever',0,0),('TargetCount',100,0),('TargetCount',50,1),('RepeatCount',0,1)])
async def test_existing_bill_capacity_must_cover_the_maintained_target(mode, existing_target, count):
    rt, identity, facts = target()
    bill = {'suspended':False,'finished':False,'products':[{'defName':'Steel','count':10}],
            'config':{'repeatMode':mode,'targetCount':existing_target},'billId':'Bill1'}
    recipe = {'defName':'NativeRecipe','products':[{'defName':'Steel','count':10}], 'availableNow':True,
              'availableOnNow':True,'ingredients':[{'costOptions':[{'defName':'WoodLog','needed':20}]}]}
    async def invoke(name, args):
        if name == 'home/resource_sources': return {'success':True,'sources':[]}
        if args['action'] == 'list': return {'benches':[{'thingId':'Bench1','bills':[bill]}]}
        return {'recipes':[recipe]}
    rt.game = SimpleNamespace(invoke=invoke)
    result = await resource_method(rt, identity, facts)
    assert (len(result[1]) if result else 0) == count
    if result:
        assert result[1][0]['arguments']['targetCount'] == 100
        assert bill['config']['targetCount'] == existing_target


@pytest.mark.asyncio
async def test_missing_recipe_and_workbench_is_explicit_not_fake_production():
    rt, identity, facts = target('Chemfuel')
    async def invoke(name, args):
        return {'success':True,'sources':[]} if name == 'home/resource_sources' else {'benches':[]}
    rt.game = SimpleNamespace(invoke=invoke)
    with pytest.raises(SkillBlocked, match='No available native production recipe'):
        await resource_method(rt, identity, facts)
    assert rt.current_plan.colony_goals[identity].evidence['deficit'] == 100


def test_native_ingredient_alternatives_remain_separate_and_unknown_costs_refuse():
    recipe={'ingredients':[{'costOptions':[{'defName':'Steel','needed':5},{'defName':'Silver','needed':50}]}]}
    assert ingredient_deficits(recipe, {'Steel':3,'Silver':25}) == [[
        {'resource':'Steel','required':5,'deficit':2},{'resource':'Silver','required':50,'deficit':25}]]
    with pytest.raises(ValueError,match='unavailable'): ingredient_deficits({'ingredients':[{}]}, {})


@pytest.mark.asyncio
@pytest.mark.parametrize('failure', ['receipt','direction','load','revision'])
async def test_budget_sync_is_durable_before_write_and_rejects_stale_or_unverified_receipt(failure):
    rt, _, _ = target()
    rt.context_token='load';rt.chat_revision=1;rt.persist=lambda:None;rt.sync_identity=AsyncMock()
    rt.current_plan.control['resource_policy']={'Steel':{'reserve':10,'spending':'stop'}}
    async def invoke(name, args, **kwargs):
        assert rt.current_plan.control['production_policy_dispatch']['confirmed'] is False
        if failure=='direction':rt.chat_revision += 1
        if failure=='load':rt.context_token='different'
        if failure=='revision':rt.current_plan.revision += 1
        return {'success':True,'floors':{} if failure=='receipt' else {'Steel':10},'stopped':['Steel'],'commitments':{}}
    rt.game=SimpleNamespace(invoke=invoke)
    with pytest.raises((ValueError, InterruptedError)): await sync_production_policy(rt)
    assert not hasattr(rt, '_production_policy_signature')


def test_resource_target_contract_rejects_missing_and_misplaced_quantities():
    for payload in ({'goal':'MaintainResource'}, {'goal':'MaintainResource','resource':'Steel'},
                    {'goal':'EnsureFoodSupply','quantity':10}, {'goal':'MaintainWood','resource':'Steel'}):
        with pytest.raises(ValueError):CreateGoal(kind='CreateGoal',**payload)


@pytest.mark.asyncio
@pytest.mark.parametrize('restriction', ['stop','reserve','competing'])
async def test_explicit_material_substitution_selects_an_affordable_permitted_native_cost(restriction):
    from rimbot.colony_plan import PlanSpec
    from rimbot.resource_accounting import validate_allocations
    plan = ColonyPlan()
    if restriction == 'stop': plan.control['resource_policy']={'WoodLog':{'spending':'stop'}}
    if restriction == 'reserve': plan.control['resource_policy']={'WoodLog':{'reserve':10}}
    spec = PlanSpec(steps=[{'id':'furniture','title':'Furniture','completion_criteria':'Built',
        'action':{'kind':'place_buildings','placements':[{'def_name':'Bed','x':1,'z':1,'materials':['WoodLog','Steel']}]}}])
    if restriction == 'competing':
        spec.steps[0].action.placements.insert(0, spec.steps[0].action.placements[0].model_copy(update={'x':2,'materials':['WoodLog']}))
    async def invoke(name, args, **kwargs):
        resource=args['stuff']
        return {'canPlace':True,'costList':[{'defName':resource,'count':10}],
                'materials':{'rows':[{'defName':resource,'available':10 if resource=='WoodLog' else 100}]}}
    allocations = await validate_allocations(spec, plan, SimpleNamespace(invoke=invoke))
    assert spec.steps[0].action.placements[-1].materials == ['Steel']
    assert allocations['furniture'][str(len(spec.steps[0].action.placements)-1)] == {'Steel':10}
