from copy import deepcopy
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from rimbot.expedition_policy import ExpeditionPolicy, evaluate_expedition, evaluate_world, home_food_after


def evidence():
    facts = {'foodSupply': dict(readable=True, consumers=[
        dict(id='home', nutritionPerDay=1), dict(id='away', nutritionPerDay=1)], stocks=[
        dict(id='food', holder=None, defName='Pemmican', count=100, nutrition=10,
             eaters=['home', 'away'], perishable=False)])}
    preview = dict(route=dict(reachable=True, estimatedTicks=30000, foodDays=2, temperature=20),
                   homePawns=['home'], homeDoctors=1, returnStorage=[dict(defName='Pemmican', cells=10)])
    world = dict(success=True, complete=True, caravans=[], assemblies=[])
    return preview, facts, world


def test_home_runway_removes_departing_people_and_actual_loaded_cargo_without_mutation():
    _, facts, _ = evidence()
    before = deepcopy(facts)
    assert home_food_after(facts, {'away'}, {'Pemmican': 60}) == 4
    assert facts == before
    facts['foodSupply']['stocks'][0].update(perishable=True, rotTicks=None)
    assert home_food_after(facts, {'away'}, {}) is None


@pytest.mark.parametrize('change,reason', [
    ({'estimatedTicks': 240000}, 'time budget'),
    ({'foodDays': .1}, 'Travel food'),
    ({'temperature': -40}, 'temperature'),
    ({'reachable': False}, 'route'),
    ({'factionId': 'f', 'goodwill': -70}, 'Diplomatic'),
    ({'hostile': True}, 'Hostile'),
])
def test_explicit_outbound_orders_still_obey_player_risk_limits(change, reason):
    preview, facts, world = evidence()
    preview['route'].update(change)
    result = evaluate_expedition(ExpeditionPolicy(), preview, facts, world, action='form', crew=['away'])
    assert not result['eligible']
    assert any(reason in text for text in result['blockers'])


def test_return_preserves_recovery_option_and_reports_risk():
    preview, facts, world = evidence()
    preview['route'].update(foodDays=0, temperature=-40, estimatedTicks=300000)
    preview['returnStorage'] = [dict(cells=0)]
    result = evaluate_expedition(ExpeditionPolicy(), preview, facts, world, action='return')
    assert result['eligible'] and len(result['warnings']) == 3
    preview['route']['reachable'] = False
    assert not evaluate_expedition(ExpeditionPolicy(), preview, facts, world, action='return')['eligible']


@pytest.mark.parametrize('change', [dict(homePawns=[]), dict(homeDoctors=0), dict(returnStorage=None)])
def test_home_staff_and_storage_are_required(change):
    preview, facts, world = evidence()
    preview.update(change)
    assert not evaluate_expedition(ExpeditionPolicy(), preview, facts, world, action='form', crew=['away'])['eligible']


def test_unknown_census_and_concurrent_parties_refuse_departure():
    preview, facts, world = evidence()
    world['assemblies'] = [{}, {}]
    assert not evaluate_expedition(ExpeditionPolicy(), preview, facts, world, action='form')['eligible']
    world['operation'] = dict(ResultWasTruncated=True)
    assert not evaluate_expedition(ExpeditionPolicy(), preview, facts, world, action='stop')['eligible']


def test_evaluation_keeps_failed_objectives_terminal_and_identifies_stranded_parties():
    world = dict(success=True, complete=True, caravans=[dict(id='c', pawns=[], foodDays=0, homeRoutes=[])],
                 quests=[dict(id='q', state='EndedFailed', tradeRequests=[dict(resource='Steel', count=20)])])
    result = evaluate_world(ExpeditionPolicy(), world, {'resources': {'Steel': 5}})
    assert result['caravans'][0]['recovery_required']
    assert result['quests'][0]['resource_deficits'] == {'Steel': 15}
    assert result['quests'][0]['recommendation'] == 'Terminal objective; do not replay'


def test_policy_cannot_reverse_temperature_limits():
    with pytest.raises(ValueError):
        ExpeditionPolicy(minimum_destination_temperature=40, maximum_destination_temperature=0)


def test_incomplete_world_does_not_produce_actionable_recommendations():
    assert evaluate_world(ExpeditionPolicy(), {'success': False}, {})['readable'] is False


def test_quest_evaluation_lists_carried_goods_without_crediting_them_to_home():
    world = dict(success=True, complete=True, caravans=[dict(id='party', foodDays=1, homeRoutes=[],
        pawns=[dict(dead=False, downed=False, inventory=[dict(defName='Steel', count=20)])])],
        quests=[dict(id='q', state='Ongoing', tradeRequests=[dict(resource='Steel', count=20)])])
    quest = evaluate_world(ExpeditionPolicy(), world, {'resources': {}})['quests'][0]
    assert quest['resource_deficits'] == {'Steel': 20}
    assert quest['carried_candidates'] == ['party']
    assert 'validate native quality' in quest['recommendation']


@pytest.mark.asyncio
@pytest.mark.parametrize('cargo,remaining_warden,blocked', [(20, True, False), (80, True, True), (20, False, True)])
async def test_departure_preserves_population_food_and_assigned_care(cargo, remaining_warden, blocked):
    from rimbot.expedition_policy import guard_population_commitments
    _, facts, _ = evidence()
    facts.update(bedCapacity=3, nutritionPerDay=2, foodNutrition=10)
    snapshot = dict(success=True, people=[dict(thingId=p, admitted=True, dead=False, nutritionPerDay=1)
        for p in ('home', 'away')] + [dict(thingId='candidate', admitted=False, guest=True, dead=False, nutritionPerDay=1)])
    people = [dict(thingId=p, dead=False, downed=False, work={'types': [
        dict(name='Doctor', disabled=False, priority=1),
        dict(name='Warden', disabled=False, priority=1 if p == 'away' or remaining_warden else 0)]}) for p in ('home', 'away')]
    async def query(name, **kwargs):
        return snapshot if name == 'home/population' else dict(pawns=people)
    goal = SimpleNamespace(target={'pawn': 'candidate'}, cancelled=False, status='active')
    rt = SimpleNamespace(game=SimpleNamespace(query=AsyncMock(side_effect=query)), current_plan=SimpleNamespace(
        colony_goals={'Population-candidate': goal}, control={'population_policy': {'maximum': 3, 'food_days': 2}}))
    if blocked:
        with pytest.raises(ValueError):
            await guard_population_commitments(rt, ['away'], {'Pemmican': cargo}, facts)
    else:
        await guard_population_commitments(rt, ['away'], {'Pemmican': cargo}, facts)


def test_native_first_rot_estimate_is_visible_without_claiming_all_food_expires():
    preview, facts, world = evidence()
    preview['route']['foodRotDays'] = .1
    result = evaluate_expedition(ExpeditionPolicy(), preview, facts, world, action='form', crew=['away'])
    assert result['eligible']
    assert any('post-rot supply guarantee' in warning for warning in result['warnings'])
