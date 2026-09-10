from types import SimpleNamespace
from unittest.mock import AsyncMock
import pytest
from rimgovernor.colony_plan import ColonyPlan, ColonyGoal, StepProgress
from rimgovernor.production_policy import production_budgets, sync_production_policy, resource_method, ingredient_deficits
from rimgovernor.colony_skills import SkillBlocked, ColonySkills
from rimgovernor.player_commands import CreateGoal


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
async def test_mining_selects_nearest_safe_source_and_replenishes_from_fresh_deposit():
    rt, identity, facts = target(quantity=1)
    def source(name, distance, safety='open_surface'):
        return dict(thingId=name, resource='Steel', method='mine', safety=safety,
                    distance=distance, x=distance, z=1, yield_=10, designated=False)
    rows = [source('unsafe', 1, 'unknown'), source('far', 9), source('near', 3)]
    for row in rows: row['yield'] = row.pop('yield_')
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value={'success':True,'sources':rows}))
    method, actions = await resource_method(rt, identity, facts)
    assert actions[0]['arguments']['thingId'] == 'near'
    goal = rt.current_plan.colony_goals[identity]
    goal.evidence['methods'] = {method: ['issued-step']}
    goal.target['quantity'] = 25
    rows[-1]['sourceId'] = rows[-1]['thingId']
    rows[-1]['thingId'] = 'reloaded-compressed-rock'
    with pytest.raises(SkillBlocked, match='interrupted'):
        await resource_method(rt, identity, facts)
    goal.target['quantity'] = 1
    rows.remove(rows[-1])
    _, actions = await resource_method(rt, identity, facts)
    assert actions[0]['arguments']['thingId'] == 'far'
    assert goal.evidence['stock'] == 0


@pytest.mark.asyncio
async def test_truncated_census_accounts_for_all_pending_yield():
    rt, identity, facts = target(quantity=100)
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value={
        'success': True, 'sources': [], 'pendingYield': 120, 'truncated': True}))
    assert await resource_method(rt, identity, facts) is None
    assert rt.current_plan.colony_goals[identity].evidence['deficit'] == 100


@pytest.mark.asyncio
async def test_changed_load_source_observation_cannot_compile_orders():
    rt, identity, facts = target()
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value={
        'success':True, 'sources':[], 'loadToken':'other-load'}))
    with pytest.raises(SkillBlocked, match='changed'):
        await resource_method(rt, identity, facts)


@pytest.mark.asyncio
async def test_mining_stages_exact_resource_storage_before_designating():
    rt, identity, facts = target(quantity=10)
    sources = {'success': True, 'sources': [dict(thingId='ore', resource='Steel', method='mine',
        safety='open_surface', x=10, z=10, **{'yield': 40})], 'storage': {
        'capacity': 0, 'stackLimit': 75, 'haulers': ['hauler'], 'workType': {'name': 'Hauling'},
        'candidates': [{'x': 2, 'z': 3}]}}
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value=sources))
    method, actions = await resource_method(rt, identity, facts)
    steps, _ = ColonySkills(rt).steps(identity, method, actions, facts)
    assert steps[0].action.kind == 'create_zone'
    assert steps[0].action.allow == ['Steel'] and steps[0].action.preset == 'nothing'
    sources['storage']['capacity'] = 75
    _, actions = await resource_method(rt, identity, facts)
    assert actions[0]['tool'] == 'home/acquire_resource'
    sources['storage']['haulers'] = []
    with pytest.raises(SkillBlocked, match='no eligible hauler'):
        await resource_method(rt, identity, facts)


@pytest.mark.asyncio
async def test_slow_native_mining_progress_prevents_false_stall_without_crediting_stock():
    rt, identity, facts = target(quantity=10)
    row = dict(thingId='ore', resource='Steel', method='mine', safety='open_surface',
               designated=True, hitPoints=1500, **{'yield':40})
    census = {'success':True, 'sources':[row], 'tick':100}
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value=census))
    goal = rt.current_plan.colony_goals[identity]
    goal.last_progress_tick = 50
    assert await resource_method(rt, identity, facts) is None
    assert goal.last_progress_tick == 50
    row['hitPoints'], census['tick'] = 1420, 200
    assert await resource_method(rt, identity, facts) is None
    assert goal.last_progress_tick == 200 and goal.evidence['stock'] == 0
    census['tick'] = 500
    await resource_method(rt, identity, facts)
    assert goal.last_progress_tick == 200


@pytest.mark.asyncio
async def test_mining_watchdog_recovers_only_from_same_load_actual_work():
    from rimgovernor.production_policy import refresh_resource_progress
    rt, identity, _ = target()
    rt.context_token, rt.chat_revision = 'context', 0
    goal = rt.current_plan.colony_goals[identity]
    goal.status, goal.reason = 'blocked', 'No measurable progress'
    goal.evidence.update(mining_progress={'ore': 1500}, watchdog={'reason': goal.reason})
    census = dict(rt.identity, success=True, tick=9000, sources=[dict(thingId='ore', method='mine',
        designated=True, hitPoints=1420)])
    rt.game = SimpleNamespace(invoke=AsyncMock(return_value=census))
    census['loadToken'] = 'old'
    assert not await refresh_resource_progress(rt, identity)
    assert goal.status == 'blocked' and goal.last_progress_tick == 0
    census['loadToken'] = rt.identity['loadToken']
    assert await refresh_resource_progress(rt, identity)
    assert goal.status == 'active' and goal.last_progress_tick == 9000
    goal.status, goal.reason = 'blocked', 'Player interrupted extraction'
    census['sources'][0]['hitPoints'] = 1340
    assert not await refresh_resource_progress(rt, identity)
    assert goal.status == 'blocked'


@pytest.mark.asyncio
async def test_recorded_zero_yield_herb_order_leaves_a_deficit_for_a_new_source():
    # Native observation at tick 19525: estimated pending yield was zero
    # while actual medicine stock remained twenty.
    rt, identity, facts = target('MedicineHerbal',21)
    facts['resources']['MedicineHerbal']=20
    rt.game=SimpleNamespace(invoke=AsyncMock(return_value={'success':True,'sources':[
        {'thingId':'Plant_HealrootWild17188','resource':'MedicineHerbal','x':146,'z':82,'yield':1,'designated':False},
        {'thingId':'Plant_HealrootWild18467','resource':'MedicineHerbal','x':138,'z':163,'yield':0,'designated':True}]}))
    _,actions=await resource_method(rt,identity,facts)
    assert [a['arguments']['thingId'] for a in actions]==['Plant_HealrootWild17188']
    assert rt.current_plan.colony_goals[identity].evidence['stock']==20
    assert rt.current_plan.colony_goals[identity].evidence['deficit']==1


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


@pytest.mark.asyncio
async def test_resource_recipe_prefers_available_ingredients_before_native_name_order():
    rt, identity, facts = target('Chemfuel',35)
    facts['resources']={'WoodLog':495}
    recipes=[{'defName':name,'products':[{'defName':'Chemfuel','count':35}],
        'availableNow':True,'availableOnNow':True,
        'ingredients':[{'costOptions':[{'defName':ingredient,'needed':70}]}]}
        for name,ingredient in [('Make_ChemfuelFromOrganics','RawRice'),('Make_ChemfuelFromWood','WoodLog')]]
    async def invoke(name,args):
        if name=='home/resource_sources':return {'success':True,'sources':[]}
        if args['action']=='list':return {'benches':[{'thingId':'Refinery','bills':[]}]}
        return {'recipes':recipes}
    rt.game=SimpleNamespace(invoke=invoke)
    _,actions=await resource_method(rt,identity,facts)
    assert actions[0]['arguments']['recipe']=='Make_ChemfuelFromWood'
    assert rt.current_plan.colony_goals[identity].evidence['production_deficits'][0]['ingredients'][0][0]['deficit']==70


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
    from rimgovernor.colony_plan import PlanSpec
    from rimgovernor.resource_accounting import validate_allocations
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


@pytest.mark.asyncio
async def test_resource_prerequisite_recovery_observes_new_bench_without_writing():
    from rimgovernor.production_policy import refresh_resource_prerequisite
    rt, identity, facts=target('Chemfuel')
    goal=rt.current_plan.colony_goals[identity]
    goal.status='blocked';goal.reason='No available native production recipe and workbench for Chemfuel'
    available=False
    async def invoke(name,args):
        if name=='home/resource_sources':return {'success':True,'sources':[]}
        assert args['dryRun'] is True
        if args['action']=='list':return {'benches':[{'thingId':'Refinery','bills':[]}] if available else []}
        assert args['action']=='recipes'
        return {'recipes':[{'defName':'MakeFuel','products':[{'defName':'Chemfuel','count':35}],
            'ingredients':[], 'availableNow':True,'availableOnNow':True,'workTypes':[{'name':'Crafting','skills':[]}]}]}
    rt.game=SimpleNamespace(invoke=invoke)
    assert not await refresh_resource_prerequisite(rt,identity,facts)
    available=True
    assert await refresh_resource_prerequisite(rt,identity,facts)
    assert goal.status=='active' and goal.reason==''
    goal.status='blocked';goal.reason='Previously issued production bill no longer covers this target; explicitly renew the resource goal to replace it'
    assert not await refresh_resource_prerequisite(rt,identity,facts)
