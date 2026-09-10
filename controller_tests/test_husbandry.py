import pytest
from pydantic import ValidationError

from rimbot.husbandry import assess_herd, validate_dispatch
from rimbot.player_commands import COMMAND, MaintainHerd, semantic_tools


def inputs():
    target = MaintainHerd(kind='MaintainHerd', race='Cow', minimum=1, maximum=1,
                          feed_days=10, trainables=['Obedience']).model_dump()
    animal = dict(id='cow1', race='Cow', contained=True, safeToSlaughter=True,
                  handlers=[dict(id='handler', priority=1)],
                  slaughter=False, release=False, training=[dict(name='Obedience',
                  learned=False, wanted=False, canTrain=True)])
    observed = dict(success=True, animals=[animal])
    feed = dict(readable=True, consumers=[dict(id='cow1', runwayDays=12)])
    return target, observed, feed


def test_settings_and_future_animals_never_prove_outcomes():
    target, observed, feed = inputs()
    observed['animals'][0]['training'][0]['wanted'] = True
    observed['animals'][0]['pregnant'] = True
    target['minimum'] = target['maximum'] = 2
    result = assess_herd(target, observed, feed)
    assert result['population'] == 1 and result['training']
    assert not result['satisfied']
    assert result['waiting_births']


@pytest.mark.parametrize('days', [None, float('nan'), float('inf'), -1, True])
def test_invalid_feed_stays_unknown(days):
    target, observed, feed = inputs()
    feed['consumers'][0]['runwayDays'] = days
    result = assess_herd(target, observed, feed)
    assert result['feed_days'] is None
    assert 'Reachable stored feed unavailable' in result['blockers']


def test_population_culling_requires_policy_and_native_protection():
    target, observed, feed = inputs()
    target['minimum'] = target['maximum'] = 0
    result = assess_herd(target, observed, feed)
    assert not result['surplus']
    target['allow_slaughter'] = True
    assert len(assess_herd(target, observed, feed)['surplus']) == 1
    target['protected_ids'] = ['cow1']
    assert not assess_herd(target, observed, feed)['surplus']
    target['protected_ids'] = []
    observed['animals'][0]['safeToSlaughter'] = False
    assert not assess_herd(target, observed, feed)['surplus']


def test_culling_preserves_requested_breeding_pair():
    target, observed, feed = inputs()
    target.update(minimum=2, maximum=2, breeding_pairs=1, allow_slaughter=True, trainables=[])
    original = observed['animals'][0]
    observed['animals'] = [dict(original, id='male1', gender='Male', fertileAdult=True),
                           dict(original, id='female1', gender='Female', fertileAdult=True),
                           dict(original, id='young1', gender='Male', fertileAdult=False)]
    result = assess_herd(target, observed, feed)
    assert [a['id'] for a in result['surplus']] == ['young1']


def test_pending_removal_limits_more_orders_but_never_completes_population():
    target, observed, feed = inputs()
    target['minimum'] = target['maximum'] = 0
    target['allow_slaughter'] = True
    observed['animals'][0]['slaughter'] = True
    result = assess_herd(target, observed, feed)
    assert not result['surplus'] and not result['satisfied']


def test_seasonal_reserve_and_real_containment_required():
    target, observed, feed = inputs()
    target['feed_days'] = 30
    observed['animals'][0]['contained'] = False
    result = assess_herd(target, observed, feed)
    assert len(result['blockers']) == 2


def test_learned_training_and_collected_products_are_separate():
    target, observed, feed = inputs()
    observed['animals'][0]['training'][0]['learned'] = True
    assert assess_herd(target, observed, feed)['satisfied']
    observed['animals'][0]['milkFull'] = True
    assert not assess_herd(target, observed, feed)['satisfied']


def test_command_contract_and_discovery():
    target, _, _ = inputs()
    assert isinstance(COMMAND.validate_python(target), MaintainHerd)
    assert 'MaintainHerd' in {t['function']['name'] for t in semantic_tools()}
    for values in ({'maximum': 0}, {'feed_days': float('nan')}, {'trainables': ['x', 'x']}):
        with pytest.raises(ValidationError): COMMAND.validate_python(dict(target, **values))


def test_unowned_native_write_refused():
    from rimbot.colony_plan import ColonyPlan
    with pytest.raises(ValueError, match='player herd target'):
        validate_dispatch(ColonyPlan(), None, {})


def test_handler_workload_preserves_unknown_and_disabled_work():
    target, observed, feed = inputs()
    observed['animals'][0]['handlers'] = []
    result = assess_herd(target, observed, feed)
    assert result['handler_workload']['training_targets'] == 1
    assert any('no active capable reachable handler' in b for b in result['blockers'])


def test_slaughter_cannot_bypass_current_player_policy():
    from rimbot.colony_plan import ColonyPlan, ColonyGoal, PlanStep, PlanSpec
    args = dict(animal='cow1', slaughter=True, dryRun=False)
    step = PlanStep(id='herd-step', title='Cull surplus', goal_id='MaintainHerd-Cow',
                    completion_criteria='Native slaughter designation observed',
                    action=dict(kind='native_operation', tool='home/husbandry_config', arguments=args))
    goal = ColonyGoal(priority_class=3, source='PLAYER', target={'allow_slaughter': False})
    plan = ColonyPlan(spec=PlanSpec(steps=[step]), colony_goals={'MaintainHerd-Cow': goal})
    with pytest.raises(ValueError, match='authorization'): validate_dispatch(plan, step.id, args)
    goal.target['allow_slaughter'] = True
    assert validate_dispatch(plan, step.id, args) is goal
    goal.cancelled = True
    with pytest.raises(ValueError, match='unchanged'): validate_dispatch(plan, step.id, args)


def test_seasonal_feed_uses_native_demand_and_competing_eaters():
    from rimbot.colony_plan import ColonyGoal, ColonyPlan
    from rimbot.husbandry import update_feed_goal
    target, observed, feed = inputs()
    goal = ColonyGoal(priority_class=3, target=target, evidence={'husbandry': assess_herd(target, observed, feed)})
    plan = ColonyPlan(colony_goals={'MaintainHerd-Cow': goal})
    native = {'nativeForecastInputs': {'animalIds': ['cow1', 'cow2'], 'combinedFoodSupply': {
        'stocks': [dict(id='hay1', defName='Hay', count=20, nutrition=1, eaters=['cow1', 'cow2'], holder=None)],
        'consumers': [dict(id='cow1', nutritionPerDay=.8), dict(id='cow2', nutritionPerDay=.2)]}}}
    update_feed_goal(plan, 'MaintainHerd-Cow', goal, native, feed)
    child = plan.colony_goals[goal.evidence['feed_goal']]
    assert child.target == {'resource': 'Hay', 'quantity': 200}
    assert goal.evidence['husbandry']['feed_capacity']['future_births_credited'] is False
    native['nativeForecastInputs']['combinedFoodSupply']['stocks'][0]['eaters'] = ['cow2']
    update_feed_goal(plan, 'MaintainHerd-Cow', goal, native, feed)
    assert not goal.evidence['husbandry']['satisfied']
    assert any('safely edible' in b for b in goal.evidence['husbandry']['blockers'])


@pytest.mark.asyncio
async def test_cancelled_herd_stops_linked_feed_goal_without_native_reads():
    from types import SimpleNamespace
    from rimbot.colony_plan import ColonyGoal, ColonyPlan
    from rimbot.husbandry import refresh_husbandry
    parent = ColonyGoal(priority_class=3, cancelled=True)
    child = ColonyGoal(priority_class=3, evidence={'herd_owner': 'MaintainHerd-Cow'})
    plan = ColonyPlan(colony_goals={'MaintainHerd-Cow': parent, 'MaintainResource-herd-Cow-Hay': child})
    assert await refresh_husbandry(SimpleNamespace(current_plan=plan), {}) == []
    assert child.cancelled
