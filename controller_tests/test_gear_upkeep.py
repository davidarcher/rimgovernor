import pytest
from rimbot.colony_plan import ColonyGoal, NativeOperation, Failure
from rimbot.colony_skills import SkillBlocked
from rimbot.gear_upkeep import compile_upkeep, needs_upkeep
from rimbot.medical_outcome import pawn_order_outcome
from rimbot.bridge_game import is_write


def observation():
    return dict(success=True, pawns=[dict(pawn='Thing_Pawn1', loadout='signature', deficit=True,
        candidates=[dict(target='Thing_Shirt2', gain=.2), dict(target='Thing_Shirt3', gain=.5)])])


def test_exact_best_candidate_and_durable_method():
    goal = ColonyGoal(priority_class=3)
    method, actions = compile_upkeep(goal, observation())
    action = NativeOperation.model_validate(actions[0])
    assert action.arguments['target'] == 'Thing_Shirt3'
    assert action.completion == 'pawn_gear'
    goal.evidence['methods'] = {method: ['step']}
    assert compile_upkeep(goal, observation())[1][0]['arguments']['target'] == 'Thing_Shirt2'


def test_missing_gear_does_not_certify_recovery_and_blocked_pawn_is_preserved():
    assert needs_upkeep(None)
    with pytest.raises(SkillBlocked): compile_upkeep(ColonyGoal(priority_class=3), None)
    state = observation()
    state['pawns'][0]['blocker'] = 'Player ordered job'
    with pytest.raises(SkillBlocked): compile_upkeep(ColonyGoal(priority_class=3), state)
    state['pawns'][0]['candidates'] = []
    assert needs_upkeep(state)
    state['pawns'][0]['deficit'] = False
    assert not needs_upkeep(state)


def test_receipt_or_other_apparel_cannot_complete_dressing():
    action = NativeOperation.model_validate(compile_upkeep(ColonyGoal(priority_class=3), observation())[1][0])
    pawn = dict(thingId='Thing_Pawn1', dead=False, downed=False, job='Wear', equipment=dict(apparel=[]))
    assert pawn_order_outcome(action, [pawn]) == 'waiting'
    pawn['equipment']['apparel'] = [dict(thingId='Thing_Shirt2')]
    assert pawn_order_outcome(action, [pawn]) == 'waiting'
    pawn['job'] = 'Wait'
    assert isinstance(pawn_order_outcome(action, [pawn]), Failure)
    pawn['equipment']['apparel'] = [dict(thingId='Thing_Shirt3')]
    assert pawn_order_outcome(action, [pawn]) == 'complete'
    pawn.pop('dead')
    assert pawn_order_outcome(action, [pawn]) == 'waiting'
    pawn['dead'] = False
    pawn.pop('equipment')
    assert pawn_order_outcome(action, [pawn]) == 'waiting'


def test_apparel_write_cannot_use_receipt_completion_or_omit_identity():
    action = compile_upkeep(ColonyGoal(priority_class=3), observation())[1][0]
    with pytest.raises(ValueError): NativeOperation.model_validate(dict(action, completion='native_receipt'))
    with pytest.raises(ValueError): NativeOperation.model_validate(dict(action, arguments={'pawn': 'Thing_Pawn1'}))
    assert is_write('home/gear_upkeep', dict(dryRun=False))
    assert not is_write('home/gear_upkeep', dict(dryRun=True))


@pytest.mark.parametrize('changed', [None, 'load', 'direction', 'stale'])
def test_uncertain_dressing_reconciles_only_with_fresh_owned_context(changed):
    from types import SimpleNamespace
    from rimbot.bridge_runtime import BridgeRuntime
    from rimbot.colony_plan import ColonyPlan, PlanSpec, PlanStep, StepProgress
    rt = BridgeRuntime.__new__(BridgeRuntime)
    action = compile_upkeep(ColonyGoal(priority_class=3), observation())[1][0]
    step = PlanStep(id='wear', title='Wear replacement', action=action, completion_criteria='Exact apparel worn')
    progress = StepProgress(state='blocked', failure=Failure(code='native_failure', detail='Lost response'),
        issued={'0': dict(confirmed=False, issued_at=10, load_token='old' if changed == 'load' else 'load',
                          player_direction=1 if changed == 'direction' else 0)})
    rt.current_plan = ColonyPlan(spec=PlanSpec(steps=[step]), progress={'wear': progress})
    rt.context_token = 'load'
    rt.signal = lambda *args: None
    rt.batch = SimpleNamespace(started_at=9 if changed == 'stale' else 11,
        native={'pawns': {'pawns': [dict(thingId='Thing_Pawn1', dead=False, downed=False,
            equipment={'apparel': [dict(thingId='Thing_Shirt3')]})]}})
    rt.reconcile_plan()
    assert progress.state == ('complete' if changed is None else 'blocked')


@pytest.mark.asyncio
@pytest.mark.parametrize('policy,expected', [({}, 'produce'), ({'reserve': 80}, 'blocked'), ({'spending': 'stop'}, 'blocked')])
async def test_bounded_production_obeys_native_costs_and_resource_policy(policy, expected):
    from types import SimpleNamespace
    from rimbot.colony_plan import ColonyPlan
    from rimbot.gear_upkeep import compile_method
    goal = ColonyGoal(priority_class=3)
    plan = ColonyPlan(colony_goals={'MaintainEquipment': goal}, control={'resource_policy': {'Cloth': policy}})
    state = observation()
    state['pawns'][0].update(candidates=[], replacementNeeds=[dict(defName='Apparel_Shirt', stuff='Cloth')])
    async def invoke(tool, args):
        assert tool == 'home/bills'
        if args['action'] == 'list': return dict(success=True, benches=[dict(thingId='Thing_Bench1', bills=[])])
        return dict(success=True, recipes=[dict(defName='MakeShirt', availableNow=True, availableOnNow=True,
            products=[dict(defName='Apparel_Shirt')], workTypes=[dict(name='Tailoring', skills=['Crafting'])],
            ingredients=[dict(costOptions=[dict(defName='Cloth', needed=40)])])])
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(invoke=invoke))
    facts = dict(gearUpkeep=state, resources={'Cloth': 100})
    if expected == 'blocked':
        with pytest.raises(SkillBlocked): await compile_method(rt, facts)
    else:
        method, actions = await compile_method(rt, facts)
        assert actions[0]['arguments']['repeatCount'] == 1
        assert actions[0]['arguments']['only'] == 'Cloth'
        assert goal.evidence['procurement'][method]['loadout'] == 'signature'
        goal.evidence['methods'] = {method: ['bill']}
        assert await compile_method(rt, facts) is None


@pytest.mark.asyncio
async def test_existing_gear_precedes_any_procurement_reads():
    from types import SimpleNamespace
    from rimbot.colony_plan import ColonyPlan
    from rimbot.gear_upkeep import compile_method
    rt = SimpleNamespace(current_plan=ColonyPlan(colony_goals={'MaintainEquipment': ColonyGoal(priority_class=3)}))
    assert (await compile_method(rt, dict(gearUpkeep=observation())))[1][0]['tool'] == 'home/gear_upkeep'


def test_development_scheduler_admits_observed_equipment_deficit():
    from rimbot.colony_plan import ColonyPlan
    from rimbot.colony_policy import ColonyPolicy
    from rimbot.development_priorities import arbitrate, deficit
    goal = ColonyGoal(priority_class=3)
    plan = ColonyPlan(colony_goals={'MaintainEquipment': goal})
    facts = dict(tick=100, gearUpkeep=observation())
    pawn = dict(dead=False, downed=False, drafted=False, work={'applies': True})
    _, admitted = arbitrate(plan, facts, [pawn], [('MaintainEquipment', 3)], ColonyPolicy(), context='load', direction=0)
    assert admitted == {'MaintainEquipment'}
    assert deficit('MaintainEquipment', goal, {}, ColonyPolicy()) is None
    assert deficit('MaintainEquipment', goal, {'gearUpkeep': {'success': True, 'pawns': []}}, ColonyPolicy()) is None
