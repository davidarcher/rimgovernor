from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.animal_feed import feed_evidence, feed_method
from rimbot.colony_plan import ColonyPlan, ColonyGoal
from rimbot.colony_skills import SkillBlocked
from rimbot.colony_upkeep import upkeep_nodes


def facts(nutrition=0, **changes):
    return dict(tick=10, resources={}, policyResources={'Kibble': {}},
        upkeep=dict(version=1, tick=10, errors={}, animals=[dict(dict(id='animal', release=False, slaughter=False), **changes)],
                    feedDefinitions=[dict(defName='Kibble', nutritionPerItem=.1, eaters=['animal', 'other'])]),
        nativeForecastInputs=dict(readable=True, tick=10, animalIds=['animal'], combinedFoodSupply=dict(readable=True,
            consumers=[dict(id='animal', nutritionPerDay=1), dict(id='other', nutritionPerDay=1)],
            stocks=[dict(id='feed', defName='Kibble', count=10, nutrition=nutrition, holder=None,
                         eaters=['animal', 'other'], perishable=False)])))


def test_feed_hysteresis_keeps_unknowns_and_accounts_for_competing_eaters():
    control = {}
    assert feed_evidence(facts(2), control)[0]['count'] == 3
    assert feed_evidence(facts(6), control)  # Three days does not recover a four-day target.
    f = facts(20)
    f['nativeForecastInputs']['tick'] = 9
    assert feed_evidence(f, control) is None
    assert control['animal_feed_active']['animal']
    assert feed_evidence(facts(8), control) == []
    assert feed_evidence(facts(5), control) == []  # Above entry: no oscillation.
    assert feed_evidence(facts(0, release=True), control) == []


def test_player_herd_target_owns_its_feed_including_explicit_cancellation():
    goal = ColonyGoal(source='PLAYER', priority_class=3, target=dict(race='Husky', feed_days=1))
    f = facts(defName='Husky')
    assert feed_evidence(f, {}, {'MaintainHerd-Husky': goal}) == []
    goal.cancelled = True
    assert feed_evidence(f, {}, {'MaintainHerd-Husky': goal}) == []


@pytest.mark.asyncio
async def test_empty_reserve_uses_native_feed_definition_and_shared_production(monkeypatch):
    f = facts()
    plan = ColonyPlan(colony_goals={'MaintainAnimalFeed': ColonyGoal(priority_class=3)})
    upkeep_nodes(f, plan.control)
    helper = AsyncMock(return_value=('native-bill', [dict(kind='native_action')]))
    monkeypatch.setattr('rimbot.production_policy.resource_method', helper)
    assert await feed_method(SimpleNamespace(current_plan=plan), f) == helper.return_value
    assert plan.colony_goals['MaintainAnimalFeed'].target == dict(resource='Kibble', quantity=80)
    assert plan.colony_goals['MaintainAnimalFeed'].evidence['feed_resource'] == 'Kibble'


@pytest.mark.asyncio
async def test_unreachable_stock_does_not_trigger_unbounded_production(monkeypatch):
    f = facts()
    f['resources']['Kibble'] = 80
    plan = ColonyPlan(colony_goals={'MaintainAnimalFeed': ColonyGoal(priority_class=3)})
    upkeep_nodes(f, plan.control)
    helper = AsyncMock()
    monkeypatch.setattr('rimbot.production_policy.resource_method', helper)
    with pytest.raises(SkillBlocked, match='staging required'):
        await feed_method(SimpleNamespace(current_plan=plan), f)
    helper.assert_not_awaited()


@pytest.mark.asyncio
async def test_feed_respects_resource_policy_and_does_not_rewrite_animal_policy(monkeypatch):
    f = facts()
    plan = ColonyPlan(colony_goals={'MaintainAnimalFeed': ColonyGoal(priority_class=3)})
    plan.control['resource_policy'] = {'Kibble': {'spending': 'stopped'}}
    upkeep_nodes(f, plan.control)
    helper = AsyncMock()
    monkeypatch.setattr('rimbot.production_policy.resource_method', helper)
    with pytest.raises(SkillBlocked, match='restricted by player resource policy'):
        await feed_method(SimpleNamespace(current_plan=plan), f)
    helper.assert_not_awaited()
