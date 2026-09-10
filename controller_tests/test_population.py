from copy import deepcopy
from types import SimpleNamespace
import pytest

from rimbot.population import capacity, guard, refresh, SkillBlocked
from rimbot.player_commands import COMMAND, semantic_tools
from rimbot.bridge_game import is_write
from rimbot.colony_plan import ColonyPlan, ColonyGoal
from rimbot.colony_policy import required_colony_work


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
async def test_admission_requires_real_integration_and_never_recaptures_completed_recruit():
    policy, facts, snapshot, people = state()
    plan = ColonyPlan()
    goal = plan.colony_goals['Population-Thing_B'] = ColonyGoal(priority_class=3, target={'pawn': 'Thing_B', 'decision': 'recruit'})
    snapshot['people'][1].update(admitted=True, prisoner=False, ownedBed='Thing_Bed', ownedBedForPrisoners=False, needsTend=False, food=.8)
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
