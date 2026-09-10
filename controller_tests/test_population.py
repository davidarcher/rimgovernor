from copy import deepcopy
from types import SimpleNamespace
import pytest

from rimbot.population import capacity, guard, refresh, preview_order, SkillBlocked
from rimbot.player_commands import COMMAND, semantic_tools, apply_command
from rimbot.bridge_game import is_write
from rimbot.colony_plan import ColonyPlan, ColonyGoal, PlanStep, StepProgress
from rimbot.bridge import BridgeError
from mcp.types import CallToolResult
from rimbot.colony_policy import required_colony_work
from rimbot.bridge_observation import ObservationGateway


def state():
    policy = {'maximum': 3, 'food_days': 3}
    facts = {'bedCapacity': 3, 'nutritionPerDay': 1.6, 'foodNutrition': 20, 'tick': 10}
    snapshot = {'success': True, 'tick': 10, 'people': [
        {'thingId': 'Thing_A', 'admitted': True, 'dead': False},
        {'thingId': 'Thing_B', 'admitted': False, 'guest': True, 'prisoner': True,
         'dead': False, 'nutritionPerDay': 1.6, 'interaction': 'MaintainOnly'}]}
    people = [{'thingId': 'Thing_A', 'dead': False, 'downed': False, 'work': {'types': [
        {'name': k, 'disabled': False, 'priority': 1} for k in ('Doctor', 'Warden')]}}]
    return policy, facts, snapshot, people


def test_capacity_deduplicates_prisoner_and_commitment_but_counts_food():
    policy, facts, snapshot, people = state()
    result = capacity(policy, facts, snapshot, ['Thing_B'], people)
    assert result == {'admitted': 1, 'committed': 1, 'foodRequired': pytest.approx(9.6)}


@pytest.mark.parametrize('failure', ['maximum', 'beds', 'food', 'unknown_food', 'unknown_demand', 'warden', 'doctor'])
def test_capacity_refuses_missing_prerequisites(failure):
    policy, facts, snapshot, people = state()
    if failure == 'maximum': policy['maximum'] = 1
    if failure == 'beds': facts['bedCapacity'] = 1
    if failure == 'food': facts['foodNutrition'] = 2
    if failure == 'unknown_food': facts['foodNutrition'] = None
    if failure == 'unknown_demand': snapshot['people'][1]['nutritionPerDay'] = None
    if failure in ('warden', 'doctor'):
        people[0]['work']['types'] = [w for w in people[0]['work']['types'] if w['name'].lower() != failure]
    with pytest.raises(SkillBlocked): capacity(policy, facts, snapshot, ['Thing_B'], people)


def test_commands_are_explicit_and_native_reads_are_not_writes():
    assert COMMAND.validate_python({'kind': 'SetPopulationDecision', 'pawn': 'Thing_B', 'decision': 'ignore'})
    with pytest.raises(ValueError):
        COMMAND.validate_python({'kind': 'SetPopulationPolicy', 'maximum': 0, 'food_days': 3})
    assert len(semantic_tools()) > 0
    assert not is_write('home/population', {})
    assert not is_write('home/population', {'interaction': 'AttemptRecruit', 'dryRun': True})
    assert is_write('home/population', {'interaction': 'AttemptRecruit', 'dryRun': False})


def test_population_adds_warden_demand_until_commitment_is_complete():
    plan = ColonyPlan()
    goal = plan.colony_goals['Population-Thing_B'] = ColonyGoal(priority_class=3)
    assert required_colony_work(plan)['Warden'] == 'Social'
    goal.cancelled = True
    assert 'Warden' not in required_colony_work(plan)


@pytest.mark.asyncio
async def test_real_observation_gateway_allows_population_read_but_never_settings_writes():
    calls = []
    async def detail(tool):
        return SimpleNamespace(structuredContent={'inputSchema': {'type': 'object', 'properties': {}}})
    async def call(tool, **kwargs):
        calls.append((tool, kwargs))
        return SimpleNamespace(structuredContent={'success': True, 'people': []})
    gateway = ObservationGateway(SimpleNamespace(detail=detail, call=call))
    assert (await gateway.query('home/population'))['success']
    with pytest.raises(ValueError, match='cannot change'):
        await gateway.query('home/population', pawn='Thing_B', interaction='AttemptRecruit', dryRun=False)
    with pytest.raises(ValueError, match='cannot change'):
        await gateway.query('home/population', pawn='Thing_B', interaction='AttemptRecruit')
    assert calls == [('home/population', {})]


@pytest.mark.asyncio
async def test_fresh_guard_preserves_changed_individual_setting():
    policy, facts, snapshot, people = state()
    plan = ColonyPlan()
    plan.control['population_policy'] = policy
    plan.colony_goals['Population-Thing_B'] = ColonyGoal(priority_class=3, target={'pawn': 'Thing_B', 'decision': 'recruit', 'interaction': 'MaintainOnly'})
    async def query(name, **kwargs): return deepcopy(snapshot)
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=query))
    await guard(rt, 'Population-Thing_B', facts, people)
    snapshot['people'][1]['interaction'] = 'Release'
    with pytest.raises(SkillBlocked, match='Player prisoner interaction changed'):
        await guard(rt, 'Population-Thing_B', facts, people)


@pytest.mark.asyncio
async def test_explicit_new_decision_does_not_inherit_previous_setting_ownership():
    policy, facts, snapshot, people = state()
    plan = ColonyPlan()
    plan.control['population_policy'] = policy
    key = 'Population-Thing_B'
    plan.colony_goals[key] = ColonyGoal(priority_class=3, target={'pawn': 'Thing_B', 'decision': 'recruit', 'interaction': 'MaintainOnly'})
    plan.spec.steps = [PlanStep(id='old-setting', title='Prior setting', goal_id=key, completion_criteria='Setting',
        action={'kind': 'native_operation', 'tool': 'home/population', 'arguments': {'pawn': 'Thing_B', 'interaction': 'AttemptRecruit', 'dryRun': False}})]
    plan.progress['old-setting'] = StepProgress(state='complete', issued={'0': {'confirmed': True}})
    async def query(name, **kwargs): return snapshot
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=query))
    assert (await guard(rt, key, facts, people))[0]['interaction'] == 'MaintainOnly'


@pytest.mark.asyncio
async def test_admission_requires_real_integration_and_never_recaptures_completed_recruit():
    policy, facts, snapshot, people = state()
    plan = ColonyPlan()
    goal = plan.colony_goals['Population-Thing_B'] = ColonyGoal(priority_class=3, target={'pawn': 'Thing_B', 'decision': 'recruit'})
    snapshot['people'][1].update(admitted=True, prisoner=False, ownedBed='Thing_Bed', ownedBedForPrisoners=False, ownedBedIndoors=True, needsTend=False, food=.8)
    async def query(name, **kwargs): return deepcopy(snapshot)
    async def ensure(token): pass
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=query), context_token='load', chat_revision=0, ensure_context=ensure)
    assert await refresh(rt, facts, people)
    assert goal.status != 'complete'
    people.append({'thingId': 'Thing_B', 'work': {'types': [{'priority': 1}]}, 'equipment': {'primary': {'thingId': 'Thing_Gun'}}})
    assert await refresh(rt, facts, people) == []
    assert goal.status == 'complete'
    snapshot['people'][1].update(admitted=False, prisoner=False, guest=False)
    assert await refresh(rt, facts, people) == []


@pytest.mark.asyncio
async def test_player_direction_race_does_not_record_candidate():
    policy, facts, snapshot, people = state()
    plan = ColonyPlan()
    plan.control['population_policy'] = policy
    async def ensure(token): pass
    async def query(name, **kwargs):
        rt.chat_revision += 1
        return snapshot
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=query), chat_revision=1,
                         context_token='load', ensure_context=ensure)
    with pytest.raises(ValueError, match='direction changed'):
        await apply_command(rt, {'kind': 'SetPopulationDecision', 'pawn': 'Thing_B', 'decision': 'recruit'}, token='load', revision=1)
    assert not plan.colony_goals


@pytest.mark.asyncio
async def test_provisioning_retains_native_population_count():
    policy, facts, snapshot, people = state()
    facts['colonists'] = 1
    plan = ColonyPlan()
    plan.control['population_policy'] = policy
    plan.colony_goals['Population-Thing_B'] = ColonyGoal(priority_class=3, target={'pawn': 'Thing_B', 'decision': 'recruit'})
    async def ensure(token): pass
    async def query(name, **kwargs): return snapshot
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=query), chat_revision=1,
                         context_token='load', ensure_context=ensure)
    await refresh(rt, facts, people)
    assert facts['colonists'] == 1
    assert facts['populationHousingTarget'] == 2
    assert facts['populationNutritionPerDay'] == pytest.approx(3.2)


@pytest.mark.asyncio
@pytest.mark.parametrize('native_preview', [True, False])
async def test_preview_refusal_is_retained_but_infrastructure_error_is_not_eligibility(native_preview):
    payload = {'success': False, 'dryRun': native_preview, 'error': 'no bed'}
    async def inspect(*args): raise BridgeError('home/order', CallToolResult(content=[], isError=True, structuredContent=payload))
    rt = SimpleNamespace(inspect_native=inspect)
    goal = ColonyGoal(priority_class=3)
    if native_preview:
        assert await preview_order(rt, goal, {'action': 'capture'}) == payload
        assert goal.evidence['refused_previews'] == [payload]
    else:
        with pytest.raises(BridgeError): await preview_order(rt, goal, {'action': 'capture'})


@pytest.mark.asyncio
@pytest.mark.parametrize('issued_load,progress_state,expected', [('load', 'blocked', 'complete'), ('old', 'blocked', 'blocked'), ('load', 'cancelled', 'cancelled')])
async def test_uncertain_custody_requires_current_load_outcome_and_preserves_cancellation(issued_load, progress_state, expected):
    policy, facts, snapshot, people = state()
    snapshot['people'][1]['bed'] = 'Thing_PrisonBed'
    plan = ColonyPlan()
    key = 'Population-Thing_B'
    plan.colony_goals[key] = ColonyGoal(priority_class=3, target={'pawn': 'Thing_B', 'decision': 'recruit'}, steps=['capture'])
    plan.spec.steps = [PlanStep(id='capture', title='Capture', goal_id=key, completion_criteria='Observed custody',
        action={'kind': 'native_operation', 'tool': 'home/order', 'arguments': {'action': 'capture', 'pawn': 'Thing_A', 'target': 'Thing_B', 'dryRun': False}})]
    plan.progress['capture'] = StepProgress(state=progress_state, issued={'0': {'confirmed': False, 'load_token': issued_load}})
    async def ensure(token): pass
    async def query(name, **kwargs): return snapshot
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=query), chat_revision=1,
                         context_token='load', ensure_context=ensure)
    await refresh(rt, facts, people)
    assert plan.progress['capture'].state == expected


@pytest.mark.asyncio
async def test_recruit_equipping_preserves_forbidden_weapon_groups(monkeypatch):
    from rimbot import population
    pawn = {'thingId': 'Thing_Recruit', 'admitted': True}
    worker = dict(pawn, equipment={'armed': False}, bio={'incapableOfTags': []})
    async def guard(*args): return pawn, {}, [worker]
    async def query(*args, **kwargs):
        return {'things': [
            {'oursUnforbidden': 1, 'forbidden': 1, 'positions': [{'thingId': 'Thing_Forbidden'}]},
            {'oursUnforbidden': 1, 'forbidden': 0, 'positions': [{'thingId': 'Thing_Available'}]}]}
    previews = []
    async def preview(*args):
        previews.append(args[-1]['target'])
        return {'success': True}
    monkeypatch.setattr(population, 'guard', guard)
    monkeypatch.setattr(population, 'preview_order', preview)
    plan = ColonyPlan(colony_goals={'Population-Thing_Recruit': ColonyGoal(priority_class=3)})
    rt = SimpleNamespace(current_plan=plan, game=SimpleNamespace(query=query))
    method, actions = await population.compile_method(rt, 'Population-Thing_Recruit', {}, [])
    assert method == 'equip'
    assert previews == ['Thing_Available']
